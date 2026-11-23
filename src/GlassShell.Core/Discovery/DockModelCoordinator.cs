using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GlassShell.Core.Config;

namespace GlassShell.Core.Discovery;

public static class DockModelCoordinator
{
    private const int MaxRecentItems = 3;

    public static IReadOnlyList<DockItemModel> BuildDockItems(
        GlassSettings settings,
        IReadOnlyList<WindowInfo> runningWindows,
        IReadOnlyList<string> recentAppPaths,
        RecycleBinStatus recycleBin)
    {
        var items = new List<DockItemModel>();

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
            string title = openList is { Count: > 0 }
                ? openList[0].AppDisplayName
                : Path.GetFileNameWithoutExtension(rawPath);

            items.Add(new DockItemModel(
                Id: $"app:{norm}",
                Title: title,
                TargetPath: rawPath,
                Kind: DockItemKind.Application,
                Section: DockSection.PinnedApps,
                IsPinned: true,
                IsRunning: openList is { Count: > 0 },
                OpenWindowHandles: openList?.Select(w => w.Hwnd).ToList() ?? [],
                IsFlashing: openList?.Any(w => w.IsFlashing) ?? false));
        }

        // Section 2: Unpinned running apps + optional recent apps (max 3)
        var shownMiddle = new HashSet<string>(pinnedSet, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in runningByExe)
        {
            // Exclude explorer.exe (already represented by 此电脑)
            if (kvp.Key.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!shownMiddle.Add(kvp.Key)) continue;

            var list = kvp.Value;
            items.Add(new DockItemModel(
                Id: $"app:{kvp.Key}",
                Title: list[0].AppDisplayName,
                TargetPath: list[0].ExecutablePath,
                Kind: DockItemKind.Application,
                Section: DockSection.RunningOrRecent,
                IsPinned: false,
                IsRunning: true,
                OpenWindowHandles: list.Select(w => w.Hwnd).ToList(),
                IsFlashing: list.Any(w => w.IsFlashing)));
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

        // Section 3: Pinned folders + Recycle Bin rightmost
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
