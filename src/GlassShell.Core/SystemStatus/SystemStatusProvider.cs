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
    public SystemStatusSnapshot Capture(nint foregroundHwnd = 0)
    {
        var battery = QueryBattery();
        string ime = QueryInputMethodBadge(foregroundHwnd);
        bool network = NetworkInterface.GetIsNetworkAvailable();
        string clock = ClockFormatter.FormatMacZhCn(DateTime.Now);

        return new SystemStatusSnapshot(
            InputMethodLabel: ime,
            HasBattery: battery.HasBattery,
            BatteryPercent: battery.BatteryPercent,
            IsCharging: battery.IsCharging,
            IsNetworkConnected: network,
            FormattedClock: clock);
    }

    public static BatteryStatus ParseBatteryStatus(byte batteryFlag, byte lifePercent, byte acLineStatus)
    {
        // 128 = No system battery; 255 = Unknown status
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
            0x04 => "拼", // Chinese
            0x09 => "英", // English
            0x11 => "あ", // Japanese
            0x12 => "한", // Korean
            _ => "A"
        };
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
            nint target = foregroundHwnd != 0 ? foregroundHwnd : GetForegroundWindow();
            uint threadId = target != 0 ? GetWindowThreadProcessId(target, out _) : 0;
            nint hkl = GetKeyboardLayout(threadId);
            int langId = (int)((long)hkl & 0xFFFF);
            return MapLangIdToBadge(langId);
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
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint idThread);
}
