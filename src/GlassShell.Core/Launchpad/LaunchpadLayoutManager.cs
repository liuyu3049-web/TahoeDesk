using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GlassShell.Core.Search;

namespace GlassShell.Core.Launchpad;

/// <summary>
/// Manages Launchpad's 7x5 paginated app grid, folder grouping/dissolving, reordering, search filtering,
/// and JSON persistence under %LocalAppData%\GlassShell\launchpad.json.
/// </summary>
public sealed class LaunchpadLayoutManager
{
    public const int Columns = 7;
    public const int Rows = 5;
    public const int ItemsPerPage = Columns * Rows; // 35

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<LaunchpadEntry> _entries = [];

    public IReadOnlyList<LaunchpadEntry> Entries => _entries;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(_entries.Count / (double)ItemsPerPage));

    public int ClampPage(int pageIndex) => Math.Clamp(pageIndex, 0, PageCount - 1);

    public IReadOnlyList<LaunchpadEntry> GetPage(int pageIndex)
    {
        if (_entries.Count == 0) return [];
        int safe = ClampPage(pageIndex);
        return _entries.Skip(safe * ItemsPerPage).Take(ItemsPerPage).ToList();
    }

    public void SyncWithInstalledApps(IReadOnlyList<InstalledAppEntry> installed)
    {
        var byId = installed.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var synced = new List<LaunchpadEntry>();

        foreach (var entry in _entries)
        {
            if (entry.IsFolder)
            {
                var validChildren = new List<LaunchpadEntry>();
                foreach (var child in entry.Children)
                {
                    if (byId.TryGetValue(child.Id, out var fresh) && seen.Add(fresh.Id))
                    {
                        validChildren.Add(ToEntry(fresh));
                    }
                }
                if (validChildren.Count >= 2)
                {
                    synced.Add(entry with { Children = validChildren });
                }
                else if (validChildren.Count == 1)
                {
                    synced.Add(validChildren[0]);
                }
            }
            else if (byId.TryGetValue(entry.Id, out var fresh) && seen.Add(fresh.Id))
            {
                synced.Add(ToEntry(fresh));
            }
        }

        foreach (var app in installed)
        {
            if (seen.Add(app.Id))
            {
                synced.Add(ToEntry(app));
            }
        }

        _entries.Clear();
        _entries.AddRange(synced);
    }

    public IReadOnlyList<LaunchpadEntry> Filter(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _entries.ToList();

        var matches = new List<(LaunchpadEntry Entry, int Score)>();
        foreach (var entry in _entries)
        {
            if (entry.IsFolder)
            {
                foreach (var child in entry.Children)
                {
                    int s = SpotlightSearchEngine.ScoreMatch(query, child.Title, child.PinyinInitials);
                    if (s > 0) matches.Add((child, s));
                }
            }
            else
            {
                int s = SpotlightSearchEngine.ScoreMatch(query, entry.Title, entry.PinyinInitials);
                if (s > 0) matches.Add((entry, s));
            }
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.Title.Length)
            .Select(m => m.Entry)
            .ToList();
    }

    public bool Reorder(string sourceId, string targetId)
    {
        if (string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase)) return false;
        int srcIdx = _entries.FindIndex(e => string.Equals(e.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        int dstIdx = _entries.FindIndex(e => string.Equals(e.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (srcIdx < 0 || dstIdx < 0) return false;

        var item = _entries[srcIdx];
        _entries.RemoveAt(srcIdx);
        _entries.Insert(dstIdx, item);
        return true;
    }

    public bool MergeIntoFolder(string sourceId, string targetId, string defaultFolderName = "应用文件夹")
    {
        if (string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase)) return false;
        int srcIdx = _entries.FindIndex(e => string.Equals(e.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        int dstIdx = _entries.FindIndex(e => string.Equals(e.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (srcIdx < 0 || dstIdx < 0) return false;

        var src = _entries[srcIdx];
        var dst = _entries[dstIdx];
        if (src.IsFolder) return false;

        LaunchpadEntry mergedFolder;
        if (dst.IsFolder)
        {
            var children = dst.Children.ToList();
            children.Add(src);
            mergedFolder = dst with { Children = children };
        }
        else
        {
            mergedFolder = LaunchpadEntry.CreateFolder(
                id: $"folder:{Guid.NewGuid():N}",
                title: defaultFolderName,
                children: [dst, src]);
        }

        _entries[dstIdx] = mergedFolder;
        _entries.RemoveAt(srcIdx);
        return true;
    }

    public bool RenameFolder(string folderId, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle)) return false;
        int idx = _entries.FindIndex(e => e.IsFolder && string.Equals(e.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        _entries[idx] = _entries[idx] with { Title = newTitle.Trim() };
        return true;
    }

    public bool RemoveFromFolder(string folderId, string childId)
    {
        int folderIdx = _entries.FindIndex(e => e.IsFolder && string.Equals(e.Id, folderId, StringComparison.OrdinalIgnoreCase));
        if (folderIdx < 0) return false;

        var folder = _entries[folderIdx];
        var child = folder.Children.FirstOrDefault(c => string.Equals(c.Id, childId, StringComparison.OrdinalIgnoreCase));
        if (child is null) return false;

        var remaining = folder.Children.Where(c => !string.Equals(c.Id, childId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (remaining.Count == 0)
        {
            _entries.RemoveAt(folderIdx);
        }
        else if (remaining.Count == 1)
        {
            // Dissolve single-item folder cleanly back into the app icon (macOS Launchpad behavior)
            _entries[folderIdx] = remaining[0];
        }
        else
        {
            _entries[folderIdx] = folder with { Children = remaining };
        }

        _entries.Add(child);
        return true;
    }

    public void Save(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch { }
    }

    public void Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<LaunchpadEntry>>(File.ReadAllText(filePath));
            if (loaded is not null)
            {
                _entries.Clear();
                _entries.AddRange(loaded);
            }
        }
        catch { }
    }

    private static LaunchpadEntry ToEntry(InstalledAppEntry app)
        => LaunchpadEntry.CreateApp(app.Id, app.Name, app.LaunchTarget, app.IconPath, app.PinyinInitials);
}
