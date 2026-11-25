using System;
using System.Collections.Generic;
using System.Linq;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using Xunit;

namespace GlassShell.Core.Tests.Discovery;

public sealed class TrayResidentDockTests
{
    [Fact]
    public void BuildDockItems_KeepsClosedToTrayAppsRunningForBothPinnedAndUnpinnedWeChat()
    {
        const string pinnedWeChat = @"C:\Program Files\Tencent\Weixin\Weixin.exe";
        const string unpinnedQQ = @"C:\Program Files\Tencent\QQ\QQ.exe";

        var settings = GlassSettings.Default with { PinnedAppPaths = [pinnedWeChat] };
        var trayExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pinnedWeChat, unpinnedQQ };

        var items = DockModelCoordinator.BuildDockItems(
            settings,
            runningWindows: [], // All windows hidden/closed to system tray!
            recentAppPaths: [],
            recycleBin: new RecycleBinStatus(true, 0, 0),
            stableRunningOrder: [unpinnedQQ],
            trayResidentExePaths: trayExes);

        var wechatItem = items.FirstOrDefault(i => string.Equals(i.TargetPath, pinnedWeChat, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(wechatItem);
        Assert.True(wechatItem!.IsRunning);
        Assert.Empty(wechatItem.OpenWindowHandles);

        var qqItem = items.FirstOrDefault(i => string.Equals(i.TargetPath, unpinnedQQ, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(qqItem);
        Assert.True(qqItem!.IsRunning);
        Assert.False(qqItem.IsPinned);
    }
}
