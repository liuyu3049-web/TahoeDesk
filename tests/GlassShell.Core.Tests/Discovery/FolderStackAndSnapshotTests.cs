using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using Xunit;

namespace GlassShell.Core.Tests.Discovery;

public sealed class FolderStackAndSnapshotTests
{
    [Fact]
    public void FolderStackScanner_SortsNewestFirstAndRespectsMaxItems()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"GlassShell_StackTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string older = Path.Combine(dir, "older.txt");
            string newer = Path.Combine(dir, "newer.txt");
            string middle = Path.Combine(dir, "middle.txt");
            File.WriteAllText(older, "a");
            File.WriteAllText(middle, "b");
            File.WriteAllText(newer, "c");

            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-3));
            File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-1));

            var items = FolderStackScanner.Scan(dir, maxItems: 2);
            Assert.Equal(2, items.Count);
            Assert.Equal("newer.txt", items[0].Name);
            Assert.Equal("middle.txt", items[1].Name);

            Assert.Empty(FolderStackScanner.Scan(Path.Combine(dir, "nonexistent")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WindowSnapshotCache_EnforcesLruCapacityAndPrunesClosedWindows()
    {
        var cache = new WindowSnapshotCache<string>(capacity: 2);
        cache.Put(10, "bmp10");
        cache.Put(20, "bmp20");
        Assert.True(cache.TryGet(10, out var v10));
        Assert.Equal("bmp10", v10);

        // Adding 30 evicts least-recently-used (20), keeping 10 and 30
        cache.Put(30, "bmp30");
        Assert.False(cache.TryGet(20, out _));
        Assert.True(cache.TryGet(10, out _));
        Assert.True(cache.TryGet(30, out _));

        // Prune removes closed/restored window 10
        cache.Prune(new HashSet<nint> { 30 });
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(10, out _));
        Assert.True(cache.TryGet(30, out _));
    }

    [Fact]
    public void DockModelCoordinator_PlacesMinimizedWindowsInRightStorageBeforeRecycleBin()
    {
        var settings = GlassSettings.Default with { PinnedFolderPaths = [@"C:\Users\Demo\Downloads"] };
        var windows = new List<WindowInfo>
        {
            new(0x101, 10, @"C:\Windows\System32\notepad.exe", "记事本", "notes.txt - 记事本", "Notepad", IsMinimized: true, IsFlashing: false)
        };

        var items = DockModelCoordinator.BuildDockItems(settings, windows, [], new RecycleBinStatus(true, 0, 0));
        var right = items.Where(i => i.Section == DockSection.FoldersAndTrash).ToList();

        Assert.Equal(3, right.Count);
        Assert.Equal(DockItemKind.Folder, right[0].Kind);
        Assert.Equal(DockItemKind.MinimizedWindow, right[1].Kind);
        Assert.Equal("notes.txt - 记事本", right[1].Title);
        Assert.Equal(DockItemKind.RecycleBin, right[2].Kind);
    }
}
