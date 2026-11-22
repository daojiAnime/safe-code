using System.Runtime.InteropServices;
using System.Text.Json;

namespace SafeCode.Core;

public enum RdpResizeMode
{
    Original,
    Scale,
    Dynamic
}

public enum RdpKeyboardMode
{
    CmdToCtrl,
    SwapCmdCtrl,
    Standard
}

public enum RdpClientPreference
{
    Auto,
    FreeRdp,
    WindowsApp
}

public readonly record struct HostDisplayMetrics(
    int LogicalWidth,
    int LogicalHeight,
    int PhysicalWidth,
    int PhysicalHeight,
    double ScaleFactor)
{
    public static HostDisplayMetrics? DetectPrimary()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            uint displayId = CGMainDisplayID();
            if (displayId == 0) return null;
            IntPtr mode = CGDisplayCopyDisplayMode(displayId);
            if (mode == IntPtr.Zero) return null;
            try
            {
                int logicalW = checked((int)CGDisplayModeGetWidth(mode));
                int logicalH = checked((int)CGDisplayModeGetHeight(mode));
                int physicalW = checked((int)CGDisplayModeGetPixelWidth(mode));
                int physicalH = checked((int)CGDisplayModeGetPixelHeight(mode));
                if (logicalW <= 0 || logicalH <= 0 || physicalW <= 0 || physicalH <= 0) return null;
                double scale = Math.Max(1.0, (double)physicalW / logicalW);
                return new(logicalW, logicalH, physicalW, physicalH, scale);
            }
            finally
            {
                CGDisplayModeRelease(mode);
            }
        }
        catch
        {
            return null;
        }
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetHeight(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern void CGDisplayModeRelease(IntPtr mode);
}

public sealed record DesktopDisplayConfig(
    int Width,
    int Height,
    RdpResizeMode DisplayMode,
    bool Fullscreen,
    int RemoteScale);

public static class RdpDisplayConstants
{
    public static readonly (int Width, int Height)[] SupportedResolutions =
    [
        (1024, 768),
        (1280, 800),
        (1366, 768),
        (1440, 900),
        (1512, 982),
        (1600, 900),
        (1920, 1080)
    ];

    public static readonly int[] SupportedScales = [100, 150, 200];

    public static (int Width, int Height) DefaultResolution =>
        OperatingSystem.IsMacOS() ? (1440, 900) : (1280, 800);

    public static bool IsRetinaDisplay =>
        OperatingSystem.IsMacOS() && HostDisplayMetrics.DetectPrimary() is { ScaleFactor: >= 1.5 };

    public static bool IsSupportedResolution(int width, int height) =>
        Array.Exists(SupportedResolutions, item => item.Width == width && item.Height == height);

    public static bool IsSupportedScale(int scale) =>
        Array.IndexOf(SupportedScales, scale) >= 0;

    public static RdpResizeMode ParseMode(string? raw, RdpResizeMode fallback) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "original" => RdpResizeMode.Original,
            "scale" => RdpResizeMode.Scale,
            "dynamic" => RdpResizeMode.Dynamic,
            _ => fallback
        };

    // On macOS Retina displays, default to native 1:1 physical pixels (Dynamic + 200% remote HiDPI)
    // so text and Chrome render razor-sharp with zero 2x linear-upscaling blur.
    public static RdpResizeMode DefaultMode =>
        IsRetinaDisplay
            ? RdpResizeMode.Dynamic
            : (OperatingSystem.IsMacOS() ? RdpResizeMode.Scale : RdpResizeMode.Original);

    public static int DefaultRemoteScale =>
        IsRetinaDisplay ? 200 : 100;

    public static RdpKeyboardMode DefaultKeyboardMode =>
        OperatingSystem.IsMacOS() ? RdpKeyboardMode.CmdToCtrl : RdpKeyboardMode.Standard;

    public static RdpClientPreference DefaultClientPreference =>
        RdpClientPreference.Auto;

    public static bool TryParseKeyboardMode(string? raw, out RdpKeyboardMode mode)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "cmdtoctrl":
                mode = RdpKeyboardMode.CmdToCtrl;
                return true;
            case "swapcmdctrl":
                mode = RdpKeyboardMode.SwapCmdCtrl;
                return true;
            case "standard":
                mode = RdpKeyboardMode.Standard;
                return true;
            default:
                mode = DefaultKeyboardMode;
                return false;
        }
    }

    public static RdpKeyboardMode ParseKeyboardMode(string? raw, RdpKeyboardMode fallback) =>
        TryParseKeyboardMode(raw, out var parsed) ? parsed : fallback;

    public static bool TryParseClientPreference(string? raw, out RdpClientPreference pref)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "auto":
                pref = RdpClientPreference.Auto;
                return true;
            case "freerdp":
                pref = RdpClientPreference.FreeRdp;
                return true;
            case "windowsapp":
                pref = RdpClientPreference.WindowsApp;
                return true;
            default:
                pref = DefaultClientPreference;
                return false;
        }
    }

    public static RdpClientPreference ParseClientPreference(string? raw, RdpClientPreference fallback) =>
        TryParseClientPreference(raw, out var parsed) ? parsed : fallback;
}

