using System;
using System.IO;
using GlassShell.Core.Config;
using GlassShell.Core.Diagnostics;
using GlassShell.Core.Glass;
using GlassShell.Core.Shell;
using Xunit;

namespace GlassShell.Core.Tests.Diagnostics;

public sealed class Phase3ServicesTests
{
    [Fact]
    public void AutoStartService_QuotesExecutablePathCleanly()
    {
        Assert.Equal("\"C:\\Program Files\\GlassShell\\GlassShell.UI.exe\"", AutoStartService.BuildRunCommand(@"C:\Program Files\GlassShell\GlassShell.UI.exe"));
        Assert.Equal("\"D:\\App\\GlassShell.UI.exe\"", AutoStartService.BuildRunCommand(" \"D:\\App\\GlassShell.UI.exe\" "));
        Assert.Equal(string.Empty, AutoStartService.BuildRunCommand("   "));
    }

    [Fact]
    public void DiagnosticsReportBuilder_IncludesVersionMonitorsSettingsAndCrashTail()
    {
        string tempLog = Path.Combine(Path.GetTempPath(), $"GlassShell_Diag_{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllLines(tempLog, ["[2026-11-24 10:00:00] SampleEntry1", "[2026-11-24 10:01:00] SampleEntry2"]);
            var monitor = new MonitorContext(
                MonitorHandle: new nint(1),
                Bounds: new ScreenRect(0, 0, 2560, 1440),
                DpiScale: 1.5,
                IsPrimary: true,
                WorkArea: new ScreenRect(0, 28, 2560, 1440));

            string report = DiagnosticsReportBuilder.BuildReport(
                GlassSettings.Default with { HarmonizeDockIcons = true },
                [monitor],
                SamplerHealth.Ready,
                tempLog);

            Assert.Contains($"v{DiagnosticsReportBuilder.AppVersion}", report);
            Assert.Contains("2560x1440", report);
            Assert.Contains("1.50x", report);
            Assert.Contains("\"HarmonizeDockIcons\": true", report);
            Assert.Contains("SampleEntry2", report);
        }
        finally
        {
            if (File.Exists(tempLog)) File.Delete(tempLog);
        }
    }
}
