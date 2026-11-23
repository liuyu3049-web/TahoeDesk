using System;
using GlassShell.Core.SystemStatus;
using Xunit;

namespace GlassShell.Core.Tests.SystemStatus;

public sealed class SystemStatusProviderTests
{
    [Fact]
    public void FormatMacZhCn_MatchesSpecFormat()
    {
        var dt = new DateTime(2026, 11, 23, 10, 3, 0); // Monday
        Assert.Equal("11月23日 周一 10:03", ClockFormatter.FormatMacZhCn(dt));
    }

    [Fact]
    public void FormatMacZhCn_HandlesSunday()
    {
        var dt = new DateTime(2026, 11, 22, 23, 59, 0); // Sunday
        Assert.Equal("11月22日 周日 23:59", ClockFormatter.FormatMacZhCn(dt));
    }

    [Fact]
    public void BatteryStatus_NoSystemBatteryFlag_SetsHasBatteryFalse()
    {
        var status = SystemStatusProvider.ParseBatteryStatus(batteryFlag: 128, lifePercent: 255, acLineStatus: 1);
        Assert.False(status.HasBattery);
        Assert.Equal(0, status.BatteryPercent);
    }

    [Fact]
    public void BatteryStatus_ChargingLaptop_ReportsPercentAndCharging()
    {
        var status = SystemStatusProvider.ParseBatteryStatus(batteryFlag: 8, lifePercent: 84, acLineStatus: 1);
        Assert.True(status.HasBattery);
        Assert.Equal(84, status.BatteryPercent);
        Assert.True(status.IsCharging);
    }

    [Fact]
    public void InputLanguageMapping_ResolvesChineseAndEnglishBadges()
    {
        Assert.Equal("拼", SystemStatusProvider.MapLangIdToBadge(0x0804));
        Assert.Equal("英", SystemStatusProvider.MapLangIdToBadge(0x0409));
    }
}
