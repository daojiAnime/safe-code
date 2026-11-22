using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCode.Core;
namespace SafeCode.Desktop;
internal static class MacSelfTest
{
    public static async Task RunAsync()
    {
        LocalSecrets.Initialize(RandomNumberGenerator.GetBytes(32));byte[] plain=Encoding.UTF8.GetBytes("fixture-password-not-a-real-secret");byte[] cipher=LocalSecrets.Protect(plain);
        if(!LocalSecrets.Unprotect(cipher).SequenceEqual(plain))throw new Exception("加密保存往返失败。");
        cipher[^1]^=1;try{LocalSecrets.Unprotect(cipher);throw new Exception("接受了被改写的密文。");}catch(CryptographicException){}
        if(MacClients.Quote("a'b")!="'a'\\''b'")throw new Exception("Shell 参数转义失败。");
        string Feed(string suffix)=>JsonSerializer.Serialize(new{tag_name="Beta-0.4.4.2",draft=false,prerelease=true,assets=new[]{new{name="SafeCode-Beta-0.4.4.2-"+suffix,browser_download_url="https://github.com/Muchuaner/safe-code/releases/download/Beta-0.4.4.2/SafeCode-Beta-0.4.4.2-"+suffix,digest="sha256:"+new string('a',64),size=123}}});
        using(var win=JsonDocument.Parse(Feed("Setup-x64.exe")))if(ClientServices.ParseRelease(win.RootElement,"Beta-0.4.4.1","macOS-arm64.dmg").CanInstall)throw new Exception("Mac 更新误选 Windows 安装包。");
        using(var mac=JsonDocument.Parse(Feed("macOS-arm64.dmg")))if(!ClientServices.ParseRelease(mac.RootElement,"Beta-0.4.4.1","macOS-arm64.dmg").CanInstall||ClientServices.ParseRelease(mac.RootElement,"Beta-0.4.4.1","macOS-x64.dmg").CanInstall)throw new Exception("架构筛选失败。");
        var profile=MacClaudeDesktopBridge.MakeProfile("http://127.0.0.1:18899","fixture-local-token",
            [new("claude-fixture-opus","Opus",["low","high"],true,"opus"),
             new("claude-fixture-classifier","Classifier",["low"],false,null,true)]);
        var model=profile["inferenceModels"]!.AsArray().Single()!;
        if((string?)model["name"]!="claude-fixture-opus"||(string?)model["maxEffort"]!="high"||
           (string?)profile["claudeCodeSettings"]?["env"]?["CLAUDE_CODE_AUTO_MODE_MODEL"]!="claude-fixture-classifier")
            throw new Exception("Mac Claude Desktop 模型和分类器配置错误。");
        Console.WriteLine("MAC_CLAUDE_PROFILE_CHAT_AND_CLASSIFIER_CONFIGURATION_OK");
        string profileTest = Directory.CreateTempSubdirectory("safecode-mac-profile-").FullName;
        try
        {
            string library = Path.Combine(profileTest, "configLibrary");
            string id = Guid.NewGuid().ToString("D");
            string profilePath = Path.Combine(library, id + ".json");
            string metaPath = Path.Combine(library, "_meta.json");
            string statePath = Path.Combine(profileTest, "desktop-ssh.aes");
            var meta = new JsonObject
            {
                ["appliedId"] = id,
                ["entries"] = new JsonArray(new JsonObject { ["id"] = id, ["name"] = "SafeCode SSH" })
            };
            profile["inferenceGatewayApiKey"] = "__sealed__:v1:fixture-only";
            DesktopKeyAccess.WriteProfileFile(profilePath, JsonSerializer.SerializeToUtf8Bytes(profile));
            DesktopKeyAccess.WriteProfileFile(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta));
            using var bridge = new MacClaudeDesktopBridge(profileTest, false, library);
            var orphan = JsonSerializer.SerializeToElement(await bridge.RefreshAsync(CancellationToken.None));
            if (orphan.GetProperty("oldProfile").GetString() != id)
                throw new Exception("未识别旧版遗留的 SafeCode SSH 配置。");
            meta["hybridPointer"] = "fixture";
            DesktopKeyAccess.WriteProfileFile(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta));
            var protectedProfile = JsonSerializer.SerializeToElement(await bridge.RefreshAsync(CancellationToken.None));
            if (protectedProfile.GetProperty("oldProfile").ValueKind != JsonValueKind.Null)
                throw new Exception("将 Claude 混合模式误判为可替换的旧配置。");
            meta.Remove("hybridPointer");
            DesktopKeyAccess.WriteProfileFile(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta));

            var state = new { Server = new GatewayPreference(), Id = id, Token = "fixture-local-token", Port = 18899 };
            DesktopKeyAccess.WriteProfileFile(statePath, LocalSecrets.Protect(JsonSerializer.SerializeToUtf8Bytes(state)));
            await bridge.StopAsync(CancellationToken.None);
            var after = DesktopKeyAccess.ReadProfileMeta(library);
            if (File.Exists(profilePath) || File.Exists(statePath) || after["appliedId"]?.GetValue<string>() != "")
                throw new Exception("停止 SSH 接入后遗留了加密过的本机配置。");
            if (MacClaudeDesktopBridge.IsSealedGatewayProfile(profile, 18900))
                throw new Exception("密文前缀匹配时也必须校验原端口。");
            Console.WriteLine("MAC_SEALED_SSH_PROFILE_DETECTION_AND_STOP_OK");
        }
        finally { Directory.Delete(profileTest, true); }
        string fingerprint=new('A',64);
        var rdp=MacClients.FreeRdpArguments(new(22119,"fixture",fingerprint),new("workspace","fixture password"),
            new(2560,1600),RdpResizeMode.Dynamic,true);
        if(!rdp.Contains("/cert:deny,fingerprint:sha256:"+fingerprint)||!rdp.Contains("/p:fixture password")||
           !rdp.Contains("/f")||!rdp.Contains("+dynamic-resolution")||rdp.Any(a=>a.StartsWith("/smart-sizing")))
            throw new Exception("Mac RDP 指纹和动态分辨率参数错误。");
        try{MacClients.FreeRdpArguments(new(22119,"fixture",null),new("workspace","fixture password"),new(1920,1080),RdpResizeMode.Scale,false);throw new Exception("Mac RDP 未拦截缺失证书指纹。");}catch(InvalidOperationException){}
        var scaled=MacClients.FreeRdpArguments(new(22119,"fixture",fingerprint),new("workspace","fixture password"),
            new(1920,1080),RdpResizeMode.Scale,false);
        if(!scaled.Contains("/smart-sizing")||scaled.Contains("+dynamic-resolution")||scaled.Contains("/f"))
            throw new Exception("Mac RDP 固定分辨率缩放参数错误。");
        Console.WriteLine("MAC_RDP_PINNED_CERTIFICATE_AND_DISPLAY_MODES_OK");

        // Verify RDP HiDPI display preferences, Retina 5K/14" scaling, SDL3 hints, keyboard modes, and .rdp generation.
        if(!RdpDisplayConstants.IsSupportedResolution(1440,900)||!RdpDisplayConstants.IsSupportedResolution(1512,982))throw new Exception("高分屏分辨率列表缺失。");
        if(!ClientPreferences.Resolutions.Contains(new(1440,900))||!ClientPreferences.Resolutions.Contains(new(1512,982))||!ClientPreferences.Resolutions.Contains(new(2560,1440))||!ClientPreferences.Resolutions.Contains(new(2560,1600)))throw new Exception("客户端分辨率列表不完整。");
        if(!RdpDisplayConstants.IsSupportedScale(100)||!RdpDisplayConstants.IsSupportedScale(150)||!RdpDisplayConstants.IsSupportedScale(200)||RdpDisplayConstants.IsSupportedScale(125))throw new Exception("远端缩放校验异常。");
        string tempDir=Path.Combine(Path.GetTempPath(),"safecode-rdp-selftest-"+Guid.NewGuid().ToString("N"));
        try
        {
            var prefs=new RdpDisplayPreferences(tempDir);
            if(prefs.HasSavedPreferences||prefs.ReadMode()!=RdpDisplayConstants.DefaultMode||prefs.ReadFullscreen()||prefs.ReadRemoteScale()!=RdpDisplayConstants.DefaultRemoteScale||prefs.ReadKeyboardMode()!=RdpDisplayConstants.DefaultKeyboardMode||prefs.ReadClientPreference()!=RdpClientPreference.Auto)throw new Exception("默认 RDP 显示配置异常。");
            prefs.Save(RdpResizeMode.Dynamic,true,200,RdpKeyboardMode.SwapCmdCtrl,RdpClientPreference.WindowsApp);
            if(!prefs.HasSavedPreferences||prefs.ReadMode()!=RdpResizeMode.Dynamic||!prefs.ReadFullscreen()||prefs.ReadRemoteScale()!=200||prefs.ReadKeyboardMode()!=RdpKeyboardMode.SwapCmdCtrl||prefs.ReadClientPreference()!=RdpClientPreference.WindowsApp)throw new Exception("RDP 显示配置持久化往返失败。");
            try{prefs.Save(RdpResizeMode.Scale,false,175);throw new Exception("接受了非法远端缩放值。");}catch(ArgumentException){}
            var env=new Dictionary<string,string?>();
            RdpLaunchBuilder.ApplyFreeRdpEnvironment(env,tempDir,macOsOverride:true);
            string sdlConfigPath=Path.Combine(env["XDG_CONFIG_HOME"]!,"freerdp","sdl-freerdp.json");
            if(!File.Exists(sdlConfigPath))throw new Exception("未生成 sdl-freerdp.json 配置。");
            using var sdlCfg=JsonDocument.Parse(File.ReadAllText(sdlConfigPath));
            var mask=sdlCfg.RootElement.GetProperty("SDL_KeyModMask");
            if(mask.GetArrayLength()!=1||mask[0].GetString()!="SDL_KMOD_NONE")throw new Exception("sdl-freerdp 热键屏蔽配置异常。");
            if(env["SDL_MAC_PRESS_AND_HOLD"]!="0"||env["SDL_MAC_OPTION_AS_ALT"]!="both"||env["SDL_MAC_CTRL_CLICK_EMULATE_RIGHT_CLICK"]!="0"||env["SDL_MAC_SCROLL_MOMENTUM"]!="0"||env["SDL_MOUSE_FOCUS_CLICKTHROUGH"]!="1"||env["SDL_VIDEO_MAC_FULLSCREEN_SPACES"]!="1"||env["SDL_VIDEO_MAC_FULLSCREEN_MENU_VISIBILITY"]!="1"||env["SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS"]!="0")throw new Exception("macOS SDL3 输入优化环境变量缺失。");
        }
        finally{if(Directory.Exists(tempDir))Directory.Delete(tempDir,true);}

        var account=new AccountInfo("workspace","secret-pass-1234");
        var tlsEndpoint=new RemoteDesktopConnection(45123,"0123456789abcdef0123",fingerprint);
        var scaleArgs1x=RdpLaunchBuilder.BuildFreeRdpArguments(tlsEndpoint,account,1440,900,RdpResizeMode.Scale,false,"Safe Code · 远程桌面",100,null,RdpKeyboardMode.CmdToCtrl,macOsOverride:true);
        if(!scaleArgs1x.Contains("/size:1440x900")||!scaleArgs1x.Contains("/bpp:32")||!scaleArgs1x.Contains("/smart-sizing")||scaleArgs1x.Contains("+dynamic-resolution")||!scaleArgs1x.Contains("/clipboard:direction-to:all")||!scaleArgs1x.Contains("+fonts")||!scaleArgs1x.Contains("-grab-keyboard")||!scaleArgs1x.Contains("/kbd:layout:0x00000409,remap:0x15b=0x1d,remap:0x15c=0x1d")||!scaleArgs1x.Contains("/sec:tls")||!scaleArgs1x.Contains("/cert:deny,fingerprint:sha256:"+fingerprint)||scaleArgs1x.Contains("/f"))throw new Exception("macOS CmdToCtrl Scale 模式 FreeRDP 参数错误。");
        var swapArgs=RdpLaunchBuilder.BuildFreeRdpArguments(tlsEndpoint,account,1440,900,RdpResizeMode.Scale,false,"Safe Code · 远程桌面",100,null,RdpKeyboardMode.SwapCmdCtrl,macOsOverride:true);
        if(!swapArgs.Contains("/kbd:layout:0x00000409,remap:0x15b=0x1d,remap:0x15c=0x1d,remap:0x1d=0x15b"))throw new Exception("SwapCmdCtrl 键盘映射参数错误。");
        var stdArgs=RdpLaunchBuilder.BuildFreeRdpArguments(tlsEndpoint,account,1440,900,RdpResizeMode.Scale,false,"Safe Code · 远程桌面",100,null,RdpKeyboardMode.Standard,macOsOverride:true);
        if(!stdArgs.Contains("/kbd:layout:0x00000409")||stdArgs.Any(a=>a.Contains("remap:")))throw new Exception("Standard 键盘模式不应含 remap。");
        var display5k=new HostDisplayMetrics(2560,1440,5120,2880,2.0);
        var scaleArgs5k=RdpLaunchBuilder.BuildFreeRdpArguments(tlsEndpoint,account,1440,900,RdpResizeMode.Scale,false,"Safe Code · 远程桌面",100,display5k,RdpKeyboardMode.CmdToCtrl,macOsOverride:true);
        if(!scaleArgs5k.Contains("/size:2880x1800")||!scaleArgs5k.Contains("/smart-sizing:1440x900")||scaleArgs5k.Contains("+dynamic-resolution"))throw new Exception("5K Retina Scale 模式 FreeRDP 参数未按 2x 放大窗口。");
        var display14=new HostDisplayMetrics(1512,982,3024,1964,2.0);
        var scaleArgs14=RdpLaunchBuilder.BuildFreeRdpArguments(tlsEndpoint,account,1440,900,RdpResizeMode.Scale,false,"Safe Code · 远程桌面",100,display14);
        if(!scaleArgs14.Contains("/smart-sizing:1440x900")||scaleArgs14.Contains("/size:2880x1800"))throw new Exception("14 寸 Retina 屏幕未按可用边界安全限幅。");
        var dynArgs200=RdpLaunchBuilder.BuildFreeRdpArguments(tlsEndpoint,account,1440,900,RdpResizeMode.Dynamic,false,"Safe Code · 远程桌面",200,display5k);
        if(!dynArgs200.Contains("/size:2880x1800")||!dynArgs200.Contains("+dynamic-resolution")||dynArgs200.Any(a=>a.StartsWith("/smart-sizing")))throw new Exception("Retina Dynamic 200% 模式 FreeRDP 物理尺寸错误。");
        var dynArgs=RdpLaunchBuilder.BuildFreeRdpArguments(new RemoteDesktopConnection(45123,"0123456789abcdef0123",null),account,1512,982,RdpResizeMode.Dynamic,true);
        if(!dynArgs.Contains("+dynamic-resolution")||dynArgs.Contains("/smart-sizing")||!dynArgs.Contains("/f")||!dynArgs.Contains("/cert:ignore"))throw new Exception("Dynamic 模式 FreeRDP 参数错误。");
        if(OperatingSystem.IsMacOS()&&HostDisplayMetrics.DetectPrimary() is not { LogicalWidth: > 0, PhysicalWidth: > 0, ScaleFactor: >= 1.0 })throw new Exception("macOS 主显示器缩放探测失败。");
        try{RdpLaunchBuilder.BuildFreeRdpArguments(tlsEndpoint,new AccountInfo("bad\nuser","p"),1440,900,RdpResizeMode.Scale,false);throw new Exception("未拦截含换行符凭据。");}catch(InvalidOperationException){}
        string rdpText=RdpLaunchBuilder.BuildRdpFileContent(tlsEndpoint,account,1440,900,RdpResizeMode.Scale,true);
        if(!rdpText.Contains("smart sizing:i:1")||!rdpText.Contains("dynamic resolution:i:0")||!rdpText.Contains("screen mode id:i:2")||!rdpText.Contains("desktopwidth:i:1440"))throw new Exception(".rdp 文件生成内容错误。");

        Console.WriteLine("MAC_AES_GCM_TAMPER_SHELL_QUOTING_AND_PLATFORM_UPDATE_SELECTION_OK");
        string folder=Directory.CreateTempSubdirectory("safecode-host-resume-").FullName;
        try
        {
            var saved=new GatewayPreference(true,"vps.example",2222,"operator","SHA256:fixture");
            var settings=new ConnectionSettings("vps.example","operator","fixture",2222);
            string preference=Path.Combine(folder,"host-control-resume.aes");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new{Server=saved,WorkspaceUser="work",Alias="fixture",Client=new string('a',20)});
            DesktopKeyAccess.WriteProfileFile(preference,LocalSecrets.Protect(bytes));
            using var host=new MacHostControl(folder);
            if(!host.CanResume(settings)||host.CanResume(settings with{Port=22})||
                host.CanResume(settings with{Username="another"})||host.CanResume(settings with{Host="different.example"})||
                saved.Matches(settings,"SHA256:different"))
                throw new Exception("本机控制的恢复范围有误。");
            await host.SuspendAsync(CancellationToken.None);
            if(!host.CanResume(settings))throw new Exception("安全退出丢失本机控制恢复设置。");
            await host.DisableAsync(CancellationToken.None);
            if(host.CanResume(settings)||File.Exists(preference))throw new Exception("停用后保留了本机控制授权。");
            Console.WriteLine("MAC_HOST_CONTROL_RESUME_SCOPE_AND_OPT_OUT_OK");
        }
        finally{Directory.Delete(folder,true);}
    }
}
