using System.Linq;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using Xunit;

namespace GlassShell.Core.Tests.Discovery;

public sealed class DockModelCoordinatorTests
{
    [Fact]
    public void BuildDockItems_OrdersThreeSections_ExplorerAndLaunchpadLeft_RecycleBinRight()
    {
        var settings = GlassSettings.Default with
        {
            PinnedAppPaths = [@"C:\App\Code.exe"],
            PinnedFolderPaths = [@"C:\Users\Me\Downloads"]
        };
        var running = new[]
        {
            new WindowInfo(10, 100, @"C:\App\Code.exe", "VS Code", "main.cs", "Chrome_WidgetWin_1", false, false),
            new WindowInfo(20, 200, @"C:\App\Chrome.exe", "Chrome", "Tab", "Chrome_WidgetWin_1", false, false)
        };

        var items = DockModelCoordinator.BuildDockItems(settings, running, [], new RecycleBinStatus(false, 3, 1024));

        Assert.Equal(DockItemKind.Explorer, items[0].Kind);
        Assert.Equal("此电脑", items[0].Title);
        Assert.Equal(DockItemKind.Launchpad, items[1].Kind);
        Assert.Equal("启动台", items[1].Title);
        Assert.Equal(DockItemKind.RecycleBin, items[^1].Kind);
        Assert.Equal("回收站", items[^1].Title);

        var code = items.Single(i => i.TargetPath == @"C:\App\Code.exe");
        Assert.Equal(DockSection.PinnedApps, code.Section);
        Assert.True(code.IsPinned);
        Assert.True(code.IsRunning);
        Assert.Single(code.OpenWindowHandles);

        var chrome = items.Single(i => i.TargetPath == @"C:\App\Chrome.exe");
        Assert.Equal(DockSection.RunningOrRecent, chrome.Section);
        Assert.False(chrome.IsPinned);
        Assert.True(chrome.IsRunning);
    }

    [Fact]
    public void BuildDockItems_DeduplicatesMultipleWindowsOfSameApp_AndPropagatesFlashing()
    {
        var running = new[]
        {
            new WindowInfo(10, 100, @"C:\App\WeChat.exe", "微信", "聊天1", "WeChatMainWndForPC", false, false),
            new WindowInfo(11, 100, @"c:\app\wechat.exe", "微信", "聊天2", "WeChatMainWndForPC", false, true)
        };

        var items = DockModelCoordinator.BuildDockItems(GlassSettings.Default, running, [], new RecycleBinStatus(true, 0, 0));
        var wechat = items.Single(i => i.Section == DockSection.RunningOrRecent);
        Assert.Equal(2, wechat.OpenWindowHandles.Count);
        Assert.True(wechat.IsFlashing);
    }

    [Fact]
    public void BuildDockItems_LimitsRecentUnpinnedAppsToThree_WhenShowRecentAppsTrue()
    {
        var settings = GlassSettings.Default with { ShowRecentApps = true };
        var recents = new[] { @"C:\a.exe", @"C:\b.exe", @"C:\c.exe", @"C:\d.exe" };
        var items = DockModelCoordinator.BuildDockItems(settings, [], recents, new RecycleBinStatus(true, 0, 0));
        var middle = items.Where(i => i.Section == DockSection.RunningOrRecent).ToList();
        Assert.Equal(3, middle.Count);
        Assert.All(middle, m => Assert.False(m.IsRunning));
    }

    [Fact]
    public void BuildDockItems_HidesRecentApps_WhenShowRecentAppsFalse()
    {
        var settings = GlassSettings.Default with { ShowRecentApps = false };
        var recents = new[] { @"C:\a.exe", @"C:\b.exe" };
        var items = DockModelCoordinator.BuildDockItems(settings, [], recents, new RecycleBinStatus(true, 0, 0));
        Assert.DoesNotContain(items, i => i.Section == DockSection.RunningOrRecent);
    }
}
