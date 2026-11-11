using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeCode.Core;

public sealed record HerdrSavedMachine(
    string Id,
    string Label,
    string Target,
    string Session,
    bool Enabled,
    bool Selected);

public static partial class HerdrClient
{
    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeAliasChars();

    [GeneratedRegex(@"^\s*(Host|Match)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex HostOrMatchDirective();

    [GeneratedRegex(@"^\s*Include\s+.*safe-code\.conf\b", RegexOptions.IgnoreCase)]
    private static partial Regex SafeCodeIncludeDirective();

    public static string? FindLocalExecutable()
    {
        string? envOverride = Environment.GetEnvironmentVariable("HERDR_BIN");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
            return envOverride;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string exeName = OperatingSystem.IsWindows() ? "herdr.exe" : "herdr";

        List<string> candidates =
        [
            Path.Combine(home, ".local", "bin", exeName)
        ];

        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "Programs", "herdr", "herdr.exe"));
            candidates.Add(Path.Combine(localAppData, "herdr", "herdr.exe"));
        }
        else
        {
            candidates.Add("/opt/homebrew/bin/herdr");
            candidates.Add("/usr/local/bin/herdr");
            candidates.Add("/usr/bin/herdr");
        }

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, exeName));
        }

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore inaccessible PATH entries.
            }
        }

        return null;
    }

    public static string BuildAlias(string host, int port)
    {
        string sanitized = UnsafeAliasChars().Replace(host.Trim(), "-").Trim('-');
        if (string.IsNullOrEmpty(sanitized))
            sanitized = "server";
        return $"safe-code-{sanitized}";
    }

    public static string EnsureManagedSshAlias(SshSession session, string username, string? sshDirectoryOverride = null)
    {
        string sshDir = sshDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Directory.CreateDirectory(sshDir);
        RestrictUnixPermissions(sshDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string alias = BuildAlias(session.Host, session.Port);
        string managedConfPath = Path.Combine(sshDir, "safe-code.conf");
        string sshConfigPath = Path.Combine(sshDir, "config");

        string identityPath = EscapeSshConfigPath(session.KeyPath);
        string knownHostsPath = EscapeSshConfigPath(session.KnownHostsPath);

        string beginMarker = $"# BEGIN safe-code:{alias}";
        string endMarker = $"# END safe-code:{alias}";
        string newBlock = string.Join("\n",
        [
            beginMarker,
            $"Host {alias}",
            $"  HostName {session.Host}",
            $"  Port {session.Port}",
            $"  User {username}",
            $"  IdentityFile \"{identityPath}\"",
            "  IdentitiesOnly yes",
            "  ForwardAgent no",
            "  StrictHostKeyChecking yes",
            $"  UserKnownHostsFile \"{knownHostsPath}\"",
            endMarker
        ]);

        string existingManaged = File.Exists(managedConfPath)
            ? File.ReadAllText(managedConfPath).Replace("\r\n", "\n")
            : "# Managed by Safe Code. Do not edit inside BEGIN/END blocks.\n";

        string updatedManaged = UpsertBlock(existingManaged, beginMarker, endMarker, newBlock);
        WriteAtomic(managedConfPath, updatedManaged, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        EnsureGlobalInclude(sshConfigPath);
        return alias;
    }

    public static async Task<bool> EnsureMachineSavedAsync(
        string herdrExecutable,
        string sshAlias,
        string preferredLabel,
        string remoteSession = "safe-code",
        CancellationToken ct = default)
    {
        var (listExit, listStdout, _) = await RunHerdrCliAsync(herdrExecutable, ["machine", "list", "--json"], TimeSpan.FromSeconds(10), ct);
        List<HerdrSavedMachine> machines = [];
        if (listExit == 0 && !string.IsNullOrWhiteSpace(listStdout))
        {
            try
            {
                machines = JsonSerializer.Deserialize<List<HerdrSavedMachine>>(listStdout, JsonDefaults.Options) ?? [];
            }
            catch
            {
                machines = [];
            }
        }

        var existing = machines.FirstOrDefault(m =>
            string.Equals(m.Target, sshAlias, StringComparison.Ordinal) &&
            string.Equals(m.Session, remoteSession, StringComparison.Ordinal));

        if (existing != null)
        {
            if (!existing.Enabled)
            {
                var (enableExit, _, _) = await RunHerdrCliAsync(herdrExecutable, ["machine", "enable", existing.Id], TimeSpan.FromSeconds(10), ct);
                return enableExit == 0;
            }
            return true;
        }

        string baseLabel = string.IsNullOrWhiteSpace(preferredLabel) ? sshAlias : preferredLabel.Trim();
        string chosenLabel = baseLabel;
        int suffix = 2;
        while (machines.Any(m => string.Equals(m.Label, chosenLabel, StringComparison.OrdinalIgnoreCase)))
        {
            chosenLabel = $"{baseLabel}-{suffix++}";
        }

        var (addExit, _, _) = await RunHerdrCliAsync(
            herdrExecutable,
            ["machine", "add", sshAlias, "--remote-session", remoteSession, "--label", chosenLabel],
            TimeSpan.FromSeconds(30),
            ct);

        return addExit == 0;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHerdrCliAsync(
        string executable,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment.Remove("HERDR_ENV");
        if (!OperatingSystem.IsWindows())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            psi.Environment["PATH"] = $"{Path.Combine(home, ".local", "bin")}:/opt/homebrew/bin:/usr/local/bin:{currentPath}";
        }

        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "", "Failed to start herdr");

        process.StandardInput.Close();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "Timed out");
        }
    }

    private static string EscapeSshConfigPath(string path)
        => path.Replace('\\', '/').Replace("\"", "\\\"");

    private static string UpsertBlock(string content, string beginMarker, string endMarker, string block)
    {
        int start = content.IndexOf(beginMarker, StringComparison.Ordinal);
        if (start >= 0)
        {
            int end = content.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end >= 0)
            {
                int afterEnd = end + endMarker.Length;
                if (afterEnd < content.Length && content[afterEnd] == '\n')
                    afterEnd++;
                string replaced = content[..start] + block + "\n" + content[afterEnd..];
                return replaced.TrimEnd() + "\n";
            }
        }

        var sb = new StringBuilder(content.TrimEnd());
        if (sb.Length > 0)
            sb.Append("\n\n");
        sb.Append(block).Append('\n');
        return sb.ToString();
    }

    private static void EnsureGlobalInclude(string sshConfigPath)
    {
        const string includeLine = "Include ~/.ssh/safe-code.conf";
        if (!File.Exists(sshConfigPath))
        {
            WriteAtomic(sshConfigPath, includeLine + "\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        string raw = File.ReadAllText(sshConfigPath);
        string normalized = raw.Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');

        foreach (string line in lines)
        {
            if (HostOrMatchDirective().IsMatch(line))
                break;
            if (SafeCodeIncludeDirective().IsMatch(line))
                return;
        }

        string updated = includeLine + "\n\n" + raw.TrimStart('\r', '\n');
        if (!updated.EndsWith('\n'))
            updated += "\n";
        WriteAtomic(sshConfigPath, updated, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void WriteAtomic(string path, string content, UnixFileMode unixMode)
    {
        string dir = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(dir, $".safe-code-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictUnixPermissions(temp, unixMode);
        File.Move(temp, path, overwrite: true);
        RestrictUnixPermissions(path, unixMode);
    }

    private static void RestrictUnixPermissions(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, mode); } catch { }
        }
    }
}
