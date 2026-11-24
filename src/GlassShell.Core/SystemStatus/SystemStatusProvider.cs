using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace GlassShell.Core.SystemStatus;

public static class ClockFormatter
{
    private static readonly string[] ChineseDays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public static string FormatMacZhCn(DateTime localTime)
    {
        string dow = ChineseDays[(int)localTime.DayOfWeek];
        return $"{localTime.Month}月{localTime.Day}日 {dow} {localTime:HH:mm}";
    }
}

public sealed class SystemStatusProvider
{
    private const int VK_CAPITAL = 0x14;
    private const uint WM_IME_CONTROL = 0x0283;
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const nint IMC_GETCONVERSIONMODE = 0x0001;
    private const nint IMC_SETCONVERSIONMODE = 0x0002;
    private const nint IMC_GETOPENSTATUS = 0x0005;
    private const nint IMC_SETOPENSTATUS = 0x0006;
    private const nint IME_CMODE_NATIVE = 0x0001;
    private const nint INPUTLANGCHANGE_FORWARD = 0x0002;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private long _lastNetworkCheckTicks;
    private bool _cachedNetworkAvailable = true;

    public SystemStatusSnapshot Capture(nint foregroundHwnd = 0)
    {
        var battery = QueryBattery();
        string ime = QueryInputMethodBadge(foregroundHwnd);
        long now = Environment.TickCount64;
        if (now - _lastNetworkCheckTicks >= 5000 || _lastNetworkCheckTicks == 0)
        {
            _lastNetworkCheckTicks = now;
            _cachedNetworkAvailable = NetworkInterface.GetIsNetworkAvailable();
        }
        string clock = ClockFormatter.FormatMacZhCn(DateTime.Now);

        return new SystemStatusSnapshot(
            InputMethodLabel: ime,
            HasBattery: battery.HasBattery,
            BatteryPercent: battery.BatteryPercent,
            IsCharging: battery.IsCharging,
            IsNetworkConnected: _cachedNetworkAvailable,
            FormattedClock: clock);
    }

    public string CaptureImeBadgeOnly(nint foregroundHwnd = 0) => QueryInputMethodBadge(foregroundHwnd);

    public static BatteryStatus ParseBatteryStatus(byte batteryFlag, byte lifePercent, byte acLineStatus)
    {
        if ((batteryFlag & 128) != 0 || batteryFlag == 255 || lifePercent > 100)
        {
            return new BatteryStatus(false, 0, false);
        }

        bool charging = acLineStatus == 1 || (batteryFlag & 8) != 0;
        return new BatteryStatus(true, lifePercent, charging);
    }

    public static string MapLangIdToBadge(int langId)
    {
        int primaryLang = langId & 0x03FF;
        return primaryLang switch
        {
            0x04 => "拼",
            0x09 => "英",
            0x11 => "あ",
            0x12 => "한",
            _ => "A"
        };
    }

    public static string ResolveImeBadge(int langId, bool capsLockOn, bool? imeChineseNativeMode)
    {
        if (capsLockOn)
        {
            return "⇪A";
        }

        int primaryLang = langId & 0x03FF;
        if (primaryLang == 0x04)
        {
            if (imeChineseNativeMode.HasValue)
            {
                return imeChineseNativeMode.Value ? "中" : "英";
            }
            return "中";
        }

        return MapLangIdToBadge(langId);
    }

    /// <summary>
    /// Toggles Chinese/English conversion mode inside the active Chinese IME (e.g. Microsoft Pinyin / Sogou),
    /// or cycles Windows keyboard layout if a non-Chinese layout is active.
    /// </summary>
    public static void ToggleChineseEnglishOrLayout(nint targetHwnd)
    {
        try
        {
            nint target = targetHwnd != 0 ? targetHwnd : GetForegroundWindow();
            if (target == 0) return;

            uint threadId = GetWindowThreadProcessId(target, out _);
            nint hkl = GetKeyboardLayout(threadId);
            int primaryLang = ((int)((long)hkl & 0xFFFF)) & 0x03FF;

            if (primaryLang == 0x04)
            {
                nint imeWnd = ImmGetDefaultIMEWnd(target);
                if (imeWnd != 0)
                {
                    bool okOpen = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0, SMTO_ABORTIFHUNG, 50, out nint openStatus) != 0;
                    bool okConv = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, IMC_GETCONVERSIONMODE, 0, SMTO_ABORTIFHUNG, 50, out nint convMode) != 0;
                    if (okOpen && okConv)
                    {
                        bool currentlyChinese = openStatus != 0 && (convMode & IME_CMODE_NATIVE) != 0;
                        if (currentlyChinese)
                        {
                            nint engMode = convMode & ~IME_CMODE_NATIVE;
                            SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, IMC_SETCONVERSIONMODE, engMode, SMTO_ABORTIFHUNG, 50, out _);
                        }
                        else
                        {
                            SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, IMC_SETOPENSTATUS, 1, SMTO_ABORTIFHUNG, 50, out _);
                            nint zhMode = convMode | IME_CMODE_NATIVE;
                            SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, IMC_SETCONVERSIONMODE, zhMode, SMTO_ABORTIFHUNG, 50, out _);
                        }
                        return;
                    }
                }
            }

            SwitchKeyboardLayout(target);
        }
        catch { }
    }

    public static void SwitchKeyboardLayout(nint targetHwnd)
    {
        try
        {
            nint target = targetHwnd != 0 ? targetHwnd : GetForegroundWindow();
            if (target != 0)
            {
                PostMessageW(target, WM_INPUTLANGCHANGEREQUEST, INPUTLANGCHANGE_FORWARD, 0);
            }
        }
        catch { }
    }

    private static BatteryStatus QueryBattery()
    {
        if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS s))
        {
            return ParseBatteryStatus(s.BatteryFlag, s.BatteryLifePercent, s.ACLineStatus);
        }

        return new BatteryStatus(false, 0, false);
    }

    private static string QueryInputMethodBadge(nint foregroundHwnd)
    {
        try
        {
            bool capsOn = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
            nint target = foregroundHwnd != 0 ? foregroundHwnd : GetForegroundWindow();
            uint threadId = target != 0 ? GetWindowThreadProcessId(target, out _) : 0;
            nint hkl = GetKeyboardLayout(threadId);
            int langId = (int)((long)hkl & 0xFFFF);

            bool? nativeChinese = null;
            if ((langId & 0x03FF) == 0x04 && target != 0)
            {
                nint imeWnd = ImmGetDefaultIMEWnd(target);
                if (imeWnd != 0)
                {
                    bool okOpen = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0, SMTO_ABORTIFHUNG, 40, out nint openStatus) != 0;
                    bool okConv = SendMessageTimeoutW(imeWnd, WM_IME_CONTROL, IMC_GETCONVERSIONMODE, 0, SMTO_ABORTIFHUNG, 40, out nint convMode) != 0;
                    if (okOpen && okConv)
                    {
                        nativeChinese = openStatus != 0 && (convMode & IME_CMODE_NATIVE) != 0;
                    }
                }
            }

            return ResolveImeBadge(langId, capsOn, nativeChinese);
        }
        catch
        {
            return "英";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("imm32.dll")]
    private static extern nint ImmGetDefaultIMEWnd(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
