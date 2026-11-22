using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Renci.SshNet.Common;
using SafeCode.Core;
using SafeCode.Desktop;

var repoRoot = FindRepoRoot();
var webRoot = Path.Combine(repoRoot, "web");
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

string url = $"http://127.0.0.1:{port}";

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var state = new SafeCodeWebHost(dataDirectory, url);
app.Lifetime.ApplicationStopping.Register(() => state.Dispose());

var contentTypes = new FileExtensionContentTypeProvider();

static Task RedirectToWebIndex(HttpContext context)
{
    string query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value! : "";
    context.Response.Redirect("/web/index.html" + query);
    return Task.CompletedTask;
}

app.MapGet("/", RedirectToWebIndex);
app.MapGet("/index.html", RedirectToWebIndex);
app.MapGet("/web", RedirectToWebIndex);
app.MapGet("/web/", RedirectToWebIndex);

app.MapGet("/web/index.html", async context =>
{
    string html = await File.ReadAllTextAsync(Path.Combine(webRoot, "index.html"));
    if (html.Contains("<script src=\"qrcode.min.js\"></script>", StringComparison.Ordinal))
        html = html.Replace("<script src=\"qrcode.min.js\"></script>", "<script src=\"/safe-code-web.js\"></script>\n<script src=\"qrcode.min.js\"></script>");
    else if (html.Contains("<script src=\"app.js\"></script>", StringComparison.Ordinal))
        html = html.Replace("<script src=\"app.js\"></script>", "<script src=\"/safe-code-web.js\"></script>\n<script src=\"app.js\"></script>");
    else
        html = html.Replace("</body>", "<script src=\"/safe-code-web.js\"></script>\n</body>");
    context.Response.Headers.CacheControl = "no-store";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
});

app.MapGet("/web/gateway.html", async context =>
{
    string html = await File.ReadAllTextAsync(Path.Combine(webRoot, "gateway.html"));
    // Allow loopback WebSocket RPC bridge while keeping gateway CSP strict on scripts/styles/forms.
    html = html.Replace("connect-src 'none'", "connect-src 'self' ws: wss:", StringComparison.Ordinal);
    if (html.Contains("<script src=\"gateway-charts.js\" defer></script>", StringComparison.Ordinal))
        html = html.Replace("<script src=\"gateway-charts.js\" defer></script>", "<script src=\"/safe-code-web.js\"></script>\n  <script src=\"gateway-charts.js\" defer></script>");
    else
        html = html.Replace("</head>", "  <script src=\"/safe-code-web.js\"></script>\n</head>");
    context.Response.Headers.CacheControl = "no-store";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
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
      const gatewayCtx = new URLSearchParams(location.search).get('ctx') || '';
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
            } else if (msg.type === 'mountStatus' && msg.mount) {
              window.safeCodeMountStatus?.(msg.mount);
            } else if (msg.type === 'claudeDesktopStatus' && msg.status) {
              window.safeCodeClaudeDesktopStatus?.(msg.status);
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

      const postMessage = payload => {
        let outgoing = payload;
        if (gatewayCtx && outgoing && outgoing.op === 'safeCodeApi.manage' && outgoing.args && !outgoing.args.context) {
          outgoing = { ...outgoing, args: { ...outgoing.args, context: gatewayCtx } };
        }
        const serialized = JSON.stringify(outgoing);
        if (socket && socket.readyState === WebSocket.OPEN) {
          socket.send(serialized);
        } else {
          outbox.push(serialized);
          if (!socket || socket.readyState > WebSocket.OPEN) connectSocket();
        }
      };

      window.__safeCodeWebPostMessage = postMessage;
      if (!window.chrome?.webview) {
        window.chrome = Object.assign(window.chrome || {}, { webview: { postMessage } });
      }

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

string[] allowedWebFiles =
[
    "styles.css",
    "app.js",
    "native.js",
    "qrcode.min.js",
    "gateway.css",
    "gateway.js",
    "gateway-charts.js",
    "gateway-status.js",
    "client-preview.html"
];

foreach (string file in allowedWebFiles)
{
    string captured = file;
    app.MapGet("/web/" + captured, async context =>
    {
        string fullPath = Path.Combine(webRoot, captured);
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

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"SAFE_CODE_WEB_READY {url}");
    Console.Out.Flush();
    if (openAppWindow) PlatformLauncher.TryOpenAppWindow(url + "/web/index.html?shell=app");
    else if (openBrowser) PlatformLauncher.TryOpenBrowser(url + "/web/index.html");
});

await app.RunAsync();

static string FindRepoRoot()
{
    foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var current = new DirectoryInfo(start);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "web", "index.html")) &&
                File.Exists(Path.Combine(current.FullName, "web", "native.js")))
                return current.FullName;
            current = current.Parent;
        }
    }
    throw new FileNotFoundException("未找到 Safe Code 前端资源目录（web/index.html / web/native.js）。");
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
internal sealed record DeploymentChoices(bool AccountSaved, string? LocaleRegion);
internal sealed record DesktopResolution(int Width = 1280, int Height = 800);

internal sealed class WebLocalVault
{
    private readonly string directory;
    private readonly string masterKeyPath;
    private readonly object keyLock = new();

    public WebLocalVault(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        masterKeyPath = Path.Combine(directory, "vault.key");
    }

    private byte[] GetOrCreateKey()
    {
        lock (keyLock)
        {
            if (File.Exists(masterKeyPath))
            {
                byte[] existing = File.ReadAllBytes(masterKeyPath);
                if (existing.Length == 32) return existing;
            }
            byte[] created = RandomNumberGenerator.GetBytes(32);
            string temp = masterKeyPath + "." + Guid.NewGuid().ToString("N") + ".new";
            File.WriteAllBytes(temp, created);
            RestrictFilePermissions(temp);
            File.Move(temp, masterKeyPath, true);
            return created;
        }
    }

