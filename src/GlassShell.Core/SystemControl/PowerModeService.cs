using System;
using System.Runtime.InteropServices;

namespace GlassShell.Core.SystemControl;

public enum WindowsPowerMode
{
    BestPowerEfficiency,
    Balanced,
    BestPerformance
}

public sealed record BatteryDetailState(
    bool HasBattery,
    int Percent,
    bool IsCharging,
    int RemainingMinutes,
    WindowsPowerMode PowerMode);

/// <summary>
/// Reads battery status and reads/switches Windows 11 Power Mode (最佳能效 / 平衡 / 最佳性能)
/// using official powrprof.dll overlay & power scheme APIs.
/// </summary>
public sealed class PowerModeService
{
    // Windows 10/11 official Power Overlay Scheme GUIDs
    private static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid OverlayBalanced = Guid.Empty;
    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    // Fallback classic Power Plan GUIDs
    private static readonly Guid PlanPowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid PlanBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid PlanHighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public BatteryDetailState GetBatteryDetail()
    {
        bool hasBat = false;
        int pct = 100;
        bool charging = false;
        int remainMin = -1;

        if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps))
        {
            hasBat = sps.BatteryFlag != 128 && sps.BatteryFlag != 255;
            pct = sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : 100;
            charging = sps.ACLineStatus == 1;
            if (!charging && sps.BatteryLifeTime != uint.MaxValue && sps.BatteryLifeTime > 0)
            {
                remainMin = (int)(sps.BatteryLifeTime / 60);
            }
        }

        return new BatteryDetailState(hasBat, pct, charging, remainMin, GetCurrentPowerMode());
    }

    public WindowsPowerMode GetCurrentPowerMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out Guid overlay) == 0)
            {
                if (overlay == OverlayBestEfficiency) return WindowsPowerMode.BestPowerEfficiency;
                if (overlay == OverlayBestPerformance) return WindowsPowerMode.BestPerformance;
                return WindowsPowerMode.Balanced;
            }
        }
        catch { }

        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out nint ptr) == 0 && ptr != 0)
            {
                try
                {
                    var guid = Marshal.PtrToStructure<Guid>(ptr);
                    if (guid == PlanPowerSaver) return WindowsPowerMode.BestPowerEfficiency;
                    if (guid == PlanHighPerformance) return WindowsPowerMode.BestPerformance;
                }
                finally
                {
                    LocalFree(ptr);
                }
            }
        }
        catch { }

        return WindowsPowerMode.Balanced;
    }

    public bool SetPowerMode(WindowsPowerMode mode)
    {
        Guid overlayGuid = mode switch
        {
            WindowsPowerMode.BestPowerEfficiency => OverlayBestEfficiency,
            WindowsPowerMode.BestPerformance => OverlayBestPerformance,
            _ => OverlayBalanced
        };

        try
        {
            if (PowerSetActiveOverlayScheme(overlayGuid) == 0)
            {
                return true;
            }
        }
        catch { }

        Guid planGuid = mode switch
        {
            WindowsPowerMode.BestPowerEfficiency => PlanPowerSaver,
            WindowsPowerMode.BestPerformance => PlanHighPerformance,
            _ => PlanBalanced
        };

        try
        {
            return PowerSetActiveScheme(IntPtr.Zero, ref planGuid) == 0;
        }
        catch
        {
            return false;
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

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint hMem);
}
