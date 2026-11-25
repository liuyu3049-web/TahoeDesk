using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TahoeDesk.Core.Config;

namespace TahoeDesk.Core.Discovery;

public static class DockModelCoordinator
{
    private const int MaxRecentItems = 3;

    public static IReadOnlyList<DockItemModel> BuildDockItems(
        GlassSettings settings,
        IReadOnlyList<WindowInfo> runningWindows,
        IReadOnlyList<string> recentAppPaths,
        RecycleBinStatus recycleBin,
        IReadOnlyList<string>? stableRunningOrder = null,
        IReadOnlySet<string>? trayResidentExePaths = null)
    {
        var items = new List<DockItemModel>();

        var traySet = trayResidentExePaths is { Count: > 0 }
            ? new HashSet<string>(trayResidentExePaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase)
            : null;

        // Group running windows by normalized executable path
        var runningByExe = runningWindows
            .Where(w => !string.IsNullOrWhiteSpace(w.ExecutablePath))
            .GroupBy(w => NormalizePath(w.ExecutablePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var explorerWindows = runningWindows
            .Where(w => string.Equals(w.ClassName, "CabinetWClass", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Hwnd)
            .ToList();

        // Section 1: Leftmost fixed macOS-equivalent anchors + pinned apps
        items.Add(new DockItemModel(
            Id: "system:explorer",
            Title: "此电脑",
            TargetPath: "shell:MyComputerFolder",
            Kind: DockItemKind.Explorer,
            Section: DockSection.PinnedApps,
            IsPinned: true,
            IsRunning: explorerWindows.Count > 0,
            OpenWindowHandles: explorerWindows,
            IsFlashing: false));

        items.Add(new DockItemModel(
            Id: "system:launchpad",
            Title: "启动台",
            TargetPath: null,
            Kind: DockItemKind.Launchpad,
            Section: DockSection.PinnedApps,
            IsPinned: true,
            IsRunning: false,
            OpenWindowHandles: [],
            IsFlashing: false));

        var pinnedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in settings.PinnedAppPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string norm = NormalizePath(rawPath);
            if (!pinnedSet.Add(norm)) continue;

            runningByExe.TryGetValue(norm, out var openList);
            bool hasVisibleWindow = openList is { Count: > 0 };
            bool isTrayResident = traySet?.Contains(norm) == true;
            string title = hasVisibleWindow
                ? openList![0].AppDisplayName
                : Path.GetFileNameWithoutExtension(rawPath);

            items.Add(new DockItemModel(
                Id: $"app:{norm}",
                Title: title,
                TargetPath: rawPath,
                Kind: DockItemKind.Application,
                Section: DockSection.PinnedApps,
                IsPinned: true,
                IsRunning: hasVisibleWindow || isTrayResident,
                OpenWindowHandles: openList?.Select(w => w.Hwnd).ToList() ?? [],
                IsFlashing: openList?.Any(w => w.IsFlashing) ?? false));
        }

        // Section 2: Unpinned running apps in STABLE first-seen order (never jumps to slot #1 on click!) + optional recent apps (max 3)
        var shownMiddle = new HashSet<string>(pinnedSet, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> orderedRunningKeys = stableRunningOrder is { Count: > 0 }
            ? stableRunningOrder.Select(NormalizePath).Concat(runningByExe.Keys)
            : runningByExe.Keys;

        foreach (var exeKey in orderedRunningKeys)
        {
            if (exeKey.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (runningByExe.TryGetValue(exeKey, out var list))
            {
                if (!shownMiddle.Add(exeKey)) continue;
                items.Add(new DockItemModel(
                    Id: $"app:{exeKey}",
                    Title: list[0].AppDisplayName,
                    TargetPath: list[0].ExecutablePath,
                    Kind: DockItemKind.Application,
                    Section: DockSection.RunningOrRecent,
                    IsPinned: false,
                    IsRunning: true,
                    OpenWindowHandles: list.Select(w => w.Hwnd).ToList(),
                    IsFlashing: list.Any(w => w.IsFlashing)));
            }
            else if (traySet?.Contains(exeKey) == true && shownMiddle.Add(exeKey))
            {
                // App was open and closed to system tray (e.g. WeChat X button): keep it in the Dock as running!
                items.Add(new DockItemModel(
                    Id: $"app:{exeKey}",
                    Title: Path.GetFileNameWithoutExtension(exeKey),
                    TargetPath: exeKey,
                    Kind: DockItemKind.Application,
                    Section: DockSection.RunningOrRecent,
                    IsPinned: false,
                    IsRunning: true,
                    OpenWindowHandles: [],
                    IsFlashing: false));
            }
        }

        if (settings.ShowRecentApps)
        {
            int addedRecents = 0;
            foreach (var recent in recentAppPaths)
            {
                if (addedRecents >= MaxRecentItems) break;
                if (string.IsNullOrWhiteSpace(recent)) continue;
                string norm = NormalizePath(recent);
                if (!shownMiddle.Add(norm)) continue;

                items.Add(new DockItemModel(
                    Id: $"recent:{norm}",
                    Title: Path.GetFileNameWithoutExtension(recent),
                    TargetPath: recent,
                    Kind: DockItemKind.Application,
                    Section: DockSection.RunningOrRecent,
                    IsPinned: false,
                    IsRunning: false,
                    OpenWindowHandles: [],
                    IsFlashing: false));
                addedRecents++;
            }
        }

        // Section 3: Pinned folders + Minimized window thumbnails + Recycle Bin rightmost
        foreach (var folder in settings.PinnedFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = folder;

            items.Add(new DockItemModel(
                Id: $"folder:{NormalizePath(folder)}",
                Title: name,
                TargetPath: folder,
                Kind: DockItemKind.Folder,
                Section: DockSection.FoldersAndTrash,
                IsPinned: true,
                IsRunning: false,
                OpenWindowHandles: [],
                IsFlashing: false));
        }

        if (settings.ShowMinimizedWindowsInDock)
        {
            const int maxMinimizedDockTiles = 4;
            int addedMinimized = 0;
            foreach (var win in runningWindows)
            {
                if (!win.IsMinimized || win.Hwnd == 0) continue;
                if (addedMinimized >= maxMinimizedDockTiles) break;

                string title = string.IsNullOrWhiteSpace(win.Title) ? win.AppDisplayName : win.Title;
                items.Add(new DockItemModel(
                    Id: $"min:{win.Hwnd}",
                    Title: title,
                    TargetPath: win.ExecutablePath,
                    Kind: DockItemKind.MinimizedWindow,
                    Section: DockSection.FoldersAndTrash,
                    IsPinned: false,
                    IsRunning: true,
                    OpenWindowHandles: [win.Hwnd],
                    IsFlashing: win.IsFlashing));
                addedMinimized++;
            }
        }

        items.Add(new DockItemModel(
            Id: "system:recyclebin",
            Title: "回收站",
            TargetPath: "shell:RecycleBinFolder",
            Kind: DockItemKind.RecycleBin,
            Section: DockSection.FoldersAndTrash,
            IsPinned: true,
            IsRunning: false,
            OpenWindowHandles: [],
            IsFlashing: false,
            IsRecycleBinEmpty: recycleBin.IsEmpty));

        return items;
    }

    public static bool AreEquivalent(IReadOnlyList<DockItemModel> left, IReadOnlyList<DockItemModel> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;

        for (int i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.Title, b.Title, StringComparison.Ordinal)
                || a.Kind != b.Kind
                || a.Section != b.Section
                || a.IsPinned != b.IsPinned
                || a.IsRunning != b.IsRunning
                || a.IsFlashing != b.IsFlashing
                || a.IsRecycleBinEmpty != b.IsRecycleBinEmpty
                || a.OpenWindowHandles.Count != b.OpenWindowHandles.Count)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePath(string path) => path.Trim().TrimEnd('\\');
}
