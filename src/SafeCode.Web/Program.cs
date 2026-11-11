using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Renci.SshNet.Common;
using SafeCode.Core;

var repoRoot = FindRepoRoot();
var dataDirectory = Environment.GetEnvironmentVariable("SAFECODE_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafeCode");
Directory.CreateDirectory(dataDirectory);

int requestedPort = 18765;
bool openAppWindow = false;
bool openBrowser = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
        requestedPort = parsedPort;
    else if (args[i] == "--open-app")
        openAppWindow = true;
    else if (args[i] == "--open-browser")
        openBrowser = true;
}

int port = EnsureAvailableLoopbackPort(requestedPort);
string tokenFile = Path.Combine(dataDirectory, "web-session.token");
string sessionToken;
if (File.Exists(tokenFile) && Regex.IsMatch(File.ReadAllText(tokenFile).Trim(), "^[a-f0-9]{48}$"))
{
    sessionToken = File.ReadAllText(tokenFile).Trim();
}
else
{
    sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    File.WriteAllText(tokenFile, sessionToken);
    WebLocalVault.RestrictFilePermissions(tokenFile);
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var state = new SafeCodeWebHost(dataDirectory);
app.Lifetime.ApplicationStopping.Register(() => state.Dispose());

var contentTypes = new FileExtensionContentTypeProvider();

app.MapGet("/", async context =>
{
    string html = await File.ReadAllTextAsync(Path.Combine(repoRoot, "index.html"));
    html = html.Replace("<script src=\"app.js\"></script>", "<script src=\"/safe-code-web.js\"></script>\n<script src=\"app.js\"></script>");
    context.Response.Headers.CacheControl = "no-store";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
});

app.MapGet("/index.html", context =>
{
    string query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value! : "";
    context.Response.Redirect("/" + query);
    return Task.CompletedTask;
});

app.MapGet("/safe-code-web.js", async context =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.ContentType = "application/javascript; charset=utf-8";
    string js = $$"""
    (() => {
      window.__safeCodeDesktop = true;
      window.__safeCodeWeb = true;
      const token = "{{sessionToken}}";
      const outbox = [];
      let socket = null;

      function connectSocket() {
        const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const ws = new WebSocket(`${protocol}//${location.host}/ws?token=${encodeURIComponent(token)}`);
        socket = ws;
        ws.addEventListener('open', () => {
          while (outbox.length > 0 && ws.readyState === WebSocket.OPEN) {
            ws.send(outbox.shift());
          }
        });
        ws.addEventListener('message', event => {
          try {
            const msg = JSON.parse(event.data);
            if (msg.type === 'response') {
              window.safeCodeNativeResponse?.({ id: msg.id, result: msg.result, error: msg.error });
            } else if (msg.type === 'logs' && Array.isArray(msg.lines)) {
              window.safeCodeNativeLogs?.(msg.lines);
            } else if (msg.type === 'updateProgress') {
              window.safeCodeUpdateProgress?.(msg.done, msg.total);
            }
          } catch {}
        });
        ws.addEventListener('close', () => {
          if (socket === ws) {
            socket = null;
            setTimeout(connectSocket, 1000);
          }
        });
      }

      window.__safeCodeWebPostMessage = payload => {
        const serialized = JSON.stringify(payload);
        if (socket && socket.readyState === WebSocket.OPEN) {
          socket.send(serialized);
        } else {
          outbox.push(serialized);
          if (!socket || socket.readyState > WebSocket.OPEN) connectSocket();
        }
      };

      connectSocket();
    })();
    """;
    await context.Response.WriteAsync(js);
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }
    string suppliedToken = context.Request.Query["token"].ToString();
    byte[] expectedBytes = Encoding.ASCII.GetBytes(sessionToken);
    byte[] suppliedBytes = Encoding.ASCII.GetBytes(suppliedToken);
    if (suppliedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
    {
        context.Response.StatusCode = 403;
        return;
    }
    string origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) &&
        origin != $"http://127.0.0.1:{port}" &&
        origin != $"http://localhost:{port}")
    {
        context.Response.StatusCode = 403;
        return;
    }

    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await state.RunWebSocketSessionAsync(ws, app.Lifetime.ApplicationStopping);
});

string[] allowedStaticFiles = ["styles.css", "app.js", "native.js", "client-preview.html"];
foreach (string file in allowedStaticFiles)
{
    string captured = file;
    app.MapGet("/" + captured, async context =>
    {
        string fullPath = Path.Combine(repoRoot, captured);
        if (!File.Exists(fullPath)) { context.Response.StatusCode = 404; return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = contentTypes.TryGetContentType(fullPath, out var mime) ? mime : "application/octet-stream";
        await context.Response.SendFileAsync(fullPath);
    });
}

app.MapGet("/assets/{**subpath}", async context =>
{
    string subpath = context.Request.RouteValues["subpath"]?.ToString() ?? "";
    string assetsRoot = Path.GetFullPath(Path.Combine(repoRoot, "assets"));
    string fullPath = Path.GetFullPath(Path.Combine(assetsRoot, subpath));
    if (!fullPath.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(fullPath))
    {
        context.Response.StatusCode = 404;
        return;
    }
    context.Response.ContentType = contentTypes.TryGetContentType(fullPath, out var mime) ? mime : "application/octet-stream";
    await context.Response.SendFileAsync(fullPath);
});

string url = $"http://127.0.0.1:{port}";
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"SAFE_CODE_WEB_READY {url}");
    Console.Out.Flush();
    if (openAppWindow) PlatformLauncher.TryOpenAppWindow(url + "/?shell=app");
    else if (openBrowser) PlatformLauncher.TryOpenBrowser(url);
});