    public byte[] ProtectBytes(byte[] plain)
    {
        byte[] key = GetOrCreateKey();
        try
        {
            byte[] result = new byte[28 + plain.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] UnprotectBytes(byte[] encrypted)
    {
        if (encrypted.Length < 28)
            throw new CryptographicException("保存的数据不完整。");
        byte[] key = GetOrCreateKey();
        try
        {
            byte[] plain = new byte[encrypted.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plain);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public bool Exists(string relativePath) => File.Exists(Path.Combine(directory, relativePath));

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
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                File.WriteAllBytes(temp, combined);
                RestrictFilePermissions(temp);
                File.Move(temp, target, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
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
    private static readonly DesktopResolution[] Resolutions =
    [
        new(1024, 768),
        new(1280, 800),
        new(1366, 768),
        new(1440, 900),
        new(1512, 982),
        new(1600, 900),
        new(1920, 1080),
        new(2560, 1440),
        new(2560, 1600)
    ];

    private readonly string directory;
    private readonly WebLocalVault vault;

    public WebPreferences(string directory, WebLocalVault vault)
    {
        this.directory = directory;
        this.vault = vault;
    }

    private string ClaudeDesktopWarningPath => Path.Combine(directory, "claude-desktop-warning-accepted-v1");
    public bool ClaudeDesktopWarningAccepted => File.Exists(ClaudeDesktopWarningPath);

    public void AcceptClaudeDesktopWarning()
    {
        Directory.CreateDirectory(directory);
        using var _ = new FileStream(ClaudeDesktopWarningPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
    }

    private static string DraftRelativePath(SshSession session) =>
        Path.Combine("account-drafts", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.Host + ":" + session.Port + ":" + session.Fingerprint))).ToLowerInvariant() + ".enc");

    private string DraftFullPath(SshSession session) => Path.Combine(directory, DraftRelativePath(session));
    private string DeploymentLocalePath(SshSession session) => DraftFullPath(session) + ".locale";

    public DeploymentChoices ReadDeploymentChoices(SshSession session)
    {
        string draftPath = DraftFullPath(session);
        string localePath = DeploymentLocalePath(session);
        bool accountSaved = File.Exists(draftPath) ||
            (session.Server?.Statuses.GetValueOrDefault("account") == true && File.Exists(draftPath + ".configured"));
        string? region = File.Exists(localePath) ? File.ReadAllText(localePath).Trim() : null;
        return new(accountSaved, region != null && DeploymentCatalog.FindLocale(region) != null ? region : null);
    }

    public DeploymentChoices SaveDeploymentLocale(SshSession session, string region)
    {
        if (DeploymentCatalog.FindLocale(region) == null)
            throw new ArgumentException("请选择有效的时区和系统语言。");
        string path = DeploymentLocalePath(session);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(temp, region);
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
        return ReadDeploymentChoices(session);
    }

    public LocaleSettings? ReadSavedLocale(SshSession session)
    {
        string? region = ReadDeploymentChoices(session).LocaleRegion;
        return region != null ? DeploymentCatalog.FindLocale(region) : null;
    }

    public AccountDraft? ReadDraft(SshSession session) => vault.LoadEncrypted<AccountDraft>(DraftRelativePath(session));

    public bool RequiresAccountConfirmation(SshSession session) =>
        session.Server?.Statuses.GetValueOrDefault("account") == true ||
        ReadDraft(session) != null ||
        File.Exists(DraftFullPath(session) + ".configured");

    public void SaveDraft(SshSession session, AccountDraft draft)
    {
        if (!Regex.IsMatch(draft.Username, "^[a-z][a-z0-9_-]{1,30}$"))
            throw new ArgumentException("用户名须为 2–31 位小写字母、数字、下划线或短横线。");
        if (!Regex.IsMatch(draft.Hostname, "^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$"))
            throw new ArgumentException("机器名称须为 1–63 位字母、数字或短横线。");
        if (draft.Password.Length > 0 && (draft.Password.Length < 12 || draft.Password.Length > 256 || draft.Password.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new ArgumentException("工作密码须为 12–256 位，不能包含换行或空字符。");
        string relative = DraftRelativePath(session);
        vault.SaveEncrypted(relative, draft);
        File.WriteAllText(Path.Combine(directory, relative) + ".configured", "1");
    }

    public void ClearDraft(SshSession session) => vault.Delete(DraftRelativePath(session));

    public static bool IsSupportedResolution(int width, int height) =>
        RdpDisplayConstants.IsSupportedResolution(width, height) ||
        Array.Exists(Resolutions, r => r.Width == width && r.Height == height);

    private static DesktopResolution DefaultResolution() =>
        new(RdpDisplayConstants.DefaultResolution.Width, RdpDisplayConstants.DefaultResolution.Height);

    public DesktopResolution ReadResolution()
    {
        try
        {
            string path = Path.Combine(directory, "desktop-resolution.json");
            if (!File.Exists(path)) return DefaultResolution();
            var value = JsonSerializer.Deserialize<DesktopResolution>(File.ReadAllText(path), JsonDefaults.Options);
            return value != null && IsSupportedResolution(value.Width, value.Height) ? value : DefaultResolution();
        }
        catch { return DefaultResolution(); }
    }

    public void SaveResolution(DesktopResolution resolution)
    {
        if (!IsSupportedResolution(resolution.Width, resolution.Height))
            throw new ArgumentException("请选择支持的分辨率。");
        string path = Path.Combine(directory, "desktop-resolution.json");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(resolution, JsonDefaults.Options));
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}

internal static class PlatformLauncher
{
    public static async Task<string> RunProcessAsync(string file, IEnumerable<string> args, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统工具。");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { try { process.Kill(true); } catch { } throw; }
        if (process.ExitCode != 0) throw new IOException("系统工具执行失败：" + await error);
        return await output;
    }

    public static async Task<string> OpenLocalClientAsync(string name, CancellationToken ct)
    {
        if (name is not ("Codex" or "Claude"))
            throw new ArgumentException("未知客户端。");
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException($"请在当前系统手动打开 {name} 客户端。");
        try
        {
            await RunProcessAsync("/usr/bin/open", ["-a", name], ct);
            return "已请求打开 " + name + " 客户端。";
        }
        catch (IOException)
        {
            throw new InvalidOperationException("未能打开 " + name + "，请先安装官方客户端，或从应用程序目录启动。");
        }
    }

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

    public static void TryOpenAppWindow(string url, int width = 460, int height = 640)
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
                        start.ArgumentList.Add($"--window-size={width},{height}");
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

// Mirrors MacHostControl using WebLocalVault encryption and macOS Remote Login (never enables sshd).
internal sealed class WebHostControl : IDisposable
{
    private sealed record ResumePreference(GatewayPreference Server, string WorkspaceUser, string Alias, string Client);

    private readonly string directory;
    private readonly WebLocalVault vault;
    private readonly string sshDir;
    private readonly string authPath;
    private readonly string ownedPath;
    private readonly string preferencePath;
    private volatile SshSession? session;
    private string client = "";

    public bool Enabled => session?.HasHostControl == true;
    public HostControlStatus Status => session?.HostControlStatus ?? new(
        false,
        "disabled",
        "",
        "",
        OperatingSystem.IsMacOS() ? "未启用；需先开启 macOS 系统远程登录" : "当前系统 Web 宿主暂不支持本机远程控制");

    public WebHostControl(string directory, WebLocalVault vault)
    {
        this.directory = directory;
        this.vault = vault;
        sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        authPath = Path.Combine(sshDir, "authorized_keys");
        ownedPath = Path.Combine(directory, "host-authorized-line.json");
        preferencePath = Path.Combine(directory, "host-control-resume.web.enc");
        if (OperatingSystem.IsMacOS())
        {
            try { RevokeLocal(); } catch { }
        }
    }

    public async Task<HostControlStatus> EnableAsync(SshSession target, string alias, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException("当前本地 Web 宿主仅在 macOS 上支持本机远程控制。");
        if (Enabled)
            throw new InvalidOperationException("本机控制已启用。");
        string safeAlias = string.IsNullOrWhiteSpace(alias) ? "mac" : alias.Trim();
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory + target.Host + target.Port + target.Fingerprint)))[..20].ToLowerInvariant();
        if (File.Exists(preferencePath)) File.Delete(preferencePath);
        var status = await EnableCoreAsync(target, safeAlias, id, ct);
        if (target.Server?.Username is { Length: > 0 } workspaceUser)
        {
            try
            {
                var preference = new ResumePreference(GatewayPreference.For(target), workspaceUser, safeAlias, id);
                DesktopKeyAccess.WriteProfileFile(preferencePath, vault.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(preference)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return status with { Message = "本机控制已连接。未能保存重启后的恢复设置。" };
            }
        }
        return status;
    }

    private ResumePreference? ReadPreference()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(preferencePath)) return null;
        try
        {
            byte[] data = vault.UnprotectBytes(File.ReadAllBytes(preferencePath));
            try
            {
                var value = JsonSerializer.Deserialize<ResumePreference>(data);
                return value?.Server is { Fingerprint.Length: > 0 } ? value : null;
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public bool CanResume(ConnectionSettings settings) => ReadPreference()?.Server.Matches(settings) == true;

    public Task MaintainForChatAsync(CancellationToken ct) => session?.MaintainHostControlForChatAsync(ct) ?? Task.CompletedTask;

    public async Task<string?> ResumeAsync(SshSession target, CancellationToken ct)
    {
        var preference = ReadPreference();
        if (Enabled || preference == null) return null;
        if (!preference.Server.Matches(target)) return "服务器身份与保存的授权不一致，请重新启用本机远程控制。";
        if (target.Server?.Username != preference.WorkspaceUser) return "服务器工作用户已变化，请重新启用本机远程控制。";
        try
        {
            await EnableCoreAsync(target, preference.Alias, preference.Client, ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return "本机远程控制恢复失败：" + error.Message; }
    }

    private async Task<HostControlStatus> EnableCoreAsync(SshSession target, string alias, string id, CancellationToken ct)
    {
        if (Enabled) throw new InvalidOperationException("本机控制已启用。");
        using (var probe = new TcpClient())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try { await probe.ConnectAsync("127.0.0.1", 22, timeout.Token); }
            catch
            {
                throw new InvalidOperationException("请先在 macOS 系统设置 → 通用 → 共享中开启“远程登录”，并仅允许当前账户。Safe Code 不会自行开启系统 SSH 服务。");
            }
        }
        string hostKey = File.ReadAllText("/etc/ssh/ssh_host_ed25519_key.pub").Trim();
        string uid = (await PlatformLauncher.RunProcessAsync("/usr/bin/id", ["-u"], ct)).Trim();
        if (!uint.TryParse(uid, out uint userId) || userId == 0)
            throw new InvalidOperationException("不支持以 root 账户开放本机控制。");
        client = id;
        session = target;
        try
        {
            var identity = await target.PrepareHostControlAsync(client, ct);
            Authorize(identity.PublicKey);
            return await target.StartHostControlAsync(client, 22, Environment.UserName, "macos:" + uid, hostKey, alias, ct);
        }
        catch
        {
            RevokeLocal();
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await target.StopHostControlAsync(cleanup.Token);
                await target.RequestAsync<JsonElement>("host_bridge", new { clientId = client, action = "remove" }, cleanup.Token);
            }
            catch { }
            session = null;
            throw;
        }
    }

    private void ValidatePaths()
    {
        if (new DirectoryInfo(sshDir).LinkTarget != null || new FileInfo(authPath).LinkTarget != null)
            throw new InvalidOperationException("SSH 授权路径包含链接，请使用普通目录和文件后重试。");
        if (File.Exists(sshDir) || Directory.Exists(authPath))
            throw new InvalidOperationException("SSH 授权路径类型错误。");
    }

    private void Authorize(string publicKey)
    {
        ValidatePaths();
        Directory.CreateDirectory(sshDir);
        if (!Directory.Exists(sshDir)) throw new IOException("无法创建 SSH 目录。");
        string[] parts = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "ssh-ed25519" || parts[1].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
            throw new InvalidOperationException("服务器提供的专用公钥无效。");
        var lines = File.Exists(authPath) ? File.ReadAllLines(authPath).ToList() : new List<string>();
        if (lines.Any(line => line.Split(' ').Contains(parts[1])))
            throw new InvalidOperationException("此公钥已有其他授权，未重复或放宽权限。");
        string entry = "restrict,pty " + parts[0] + " " + parts[1] + " safe-code-mac-" + client;
        File.WriteAllText(ownedPath, JsonSerializer.Serialize(entry));
        WebLocalVault.RestrictFilePermissions(ownedPath);
        lines.Add(entry);
        WriteAuth(lines);
    }

    private void WriteAuth(IEnumerable<string> lines)
    {
        string temp = Path.Combine(sshDir, ".safe-code-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                WebLocalVault.RestrictFilePermissions(temp);
                using var writer = new StreamWriter(stream);
                foreach (var line in lines) writer.WriteLine(line);
            }
            File.Move(temp, authPath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void RevokeLocal()
    {
        if (!File.Exists(ownedPath)) return;
        ValidatePaths();
        string? entry = JsonSerializer.Deserialize<string>(File.ReadAllText(ownedPath));
        if (entry == null || !entry.StartsWith("restrict,pty ssh-ed25519 ", StringComparison.Ordinal) || !entry.Contains(" safe-code-mac-", StringComparison.Ordinal))
            throw new IOException("本机控制撤权记录无效，请检查授权文件。");
        if (File.Exists(authPath)) WriteAuth(File.ReadAllLines(authPath).Where(line => line != entry));
        File.Delete(ownedPath);
    }

    public async Task<HostControlStatus> DisableAsync(CancellationToken ct)
    {
        if (File.Exists(preferencePath)) File.Delete(preferencePath);
        return await SuspendAsync(ct);
    }

    public async Task<HostControlStatus> SuspendAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS()) RevokeLocal();
        if (session != null) await session.StopHostControlAsync(ct);
        var result = Status;
        session = null;
        return result;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS())
        {
            try { RevokeLocal(); } catch { }
        }
    }
}

// Mirrors MacClaudeDesktopBridge using WebLocalVault encryption and DesktopVpsGateway.
internal sealed class WebClaudeDesktopBridge : IDisposable
{
    private sealed record ProfileState(GatewayPreference Server, string Id, string Token, int Port);

    private readonly WebLocalVault vault;
    private readonly SemaphoreSlim gate = new(1);
    private readonly string statePath;
    private readonly string library;
    private volatile DesktopVpsGateway? gateway;
    private DateTimeOffset? started;
    private string? message;
    private DateTimeOffset nextHealthCheck;

    public WebClaudeDesktopBridge(string dataDirectory, WebLocalVault vault, string? profileDirectory = null)
    {
        this.vault = vault;
        statePath = Path.Combine(dataDirectory, "desktop-ssh.web.enc");
        library = profileDirectory ?? DesktopKeyAccess.ProfileLibrary();
    }

    public bool Configured => File.Exists(statePath);
    public bool Running => gateway != null;

    private static bool Installed => OperatingSystem.IsMacOS() &&
        (File.Exists("/Applications/Claude.app/Contents/Info.plist") ||
         File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Claude.app/Contents/Info.plist")));

    private ProfileState ReadState()
    {
        byte[] data = vault.UnprotectBytes(File.ReadAllBytes(statePath));
        try
        {
            var state = JsonSerializer.Deserialize<ProfileState>(data);
            if (state == null || !Guid.TryParseExact(state.Id, "D", out _) || state.Port is < 1024 or > 65535)
                throw new IOException("保存的 SSH 接入配置无效。");
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public bool CanResume(ConnectionSettings settings)
    {
        if (!Configured) return false;
        try { return ReadState().Server.Matches(settings); }
        catch { return false; }
    }

    public async Task<object> RefreshAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (gateway != null)
            {
                bool applied;
                try { applied = IsApplied(ReadState()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
                {
                    applied = false;
                }
                if (!applied)
                {
                    await SuspendCoreAsync(ct);
                    message = "Claude Desktop 配置已切换或无法读取，SSH 网关已关闭。";
                }
                else if (DateTimeOffset.UtcNow >= nextHealthCheck)
                {
                    message = await gateway.RefreshAsync(ct) ? null : "SSH 网关暂不可用，请检查 VPS 连接。";
                    nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(4);
                }
            }
            return Status();
        }
        finally { gate.Release(); }
    }

    private object Status()
    {
        bool active = gateway?.Connected == true;
        bool managed = Configured;
        return new
        {
            active,
            managed,
            configured = managed,
            installed = Installed,
            oldProfile = !managed ? FindOldProfile() : null,
            state = active ? "active" : managed ? "attention" : "unconfigured",
            message = message ?? (active ? "SSH 网关已连接，重启 Claude Desktop 后可使用。" : managed
                ? "已保存 SSH 接入。请在原服务器上验证本机远程控制后恢复。" : "尚未启用 SSH 接入。"),
            endpoint = gateway?.Endpoint ?? "",
            startedAt = started,
            usage = gateway?.Usage ?? IntegrationUsage.Unavailable("尚未连接 VPS 网关"),
            usageSource = "VPS CloudCLI"
        };
    }

    public async Task<object> StartAsync(SshSession target, CancellationToken ct, string replaceOldProfile = "")
    {
        await gate.WaitAsync(ct);
        try
        {
            target.RequireVerifiedHostControl();
            if (gateway?.Connected == true) return Status();
            if (gateway != null) await SuspendCoreAsync(ct);
            if (Configured)
            {
                var state = ReadState();
                if (!state.Server.Matches(target))
                    throw new InvalidOperationException("保存的 SSH 接入绑定到原服务器。请连接原服务器，或先停用后重新设置。");
                if (!IsApplied(state))
                    throw new IOException("Claude Desktop 已切换配置。请先停用旧接入后重新设置。");
                var saved = new DesktopVpsGateway(target, Guid.Parse(state.Id).ToString("N"), state.Token, (uint)state.Port);
                try { await saved.StartAsync(ct); }
                catch { saved.Dispose(); throw; }
                gateway = saved;
            }
            else
            {
                if (!Installed) throw new InvalidOperationException("请先安装官方 Claude Desktop。");
                EnsureProfileSlotAvailable(replaceOldProfile);
                var id = Guid.NewGuid();
                var pending = new DesktopVpsGateway(target, id.ToString("N"));
                try
                {
                    await pending.StartAsync(ct);
                    var models = await pending.ModelsAsync(ct);
                    target.RequireVerifiedHostControl();
                    int port = new Uri(pending.Endpoint).Port;
                    var state = new ProfileState(GatewayPreference.For(target), id.ToString("D"), pending.LocalToken, port);
                    var profile = MakeProfile(pending.Endpoint, state.Token, models);
                    Activate(state, profile, replaceOldProfile);
                    gateway = pending;
                }
                catch
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await pending.StopAsync(timeout.Token);
                    pending.Dispose();
                    throw;
                }
            }
            started = DateTimeOffset.UtcNow;
            nextHealthCheck = started.Value.AddSeconds(4);
            message = null;
            return Status();
        }
        finally { gate.Release(); }
    }

    public async Task<string?> ResumeAsync(SshSession target, CancellationToken ct)
    {
        if (!Configured) return null;
        try
        {
            if (ReadState().Server.Matches(target)) await StartAsync(target, ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return "Claude SSH 网关恢复失败：" + error.Message; }
    }

    public async Task<object> StopAsync(CancellationToken ct, bool suspend = false)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = !suspend && Configured ? ReadState() : null;
            await SuspendCoreAsync(ct);
            if (state != null) RemoveProfile(state);
            return Status();
        }
        finally { gate.Release(); }
    }

    private async Task SuspendCoreAsync(CancellationToken ct)
    {
        if (gateway == null) return;
        var current = gateway;
        gateway = null;
        try { message = await current.StopAsync(ct); }
        finally { current.Dispose(); started = null; }
    }

    private string MetaPath => Path.Combine(library, "_meta.json");
    private string ProfilePath(ProfileState state) => Path.Combine(library, state.Id + ".json");

    private string? FindOldProfile()
    {
        try { return FindOldProfile(DesktopKeyAccess.ReadProfileMeta(library)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string? FindOldProfile(JsonObject meta)
    {
        string? id = Text(meta, "appliedId");
        if (meta["hybridPointer"] != null || !Guid.TryParseExact(id, "D", out _)
            || !meta["entries"]!.AsArray().OfType<JsonObject>().Any(entry =>
                Text(entry, "id") == id && Text(entry, "name") == "SafeCode SSH"))
            return null;
        string path = Path.Combine(library, id + ".json");
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject profile
            || !Uri.TryCreate(Text(profile, "inferenceGatewayBaseUrl"), UriKind.Absolute, out var endpoint)
            || endpoint.Port is < 1024 or > 65535 || !IsSealedGatewayProfile(profile, endpoint.Port))
            return null;
        return id;
    }

    private JsonObject EnsureProfileSlotAvailable(string replaceOldProfile)
    {
        var meta = DesktopKeyAccess.ReadProfileMeta(library);
        if (!string.IsNullOrEmpty(replaceOldProfile))
        {
            if (FindOldProfile(meta) != replaceOldProfile)
                throw new InvalidOperationException("Claude Desktop 配置已变更，请重新打开 Safe Code 接入设置。");
        }
        else if (!string.IsNullOrEmpty(Text(meta, "appliedId")) || meta["hybridPointer"] != null)
            throw new InvalidOperationException("Claude Desktop 已启用其他配置，请先在 Claude Desktop 中停用该配置。");
        return meta;
    }

    private static string? Text(JsonObject? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsSealedGatewayProfile(JsonObject? profile, int port) =>
        Text(profile, "inferenceGatewayBaseUrl") == "http://127.0.0.1:" + port &&
        Text(profile, "inferenceProvider") == "gateway" &&
        Text(profile, "inferenceGatewayAuthScheme") == "bearer" &&
        Text(profile, "inferenceCredentialKind") == "static" &&
        Text(profile, "deploymentDisplayName") == "SafeCode" &&
        Text(profile, "inferenceGatewayApiKey") is { Length: > 14 } credential &&
        credential.StartsWith("__sealed__:v1:", StringComparison.Ordinal);

    private static bool OwnsProfile(ProfileState state, JsonObject? profile) =>
        (Text(profile, "inferenceGatewayBaseUrl") == "http://127.0.0.1:" + state.Port &&
         Text(profile, "inferenceGatewayApiKey") == state.Token)
        || IsSealedGatewayProfile(profile, state.Port);

    private bool IsApplied(ProfileState state)
    {
        string path = ProfilePath(state);
        return (string?)DesktopKeyAccess.ReadProfileMeta(library)["appliedId"] == state.Id
            && File.Exists(path) && OwnsProfile(state, JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject);
    }

    private void Activate(ProfileState state, JsonObject profile, string replaceOldProfile)
    {
        var meta = EnsureProfileSlotAvailable(replaceOldProfile);
        ((JsonArray)meta["entries"]!).Add(new JsonObject { ["id"] = state.Id, ["name"] = "SafeCode SSH" });
        meta["appliedId"] = state.Id;
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(state);
        try { DesktopKeyAccess.WriteProfileFile(statePath, vault.ProtectBytes(data)); }
        finally { CryptographicOperations.ZeroMemory(data); }
        try
        {
            DesktopKeyAccess.WriteProfileFile(ProfilePath(state), JsonSerializer.SerializeToUtf8Bytes(profile));
            DesktopKeyAccess.WriteProfileFile(MetaPath, JsonSerializer.SerializeToUtf8Bytes(meta));
        }
        catch
        {
            RemoveProfile(state);
            throw;
        }
    }

    private void RemoveProfile(ProfileState state)
    {
        var meta = DesktopKeyAccess.ReadProfileMeta(library);
        var entries = (JsonArray)meta["entries"]!;
        string path = ProfilePath(state);
        if (!File.Exists(path) || OwnsProfile(state, JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject))
        {
            var entry = entries.FirstOrDefault(node => (string?)node?["id"] == state.Id);
            if (entry != null) entries.Remove(entry);
            bool applied = (string?)meta["appliedId"] == state.Id;
            if (applied) meta["appliedId"] = "";
            if (entry != null || applied) DesktopKeyAccess.WriteProfileFile(MetaPath, JsonSerializer.SerializeToUtf8Bytes(meta));
            if (File.Exists(path)) File.Delete(path);
        }
        if (File.Exists(statePath)) File.Delete(statePath);
    }

    private static JsonObject MakeProfile(string endpoint, string token, DesktopGatewayModel[] models)
    {
        var entries = new JsonArray();
        foreach (var model in models.Where(model => !model.AutoModeClassifier).OrderByDescending(model => model.Family == "opus"))
        {
            var entry = new JsonObject
            {
                ["name"] = model.Id,
                ["labelOverride"] = model.DisplayName,
                ["maxEffort"] = model.SupportedEffortLevels.LastOrDefault() ?? "low",
                ["supports1m"] = model.Supports1m
            };
            if (!string.IsNullOrEmpty(model.Family))
            {
                entry["anthropicFamilyTier"] = model.Family;
                entry["isFamilyDefault"] = true;
            }
            entries.Add(entry);
        }
        if (entries.Count == 0) throw new IOException("VPS 上的 CloudCLI 未返回可用聊天模型。");
        var profile = new JsonObject
        {
            ["inferenceProvider"] = "gateway",
            ["inferenceGatewayBaseUrl"] = endpoint,
            ["inferenceGatewayAuthScheme"] = "bearer",
            ["inferenceCredentialKind"] = "static",
            ["inferenceGatewayApiKey"] = token,
            ["inferenceModels"] = entries,
            ["deploymentMode"] = "3p",
            ["disableDeploymentModeChooser"] = true,
            ["deploymentDisplayName"] = "SafeCode",
            ["modelDiscoveryEnabled"] = false,
            ["autoModeEnabled"] = true,
            ["chatTabEnabled"] = true
        };
        if (models.FirstOrDefault(model => model.AutoModeClassifier) is { } classifier)
            DesktopKeyAccess.SetClassifier(profile, classifier.Id);
        return profile;
    }

    public void Dispose() => gateway?.Dispose();
}

internal sealed class SafeCodeWebHost : IDisposable
{
    private sealed record GatewayContext(SshSession Session, string Token);
    private sealed class WebSocketClient(WebSocket socket) : IDisposable
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public void Dispose() => SendLock.Dispose();
    }

    private readonly string dataDirectory;
    private readonly string baseUrl;
    private readonly WebLocalVault vault;
    private readonly ClientServices services;
    private readonly WebPreferences preferences;
    private readonly RdpDisplayPreferences desktopDisplay;
    private readonly WebHostControl hostControl;
    private readonly WebClaudeDesktopBridge claudeDesktop;
    private readonly ConcurrentDictionary<Guid, WebSocketClient> clients = new();
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly object consentGate = new();
    private readonly object logLock = new();
    private readonly Queue<(string Step, string Line)> logQueue = new();
    private readonly Timer logTimer;
    private readonly CancellationTokenSource lifetime = new();

    private SshSession? session;
    private DesktopKeyAccess? keyAccess;
    private DesktopKeyAccess.DeviceSetup? pendingKeyDevice;
    private GatewayContext? gatewayContext;
    private CancellationTokenSource? connecting;
    private CancellationTokenSource? browsing;
    private int connectionPending;
    private int activeMutations;
    private string activeStep = "";
    private (string Token, HostKeyInfo Key)? pendingHostTrust;
    private uint? cachedCloudPort;
    private uint? cachedRdpPort;
    private string cachedTunnelHost = "";
    private string lastMountStatusJson = "";

    public SafeCodeWebHost(string dataDirectory, string baseUrl)
    {
        this.dataDirectory = dataDirectory;
        this.baseUrl = baseUrl.TrimEnd('/');
        vault = new WebLocalVault(dataDirectory);
        services = new ClientServices(dataDirectory, endpointEnabled: false, currentVersion: "Beta-0.4.4.32-debian13");
        preferences = new WebPreferences(dataDirectory, vault);
        desktopDisplay = new RdpDisplayPreferences(dataDirectory);
        hostControl = new WebHostControl(dataDirectory, vault);
        claudeDesktop = new WebClaudeDesktopBridge(dataDirectory, vault);
        logTimer = new Timer(_ => _ = FlushLogsAsync(), null, 120, 120);
        _ = Task.Run(() => MaintainBackgroundAsync(lifetime.Token));
    }

    public async Task RunWebSocketSessionAsync(WebSocket socket, CancellationToken stoppingToken)
    {
        Guid clientId = Guid.NewGuid();
        var client = new WebSocketClient(socket);
        clients[clientId] = client;
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
                _ = HandleRpcEnvelopeAsync(client, messageText);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (clients.TryRemove(clientId, out var removed))
                removed.Dispose();
        }
    }

    private async Task HandleRpcEnvelopeAsync(WebSocketClient client, string rawJson)
    {
        string id = "";
        bool acquiredMutation = false;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            string op = root.TryGetProperty("op", out var opProp) ? opProp.GetString() ?? "" : "";
            var args = root.TryGetProperty("args", out var argsProp) ? argsProp.Clone() : default;

            bool mutates = op is "deploy" or "job.resume" or "cloud.install" or "cloud.expose"
                or "mount.install" or "mount.apply" or "ip.apply" or "safeCodeApi.install"
                or "host.enable" or "host.disable" or "claudeDesktop.start" or "claudeDesktop.stop"
                or "desktopKey.enable" or "desktopKey.disable" or "desktopKey.authorizeDevice"
                or "connection.reauthenticate" or "app.shutdown";

            if (mutates)
            {
                if (!await mutationGate.WaitAsync(0))
                    throw new InvalidOperationException("已有任务正在执行，请等待完成。");
                acquiredMutation = true;
                Interlocked.Increment(ref activeMutations);
            }

            object? answer = await HandleAsync(op, args);
            await FlushLogsAsync();
            await SendJsonAsync(client, new { type = "response", id, result = answer, error = (string?)null });
        }
        catch (Exception error)
        {
            await FlushLogsAsync();
            await SendJsonAsync(client, new { type = "response", id, result = (object?)null, error = ConnectionErrors.DescribeOperation(error) });
        }
        finally
        {
            if (acquiredMutation)
            {
                activeStep = "";
                Interlocked.Decrement(ref activeMutations);
                mutationGate.Release();
            }
        }
    }

    private async Task<object?> HandleAsync(string operation, JsonElement args)
    {
        string Read(string key, string fallback = "") =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
        bool Flag(string key) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

        var ct = lifetime.Token;

        switch (operation)
        {
            case "app.state":
                return new
                {
                    busy = Volatile.Read(ref activeMutations) > 0 || Volatile.Read(ref connectionPending) != 0,
                    guarding = session?.HasMounts == true || hostControl.Enabled || claudeDesktop.Running
                };

            case "app.shutdown":
                if (Volatile.Read(ref connectionPending) != 0)
                    throw new InvalidOperationException("正在连接，请先取消连接后退出。");
                if (hostControl.Enabled) await hostControl.SuspendAsync(ct);
                if (session?.HasMounts == true) await session.UnmountAsync(ct);
                if (claudeDesktop.Running) await claudeDesktop.StopAsync(ct, suspend: true);
                if (keyAccess != null) await keyAccess.StopAsync();
                return true;

            case "window.resize":
            case "window.drag":
            case "window.minimize":
            case "window.maximize":
            case "window.close":
            case "window.fitDialog":
            case "window.dialogs":
            case "services.open":
                return true;

            case "window.gateway.open":
            {
                var gatewaySession = RequireGatewaySession();
                await gatewaySession.RequirePublicGatewayAsync(ct);
                if (!ReferenceEquals(gatewaySession, RequireGatewaySession()))
                    throw new InvalidOperationException("SSH 会话已变更，请重新打开网关管理。");
                var context = new GatewayContext(gatewaySession, Guid.NewGuid().ToString("N"));
                gatewayContext = context;
                string gatewayUrl = $"{baseUrl}/web/gateway.html?ctx={Uri.EscapeDataString(context.Token)}";
                PlatformLauncher.TryOpenAppWindow(gatewayUrl, 1100, 690);
                return new { ok = true, url = gatewayUrl, context = context.Token };
            }

            case "copy":
                PlatformLauncher.TryCopyClipboard(Read("text"));
                return true;

            case "services.preferences":
                lock (consentGate) return services.Preferences();

            case "services.consent":
            case "services.confirm":
            {
                Task<object> consent;
                lock (consentGate)
                {
                    consent = operation == "services.confirm"
                        ? services.ConfirmConsentAsync()
                        : services.SetConsentAsync(Flag("enabled"), Flag("confirmed"));
                }
                return await consent;
            }

            case "diagnostics.consent":
                lock (consentGate) return services.SetDiagnosticConsent(Flag("enabled"));

            case "diagnostics.native-failure":
                return null;

            case "feedback.send":
                throw new InvalidOperationException("当前本地 Web 模式未启用在线反馈上报，请通过 GitHub Issues 或 QQ 群反馈。");

            case "updates.check":
                return new UpdateInfo(
                    "Beta-0.4.4.32-debian13",
                    null,
                    "当前运行 Debian 13 定制版",
                    "https://github.com/Muchuaner/safe-code/releases",
                    CanInstall: false);

            case "updates.download":
            case "updates.install":
                throw new InvalidOperationException("当前运行本地 Debian 13 定制版，请通过源码或发布页手动更新。");

            case "notice.state":
            {
                bool accepted = File.Exists(Path.Combine(dataDirectory, "notice-accepted-v2")) ||
                    File.Exists(Path.Combine(dataDirectory, "notice-accepted-v1")) ||
                    services.NoticeWasConfirmed;
                return new { accepted };
            }

            case "notice.accept":
                File.WriteAllText(Path.Combine(dataDirectory, "notice-accepted-v2"), DateTimeOffset.UtcNow.ToString("O"));
                return true;

            case "notice.claudeDesktop.state":
                return preferences.ClaudeDesktopWarningAccepted;

            case "notice.claudeDesktop.accept":
                preferences.AcceptClaudeDesktopWarning();
                return true;

            case "login.restoreAvailable":
                return vault.Exists("login.enc");

            case "login.load":
            case "login.restoreSaved":
                return vault.LoadEncrypted<ConnectionSettings>("login.enc");

            case "gateways.resume-settings":
            {
                var remembered = vault.LoadEncrypted<ConnectionSettings>("login.enc");
                if (remembered == null) return null;
                bool resumeHost = (claudeDesktop.Configured || File.Exists(Path.Combine(dataDirectory, "desktop-key.state"))) &&
                    hostControl.CanResume(remembered);
                bool resumeDesktop = claudeDesktop.CanResume(remembered);
                return resumeHost || resumeDesktop || SshSession.CanResumePublicGateway(dataDirectory, remembered)
                    ? remembered
                    : null;
            }

            case "preview.begin":
                if (Volatile.Read(ref activeMutations) > 0 || Volatile.Read(ref connectionPending) != 0 ||
                    session?.HasMounts == true || hostControl.Enabled || claudeDesktop.Running)
                    throw new InvalidOperationException("请先结束当前操作并安全卸载目录，再进入开发预览。");
                connecting?.Cancel();
                pendingHostTrust = null;
                gatewayContext = null;
                cachedCloudPort = null;
                cachedRdpPort = null;
                cachedTunnelHost = "";
                session?.Dispose();
                session = null;
                return true;

            case "pickFolder":
                return await PlatformLauncher.PickFolderAsync(ct);

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
                if (Volatile.Read(ref activeMutations) > 0 || session?.HasMounts == true || hostControl.Enabled || claudeDesktop.Running)
                    throw new InvalidOperationException("正在保护挂载目录、SSH 接入或本机控制通道，请先停用并安全卸载再更换连接。");
                if (Interlocked.CompareExchange(ref connectionPending, 1, 0) != 0)
                    throw new InvalidOperationException("连接正在处理中，请稍候。");
                try
                {
                    connecting?.Dispose();
                    connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connecting.CancelAfter(TimeSpan.FromSeconds(90));
                    gatewayContext = null;
                    session?.Dispose();
                    cachedCloudPort = null;
                    cachedRdpPort = null;
                    cachedTunnelHost = "";
                    lastMountStatusJson = "";
                    session = new SshSession(dataDirectory);
                    session.Log += text => QueueLog(activeStep, text);
                    session.DirectoryLog += text => QueueLog("folder", text);
                    var settings = new ConnectionSettings(
                        Read("host"),
                        Read("username"),
                        Read("password"),
                        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("port", out var p) ? p.GetInt32() : 22);
                    var current = session;
                    var server = await Task.Run(() => current.ConnectAsync(settings, _ => false, connecting.Token), connecting.Token);
                    pendingHostTrust = null;
                    cachedTunnelHost = current.Host + ":" + current.Port;
                    var warnings = new List<string>();
                    try
                    {
                        vault.SaveEncrypted("login.enc", settings);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
                    {
                        warnings.Add("连接已成功，但无法保存本机登录信息。");
                    }

                    bool keyConfigured = File.Exists(Path.Combine(dataDirectory, "desktop-key.state"));
                    if ((keyConfigured || claudeDesktop.Configured) && hostControl.CanResume(settings))
                    {
                        using var hostRestore = CancellationTokenSource.CreateLinkedTokenSource(connecting.Token);
                        hostRestore.CancelAfter(TimeSpan.FromSeconds(40));
                        try
                        {
                            if (await hostControl.ResumeAsync(current, hostRestore.Token) is { } hostWarning)
                                warnings.Add(hostWarning);
                        }
                        catch (OperationCanceledException) when (!connecting.IsCancellationRequested)
                        {
                            warnings.Add("本机远程控制恢复超时，可在设置中重试。");
                        }
                    }

                    if (!keyConfigured && claudeDesktop.Configured)
                    {
                        using var desktopRestore = CancellationTokenSource.CreateLinkedTokenSource(connecting.Token);
                        desktopRestore.CancelAfter(TimeSpan.FromSeconds(40));
                        try
                        {
                            if (await claudeDesktop.ResumeAsync(current, desktopRestore.Token) is { } desktopWarning)
                                warnings.Add(desktopWarning);
                        }
                        catch (OperationCanceledException) when (!connecting.IsCancellationRequested)
                        {
                            warnings.Add("Claude SSH 网关恢复超时，可在设置中重试。");
                        }
                    }

                    connecting.Token.ThrowIfCancellationRequested();
                    return new { ready = true, server, gatewayWarnings = warnings };
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
                    if (Flag("automatic") && ConnectionErrors.CanRetry(error))
                        return new { retryable = true, message = ConnectionErrors.Describe(error) };
                    throw new InvalidOperationException(ConnectionErrors.Describe(error), error);
                }
                finally
                {
                    Interlocked.Exchange(ref connectionPending, 0);
                }

            case "job.resume":
                activeStep = Read("operation");
                return await RequireSession().ResumeJobAsync(Read("jobId"), ct);

            case "inspect":
                return await RequireSession().RequestAsync<ServerInfo>("inspect", null, ct);

            case "ip.info":
                return await IpIntelligenceService.QueryAsync(Read("ip"), ct);

            case "metrics":
                return await RequireSession().MeasureNetworkAsync(ct);

            case "resources":
                return await RequireSession().ReadResourceStatsAsync(ct);

            case "environment.assess":
                return await RequireSession().AssessEnvironmentAsync(ct);

            case "deploy":
            {
                activeStep = Read("step");
                if (activeStep == "account" && (hostControl.Enabled || claudeDesktop.Running))
                    throw new InvalidOperationException("请先停用本机控制和 Claude Desktop SSH 接入，再修改工作账户。");
                var currentSession = RequireSession();
                if (activeStep == "desktop")
                {
                    if (currentSession.Server?.Statuses.GetValueOrDefault("account") != true)
                        throw new InvalidOperationException("请先创建工作用户。");
                    int savedScale = desktopDisplay.ReadRemoteScale();
                    return await currentSession.RequestAsync<ServerInfo>(
                        "desktop",
                        new { scale = savedScale, desktopScale = savedScale },
                        ct);
                }

                LocaleSettings? locale = null;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("locale", out var region) && region.ValueKind == JsonValueKind.Object)
                    locale = JsonSerializer.Deserialize<LocaleSettings>(region, JsonDefaults.Options);
                locale ??= preferences.ReadSavedLocale(currentSession);

                var draft = activeStep == "account" ? preferences.ReadDraft(currentSession) : null;
                var result = await currentSession.DeployAsync(
                    activeStep,
                    locale,
                    draft?.Username ?? Read("username", "workspace"),
                    ct,
                    draft?.Password ?? Read("password"),
                    draft?.Hostname ?? Read("hostname"));
                if (activeStep == "account" && result.Statuses.GetValueOrDefault("account"))
                    preferences.ClearDraft(currentSession);
                if (activeStep == "ssh")
                {
                    await currentSession.VerifyWorkspaceKeyAsync(ct);
                    await TrySyncHerdrMachineAsync(currentSession, result, ct);
                }
                return result;
            }

            case "connection.info":
                return session == null ? null : new { host = session.Host, port = session.Port, username = session.LoginUsername };

            case "connection.reauthenticate":
            {
                if (Volatile.Read(ref connectionPending) != 0)
                    throw new InvalidOperationException("请等待当前连接完成。");
                var recovering = session ?? throw new InvalidOperationException("请先登录服务器。");
                string newPassword = Read("password");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    await recovering.ReauthenticateAsync(newPassword, timeout.Token);
                    cachedCloudPort = null;
                    cachedRdpPort = null;
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(ConnectionErrors.Describe(error), error);
                }
                try
                {
                    vault.SaveEncrypted("login.enc", new ConnectionSettings(recovering.Host, recovering.LoginUsername, newPassword, recovering.Port));
                }
                catch
                {
                    QueueLog("connection", "新密码已验证，但登录记忆未能保存，下次请重新填写。");
                }
                return new { ready = true, mount = recovering.GetMountStatus() };
            }

            case "bridge.status":
                return new
                {
                    running = false,
                    connected = false,
                    cleanupPending = false,
                    port = 18797,
                    model = "sonnet",
                    writable = false,
                    message = "当前附件仅提供 Windows 适配器，macOS / Web 模式暂不可用",
                    startedAt = (string?)null,
                    stoppedAt = (string?)null,
                    usage = new { available = false, input = 0L, output = 0L, cacheRead = 0L, cacheWrite = 0L, total = 0L, note = "macOS / Web 暂无可用的 Codex 接入用量来源" },
                    usageSource = ""
                };

            case "bridge.start":
            case "bridge.copy-key":
            case "bridge.copy-config":
                throw new InvalidOperationException("尚未取得经过验证的 macOS 适配器，不能运行 Windows EXE。此模块保持关闭。");

            case "bridge.stop":
                return new { running = false, connected = false, cleanupPending = false, message = "未启动" };

            case "host.status":
                return hostControl.Status;

            case "host.enable":
                if (!Flag("riskAccepted"))
                    throw new InvalidOperationException("请先阅读并确认本机远程控制权限说明。");
                activeStep = "host_enable";
                return await hostControl.EnableAsync(RequireSession(), Read("alias", "mac"), ct);

            case "host.disable":
                if (Volatile.Read(ref connectionPending) != 0)
                    throw new InvalidOperationException("请等待当前服务器连接完成。");
                return await hostControl.DisableAsync(ct);

            case "safeCodeApi.refreshSubscription":
                return await RequireGatewaySession().RefreshPublicGatewaySubscriptionAsync(ct);

            case "safeCodeApi.publicStatus":
                return await RequireGatewaySession().ReadPublicGatewayAsync(true, ct);

            case "safeCodeApi.install":
            {
                if (!Flag("accepted"))
                    throw new InvalidOperationException("请先确认安装 Safe Code API 服务。");
                activeStep = "gateway_install";
                return await RequireGatewaySession().InstallPublicGatewayAsync(
                    ct,
                    args.ValueKind == JsonValueKind.Object && args.TryGetProperty("cloudcli", out var cloudcliInstall) ? cloudcliInstall : null);
            }

            case "safeCodeApi.enablePublic":
                if (!Flag("accepted"))
                    throw new InvalidOperationException("请先确认「保证自用」。");
                return await RequireGatewaySession().EnablePublicGatewayAsync(ct);

            case "safeCodeApi.context":
            {
                var currentGatewaySession = RequireGatewaySession();
                await currentGatewaySession.RequirePublicGatewayAsync(ct);
                if (!ReferenceEquals(currentGatewaySession, RequireGatewaySession()))
                    throw new InvalidOperationException("SSH 会话已变更，请重新打开网关管理。");
                var context = new GatewayContext(currentGatewaySession, Guid.NewGuid().ToString("N"));
                gatewayContext = context;
                return context.Token;
            }

            case "safeCodeApi.manage":
            {
                var gatewaySession = RequireGatewaySession();
                var bound = gatewayContext;
                string suppliedContext = Read("context");
                if (!string.IsNullOrEmpty(suppliedContext))
                {
                    if (bound == null || !ReferenceEquals(gatewaySession, bound.Session) || suppliedContext != bound.Token)
                        throw new InvalidOperationException("SSH 会话已变更，请重新打开网关管理。");
                }
                else if (bound != null && !ReferenceEquals(gatewaySession, bound.Session))
                {
                    throw new InvalidOperationException("SSH 会话已变更，请重新打开网关管理。");
                }
                else if (bound == null)
                {
                    await gatewaySession.RequirePublicGatewayAsync(ct);
                    gatewayContext = new GatewayContext(gatewaySession, Guid.NewGuid().ToString("N"));
                }

                var request = (args.ValueKind == JsonValueKind.Object
                        ? args.Deserialize<Dictionary<string, JsonElement>>()
                        : null)
                    ?? throw new ArgumentException("缺少网关管理参数。");
                request.Remove("background");
                request.Remove("context");
                return await gatewaySession.ManagePublicGatewayAsync(request, ct);
            }

            case "desktopKey.status":
                return await KeyAccess().RestoreAsync(ct, interactive: Flag("interactive"));

            case "desktopKey.enable":
                if (claudeDesktop.Configured)
                    throw new InvalidOperationException("请先停用 Claude Desktop 的 SSH 接入。");
                pendingKeyDevice = null;
                return await KeyAccess().EnableAsync(Read("key"), ct, replace: Flag("replace"), useSaved: Flag("useSaved"));

            case "desktopKey.disable":
                pendingKeyDevice = null;
                return await KeyAccess().DisableAsync(ct);

            case "desktopKey.balance":
                return await KeyAccess().ReadBalanceAsync(ct);

            case "desktopKey.authorizeDevice":
            {
                if (!Flag("riskAccepted"))
                    throw new InvalidOperationException("请先阅读并同意本机远程控制权限说明。");
                var setup = pendingKeyDevice ?? throw new InvalidOperationException("请重新验证接入 Key。");
                pendingKeyDevice = null;
                return await KeyAccess().EnableAsync("", ct, replace: setup.Replace, useSaved: setup.UseSaved);
            }

            case "desktopKey.cancelDevice":
                pendingKeyDevice = null;
                return true;

            case "desktopKey.deviceStatus":
                return hostControl.Status;

            case "claudeDesktop.status":
                return await claudeDesktop.RefreshAsync(ct);

            case "claudeDesktop.start":
            {
                if (!Flag("accepted"))
                    throw new InvalidOperationException("请先确认通过 VPS 上的 CloudCLI 接入。");
                if (File.Exists(Path.Combine(dataDirectory, "desktop-key.state")))
                    throw new InvalidOperationException("请先停用 API Key 接入，再启用 SSH 接入。");
                activeStep = "claude_desktop";
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                return await claudeDesktop.StartAsync(RequireSession(), deadline.Token, Read("replaceOldProfile"));
            }

            case "claudeDesktop.stop":
                if (Volatile.Read(ref connectionPending) != 0)
                    throw new InvalidOperationException("请等待当前服务器连接完成。");
                activeStep = "claude_desktop";
                return await claudeDesktop.StopAsync(ct);

            case "claudeDesktop.open":
                return await PlatformLauncher.OpenLocalClientAsync("Claude", ct);

            case "codex.open":
                return await PlatformLauncher.OpenLocalClientAsync("Codex", ct);

            case "client.verify":
            {
                var current = RequireSession();
                bool nativeSsh = false;
                try
                {
                    await current.VerifyWorkspaceKeyAsync(ct);
                    nativeSsh = true;
                }
                catch { }
                bool savedLogin = vault.LoadEncrypted<ConnectionSettings>("login.enc")?.Host == current.Host;
                bool freeRdp = PlatformLauncher.FindExecutable(
                    "sdl-freerdp",
                    "sdl-freerdp3",
                    "/opt/homebrew/bin/sdl-freerdp",
                    "/usr/local/bin/sdl-freerdp") != null;
                return new
                {
                    nativeSsh,
                    desktopLogin = current.Server?.DesktopReady == true,
                    savedLogin,
                    herdrReady = current.Server?.HerdrReady == true,
                    localHerdr = HerdrClient.FindLocalExecutable() != null,
                    freeRdp
                };
            }

            case "folder.cancel":
                try { browsing?.Cancel(); } catch (ObjectDisposedException) { }
                return true;

            case "folder.create":
                return await RequireSession().CreateDirectoryAsync(Read("path"), Read("name"), ct);

            case "folder.list":
                try { browsing?.Cancel(); } catch (ObjectDisposedException) { }
                browsing = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

            case "account.change.status":
                return new { requiresConfirmation = preferences.RequiresAccountConfirmation(RequireSession()) };

            case "account.draft.save":
                preferences.SaveDraft(RequireSession(), new(Read("username"), Read("hostname"), Read("password")));
                return true;

            case "deployment.settings.get":
                return preferences.ReadDeploymentChoices(RequireSession());

            case "deployment.locale.save":
                return preferences.SaveDeploymentLocale(RequireSession(), Read("region"));

            case "desktop.resolution.get":
            {
                var res = preferences.ReadResolution();
                var mode = desktopDisplay.ReadMode();
                bool fullscreen = desktopDisplay.ReadFullscreen();
                bool desktopReady = session?.Server?.DesktopReady == true;
                int remoteScale = desktopDisplay.HasSavedPreferences
                    ? desktopDisplay.ReadRemoteScale()
                    : (RdpDisplayConstants.IsRetinaDisplay
                        ? RdpDisplayConstants.DefaultRemoteScale
                        : (desktopReady && RdpDisplayConstants.IsSupportedScale(session!.Server!.DesktopScale)
                            ? session.Server.DesktopScale
                            : desktopDisplay.ReadRemoteScale()));
                return new
                {
                    width = res.Width,
                    height = res.Height,
                    displayMode = mode.ToString(),
                    fullscreen,
                    remoteScale,
                    keyboardMode = desktopDisplay.ReadKeyboardMode().ToString(),
                    clientPreference = desktopDisplay.ReadClientPreference().ToString(),
                    desktopReady
                };
            }

            case "desktop.resolution.save":
            {
                if (args.ValueKind != JsonValueKind.Object ||
                    !args.TryGetProperty("width", out var widthProp) || !widthProp.TryGetInt32(out int width) ||
                    !args.TryGetProperty("height", out var heightProp) || !heightProp.TryGetInt32(out int height))
                    throw new ArgumentException("请选择支持的分辨率。");

                var resolution = new DesktopResolution(width, height);
                if (!WebPreferences.IsSupportedResolution(resolution.Width, resolution.Height))
                    throw new ArgumentException("请选择支持的分辨率。");

                RdpResizeMode mode = desktopDisplay.ReadMode();
                if (args.TryGetProperty("displayMode", out var modeProp) && modeProp.ValueKind != JsonValueKind.Null)
                {
                    if (modeProp.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("请选择支持的远程桌面显示模式。");
                    string? rawMode = modeProp.GetString();
                    if (!string.IsNullOrWhiteSpace(rawMode))
                    {
                        var parsedA = RdpDisplayConstants.ParseMode(rawMode, RdpResizeMode.Original);
                        var parsedB = RdpDisplayConstants.ParseMode(rawMode, RdpResizeMode.Scale);
                        if (parsedA != parsedB)
                            throw new ArgumentException("请选择支持的远程桌面显示模式。");
                        mode = parsedA;
                    }
                }

                bool fullscreen = desktopDisplay.ReadFullscreen();
                if (args.TryGetProperty("fullscreen", out var fullscreenProp) && fullscreenProp.ValueKind != JsonValueKind.Null)
                {
                    if (fullscreenProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ArgumentException("远程桌面全屏设置无效。");
                    fullscreen = fullscreenProp.GetBoolean();
                }

                RdpKeyboardMode keyboardMode = desktopDisplay.ReadKeyboardMode();
                if (args.TryGetProperty("keyboardMode", out var kbdProp) && kbdProp.ValueKind != JsonValueKind.Null)
                {
                    if (kbdProp.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("请选择支持的键盘映射模式。");
                    string? rawKbd = kbdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(rawKbd))
                    {
                        if (!RdpDisplayConstants.TryParseKeyboardMode(rawKbd, out keyboardMode))
                            throw new ArgumentException("请选择支持的键盘映射模式。");
                    }
                }

                RdpClientPreference clientPreference = desktopDisplay.ReadClientPreference();
                if (args.TryGetProperty("clientPreference", out var clientProp) && clientProp.ValueKind != JsonValueKind.Null)
                {
                    if (clientProp.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("请选择支持的远程桌面客户端。");
                    string? rawClient = clientProp.GetString();
                    if (!string.IsNullOrWhiteSpace(rawClient))
                    {
                        if (!RdpDisplayConstants.TryParseClientPreference(rawClient, out clientPreference))
                            throw new ArgumentException("请选择支持的远程桌面客户端。");
                    }
                }

                bool hasRemoteScale = args.TryGetProperty("remoteScale", out var scaleProp) && scaleProp.ValueKind != JsonValueKind.Null;
                int remoteScale = desktopDisplay.ReadRemoteScale();
                if (hasRemoteScale)
                {
                    if (scaleProp.ValueKind == JsonValueKind.Number && scaleProp.TryGetInt32(out int parsedScale))
                        remoteScale = parsedScale;
                    else if (scaleProp.ValueKind == JsonValueKind.String && int.TryParse(scaleProp.GetString(), out int parsedStringScale))
                        remoteScale = parsedStringScale;
                    else
                        throw new ArgumentException("请选择支持的远程桌面缩放比例（100%、150% 或 200%）。");
                }
                if (!RdpDisplayConstants.IsSupportedScale(remoteScale))
                    throw new ArgumentException("请选择支持的远程桌面缩放比例（100%、150% 或 200%）。");

                preferences.SaveResolution(resolution);
                desktopDisplay.Save(mode, fullscreen, remoteScale, keyboardMode, clientPreference);

                if (session?.IsConnected == true && session.Server?.DesktopReady == true && hasRemoteScale)
                {
                    var scaleResult = await session.RequestAsync<JsonElement>("desktop_scale", new { scale = remoteScale }, ct);
                    bool appliedLive = scaleResult.ValueKind == JsonValueKind.Object &&
                        scaleResult.TryGetProperty("appliedLive", out var liveProp) &&
                        liveProp.ValueKind == JsonValueKind.True;
                    int effectiveScale = remoteScale;
                    if (scaleResult.ValueKind == JsonValueKind.Object)
                    {
                        if (scaleResult.TryGetProperty("server", out var serverProp) && serverProp.ValueKind == JsonValueKind.Object)
                        {
                            var updatedServer = JsonSerializer.Deserialize<ServerInfo>(serverProp, JsonDefaults.Options);
                            if (updatedServer != null && RdpDisplayConstants.IsSupportedScale(updatedServer.DesktopScale))
                                effectiveScale = updatedServer.DesktopScale;
                        }
                        else if (scaleResult.TryGetProperty("desktopScale", out var dsProp) && dsProp.TryGetInt32(out int ds) && RdpDisplayConstants.IsSupportedScale(ds))
                        {
                            effectiveScale = ds;
                        }
                        else if (scaleResult.TryGetProperty("scale", out var scProp) && scProp.TryGetInt32(out int sc) && RdpDisplayConstants.IsSupportedScale(sc))
                        {
                            effectiveScale = sc;
                        }
                    }
                    if (session.Server != null)
                        session.Server.DesktopScale = effectiveScale;
                    if (effectiveScale != remoteScale)
                        desktopDisplay.Save(mode, fullscreen, effectiveScale, keyboardMode, clientPreference);

                    return new
                    {
                        windows = 0,
                        dynamicUpdated = appliedLive ? 1 : 0,
                        deferred = appliedLive ? 0 : 1,
                        displayMode = mode.ToString(),
                        fullscreen,
                        remoteScale = effectiveScale,
                        keyboardMode = keyboardMode.ToString(),
                        clientPreference = clientPreference.ToString(),
                        remoteScaleApplied = true,
                        appliedLive
                    };
                }

                return new
                {
                    windows = 0,
                    dynamicUpdated = 0,
                    deferred = 1,
                    displayMode = mode.ToString(),
                    fullscreen,
                    remoteScale,
                    keyboardMode = keyboardMode.ToString(),
                    clientPreference = clientPreference.ToString(),
                    remoteScaleApplied = false,
                    appliedLive = false
                };
            }

            case "account":
                return await RequireSession().RequestAsync<AccountInfo>("account_info", null, ct);

            case "desktop":
                return await OpenRemoteDesktopAsync(RequireSession(), preferences.ReadResolution(), ct);

            case "terminal":
                return await OpenRemoteTerminalAsync(RequireSession(), ct);

            case "terminal.disconnect":
                return await CloseRemoteTerminalAsync(RequireSession(), ct);

            case "cloud.install":
                activeStep = "cloud";
                return await Task.Run(() => RequireSession().RequestAsync<ServerInfo>("cloud", null, ct), ct);

            case "cloud.open":
            {
                var currentSession = RequireSession();
                if (currentSession.Server?.CloudReady != true)
                    throw new InvalidOperationException("请先安装 CloudCLI。");
                if (Volatile.Read(ref activeMutations) > 0)
                    throw new InvalidOperationException("请等待当前部署完成后打开 CloudCLI。");
                await currentSession.RequestAsync<ServerInfo>("cloud_setup", null, ct);
                uint cloudPort = await EnsureTunnelAsync(currentSession, 3001, isCloud: true, ct);
                return new { url = "http://127.0.0.1:" + cloudPort };
            }

            case "cloud.expose":
            {
                var currentSession = RequireSession();
                if (currentSession.Server?.CloudReady != true)
                    throw new InvalidOperationException("请先安装 CloudCLI。");
                activeStep = "cloud_public";
                return await Task.Run(() => currentSession.RequestAsync<JsonElement>("cloud_public", new { host = currentSession.Host }, ct), ct);
            }

            case "mount.settings":
                return RequireSession().ReadMountMappings();

            case "mount.status":
            case "mount.status.open":
                return session?.GetMountStatus() ?? new MountStatus(0, "idle", "未挂载目录", 0, 0, 0, 0, 0);

            case "mount.reconnect":
                if (session is { HasMounts: true } mountSession)
                    await mountSession.RecoverMountsAsync(ct);
                return true;

            case "mount.install":
                activeStep = "mount_install";
                return await Task.Run(() => RequireSession().RequestAsync<ServerInfo>("mount_install", null, ct), ct);

            case "mount.apply":
            {
                var mappings = JsonSerializer.Deserialize<List<MountMapping>>(args.GetProperty("mappings"), JsonDefaults.Options) ?? [];
                activeStep = "mount_apply";
                var mountResult = await Task.Run(() => RequireSession().ApplyMountsAsync(mappings, ct), ct);
                if (session != null)
                    _ = BroadcastAsync(new { type = "mountStatus", mount = session.GetMountStatus() });
                return mountResult;
            }

            case "ip.apply":
            {
                activeStep = "ip_policy";
                var currentSession = RequireSession();
                var changed = await currentSession.ApplyIpPolicyAsync(
                    JsonSerializer.Deserialize<IpPolicySettings>(args, JsonDefaults.Options) ?? throw new ArgumentException("缺少 IP 设置。"),
                    ct);
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

            default:
                throw new InvalidOperationException("未知客户端操作：" + operation);
        }
    }

    private DesktopKeyAccess KeyAccess() => keyAccess ??= new DesktopKeyAccess(
        dataDirectory,
        vault.ProtectBytes,
        vault.UnprotectBytes,
        diagnostics: services,
        beforeMessages: ct => hostControl.MaintainForChatAsync(ct));

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

    private static string DescribeDisplayMode(RdpResizeMode mode, bool fullscreen)
    {
        string modeLabel = mode switch
        {
            RdpResizeMode.Scale => "自适应缩放",
            RdpResizeMode.Dynamic => "动态调整分辨率",
            _ => "原始分辨率"
        };
        return fullscreen ? modeLabel + "（全屏）" : modeLabel;
    }

    private async Task<object> OpenRemoteDesktopAsync(SshSession currentSession, DesktopResolution resolution, CancellationToken ct)
    {
        if (currentSession.Server?.DesktopReady != true)
            throw new InvalidOperationException("请先安装虚拟桌面。");

        var mode = desktopDisplay.ReadMode();
        bool fullscreen = desktopDisplay.ReadFullscreen();
        var keyboardMode = desktopDisplay.ReadKeyboardMode();
        var clientPreference = desktopDisplay.ReadClientPreference();
        int desiredScale = desktopDisplay.ReadRemoteScale();

        // Automatically keep remote XFCE HiDPI scale synchronized with client preference
        // (e.g., 200% true Retina on macOS HiDPI) before opening RDP so text is razor-sharp out of the box.
        if (RdpDisplayConstants.IsSupportedScale(desiredScale) &&
            currentSession.Server.DesktopScale != desiredScale)
        {
            try
            {
                await currentSession.RequestAsync<JsonElement>("desktop_scale", new { scale = desiredScale }, ct);
                currentSession.Server.DesktopScale = desiredScale;
            }
            catch
            {
                // Non-fatal: continue opening desktop even if remote scale sync is unavailable.
            }
        }

        var account = await currentSession.RequestAsync<AccountInfo>("account_info", null, ct);

        string? freeRdp = clientPreference == RdpClientPreference.WindowsApp
            ? null
            : PlatformLauncher.FindExecutable(
                "sdl-freerdp",
                "sdl-freerdp3",
                "/opt/homebrew/bin/sdl-freerdp",
                "/usr/local/bin/sdl-freerdp");

        if (clientPreference == RdpClientPreference.FreeRdp && freeRdp == null)
            throw new InvalidOperationException("未找到 FreeRDP（sdl-freerdp），请先执行 brew install freerdp 或在显示设置中切换为 Windows App。");

        var endpoint = await currentSession.PrepareRemoteDesktopAsync(account.Username, acquireCertificate: freeRdp != null, ct);

        if (freeRdp != null)
        {
            int effectiveScale = RdpDisplayConstants.IsSupportedScale(currentSession.Server.DesktopScale)
                ? currentSession.Server.DesktopScale
                : desiredScale;
            HostDisplayMetrics? hostDisplay = HostDisplayMetrics.DetectPrimary();

            string[] freeRdpArgs = RdpLaunchBuilder.BuildFreeRdpArguments(
                endpoint,
                account,
                resolution.Width,
                resolution.Height,
                mode,
                fullscreen,
                "Safe Code · 远程桌面",
                effectiveScale,
                hostDisplay,
                keyboardMode);

            var start = new ProcessStartInfo(freeRdp)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = Encoding.UTF8
            };
            RdpLaunchBuilder.ApplyFreeRdpEnvironment(start.Environment, dataDirectory);
            start.ArgumentList.Add("/args-from:stdin");

            var proc = Process.Start(start) ?? throw new IOException("无法启动 FreeRDP 客户端（sdl-freerdp）。");
            var stderrLines = new ConcurrentQueue<string>();
            var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    stderrDone.TrySetResult();
                    return;
                }
                stderrLines.Enqueue(e.Data);
                while (stderrLines.Count > 20) stderrLines.TryDequeue(out string? _);
            };
            proc.BeginErrorReadLine();

            try
            {
                await proc.StandardInput.WriteAsync(string.Join('\n', freeRdpArgs) + "\n");
                await proc.StandardInput.FlushAsync(ct);
            }
            catch (IOException) when (proc.HasExited)
            {
                // Process exited before reading stdin; surface stderr below.
            }
            finally
            {
                try { proc.StandardInput.Close(); } catch { }
            }

            await Task.Delay(350, ct);
            if (proc.HasExited && proc.ExitCode != 0)
            {
                await Task.WhenAny(stderrDone.Task, Task.Delay(250, ct));
                string tail = string.Join("\n", stderrLines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(6));
                if (!string.IsNullOrEmpty(account.Password))
                    tail = tail.Replace(account.Password, "[已隐藏]", StringComparison.Ordinal);
                int exitCode = proc.ExitCode;
                proc.Dispose();
                string detail = string.IsNullOrEmpty(tail) ? $"退出码 {exitCode}" : $"退出码 {exitCode}：{tail}";
                throw new IOException("FreeRDP 远程桌面启动失败（" + detail + "）。");
            }

            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => proc.Dispose();
            if (proc.HasExited) proc.Dispose();
            return new { ok = true };
        }

        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool hasMacRdpApp = OperatingSystem.IsMacOS() &&
            (Directory.Exists("/Applications/Windows App.app") ||
             Directory.Exists("/Applications/Microsoft Remote Desktop.app") ||
             Directory.Exists(Path.Combine(homeDir, "Applications", "Windows App.app")) ||
             Directory.Exists(Path.Combine(homeDir, "Applications", "Microsoft Remote Desktop.app")));

        if (clientPreference == RdpClientPreference.WindowsApp && !hasMacRdpApp)
            throw new InvalidOperationException("未检测到 Windows App 或 Microsoft Remote Desktop，请先从 App Store 安装，或在显示设置中切回 FreeRDP。");

        string displaySummary = $"{resolution.Width}×{resolution.Height} · {DescribeDisplayMode(mode, fullscreen)}";

        if (hasMacRdpApp)
        {
            string rdpPath = Path.Combine(dataDirectory, $"safe-code-{endpoint.ProfileId}.rdp");
            string rdpContent = RdpLaunchBuilder.BuildRdpFileContent(
                endpoint,
                account,
                resolution.Width,
                resolution.Height,
                mode,
                fullscreen);
            string tempPath = rdpPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WebLocalVault.RestrictFilePermissions(tempPath);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.Write(rdpContent);
                }
                WebLocalVault.RestrictFilePermissions(tempPath);
                File.Move(tempPath, rdpPath, true);
                WebLocalVault.RestrictFilePermissions(rdpPath);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }

            PlatformLauncher.TryCopyClipboard(account.Password);

            bool opened = false;
            var openByBundle = new ProcessStartInfo("open") { UseShellExecute = false, RedirectStandardError = true };
            openByBundle.ArgumentList.Add("-b");
            openByBundle.ArgumentList.Add("com.microsoft.rdc.macos");
            openByBundle.ArgumentList.Add(rdpPath);
            using (var bundleProc = Process.Start(openByBundle))
            {
                if (bundleProc != null)
                {
                    await bundleProc.WaitForExitAsync(ct);
                    opened = bundleProc.ExitCode == 0;
                }
            }

            if (!opened)
            {
                var openDefault = new ProcessStartInfo("open") { UseShellExecute = false, RedirectStandardError = true };
                openDefault.ArgumentList.Add(rdpPath);
                using var fallbackProc = Process.Start(openDefault);
                if (fallbackProc != null)
                {
                    await fallbackProc.WaitForExitAsync(ct);
                    if (fallbackProc.ExitCode != 0)
                    {
                        string err = (await fallbackProc.StandardError.ReadToEndAsync(ct)).Trim();
                        throw new IOException(string.IsNullOrEmpty(err) ? "无法打开远程桌面配置文件。" : "无法打开远程桌面配置文件：" + err);
                    }
                }
            }

            return new
            {
                ok = true,
                title = "远程桌面隧道已开启",
                message = $"已调用本机远程桌面客户端连接 SSH 加密隧道 127.0.0.1:{endpoint.Port}。\n显示设置：{displaySummary}\n工作用户名：{account.Username}\n工作密码已自动复制到剪贴板，粘贴即可登录。"
            };
        }

        PlatformLauncher.TryCopyClipboard(account.Password);
        return new
        {
            ok = true,
            title = "远程桌面 SSH 隧道已就绪",
            message = $"加密隧道已监听本机地址：127.0.0.1:{endpoint.Port}\n显示设置：{displaySummary}\n用户名：{account.Username}\n（密码已复制到剪贴板，也可在“工作账户”卡片查看）\n请使用任意 RDP 客户端连接上述地址。"
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

    private async Task<object> CloseRemoteTerminalAsync(SshSession currentSession, CancellationToken ct)
    {
        string sshAlias = HerdrClient.BuildAlias(currentSession.Host, currentSession.Port);
        string? localHerdr = HerdrClient.FindLocalExecutable();
        bool removedLocal = false;
        if (!string.IsNullOrEmpty(localHerdr))
        {
            removedLocal = await HerdrClient.RemoveSavedMachineAsync(localHerdr, sshAlias, "safe-code", ct);
        }

        string? workspaceUser = currentSession.Server?.Username;
        if (!string.IsNullOrWhiteSpace(workspaceUser))
        {
            try
            {
                string prefix = currentSession.Server?.IsRoot == true ? "" : "sudo -n ";
                string userQuoted = SshSession.Quote(workspaceUser);
                string stopCmd = prefix + "runuser -u " + userQuoted + " -- sh -lc 'if [ -x \"$HOME/.local/bin/herdr\" ]; then \"$HOME/.local/bin/herdr\" session stop safe-code >/dev/null 2>&1 || true; fi; tmux kill-session -t safe-code >/dev/null 2>&1 || true'";
                await currentSession.ExecuteTextAsync(stopCmd, ct);
            }
            catch
            {
                // Ignore if remote session is already stopped.
            }
        }

        return new
        {
            removedLocal,
            title = "远程会话已关闭",
            message = "已从本机 Herdr 侧边栏移除该远程机器，并停止远端 safe-code 持久终端会话。"
        };
    }

    private async Task MaintainBackgroundAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        int tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                tick++;
                if (!clients.IsEmpty && session is { } current)
                {
                    var mount = current.GetMountStatus();
                    string serialized = JsonSerializer.Serialize(mount, JsonDefaults.Options);
                    if (serialized != lastMountStatusJson)
                    {
                        lastMountStatusJson = serialized;
                        await BroadcastAsync(new { type = "mountStatus", mount });
                    }
                }

                if (tick % 8 == 0 && claudeDesktop.Running)
                {
                    try
                    {
                        var status = await claudeDesktop.RefreshAsync(ct);
                        if (!clients.IsEmpty)
                            await BroadcastAsync(new { type = "claudeDesktopStatus", status });
                    }
                    catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or CryptographicException)
                    {
                        QueueLog("claude_desktop", "无法刷新 Claude Desktop SSH 接入状态。");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
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
            await SendRawAsync(pair.Value, bytes);
        }
    }

    private Task SendJsonAsync(WebSocketClient client, object payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonDefaults.Options));
        return SendRawAsync(client, bytes);
    }

    private async Task SendRawAsync(WebSocketClient client, byte[] bytes)
    {
        if (client.Socket.State != WebSocketState.Open) return;
        bool locked = false;
        try
        {
            await client.SendLock.WaitAsync(lifetime.Token);
            locked = true;
            if (client.Socket.State == WebSocketState.Open)
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token);
        }
        catch { }
        finally
        {
            if (locked)
            {
                try { client.SendLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private SshSession RequireGatewaySession() =>
        Volatile.Read(ref connectionPending) == 0 && session?.Server != null
            ? session
            : throw new InvalidOperationException("请先连接服务器。");

    private SshSession RequireSession() =>
        Volatile.Read(ref connectionPending) == 0 && session?.IsConnected == true && session.Server != null
            ? session
            : throw new InvalidOperationException("SSH 连接已断开，请重新连接。");

    public void Dispose()
    {
        logTimer.Dispose();
        lifetime.Cancel();
        try { if (claudeDesktop.Running) claudeDesktop.StopAsync(CancellationToken.None, suspend: true).GetAwaiter().GetResult(); } catch { }
        try { if (hostControl.Enabled) hostControl.SuspendAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { if (keyAccess != null) keyAccess.StopAsync().GetAwaiter().GetResult(); } catch { }
        try { if (session?.HasMounts == true) session.UnmountAsync().GetAwaiter().GetResult(); } catch { }
        claudeDesktop.Dispose();
        keyAccess?.Dispose();
        hostControl.Dispose();
        session?.Dispose();
        services.Dispose();
        connecting?.Dispose();
        browsing?.Dispose();
        mutationGate.Dispose();
        lifetime.Dispose();
    }
}