public sealed class RdpDisplayPreferences(string directory)
{
    private const string FileName = "desktop-display.json";

    private sealed record StoredDisplayPreferences(
        string? DisplayMode,
        bool? Fullscreen,
        int? RemoteScale,
        string? KeyboardMode = null,
        string? ClientPreference = null);

    private StoredDisplayPreferences? ReadStored()
    {
        try
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StoredDisplayPreferences>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool HasSavedPreferences => File.Exists(Path.Combine(directory, FileName));

    public RdpResizeMode ReadMode() =>
        RdpDisplayConstants.ParseMode(ReadStored()?.DisplayMode, RdpDisplayConstants.DefaultMode);

    public bool ReadFullscreen() =>
        ReadStored()?.Fullscreen ?? false;

    public int ReadRemoteScale()
    {
        int? stored = ReadStored()?.RemoteScale;
        return stored is int scale && RdpDisplayConstants.IsSupportedScale(scale)
            ? scale
            : RdpDisplayConstants.DefaultRemoteScale;
    }

    public RdpKeyboardMode ReadKeyboardMode() =>
        RdpDisplayConstants.ParseKeyboardMode(ReadStored()?.KeyboardMode, RdpDisplayConstants.DefaultKeyboardMode);

    public RdpClientPreference ReadClientPreference() =>
        RdpDisplayConstants.ParseClientPreference(ReadStored()?.ClientPreference, RdpDisplayConstants.DefaultClientPreference);

    public void Save(RdpResizeMode mode, bool fullscreen, int remoteScale) =>
        Save(mode, fullscreen, remoteScale, ReadKeyboardMode(), ReadClientPreference());

    public void Save(
        RdpResizeMode mode,
        bool fullscreen,
        int remoteScale,
        RdpKeyboardMode keyboardMode,
        RdpClientPreference clientPreference)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentException("请选择支持的显示模式。", nameof(mode));
        if (!RdpDisplayConstants.IsSupportedScale(remoteScale))
            throw new ArgumentException("请选择支持的远端缩放比例（100%、150% 或 200%）。", nameof(remoteScale));
        if (!Enum.IsDefined(keyboardMode))
            throw new ArgumentException("请选择支持的键盘映射模式。", nameof(keyboardMode));
        if (!Enum.IsDefined(clientPreference))
            throw new ArgumentException("请选择支持的远程桌面客户端。", nameof(clientPreference));

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        var payload = new StoredDisplayPreferences(
            mode.ToString(),
            fullscreen,
            remoteScale,
            keyboardMode.ToString(),
            clientPreference.ToString());
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(payload, JsonDefaults.Options));
        File.Move(path + ".new", path, true);
    }
}

