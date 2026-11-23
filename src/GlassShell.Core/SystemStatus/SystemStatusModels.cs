namespace GlassShell.Core.SystemStatus;

public readonly record struct BatteryStatus(bool HasBattery, int BatteryPercent, bool IsCharging);

public sealed record SystemStatusSnapshot(
    string InputMethodLabel,
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    bool IsNetworkConnected,
    string FormattedClock);
