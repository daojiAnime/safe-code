using System.IO;
using SafeCode.Core;
using System.Diagnostics;

namespace SafeCode.Desktop;

public sealed class WindowsClients : IDisposable
{
    private readonly List<Process> terminals = [];
    private readonly List<RdpDesktop> desktops=[];
    internal RdpResizePreferences? ResizePreferences { get; set; }
    internal Action<Exception?>? DisplayOperationCompleted { get; set; }
    public async Task OpenDesktopAsync(SshSession session,CancellationToken ct,DesktopResolution? resolution=null)=>await LaunchDesktopAsync(session,ct,resolution);
    private async Task<RdpDesktop> LaunchDesktopAsync(SshSession session,CancellationToken ct,DesktopResolution? resolution=null)
    {
        if(session.Server?.DesktopReady!=true)throw new InvalidOperationException("请先安装虚拟桌面。");
        var account=await session.RequestAsync<AccountInfo>("account_info",null,ct);
        var connection=await session.PrepareRemoteDesktopAsync(account.Username,false,ct);
        var window=new RdpDesktop(resolution,ResizePreferences,DisplayOperationCompleted);desktops.Add(window);
        window.FormClosed+=(_,_)=>desktops.Remove(window);
        try{window.Show();window.Connect("127.0.0.1",connection.Port,account.Username,account.Password,resolution);return window;}catch{window.Close();window.Dispose();throw;}
    }
    public object ResizeOpenDesktops(DesktopResolution resolution)
    {
        int updated=0,deferred=0;foreach(var desktop in desktops.ToArray()){if(desktop.IsDisposed)continue;if(desktop.ApplyResolution(resolution))updated++;else deferred++;}
        return new{windows=updated+deferred,dynamicUpdated=updated,deferred};
    }
    public async Task<bool> VerifyDesktopLoginAsync(SshSession session,CancellationToken ct)
    {
        var before=await session.RequestAsync<System.Text.Json.JsonElement>("desktop_login_probe",null,ct);
        long offset=before.GetProperty("offset").GetInt64();
        using var desktop=await LaunchDesktopAsync(session,ct);
        try{for(int i=0;i<30;i++){await Task.Delay(1000,ct);var state=await session.RequestAsync<System.Text.Json.JsonElement>("desktop_login_probe",new{offset},ct);if(state.GetProperty("loggedIn").GetBoolean())return true;}throw new IOException("远程桌面未在 30 秒内完成自动登录。");}
        finally{desktop.Close();}
    }
    private static string NativeSshPath()
    {
        string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"OpenSSH","ssh.exe");
        return File.Exists(path)?path:"ssh.exe";
    }
    private static string[] SshOptions(SshSession session,string username,bool interactive)
    {
        string config=Path.Combine(Path.GetDirectoryName(session.KnownHostsPath)!,"ssh-client-config");
        File.WriteAllText(config,"# Safe Code 单次连接不加载用户的通配主机配置。\n");
        return [interactive?"-t":"-T","-F",config,"-i",session.KeyPath,"-p",session.Port.ToString(),"-o","BatchMode=yes","-o","ForwardAgent=no","-o","IdentitiesOnly=yes","-o","StrictHostKeyChecking=yes","-o",SshClientOptions.UserKnownHostsFile(session.KnownHostsPath),username+"@"+session.Host];
    }
    public async Task<bool> VerifyNativeSshAsync(SshSession session,CancellationToken ct)
    {
        var account=await session.RequestAsync<AccountInfo>("account_info",null,ct);session.CreateWorkspaceKey();
        var start=new ProcessStartInfo(NativeSshPath()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in SshOptions(session,account.Username,false))start.ArgumentList.Add(arg);
        start.ArgumentList.Add("test -x .local/bin/safe-code-terminal && printf SAFE_CODE_NATIVE_SSH_OK");
        using var process=Process.Start(start)??throw new IOException("无法启动 OpenSSH 客户端。");
        var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try{await process.WaitForExitAsync(timeout.Token);}catch{if(!process.HasExited)process.Kill(true);throw;}
        if(process.ExitCode!=0||!(await output).Contains("SAFE_CODE_NATIVE_SSH_OK"))throw new IOException("Windows OpenSSH 验证失败："+await error);
        return true;
    }
    public async Task OpenTerminalAsync(SshSession session, CancellationToken ct)
    {
        if (session.Server?.Statuses.GetValueOrDefault("account") != true) throw new InvalidOperationException("请先部署工作用户。");
        var updatedServer = await session.DeployAsync("ssh", null, ct: ct); await session.VerifyWorkspaceKeyAsync(ct);
        var account = await session.RequestAsync<AccountInfo>("account_info", null, ct);
        string sshAlias = HerdrClient.EnsureManagedSshAlias(session, account.Username);
        string? localHerdr = HerdrClient.FindLocalExecutable();
        if (!string.IsNullOrEmpty(localHerdr) && updatedServer.HerdrReady)
        {
            string preferredLabel = !string.IsNullOrWhiteSpace(updatedServer.Hostname) ? updatedServer.Hostname : session.Host;
            bool merged = await HerdrClient.EnsureMachineSavedAsync(localHerdr, sshAlias, preferredLabel, "safe-code", ct);
            var herdrStart = new ProcessStartInfo(localHerdr) { UseShellExecute = true };
            if (!merged)
            {
                herdrStart.ArgumentList.Add("--remote");
                herdrStart.ArgumentList.Add(sshAlias);
                herdrStart.ArgumentList.Add("--session");
                herdrStart.ArgumentList.Add("safe-code");
            }
            var herdrProc = Process.Start(herdrStart) ?? throw new IOException("无法启动本机 Herdr 客户端。");
            terminals.Add(herdrProc);
            return;
        }
        string ssh=NativeSshPath();
        string[] args=[..SshOptions(session,account.Username,true),".local/bin/safe-code-terminal"];
        var start=new ProcessStartInfo(ssh){UseShellExecute=true};
        foreach(string arg in args)start.ArgumentList.Add(arg);
        var process=Process.Start(start)??throw new IOException("无法打开 Windows 终端。");terminals.Add(process);
    }
    public async Task<object> DisconnectTerminalAsync(SshSession session, CancellationToken ct)
    {
        string sshAlias = HerdrClient.BuildAlias(session.Host, session.Port);
        string? localHerdr = HerdrClient.FindLocalExecutable();
        bool removedLocal = false;
        if (!string.IsNullOrEmpty(localHerdr))
        {
            removedLocal = await HerdrClient.RemoveSavedMachineAsync(localHerdr, sshAlias, "safe-code", ct);
        }

        string? workspaceUser = session.Server?.Username;
        if (!string.IsNullOrWhiteSpace(workspaceUser))
        {
            try
            {
                string prefix = session.Server?.IsRoot == true ? "" : "sudo -n ";
                string userQuoted = SshSession.Quote(workspaceUser);
                string stopCmd = prefix + "runuser -u " + userQuoted + " -- sh -lc 'if [ -x \"$HOME/.local/bin/herdr\" ]; then \"$HOME/.local/bin/herdr\" session stop safe-code >/dev/null 2>&1 || true; fi; tmux kill-session -t safe-code >/dev/null 2>&1 || true'";
                await session.ExecuteTextAsync(stopCmd, ct);
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
    public static string OpenCodex()
    {
        try
        {
            Process.Start(new ProcessStartInfo("codex://") { UseShellExecute = true });
            return "已请求打开 Codex 客户端。";
        }
        catch
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(Path.Combine(packages, "OpenAI.Codex_2p2nqsd0c76g0")))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App") { UseShellExecute = true });
                    return "已请求打开 Codex 客户端。";
                }
                catch { }
            }
            throw new InvalidOperationException("未能打开 Codex，请先安装官方客户端，或从开始菜单启动。");
        }
    }
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidOperationException("拒绝打开不受支持的地址。");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    public void Dispose()
    {
        foreach(var desktop in desktops.ToArray()){try{desktop.Close();desktop.Dispose();}catch{}}
        foreach(var terminal in terminals){try{if(!terminal.HasExited)terminal.Kill();terminal.Dispose();}catch{}}
    }
}