await app.RunAsync();

static string FindRepoRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "index.html")) && File.Exists(Path.Combine(current.FullName, "native.js")))
            return current.FullName;
        current = current.Parent;
    }
    var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (cwd != null)
    {
        if (File.Exists(Path.Combine(cwd.FullName, "index.html")) && File.Exists(Path.Combine(cwd.FullName, "native.js")))
            return cwd.FullName;
        cwd = cwd.Parent;
    }
    throw new FileNotFoundException("未找到 Safe Code 前端资源目录（index.html / native.js）。");
}

static int EnsureAvailableLoopbackPort(int preferred)
{
    try
    {
        using var probe = new TcpListener(IPAddress.Loopback, preferred);
        probe.Start();
        return preferred;
    }
    catch (SocketException)
    {
        using var fallback = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start();
        return ((IPEndPoint)fallback.LocalEndpoint).Port;
    }
}

internal sealed record AccountDraft(string Username, string Hostname, string Password);
internal sealed record DesktopResolution(int Width = 1280, int Height = 800);

internal sealed class WebLocalVault
{
    private readonly string directory;
    private readonly string masterKeyPath;

    public WebLocalVault(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        masterKeyPath = Path.Combine(directory, "vault.key");
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(masterKeyPath))
        {
            byte[] existing = File.ReadAllBytes(masterKeyPath);
            if (existing.Length == 32) return existing;
        }
        byte[] created = RandomNumberGenerator.GetBytes(32);
        string temp = masterKeyPath + ".new";
        File.WriteAllBytes(temp, created);
        RestrictFilePermissions(temp);
        File.Move(temp, masterKeyPath, true);
        return created;
    }

    public void SaveEncrypted<T>(string relativePath, T value)
    {
        byte[] key = GetOrCreateKey();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plain, cipher, tag);
            }
            byte[] combined = new byte[1 + nonce.Length + tag.Length + cipher.Length];
            combined[0] = 1;
            Buffer.BlockCopy(nonce, 0, combined, 1, nonce.Length);
            Buffer.BlockCopy(tag, 0, combined, 1 + nonce.Length, tag.Length);
            Buffer.BlockCopy(cipher, 0, combined, 1 + nonce.Length + tag.Length, cipher.Length);

            string target = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".new";
            File.WriteAllBytes(temp, combined);
            RestrictFilePermissions(temp);
            File.Move(temp, target, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public T? LoadEncrypted<T>(string relativePath) where T : class
    {
        string target = Path.Combine(directory, relativePath);
        if (!File.Exists(target)) return null;
        byte[] key = GetOrCreateKey();
        byte[]? plain = null;
        try
        {
            byte[] combined = File.ReadAllBytes(target);
            if (combined.Length < 29 || combined[0] != 1) return null;
            byte[] nonce = combined[1..13];
            byte[] tag = combined[13..29];
            byte[] cipher = combined[29..];
            plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }
            return JsonSerializer.Deserialize<T>(plain, JsonDefaults.Options);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Delete(string relativePath)
    {
        string target = Path.Combine(directory, relativePath);
        try { if (File.Exists(target)) File.Delete(target); } catch { }
    }

    public static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
    }
}

internal sealed class WebPreferences
{
    private static readonly DesktopResolution[] Resolutions = [new(1024, 768), new(1280, 800), new(1366, 768), new(1600, 900), new(1920, 1080)];
    private readonly string directory;
    private readonly WebLocalVault vault;

    public WebPreferences(string directory, WebLocalVault vault)
    {
        this.directory = directory;
        this.vault = vault;
    }

