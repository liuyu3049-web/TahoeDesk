using System;
using System.Collections.Generic;
using System.IO;
using GlassShell.Core.Launchpad;
using GlassShell.Core.Search;
using Xunit;

namespace GlassShell.Core.Tests.Launchpad;

public sealed class LaunchpadLayoutTests
{
    private static List<InstalledAppEntry> MakeApps(int count)
    {
        var list = new List<InstalledAppEntry>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new InstalledAppEntry($"app:{i}", $"App{i}", $@"C:\App{i}.exe", null, false, $"a{i}"));
        }
        return list;
    }

    [Fact]
    public void Pagination_ComputesExact7x5PagesAndClampsIndex()
    {
        var mgr = new LaunchpadLayoutManager();
        mgr.SyncWithInstalledApps(MakeApps(72));

        Assert.Equal(3, mgr.PageCount);
        Assert.Equal(35, mgr.GetPage(0).Count);
        Assert.Equal(35, mgr.GetPage(1).Count);
        Assert.Equal(2, mgr.GetPage(2).Count);
        Assert.Equal(2, mgr.ClampPage(99));
        Assert.Equal(0, mgr.ClampPage(-5));
    }

    [Fact]
    public void Filter_MatchesFuzzyAndPinyinInitialsAcrossFolders()
    {
        var mgr = new LaunchpadLayoutManager();
        mgr.SyncWithInstalledApps(
        [
            new("app:wx", "微信", @"C:\WeChat.exe", null, false, PinyinInitials.GetInitials("微信")),
            new("app:qq", "QQ", @"C:\QQ.exe", null, false, "qq")
        ]);
        Assert.True(mgr.MergeIntoFolder("app:wx", "app:qq", "社交"));

        var hits = mgr.Filter("wx");
        Assert.Single(hits);
        Assert.Equal("微信", hits[0].Title);
    }

    [Fact]
    public void MergeIntoFolder_CreatesFolderAndDissolvesWhenEmptied()
    {
        var mgr = new LaunchpadLayoutManager();
        mgr.SyncWithInstalledApps(MakeApps(3));

        Assert.True(mgr.MergeIntoFolder("app:1", "app:0", "工具"));
        Assert.Equal(2, mgr.Entries.Count);
        Assert.True(mgr.Entries[0].IsFolder);
        Assert.Equal(2, mgr.Entries[0].Children.Count);

        string folderId = mgr.Entries[0].Id;
        Assert.True(mgr.RemoveFromFolder(folderId, "app:1"));
        // Single remaining item dissolves the folder cleanly
        Assert.Equal(3, mgr.Entries.Count);
        Assert.DoesNotContain(mgr.Entries, e => e.IsFolder);
    }

    [Fact]
    public void SaveAndLoad_PreservesFolderStructureAndOrder()
    {
        string file = Path.Combine(Path.GetTempPath(), $"GlassShell_Launchpad_{Guid.NewGuid():N}.json");
        try
        {
            var mgr = new LaunchpadLayoutManager();
            mgr.SyncWithInstalledApps(MakeApps(4));
            mgr.MergeIntoFolder("app:2", "app:1", "开发");
            mgr.Save(file);

            var reloaded = new LaunchpadLayoutManager();
            reloaded.Load(file);
            Assert.Equal(3, reloaded.Entries.Count);
            Assert.True(reloaded.Entries[1].IsFolder);
            Assert.Equal("开发", reloaded.Entries[1].Title);
            Assert.Equal(2, reloaded.Entries[1].Children.Count);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void SyncWithInstalledApps_DeduplicatesDuplicateIdsWithoutThrowing()
    {
        var mgr = new LaunchpadLayoutManager();
        mgr.SyncWithInstalledApps(
        [
            new("app:c:\\windows\\system32\\notepad.exe", "Notepad", @"C:\Windows\System32\notepad.exe", null, false, "n"),
            new("app:c:\\windows\\system32\\notepad.exe", "记事本", @"C:\Windows\System32\notepad.exe", null, false, "jsb")
        ]);

        Assert.Single(mgr.Entries);
    }

    [Fact]
    public void SmartFilter_HidesAuxiliaryToolsAndSupportsUserHideRestore()
    {
        var mgr = new LaunchpadLayoutManager();
        mgr.SyncWithInstalledApps(
        [
            new("app:wechat", "微信", @"C:\WeChat.exe", null, false, "wx"),
            new("app:cfg", "显卡配置工具", @"C:\Cfg.exe", null, false, "xkpzgj"),
            new("app:lic", "Adobe License Manager", @"C:\Lic.exe", null, false, "alm")
        ]);

        // Primary hides auxiliary tools
        var primary = mgr.GetEntriesForFilter(LaunchpadFilterMode.Primary);
        Assert.Single(primary);
        Assert.Equal("微信", primary[0].Title);

        // All shows everything non-user-hidden
        Assert.Equal(3, mgr.GetEntriesForFilter(LaunchpadFilterMode.All).Count);

        // Hidden shows auto-filtered auxiliary tools + user-hidden apps
        Assert.True(mgr.HideApp("app:wechat"));
        Assert.Empty(mgr.GetEntriesForFilter(LaunchpadFilterMode.Primary));
        Assert.Equal(3, mgr.GetEntriesForFilter(LaunchpadFilterMode.Hidden).Count);

        // Restoring an app (even an auxiliary tool) brings it back into Primary
        Assert.True(mgr.RestoreApp("app:wechat"));
        Assert.True(mgr.RestoreApp("app:cfg"));
        var restoredPrimary = mgr.GetEntriesForFilter(LaunchpadFilterMode.Primary);
        Assert.Equal(2, restoredPrimary.Count);

        // Release notes & ICE utilities are excluded from Primary, while Office stays Primary
        Assert.True(LaunchpadLayoutManager.IsAuxiliaryUtility("发行说明"));
        Assert.True(LaunchpadLayoutManager.IsAuxiliaryUtility("知识融合ICE"));
        Assert.False(LaunchpadLayoutManager.IsAuxiliaryUtility("Microsoft Office"));

        // Sorting respects locked items pinned at their exact slots
        var sortMgr = new LaunchpadLayoutManager();
        sortMgr.SyncWithInstalledApps(
        [
            new("app:z", "Zebra", @"C:\Z.exe", null, false, "z"),
            new("app:b", "Beta", @"C:\B.exe", null, false, "b"),
            new("app:a", "Alpha", @"C:\A.exe", null, false, "a")
        ]);
        Assert.True(sortMgr.ToggleLock("app:z"));
        var sorted = sortMgr.GetEntriesForFilter(LaunchpadFilterMode.Primary, LaunchpadSortMode.NameAsc);
        Assert.Equal("Zebra", sorted[0].Title); // Locked at slot 0!
        Assert.Equal("Alpha", sorted[1].Title);
        Assert.Equal("Beta", sorted[2].Title);
        Assert.False(sortMgr.Reorder("app:z", "app:a")); // Cannot drag locked item
    }

    [Fact]
    public void ManualApps_SurviveRescanAndPersistence_AndReorderMaterializesSortedOrder()
    {
        string file = Path.Combine(Path.GetTempPath(), $"GlassShell_LaunchpadManual_{Guid.NewGuid():N}.json");
        try
        {
            var mgr = new LaunchpadLayoutManager();
            mgr.SyncWithInstalledApps(
            [
                new("app:c", "Charlie", @"C:\C.exe", null, false, "c"),
                new("app:a", "Alpha", @"C:\A.exe", null, false, "a"),
                new("app:b", "Bravo", @"C:\B.exe", null, false, "b")
            ]);

            var manual = new InstalledAppEntry("manual:d:\\tools\\mytool.exe", "MyTool", @"D:\Tools\MyTool.exe", @"D:\Tools\MyTool.exe", false, "mt");
            Assert.True(mgr.AddManualApp(manual));
            Assert.True(mgr.IsManualApp(manual.Id));

            // Rescan without manual app in scanned list MUST keep manual app intact
            mgr.SyncWithInstalledApps(
            [
                new("app:c", "Charlie", @"C:\C.exe", null, false, "c"),
                new("app:a", "Alpha", @"C:\A.exe", null, false, "a"),
                new("app:b", "Bravo", @"C:\B.exe", null, false, "b")
            ]);
            Assert.Contains(mgr.Entries, e => e.Id == manual.Id);

            // Reordering while in NameAsc materializes sorted order seamlessly and switches to Custom
            mgr.SortMode = LaunchpadSortMode.NameAsc;
            Assert.True(mgr.Reorder(manual.Id, "app:a"));
            Assert.Equal(LaunchpadSortMode.Custom, mgr.SortMode);
            Assert.Equal(manual.Id, mgr.Entries[0].Id);
            Assert.Equal("app:a", mgr.Entries[1].Id);

            // Persist and reload preserves manual apps and order
            mgr.Save(file);
            var reloaded = new LaunchpadLayoutManager();
            reloaded.Load(file);
            Assert.True(reloaded.IsManualApp(manual.Id));
            Assert.Equal(manual.Id, reloaded.Entries[0].Id);

            Assert.True(reloaded.RemoveManualApp(manual.Id));
            Assert.False(reloaded.IsManualApp(manual.Id));
            Assert.DoesNotContain(reloaded.Entries, e => e.Id == manual.Id);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
