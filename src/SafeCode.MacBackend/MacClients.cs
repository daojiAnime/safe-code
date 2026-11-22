using SafeCode.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
namespace SafeCode.Desktop;
internal sealed class MacClients(string directory)
{
    internal static string Quote(string value)=>"'"+value.Replace("'","'\\''")+"'";
    public static async Task<string> Run(string file,IEnumerable<string> args,CancellationToken ct=default)
    {
        var start=new ProcessStartInfo(file){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("无法启动系统工具。");var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
        try{await process.WaitForExitAsync(ct);}catch{try{process.Kill(true);}catch{}throw;}
        if(process.ExitCode!=0)throw new IOException("系统工具执行失败："+await error);return await output;
    }
    public static async Task<string> OpenLocalClientAsync(string name,CancellationToken ct)
    {
        if(name is not ("Codex" or "Claude"))throw new ArgumentException("未知客户端。");
        try{await Run("/usr/bin/open",["-a",name],ct);return "已请求打开 "+name+" 客户端。";}
        catch(IOException){throw new InvalidOperationException("未能打开 "+name+"，请先安装官方客户端，或从应用程序目录启动。");}
    }
    private static async Task<bool> TryFocusExistingLocalHerdrTabAsync(CancellationToken ct)
    {
        if(!OperatingSystem.IsMacOS())return false;

        const string listTabsScript="""
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

        string tabsOutput;
        try{tabsOutput=await Run("/usr/bin/osascript",["-e",listTabsScript],ct);}
        catch{return false;}
        if(string.IsNullOrWhiteSpace(tabsOutput))return false;

        foreach(string rawLine in tabsOutput.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            string[] parts=rawLine.Split('|');
            if(parts.Length!=3)continue;
            string winId=parts[0],tabIdx=parts[1],ttyName=Path.GetFileName(parts[2]);
            if(string.IsNullOrEmpty(ttyName))continue;

            string psOut;
            try{psOut=await Run("/bin/ps",["-t",ttyName,"-o","args="],ct);}
            catch{continue;}

            string[] cmdLines=psOut.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            if(cmdLines.Any(line=>line.Contains("--remote",StringComparison.Ordinal)))continue;

            bool hasLocalHerdrUi=cmdLines.Any(line=>
            {
                string[] tokens=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(tokens.Length==0||!string.Equals(Path.GetFileName(tokens[0]),"herdr",StringComparison.Ordinal))return false;
                if(tokens.Length==1)return true;
                return tokens[1]=="--session"||(tokens[1]=="session"&&tokens.Length>2&&tokens[2]=="attach");
            });
            if(!hasLocalHerdrUi)continue;

            const string focusScript="""
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
            try
            {
                await Run("/usr/bin/osascript",["-e",focusScript,"--",winId,tabIdx],ct);
                return true;
            }
            catch
            {
                // Fall through to launch a new tab/window.
            }
        }
        return false;
    }
    private async Task LaunchTerminalCommandAsync(string launchCommand,CancellationToken ct)
    {
        string homeDir=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string scriptPath=Path.Combine(directory,"open-terminal.command");
        string scriptBody=string.Join("\n",
        [
            "#!/bin/sh",
            "unset HERDR_ENV",
            "export PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\"",
            "exec "+launchCommand,
            ""
        ]);
        File.WriteAllText(scriptPath,scriptBody,new UTF8Encoding(false));
        if(OperatingSystem.IsMacOS())
            File.SetUnixFileMode(scriptPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);

        bool hasITerm=Directory.Exists("/Applications/iTerm.app")||
                      Directory.Exists(Path.Combine(homeDir,"Applications","iTerm.app"));
        if(hasITerm)
        {
            string itermCmd=scriptPath.Contains(' ')?"/bin/sh "+Quote(scriptPath):"/bin/sh "+scriptPath;
            const string appleScript="""
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
            try
            {
                await Run("/usr/bin/osascript",["-e",appleScript,"--",itermCmd],ct);
                return;
            }
            catch
            {
                try
                {
                    await Run("/usr/bin/open",["-a","iTerm",scriptPath],ct);
                    return;
                }
                catch
                {
                    // Fall back to Terminal.app below.
                }
            }
        }

        // AppleScript string encoding is separate from shell quoting. No password in args or scripts.
        string shellCommand="unset HERDR_ENV; export PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:$PATH\"; "+launchCommand;
        string literal="\""+shellCommand.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";
        await Run("/usr/bin/osascript",["-e","tell application \"Terminal\"","-e","activate","-e","do script "+literal,"-e","end tell"],ct);
    }
    public async Task OpenTerminalAsync(SshSession session,CancellationToken ct)
    {
        if(session.Server?.Statuses.GetValueOrDefault("account")!=true)throw new InvalidOperationException("请先部署工作用户。");
        var updatedServer=await session.DeployAsync("ssh",null,ct:ct);await session.VerifyWorkspaceKeyAsync(ct);
        var account=await session.RequestAsync<AccountInfo>("account_info",null,ct);
        string sshAlias=HerdrClient.EnsureManagedSshAlias(session,account.Username);
        string? localHerdr=HerdrClient.FindLocalExecutable();
        if(!string.IsNullOrEmpty(localHerdr)&&updatedServer.HerdrReady)
        {
            string preferredLabel=!string.IsNullOrWhiteSpace(updatedServer.Hostname)?updatedServer.Hostname:session.Host;
            bool merged=await HerdrClient.EnsureMachineSavedAsync(localHerdr,sshAlias,preferredLabel,"safe-code",ct);
            if(merged&&await TryFocusExistingLocalHerdrTabAsync(ct))return;
            string herdrCommand=merged
                ?Quote(localHerdr)
                :string.Join(" ",[Quote(localHerdr),"--remote",Quote(sshAlias),"--session","safe-code"]);
            await LaunchTerminalCommandAsync(herdrCommand,ct);
            return;
        }

        var config=Path.Combine(directory,"terminal-config");File.WriteAllText(config,"# Safe Code isolated SSH client\n");MacFiles.Protect(config);
        string[] args=["/usr/bin/ssh","-t","-F",config,"-i",session.KeyPath,"-p",session.Port.ToString(),"-o","BatchMode=yes","-o","ForwardAgent=no","-o","IdentitiesOnly=yes","-o","StrictHostKeyChecking=yes","-o",SshClientOptions.UserKnownHostsFile(session.KnownHostsPath),account.Username+"@"+session.Host,".local/bin/safe-code-terminal"];
        string command=string.Join(" ",args.Select(Quote));
        // AppleScript string encoding is separate from shell quoting. No password in args or scripts.
        string literal="\""+command.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";
        await Run("/usr/bin/osascript",["-e","tell application \"Terminal\"","-e","activate","-e","do script "+literal,"-e","end tell"],ct);
    }
    public async Task<object> DisconnectTerminalAsync(SshSession session,CancellationToken ct)
    {
        string sshAlias=HerdrClient.BuildAlias(session.Host,session.Port);
        string? localHerdr=HerdrClient.FindLocalExecutable();
        bool removedLocal=false;
        if(!string.IsNullOrEmpty(localHerdr))
        {
            removedLocal=await HerdrClient.RemoveSavedMachineAsync(localHerdr,sshAlias,"safe-code",ct);
        }

        string? workspaceUser=session.Server?.Username;
        if(!string.IsNullOrWhiteSpace(workspaceUser))
        {
            try
            {
                string prefix=session.Server?.IsRoot==true?"":"sudo -n ";
                string userQuoted=SshSession.Quote(workspaceUser);
                string stopCmd=prefix+"runuser -u "+userQuoted+" -- sh -lc 'if [ -x \"$HOME/.local/bin/herdr\" ]; then \"$HOME/.local/bin/herdr\" session stop safe-code >/dev/null 2>&1 || true; fi; tmux kill-session -t safe-code >/dev/null 2>&1 || true'";
                await session.ExecuteTextAsync(stopCmd,ct);
            }
            catch
            {
                // Ignore if remote session is already stopped.
            }
        }

        return new
        {
            removedLocal,
            title="远程会话已关闭",
            message="已从本机 Herdr 侧边栏移除该远程机器，并停止远端 safe-code 持久终端会话。"
        };
    }
    private static string? FindFreeRdp()
    {
        string bundledDir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","RDP"));
        string[] explicitCandidates=
        [
            Path.Combine(bundledDir,"sdl-freerdp"),
            Path.Combine(bundledDir,"sdl-freerdp3"),
            "/opt/homebrew/bin/sdl-freerdp",
            "/opt/homebrew/bin/sdl-freerdp3",
            "/usr/local/bin/sdl-freerdp",
            "/usr/local/bin/sdl-freerdp3"
        ];
        foreach(string candidate in explicitCandidates)
        {
            if(File.Exists(candidate))return candidate;
        }
        string[] pathDirs=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries);
        foreach(string name in new[]{"sdl-freerdp","sdl-freerdp3"})
        {
            foreach(string dir in pathDirs)
            {
                string candidate=Path.Combine(dir,name);
                if(File.Exists(candidate))return candidate;
            }
        }
        return null;
    }
    internal static string[] FreeRdpArguments(
        RemoteDesktopConnection endpoint,
        AccountInfo account,
        DesktopResolution resolution,
        RdpResizeMode mode,
        bool fullscreen,
        int remoteScale=100,
        HostDisplayMetrics? hostDisplay=null,
        RdpKeyboardMode? keyboardMode=null,
        bool? macOsOverride=null)
    {
        if(endpoint.CertificateFingerprint is not {Length:64})
            throw new InvalidOperationException("缺少经 SSH 获取的 RDP 证书指纹。");
        return RdpLaunchBuilder.BuildFreeRdpArguments(
            endpoint,
            account,
            resolution.Width,
            resolution.Height,
            mode,
            fullscreen,
            "Safe Code · 远程桌面",
            remoteScale,
            hostDisplay,
            keyboardMode,
            macOsOverride);
    }
    public async Task<string?> DesktopAsync(SshSession session,DesktopResolution resolution,RdpDisplayPreferences display,CancellationToken ct)
    {
        if(session.Server?.DesktopReady!=true)throw new InvalidOperationException("请先安装虚拟桌面。");
        var account=await session.RequestAsync<AccountInfo>("account_info",null,ct);
        var mode=display.ReadMode();
        bool fullscreen=display.ReadFullscreen();
        var keyboardMode=display.ReadKeyboardMode();
        var clientPreference=display.ReadClientPreference();
        int desiredScale=display.ReadRemoteScale();
        if(RdpDisplayConstants.IsSupportedScale(desiredScale)&&session.Server.DesktopScale!=desiredScale)
        {
            try
            {
                await session.RequestAsync<System.Text.Json.JsonElement>("desktop_scale",new{scale=desiredScale},ct);
                session.Server.DesktopScale=desiredScale;
            }
            catch
            {
                // Non-fatal: continue opening desktop even if remote scale sync is unavailable.
            }
        }
        string? freeRdp=clientPreference==RdpClientPreference.WindowsApp?null:FindFreeRdp();
        if(clientPreference==RdpClientPreference.FreeRdp&&freeRdp==null)
            throw new InvalidOperationException("未找到 FreeRDP（sdl-freerdp），请先执行 brew install freerdp 或切换为 Windows App。");
        var endpoint=await session.PrepareRemoteDesktopAsync(account.Username,freeRdp!=null,ct);

        if(freeRdp!=null)
        {
            int effectiveScale=RdpDisplayConstants.IsSupportedScale(session.Server.DesktopScale)
                ?session.Server.DesktopScale
                :desiredScale;
            HostDisplayMetrics? hostDisplay=HostDisplayMetrics.DetectPrimary();
            string[] freeRdpArgs=FreeRdpArguments(
                endpoint,
                account,
                resolution,
                mode,
                fullscreen,
                effectiveScale,
                hostDisplay,
                keyboardMode);
            var start=new ProcessStartInfo(freeRdp)
            {
                UseShellExecute=false,
                RedirectStandardInput=true,
                RedirectStandardOutput=true,
                RedirectStandardError=true,
                StandardInputEncoding=new UTF8Encoding(false),
                StandardErrorEncoding=Encoding.UTF8
            };
            RdpLaunchBuilder.ApplyFreeRdpEnvironment(start.Environment,directory);
            start.ArgumentList.Add("/args-from:stdin");
            var proc=Process.Start(start)??throw new IOException("无法启动 FreeRDP 客户端（sdl-freerdp）。");
            proc.OutputDataReceived+=(_,_)=>{};
            var stderrLines=new ConcurrentQueue<string>();
            var stderrDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proc.ErrorDataReceived+=(_,e)=>
            {
                if(e.Data==null){stderrDone.TrySetResult();return;}
                stderrLines.Enqueue(e.Data);
                while(stderrLines.Count>20)stderrLines.TryDequeue(out string? _);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            try
            {
                await proc.StandardInput.WriteAsync(string.Join('\n',freeRdpArgs)+"\n");
                await proc.StandardInput.FlushAsync(ct);
            }
            catch(IOException) when(proc.HasExited)
            {
                // Process exited early; report stderr below.
            }
            catch
            {
                try{if(!proc.HasExited)proc.Kill();}catch{}
                proc.Dispose();
                throw;
            }
            finally
            {
                try{proc.StandardInput.Close();}catch{}
            }
            await Task.Delay(350,ct);
            if(proc.HasExited&&proc.ExitCode!=0)
            {
                await Task.WhenAny(stderrDone.Task,Task.Delay(250,ct));
                string tail=string.Join("\n",stderrLines.Where(l=>!string.IsNullOrWhiteSpace(l)).TakeLast(6));
                if(!string.IsNullOrEmpty(account.Password))tail=tail.Replace(account.Password,"[已隐藏]",StringComparison.Ordinal);
                int exitCode=proc.ExitCode;
                proc.Dispose();
                string detail=string.IsNullOrEmpty(tail)?$"退出码 {exitCode}":$"退出码 {exitCode}：{tail}";
                throw new IOException("FreeRDP 远程桌面启动失败（"+detail+"）。");
            }
            proc.EnableRaisingEvents=true;
            proc.Exited+=(_,_)=>proc.Dispose();
            if(proc.HasExited)proc.Dispose();
            return null;
        }

        string folder=Path.Combine(directory,"Desktops");
        Directory.CreateDirectory(folder);
        if(OperatingSystem.IsMacOS())File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string path=Path.Combine(folder,$"Safe Code-{endpoint.ProfileId}.rdp");
        string content=RdpLaunchBuilder.BuildRdpFileContent(endpoint,account,resolution.Width,resolution.Height,mode,fullscreen);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
            if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var writer=new StreamWriter(temp,new UTF8Encoding(false),options))
            {
                writer.Write(content);
            }
            File.Move(temp,path,true);
            MacFiles.Protect(path);
        }
        finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
        return path;
    }
}