    private static string DraftRelativePath(SshSession session) =>
        Path.Combine("account-drafts", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.Host + ":" + session.Port + ":" + session.Fingerprint))).ToLowerInvariant() + ".enc");

    public AccountDraft? ReadDraft(SshSession session) => vault.LoadEncrypted<AccountDraft>(DraftRelativePath(session));

    public void SaveDraft(SshSession session, AccountDraft draft)
    {
        if (!Regex.IsMatch(draft.Username, "^[a-z][a-z0-9_-]{1,30}$"))
            throw new ArgumentException("用户名须为 2–31 位小写字母、数字、下划线或短横线。");
        if (!Regex.IsMatch(draft.Hostname, "^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$"))
            throw new ArgumentException("机器名称须为 1–63 位字母、数字或短横线。");
        if (draft.Password.Length > 0 && (draft.Password.Length < 12 || draft.Password.Length > 256 || draft.Password.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new ArgumentException("工作密码须为 12–256 位，不能包含换行或空字符。");
        vault.SaveEncrypted(DraftRelativePath(session), draft);
    }

    public void ClearDraft(SshSession session) => vault.Delete(DraftRelativePath(session));

    public DesktopResolution ReadResolution()
    {
        try
        {
            string path = Path.Combine(directory, "desktop-resolution.json");
            if (!File.Exists(path)) return new();
            var value = JsonSerializer.Deserialize<DesktopResolution>(File.ReadAllText(path), JsonDefaults.Options);
            return value != null && Resolutions.Contains(value) ? value : new();
        }
        catch { return new(); }
    }

    public void SaveResolution(DesktopResolution resolution)
    {
        if (!Resolutions.Contains(resolution)) throw new ArgumentException("请选择支持的分辨率。");
        string path = Path.Combine(directory, "desktop-resolution.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(resolution, JsonDefaults.Options));
        File.Move(path + ".new", path, true);
    }
}

internal static class PlatformLauncher
{
    public static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        }
        catch { }
    }

    public static void TryOpenAppWindow(string url)
    {
        if (OperatingSystem.IsMacOS())
        {
            string[] chromeCandidates =
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser"
            ];
            foreach (string candidate in chromeCandidates)
            {
                if (File.Exists(candidate))
                {
                    try
                    {
                        var start = new ProcessStartInfo(candidate) { UseShellExecute = false };
                        start.ArgumentList.Add("--app=" + url);
                        start.ArgumentList.Add("--window-size=460,640");
                        Process.Start(start);
                        return;
                    }
                    catch { }
                }
            }
        }
        TryOpenBrowser(url);
    }

    public static async Task<string?> PickFolderAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            var start = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("POSIX path of (choose folder with prompt \"选择要挂载到 Debian 13 服务器的本地目录\")");
            using var proc = Process.Start(start);
            if (proc == null) return null;
            string output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) return null;
            string trimmed = output.Trim();
            return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
        }
        throw new InvalidOperationException("当前环境不支持原生目录选择框，请直接在输入框中填写本地目录路径。");
    }

    public static void TryCopyClipboard(string text)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var start = new ProcessStartInfo("pbcopy") { UseShellExecute = false, RedirectStandardInput = true };
                using var proc = Process.Start(start);
                if (proc != null)
                {
                    proc.StandardInput.Write(text);
                    proc.StandardInput.Close();
                    proc.WaitForExit(2000);
                }
            }
            catch { }
        }
    }

    public static string? FindExecutable(params string[] names)
    {
        string[] extraDirs = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"];
        string[] pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string name in names)
        {
            if (Path.IsPathRooted(name) && File.Exists(name)) return name;
            foreach (string dir in extraDirs.Concat(pathDirs).Distinct())
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

internal sealed class SafeCodeWebHost : IDisposable
{
    private readonly string dataDirectory;
    private readonly WebLocalVault vault;
    private readonly WebPreferences preferences;
    private readonly ConcurrentDictionary<Guid, WebSocket> clients = new();
    private readonly object logLock = new();
    private readonly Queue<(string Step, string Line)> logQueue = new();
    private readonly Timer logTimer;
    private readonly CancellationTokenSource lifetime = new();

    private SshSession? session;
    private CancellationTokenSource? connecting;
    private CancellationTokenSource? browsing;
    private bool connectionPending;
    private bool deploying;
    private string activeStep = "";
    private (string Token, HostKeyInfo Key)? pendingHostTrust;
    private uint? cachedCloudPort;
    private uint? cachedRdpPort;
    private string cachedTunnelHost = "";

    public SafeCodeWebHost(string dataDirectory)
    {
        this.dataDirectory = dataDirectory;
        vault = new WebLocalVault(dataDirectory);
        preferences = new WebPreferences(dataDirectory, vault);
        logTimer = new Timer(_ => _ = FlushLogsAsync(), null, 120, 120);
    }

    public async Task RunWebSocketSessionAsync(WebSocket socket, CancellationToken stoppingToken)
    {
        Guid clientId = Guid.NewGuid();
        clients[clientId] = socket;
        byte[] buffer = new byte[65536];
        try
        {
            while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > 4 * 1024 * 1024) return;
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                string messageText = Encoding.UTF8.GetString(ms.ToArray());
                _ = HandleRpcEnvelopeAsync(socket, messageText);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            clients.TryRemove(clientId, out _);
        }
    }

    private async Task HandleRpcEnvelopeAsync(WebSocket socket, string rawJson)
    {
        string id = "";
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            string op = root.TryGetProperty("op", out var opProp) ? opProp.GetString() ?? "" : "";
            var args = root.TryGetProperty("args", out var argsProp) ? argsProp.Clone() : default;

            object? answer = await HandleAsync(op, args);
            await FlushLogsAsync();
            await SendJsonAsync(socket, new { type = "response", id, result = answer, error = (string?)null });
        }
        catch (Exception error)
        {
            await FlushLogsAsync();
            await SendJsonAsync(socket, new { type = "response", id, result = (object?)null, error = error.Message });
        }
    }

    private async Task<object?> HandleAsync(string operation, JsonElement args)
    {
        string Read(string key, string fallback = "") =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;

        switch (operation)
        {
            case "window.resize":
            case "window.drag":
            case "window.minimize":
            case "window.maximize":
            case "window.close":
                return true;

            case "copy":
                PlatformLauncher.TryCopyClipboard(Read("text"));
                return true;

            case "services.preferences":
                return ReadUsagePreferences();

            case "services.consent":
            {
                bool enabled = args.GetProperty("enabled").GetBoolean();
                bool confirmed = args.TryGetProperty("confirmed", out var c) && c.GetBoolean();
                var prefs = ReadUsagePreferencesObject();
                prefs["enabled"] = enabled;
                prefs["confirmed"] = (prefs.TryGetValue("confirmed", out var prev) && prev is true) || confirmed;
                SaveUsagePreferencesObject(prefs);
                return ReadUsagePreferences();
            }

            case "diagnostics.consent":
            {
                bool enabled = args.GetProperty("enabled").GetBoolean();
                var prefs = ReadUsagePreferencesObject();
                prefs["diagnosticsEnabled"] = enabled;
                SaveUsagePreferencesObject(prefs);
                return ReadUsagePreferences();
            }

            case "feedback.send":
                throw new InvalidOperationException("当前本地 Web 模式未启用在线反馈上报，请通过 GitHub Issues 或 QQ 群反馈。");

            case "updates.check":
                return new
                {
                    currentVersion = "Beta-0.4.3.3-debian13",
                    version = (string?)null,
                    status = "当前运行 Debian 13 定制版",
                    page = "https://github.com/Muchuaner/safe-code/releases",
                    canInstall = false
                };

            case "notice.state":
            {
                string noticePath = Path.Combine(dataDirectory, "notice-accepted-v1");
                return new { accepted = File.Exists(noticePath) };
            }

            case "notice.accept":
                File.WriteAllText(Path.Combine(dataDirectory, "notice-accepted-v1"), DateTimeOffset.UtcNow.ToString("O"));
                return true;

            case "login.load":
                return vault.LoadEncrypted<ConnectionSettings>("login.enc");

            case "pickFolder":
                return await PlatformLauncher.PickFolderAsync(lifetime.Token);

            case "connection.cancel":
                connecting?.Cancel();
                pendingHostTrust = null;
                return true;

            case "host.trust":
                if (pendingHostTrust is not { } challenge || challenge.Token != Read("token"))
                    throw new InvalidOperationException("服务器身份确认已失效，请重新连接。");
                new HostTrustStore(dataDirectory).Accept(challenge.Key);
                pendingHostTrust = null;
                return true;

            case "connect":
                if (connectionPending) throw new InvalidOperationException("连接正在处理中，请稍候。");
                connectionPending = true;
                try
                {
                    connecting?.Dispose();
                    connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    connecting.CancelAfter(TimeSpan.FromSeconds(90));
                    session?.Dispose();
                    cachedCloudPort = null;
                    cachedRdpPort = null;
                    cachedTunnelHost = "";
                    session = new SshSession(dataDirectory);
                    session.Log += text => QueueLog(activeStep, text);
                    session.DirectoryLog += text => QueueLog("folder", text);
                    var settings = new ConnectionSettings(
                        Read("host"),
                        Read("username"),
                        Read("password"),
                        args.TryGetProperty("port", out var p) ? p.GetInt32() : 22);
                    var current = session;
                    var server = await Task.Run(() => current.ConnectAsync(settings, _ => false, connecting.Token), connecting.Token);
                    pendingHostTrust = null;
                    cachedTunnelHost = current.Host + ":" + current.Port;
                    vault.SaveEncrypted("login.enc", settings);
                    if (server.Statuses.GetValueOrDefault("ssh") && server.HerdrReady)
                        _ = TrySyncHerdrMachineAsync(current, server, lifetime.Token);
                    return new { ready = true, server };
                }
                catch (HostTrustRequiredException error)
                {
                    string token = Guid.NewGuid().ToString("N");
                    pendingHostTrust = (token, error.Key);
                    return new { requiresTrust = true, token, key = error.Key };
                }
                catch (Exception error)
                {
                    pendingHostTrust = null;
                    throw new InvalidOperationException(DescribeConnectionError(error));
                }
                finally
                {
                    connectionPending = false;
                }

            case "job.resume":
                if (deploying) throw new InvalidOperationException("已有任务正在监控。");
                deploying = true; activeStep = Read("operation");
                try { return await RequireSession().ResumeJobAsync(Read("jobId"), lifetime.Token); }
                finally { deploying = false; activeStep = ""; }

            case "inspect":
                return await RequireSession().RequestAsync<ServerInfo>("inspect", null, lifetime.Token);

            case "ip.info":
                return await IpIntelligenceService.QueryAsync(Read("ip"), lifetime.Token);

            case "metrics":
                return await RequireSession().MeasureNetworkAsync(lifetime.Token);

            case "deploy":
                if (deploying) throw new InvalidOperationException("另一个任务正在执行，请等待。");
                deploying = true; activeStep = Read("step");
                try
                {
                    var currentSession = RequireSession();
                    var locale = args.TryGetProperty("locale", out var region) && region.ValueKind != JsonValueKind.Null
                        ? JsonSerializer.Deserialize<LocaleSettings>(region, JsonDefaults.Options)
                        : null;
                    var draft = activeStep == "account" ? preferences.ReadDraft(currentSession) : null;
                    var result = await currentSession.DeployAsync(
                        activeStep,
                        locale,
                        draft?.Username ?? Read("username", "workspace"),
                        lifetime.Token,
                        draft?.Password ?? Read("password"),
                        draft?.Hostname ?? Read("hostname"));
                    if (activeStep == "account" && result.Statuses.GetValueOrDefault("account"))
                        preferences.ClearDraft(currentSession);
                    if (activeStep == "ssh")
                    {
                        await currentSession.VerifyWorkspaceKeyAsync(lifetime.Token);
                        await TrySyncHerdrMachineAsync(currentSession, result, lifetime.Token);
                    }
                    return result;
                }
                finally
                {
                    deploying = false; activeStep = "";
                }

            case "folder.cancel":
                try { browsing?.Cancel(); } catch (ObjectDisposedException) { }
                return true;

            case "folder.list":
                try { browsing?.Cancel(); } catch (ObjectDisposedException) { }
                browsing = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                using (var timeout = browsing)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    try
                    {
                        return await RequireSession().ListDirectoriesAsync(Read("path"), timeout.Token);
                    }
                    catch (Exception error) when (error is OperationCanceledException or SshOperationTimeoutException)
                    {
                        throw new InvalidOperationException("读取目录超时，请检查网络或挂载目录，稍后重试。");
                    }
                }

            case "account.draft.get":
                return preferences.ReadDraft(RequireSession());

            case "account.draft.save":
                preferences.SaveDraft(RequireSession(), new(Read("username"), Read("hostname"), Read("password")));
                return true;

            case "desktop.resolution.get":
                return preferences.ReadResolution();

            case "desktop.resolution.save":
            {
                var resolution = new DesktopResolution(args.GetProperty("width").GetInt32(), args.GetProperty("height").GetInt32());
                preferences.SaveResolution(resolution);
                return new { windows = 0, dynamicUpdated = 0, deferred = 0 };
            }

            case "account":
                return await RequireSession().RequestAsync<AccountInfo>("account_info", null, lifetime.Token);

            case "desktop":
                return await OpenRemoteDesktopAsync(RequireSession(), preferences.ReadResolution(), lifetime.Token);

            case "terminal":
                return await OpenRemoteTerminalAsync(RequireSession(), lifetime.Token);

            case "cloud.install":
                if (deploying) throw new InvalidOperationException("已有任务正在执行，请等待完成。");
                deploying = true; activeStep = "cloud";
                try { return await Task.Run(() => RequireSession().RequestAsync<ServerInfo>("cloud", null, lifetime.Token)); }
                finally { deploying = false; activeStep = ""; }

            case "cloud.open":
            {
                var currentSession = RequireSession();
                if (currentSession.Server?.CloudReady != true) throw new InvalidOperationException("请先安装 CloudCLI。");
                uint cloudPort = await EnsureTunnelAsync(currentSession, 3001, isCloud: true, lifetime.Token);
                return new { url = "http://127.0.0.1:" + cloudPort };
            }

            case "mount.settings":
                return RequireSession().ReadMountMappings();

            case "mount.install":
                if (deploying) throw new InvalidOperationException("已有任务正在执行，请等待完成。");
                deploying = true; activeStep = "mount_install";
                try { return await Task.Run(() => RequireSession().RequestAsync<ServerInfo>("mount_install", null, lifetime.Token)); }
                finally { deploying = false; activeStep = ""; }

            case "mount.apply":
            {
                var mappings = JsonSerializer.Deserialize<List<MountMapping>>(args.GetProperty("mappings"), JsonDefaults.Options) ?? [];
                if (deploying) throw new InvalidOperationException("已有任务正在执行，请等待完成。");
                deploying = true; activeStep = "mount_apply";
                try { return await Task.Run(() => RequireSession().ApplyMountsAsync(mappings, lifetime.Token)); }
                finally { deploying = false; activeStep = ""; }
            }

            case "ip.apply":
                if (deploying) throw new InvalidOperationException("已有任务正在执行，请等待完成。");
                deploying = true; activeStep = "ip_policy";
                try
                {
                    var currentSession = RequireSession();
                    var changed = await currentSession.ApplyIpPolicyAsync(
                        JsonSerializer.Deserialize<IpPolicySettings>(args, JsonDefaults.Options) ?? throw new ArgumentException("缺少 IP 设置。"),
                        lifetime.Token);
                    cachedCloudPort = null;
                    cachedRdpPort = null;
                    cachedTunnelHost = currentSession.Host + ":" + currentSession.Port;
                    try
                    {
                        var saved = vault.LoadEncrypted<ConnectionSettings>("login.enc");
                        if (saved != null) vault.SaveEncrypted("login.enc", saved with { Host = currentSession.Host, Port = currentSession.Port });
                    }
                    catch
                    {
                        QueueLog(activeStep, "服务器设置已保存；本机连接记忆未能更新，下次请填写新入口。");
                    }
                    return changed;
                }
                finally
                {
                    deploying = false; activeStep = "";
                }

            default:
                throw new InvalidOperationException("未知客户端操作：" + operation);
        }
    }

    private async Task<uint> EnsureTunnelAsync(SshSession currentSession, uint remotePort, bool isCloud, CancellationToken ct)
    {
        string endpointKey = currentSession.Host + ":" + currentSession.Port;
        if (cachedTunnelHost != endpointKey)
        {
            cachedCloudPort = null;
            cachedRdpPort = null;
            cachedTunnelHost = endpointKey;
        }
        uint? existing = isCloud ? cachedCloudPort : cachedRdpPort;
        if (existing.HasValue) return existing.Value;

        uint bound = await currentSession.ForwardAsync(remotePort, ct);
        if (isCloud) cachedCloudPort = bound;
        else cachedRdpPort = bound;
        return bound;
    }

    private async Task<object> OpenRemoteDesktopAsync(SshSession currentSession, DesktopResolution resolution, CancellationToken ct)
    {
        if (currentSession.Server?.DesktopReady != true)
            throw new InvalidOperationException("请先安装虚拟桌面。");

        var account = await currentSession.RequestAsync<AccountInfo>("account_info", null, ct);
        uint rdpPort = await EnsureTunnelAsync(currentSession, 3389, isCloud: false, ct);

        string? freeRdp = PlatformLauncher.FindExecutable("sdl-freerdp", "sdl-freerdp3");
        if (freeRdp != null)
        {
            var start = new ProcessStartInfo(freeRdp)
            {
                UseShellExecute = false,
                RedirectStandardInput = true
            };
            start.ArgumentList.Add("/args-from:stdin");
            var proc = Process.Start(start);
            if (proc != null)
            {
                string[] lines =
                [
                    $"/v:127.0.0.1:{rdpPort}",
                    $"/u:{account.Username}",
                    $"/p:{account.Password}",
                    $"/size:{resolution.Width}x{resolution.Height}",
                    "+dynamic-resolution",
                    "/cert:ignore",
                    "/t:Safe Code · Debian 13 远程桌面"
                ];
                await proc.StandardInput.WriteAsync(string.Join('\n', lines) + "\n");
                proc.StandardInput.Close();
                return new { ok = true };
            }
        }

        if (OperatingSystem.IsMacOS() &&
            (Directory.Exists("/Applications/Windows App.app") || Directory.Exists("/Applications/Microsoft Remote Desktop.app")))
        {
            string rdpPath = Path.Combine(dataDirectory, $"safe-code-{rdpPort}.rdp");
            string rdpContent = string.Join("\r\n",
            [
                $"full address:s:127.0.0.1:{rdpPort}",
                $"username:s:{account.Username}",
                $"desktopwidth:i:{resolution.Width}",
                $"desktopheight:i:{resolution.Height}",
                "prompt for credentials:i:1",
                "authentication level:i:0"
            ]) + "\r\n";
            File.WriteAllText(rdpPath, rdpContent);
            WebLocalVault.RestrictFilePermissions(rdpPath);
            PlatformLauncher.TryCopyClipboard(account.Password);
            Process.Start(new ProcessStartInfo("open", rdpPath) { UseShellExecute = false });
            return new
            {
                ok = true,
                title = "远程桌面隧道已开启",
                message = $"已调用本机远程桌面客户端连接 SSH 加密隧道 127.0.0.1:{rdpPort}。\n工作用户名：{account.Username}\n工作密码已自动复制到剪贴板，粘贴即可登录。"
            };
        }

        PlatformLauncher.TryCopyClipboard(account.Password);
        return new
        {
            ok = true,
            title = "远程桌面 SSH 隧道已就绪",
            message = $"加密隧道已监听本机地址：127.0.0.1:{rdpPort}\n用户名：{account.Username}\n（密码已复制到剪贴板，也可在“工作账户”卡片查看）\n请使用任意 RDP 客户端连接上述地址。"
        };
    }

    private static async Task<bool> TrySyncHerdrMachineAsync(SshSession currentSession, ServerInfo server, CancellationToken ct)
    {
        try
        {
            string? localHerdr = HerdrClient.FindLocalExecutable();
            if (string.IsNullOrEmpty(localHerdr) || !server.HerdrReady || string.IsNullOrWhiteSpace(server.Username))
                return false;

            currentSession.CreateWorkspaceKey();
            string sshAlias = HerdrClient.EnsureManagedSshAlias(currentSession, server.Username);
            string preferredLabel = !string.IsNullOrWhiteSpace(server.Hostname) ? server.Hostname : currentSession.Host;
            return await HerdrClient.EnsureMachineSavedAsync(localHerdr, sshAlias, preferredLabel, "safe-code", ct);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryFocusExistingLocalHerdrTabAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        const string listTabsScript = """
            if application "iTerm" is running then
                tell application "iTerm"
                    set outLines to {}
                    repeat with wIdx from 1 to (count of windows)
                        set w to window wIdx
                        set wId to id of w
                        repeat with tIdx from 1 to (count of tabs of w)
                            set t to tab tIdx of w
                            try
                                set sTty to tty of current session of t
                                set end of outLines to (wId as text) & "|" & (tIdx as text) & "|" & sTty
                            end try
                        end repeat
                    end repeat
                    set AppleScript's text item delimiters to linefeed
                    return outLines as text
                end tell
            end if
            return ""
            """;

        var listPsi = new ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        listPsi.ArgumentList.Add("-e");
        listPsi.ArgumentList.Add(listTabsScript);
        using var listProc = Process.Start(listPsi);
        if (listProc == null)
            return false;

        string tabsOutput = await listProc.StandardOutput.ReadToEndAsync(ct);
        await listProc.WaitForExitAsync(ct);
        if (listProc.ExitCode != 0 || string.IsNullOrWhiteSpace(tabsOutput))
            return false;

        foreach (string rawLine in tabsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = rawLine.Split('|');
            if (parts.Length != 3) continue;
            string winId = parts[0];
            string tabIdx = parts[1];
            string ttyName = Path.GetFileName(parts[2]);
            if (string.IsNullOrEmpty(ttyName)) continue;

            var psPsi = new ProcessStartInfo("ps")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psPsi.ArgumentList.Add("-t");
            psPsi.ArgumentList.Add(ttyName);
            psPsi.ArgumentList.Add("-o");
            psPsi.ArgumentList.Add("args=");
            using var psProc = Process.Start(psPsi);
            if (psProc == null) continue;
            string psOut = await psProc.StandardOutput.ReadToEndAsync(ct);
            await psProc.WaitForExitAsync(ct);
            if (psProc.ExitCode != 0) continue;

            string[] cmdLines = psOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cmdLines.Any(line => line.Contains("--remote", StringComparison.Ordinal)))
                continue;

            bool hasLocalHerdrUi = cmdLines.Any(line =>
            {
                string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || !string.Equals(Path.GetFileName(tokens[0]), "herdr", StringComparison.Ordinal))
                    return false;
                if (tokens.Length == 1)
                    return true;
                return tokens[1] == "--session" || (tokens[1] == "session" && tokens.Length > 2 && tokens[2] == "attach");
            });

            if (!hasLocalHerdrUi)
                continue;

            const string focusScript = """
                on run argv
                    set targetWinId to (item 1 of argv) as integer
                    set targetTabIdx to (item 2 of argv) as integer
                    tell application "iTerm"
                        activate
                        set targetWin to window id targetWinId
                        set index of targetWin to 1
                        tell targetWin
                            select tab targetTabIdx
                        end tell
                    end tell
                end run
                """;
            var focusPsi = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                RedirectStandardError = true
            };
            focusPsi.ArgumentList.Add("-e");
            focusPsi.ArgumentList.Add(focusScript);
            focusPsi.ArgumentList.Add("--");
            focusPsi.ArgumentList.Add(winId);
            focusPsi.ArgumentList.Add(tabIdx);
            using var focusProc = Process.Start(focusPsi);
            if (focusProc != null)
            {
                await focusProc.WaitForExitAsync(ct);
                if (focusProc.ExitCode == 0)
                    return true;
            }
        }

        return false;
    }

    private async Task<object> OpenRemoteTerminalAsync(SshSession currentSession, CancellationToken ct)
    {
        if (currentSession.Server?.Statuses.GetValueOrDefault("account") != true)
            throw new InvalidOperationException("请先部署工作用户。");

        var updatedServer = await currentSession.DeployAsync("ssh", null, ct: ct);
        await currentSession.VerifyWorkspaceKeyAsync(ct);
        var account = await currentSession.RequestAsync<AccountInfo>("account_info", null, ct);

        string sshAlias = HerdrClient.EnsureManagedSshAlias(currentSession, account.Username);
        string? localHerdr = HerdrClient.FindLocalExecutable();
        string preferredLabel = !string.IsNullOrWhiteSpace(updatedServer.Hostname) ? updatedServer.Hostname : currentSession.Host;
        bool mergedIntoLocalHerdr = !string.IsNullOrEmpty(localHerdr) && updatedServer.HerdrReady &&
            await HerdrClient.EnsureMachineSavedAsync(localHerdr, sshAlias, preferredLabel, "safe-code", ct);

        string sshConfig = Path.Combine(Path.GetDirectoryName(currentSession.KnownHostsPath)!, "ssh-client-config");
        string escapedKnownHosts = currentSession.KnownHostsPath.Replace("\"", "\\\"");
        File.WriteAllText(sshConfig, $"# Safe Code 单次连接不加载用户的通配主机配置。\nUserKnownHostsFile \"{escapedKnownHosts}\"\n");
        WebLocalVault.RestrictFilePermissions(sshConfig);

        string sshCommand = string.Join(" ",
        [
            "ssh", "-t",
            "-F", SshSession.Quote(sshConfig),
            "-i", SshSession.Quote(currentSession.KeyPath),
            "-p", currentSession.Port.ToString(),
            "-o", "BatchMode=yes",
            "-o", "ForwardAgent=no",
            "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=yes",
            SshSession.Quote(account.Username + "@" + currentSession.Host),
            ".local/bin/safe-code-terminal"
        ]);

        string launchCommand = mergedIntoLocalHerdr
            ? SshSession.Quote(localHerdr!)
            : (!string.IsNullOrEmpty(localHerdr) && updatedServer.HerdrReady
                ? string.Join(" ", [SshSession.Quote(localHerdr), "--remote", SshSession.Quote(sshAlias), "--session", "safe-code"])
                : sshCommand);

        if (OperatingSystem.IsMacOS())
        {
            if (mergedIntoLocalHerdr && await TryFocusExistingLocalHerdrTabAsync(ct))
                return new { server = currentSession.Server };
            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string stateDir = Path.Combine(homeDir, ".local", "state", "safe-code");
            Directory.CreateDirectory(stateDir);
            try { File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }

            string scriptPath = Path.Combine(stateDir, "open-terminal.command");
            string scriptBody = string.Join("\n",
            [
                "#!/bin/sh",
                "unset HERDR_ENV",
                "export PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\"",
                "exec " + launchCommand,
                ""
            ]);
            File.WriteAllText(scriptPath, scriptBody);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            bool hasITerm = Directory.Exists("/Applications/iTerm.app") ||
                            Directory.Exists(Path.Combine(homeDir, "Applications", "iTerm.app"));
            if (hasITerm)
            {
                string itermCmd = scriptPath.Contains(' ') ? "/bin/sh " + SshSession.Quote(scriptPath) : "/bin/sh " + scriptPath;
                string appleScript = """
                    on run argv
                        set cmd to item 1 of argv
                        tell application "iTerm"
                            activate
                            if (count of windows) > 0 then
                                tell current window
                                    create tab with default profile command cmd
                                end tell
                            else
                                create window with default profile command cmd
                            end if
                        end tell
                    end run
                    """;
                var itermStart = new ProcessStartInfo("osascript") { UseShellExecute = false, RedirectStandardError = true };
                itermStart.ArgumentList.Add("-e");
                itermStart.ArgumentList.Add(appleScript);
                itermStart.ArgumentList.Add("--");
                itermStart.ArgumentList.Add(itermCmd);
                using var itermProc = Process.Start(itermStart);
                if (itermProc != null)
                {
                    await itermProc.WaitForExitAsync(ct);
                    if (itermProc.ExitCode == 0)
                        return new { server = currentSession.Server };
                }

                var openITerm = new ProcessStartInfo("open") { UseShellExecute = false };
                openITerm.ArgumentList.Add("-a");
                openITerm.ArgumentList.Add("iTerm");
                openITerm.ArgumentList.Add(scriptPath);
                using var openITermProc = Process.Start(openITerm);
                if (openITermProc != null)
                {
                    await openITermProc.WaitForExitAsync(ct);
                    if (openITermProc.ExitCode == 0)
                        return new { server = currentSession.Server };
                }
            }

            var start = new ProcessStartInfo("open") { UseShellExecute = false };
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add("Terminal");
            start.ArgumentList.Add(scriptPath);
            using var proc = Process.Start(start);
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0)
                    return new { server = currentSession.Server };
            }
        }

        return new
        {
            server = currentSession.Server,
            title = "持久终端连接就绪",
            message = "工作密钥已配置并验证通过。可在终端执行以下命令进入持久会话：\n\n" + launchCommand
        };
    }

    private static string DescribeConnectionError(Exception error)
    {
        if (error is UnsupportedOsException) return error.Message;
        if (error is SshAuthenticationException) return "服务器连接不上，请检查 IP 地址、用户名或密码。";
        if (error is SocketException or SshConnectionException or SshOperationTimeoutException or TimeoutException or OperationCanceledException)
            return "服务器连接不上，请检查 IP 地址、用户名或密码，并确认服务器已开机、SSH 端口可以访问。";
        return error.Message;
    }

    private Dictionary<string, object?> ReadUsagePreferencesObject()
    {
        string path = Path.Combine(dataDirectory, "usage-preferences.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path), JsonDefaults.Options) ?? new();
        }
        catch { }
        return new();
    }

    private void SaveUsagePreferencesObject(Dictionary<string, object?> prefs)
    {
        string path = Path.Combine(dataDirectory, "usage-preferences.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(prefs, JsonDefaults.Options));
        File.Move(path + ".new", path, true);
    }

    private object ReadUsagePreferences()
    {
        string path = Path.Combine(dataDirectory, "usage-preferences.json");
        bool enabled = false, confirmed = false, diagnosticsEnabled = false;
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                if (r.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = e.GetBoolean();
                if (r.TryGetProperty("confirmed", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False) confirmed = c.GetBoolean();
                if (r.TryGetProperty("diagnosticsEnabled", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False) diagnosticsEnabled = d.GetBoolean();
            }
        }
        catch { }
        return new { enabled, confirmed, diagnosticsEnabled, serviceAvailable = false, recipient = (string?)null };
    }

    private void QueueLog(string step, string line)
    {
        lock (logLock)
        {
            logQueue.Enqueue((step, line));
            while (logQueue.Count > 1000) logQueue.Dequeue();
        }
    }

    private async Task FlushLogsAsync()
    {
        List<object> lines = [];
        lock (logLock)
        {
            while (logQueue.Count > 0)
            {
                var item = logQueue.Dequeue();
                lines.Add(new { step = item.Step, line = item.Line });
            }
        }
        if (lines.Count == 0 || clients.IsEmpty) return;
        await BroadcastAsync(new { type = "logs", lines });
    }

    private async Task BroadcastAsync(object payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonDefaults.Options));
        foreach (var pair in clients)
        {
            if (pair.Value.State == WebSocketState.Open)
            {
                try { await pair.Value.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token); }
                catch { }
            }
        }
    }

    private async Task SendJsonAsync(WebSocket socket, object payload)
    {
        if (socket.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonDefaults.Options));
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token); }
        catch { }
    }

    private SshSession RequireSession() =>
        !connectionPending && session?.IsConnected == true && session.Server != null
            ? session
            : throw new InvalidOperationException("SSH 连接已断开，请重新连接。");

    public void Dispose()
    {
        logTimer.Dispose();
        lifetime.Cancel();
        try { if (session?.HasMounts == true) session.UnmountAsync().GetAwaiter().GetResult(); } catch { }
        session?.Dispose();
    }
}