public static class RdpLaunchBuilder
{
    private static void RejectControlChars(string value, string fieldName)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException($"{fieldName}包含无效的控制字符。");
    }

    private static int Even(int value) => Math.Max(2, value - (value % 2));

    private static (int Width, int Height) FitToDisplay(int targetW, int targetH, HostDisplayMetrics? display)
    {
        int w = Even(targetW);
        int h = Even(targetH);
        if (display is not { PhysicalWidth: > 0, PhysicalHeight: > 0 } metrics)
            return (w, h);

        int maxW = Even((int)(metrics.PhysicalWidth * 0.92));
        int maxH = Even((int)(metrics.PhysicalHeight * 0.88));
        if (w <= maxW && h <= maxH)
            return (w, h);

        double ratio = Math.Min((double)maxW / w, (double)maxH / h);
        return (Even((int)Math.Floor(w * ratio)), Even((int)Math.Floor(h * ratio)));
    }

    public static string[] BuildFreeRdpArguments(
        RemoteDesktopConnection endpoint,
        AccountInfo account,
        int width,
        int height,
        RdpResizeMode mode,
        bool fullscreen,
        string windowTitle = "Safe Code · 远程桌面",
        int remoteScale = 100,
        HostDisplayMetrics? hostDisplay = null,
        RdpKeyboardMode? keyboardMode = null,
        bool? macOsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(account.Username);
        ArgumentNullException.ThrowIfNull(account.Password);
        ArgumentNullException.ThrowIfNull(windowTitle);

        RejectControlChars(account.Username, "用户名");
        RejectControlChars(account.Password, "密码");
        RejectControlChars(windowTitle, "窗口标题");

        bool isMacOs = macOsOverride ?? OperatingSystem.IsMacOS();
        RdpKeyboardMode effectiveKbd = keyboardMode ?? (isMacOs ? RdpKeyboardMode.CmdToCtrl : RdpKeyboardMode.Standard);

        int sizeW = width;
        int sizeH = height;
        string? smartSizingArg = null;

        if (mode == RdpResizeMode.Scale)
        {
            // FreeRDP SDL3 creates a high_pixel_density window and divides /size by the
            // macOS display backing scale (SdlWindow::resizeToPixelSize). When /smart-sizing:WxH
            // is supplied, FreeRDP uses /size for the local window's physical pixels and
            // /smart-sizing:WxH as the remote desktop resolution — giving exact logical-point
            // window sizing with 2x integer-block scaling on Retina displays.
            if (!fullscreen && hostDisplay is { ScaleFactor: > 1.05 } retina)
            {
                (sizeW, sizeH) = FitToDisplay(
                    (int)Math.Round(width * retina.ScaleFactor),
                    (int)Math.Round(height * retina.ScaleFactor),
                    retina);
                smartSizingArg = $"/smart-sizing:{width}x{height}";
            }
            else
            {
                smartSizingArg = "/smart-sizing";
            }
        }
        else if (mode == RdpResizeMode.Dynamic && !fullscreen && remoteScale > 100)
        {
            double factor = remoteScale / 100.0;
            (sizeW, sizeH) = FitToDisplay(
                (int)Math.Round(width * factor),
                (int)Math.Round(height * factor),
                hostDisplay);
        }

        List<string> args =
        [
            $"/v:127.0.0.1:{endpoint.Port}",
            "/u:" + account.Username,
            "/p:" + account.Password,
            $"/size:{sizeW}x{sizeH}",
            "/bpp:32",
            "/clipboard:direction-to:all",
            "+fonts",
            "/network:auto",
            "/t:" + windowTitle
        ];

        if (isMacOs)
        {
            // Prevent SDL3 keyboard grab from trapping focus or leaving stuck modifiers
            // across macOS Spaces / Cmd+Tab switches; normal keys still forward cleanly.
            args.Add("-grab-keyboard");

            // Explicit US ANSI layout (0x00000409) avoids locale-mismatch scancode errors
            // with Debian xrdp, and FreeRDP 3.24 requires unpadded hex scancodes (0x15b, 0x15c, 0x1d).
            string kbdArg = effectiveKbd switch
            {
                RdpKeyboardMode.CmdToCtrl => "/kbd:layout:0x00000409,remap:0x15b=0x1d,remap:0x15c=0x1d",
                RdpKeyboardMode.SwapCmdCtrl => "/kbd:layout:0x00000409,remap:0x15b=0x1d,remap:0x15c=0x1d,remap:0x1d=0x15b",
                _ => "/kbd:layout:0x00000409"
            };
            args.Add(kbdArg);
        }
        else if (effectiveKbd != RdpKeyboardMode.Standard)
        {
            string kbdArg = effectiveKbd == RdpKeyboardMode.SwapCmdCtrl
                ? "/kbd:remap:0x15b=0x1d,remap:0x15c=0x1d,remap:0x1d=0x15b"
                : "/kbd:remap:0x15b=0x1d,remap:0x15c=0x1d";
            args.Add(kbdArg);
        }

        if (endpoint.CertificateFingerprint is { Length: 64 } fingerprint)
        {
            RejectControlChars(fingerprint, "证书指纹");
            args.Add("/sec:tls");
            args.Add("/cert:deny,fingerprint:sha256:" + fingerprint);
        }
        else
        {
            args.Add("/cert:ignore");
        }

        if (fullscreen)
            args.Add("/f");

        if (smartSizingArg != null)
            args.Add(smartSizingArg);
        else if (mode == RdpResizeMode.Dynamic)
            args.Add("+dynamic-resolution");

        return [.. args];
    }

    public static string EnsureFreeRdpConfigDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        string xdgConfigHome = Path.Combine(dataDirectory, "freerdp-xdg");
        string freerdpDir = Path.Combine(xdgConfigHome, "freerdp");
        Directory.CreateDirectory(freerdpDir);
        string configPath = Path.Combine(freerdpDir, "sdl-freerdp.json");
        // FreeRDP 3 SDL3 defaults SDL_KeyModMask to KMOD_RSHIFT, causing Right Shift + D/M/R/G/Enter
        // to disconnect, minimize, or toggle window state while typing uppercase letters.
        // Setting SDL_KMOD_NONE cleanly disables SDL client hotkey hijacking with zero warnings.
        const string content = "{\n  \"SDL_KeyModMask\": [\"SDL_KMOD_NONE\"]\n}\n";
        if (!File.Exists(configPath) || File.ReadAllText(configPath) != content)
        {
            string tempPath = configPath + ".new";
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, configPath, true);
        }
        return xdgConfigHome;
    }

    public static void ApplyFreeRdpEnvironment(
        IDictionary<string, string?> environment,
        string dataDirectory,
        bool? macOsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        environment["XDG_CONFIG_HOME"] = EnsureFreeRdpConfigDirectory(dataDirectory);

        if (!(macOsOverride ?? OperatingSystem.IsMacOS()))
            return;

        // Official SDL3 macOS input & windowing hints (verified against SDL3 SDL_hints.h):
        // - Disable Press-and-Hold accent popup so holding keys repeats normally in editors/terminals.
        environment["SDL_MAC_PRESS_AND_HOLD"] = "0";
        // - Treat both Option keys as Alt for Linux shortcuts without macOS diacritic composition.
        environment["SDL_MAC_OPTION_AS_ALT"] = "both";
        // - Keep Ctrl+Click as true Ctrl+Click (multi-select, open-in-tab, go-to-definition).
        environment["SDL_MAC_CTRL_CLICK_EMULATE_RIGHT_CLICK"] = "0";
        // - Prevent macOS trackpad inertial scroll momentum from flooding remote RDP wheel events.
        environment["SDL_MAC_SCROLL_MOMENTUM"] = "0";
        // - Deliver first click immediately when refocusing the remote desktop window.
        environment["SDL_MOUSE_FOCUS_CLICKTHROUGH"] = "1";
        // - Use native macOS fullscreen Spaces and keep top menu bar reachable on hover.
        environment["SDL_VIDEO_MAC_FULLSCREEN_SPACES"] = "1";
        environment["SDL_VIDEO_MAC_FULLSCREEN_MENU_VISIBILITY"] = "1";
        // - Do not auto-minimize fullscreen remote desktop on Cmd+Tab / focus loss.
        environment["SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS"] = "0";
    }

    public static string BuildRdpFileContent(
        RemoteDesktopConnection endpoint,
        AccountInfo account,
        int width,
        int height,
        RdpResizeMode mode,
        bool fullscreen)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(account.Username);

        RejectControlChars(account.Username, "用户名");

        string[] lines =
        [
            $"full address:s:127.0.0.1:{endpoint.Port}",
            $"username:s:{account.Username}",
            "prompt for credentials:i:1",
            $"screen mode id:i:{(fullscreen ? 2 : 1)}",
            $"dynamic resolution:i:{(mode == RdpResizeMode.Dynamic ? 1 : 0)}",
            $"smart sizing:i:{(mode == RdpResizeMode.Scale ? 1 : 0)}",
            $"desktopwidth:i:{width}",
            $"desktopheight:i:{height}",
            "authentication level:i:2",
            "redirectclipboard:i:1",
            "bandwidthautodetect:i:1",
            "networkautodetect:i:1"
        ];

        return string.Join("\r\n", lines) + "\r\n";
    }
}
