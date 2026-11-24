using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GlassShell.Core.Search;

namespace GlassShell.Core.Launchpad;

/// <summary>
/// Manages Launchpad's 7x5 paginated app grid, smart primary-app filtering, sorting rules,
/// individual item locking, manual app additions, direct drag-to-reorder auto-flow, folder grouping, and JSON persistence.
/// </summary>
public sealed class LaunchpadLayoutManager
{
    public const int Columns = 7;
    public const int Rows = 5;
    public const int ItemsPerPage = Columns * Rows; // 35

    private static readonly string[] AuxiliaryKeywords =
    [
        "发行说明", "更新日志", "知识融合", "配置工具", "许可", "许可证", "授权工具", "激活工具", "诊断工具",
        "修复工具", "更新程序", "升级工具", "安装向导", "开发者命令", "说明文档", "使用手册", "帮助文档",
        "卸载", "管理工具", "release notes", "changelog", "license manager", "licensing",
        "auto update", "updater", "native tools", "developer command", "developer powershell",
        "command prompt for", "uninstall", "readme", "read me", "documentation", "ice"
    ];

    private sealed class PersistedState
    {
        public List<LaunchpadEntry> Entries { get; set; } = [];
        public List<InstalledAppEntry> ManualApps { get; set; } = [];
        public List<string> HiddenAppIds { get; set; } = [];
        public List<string> ForceShownAppIds { get; set; } = [];
        public List<string> LockedAppIds { get; set; } = [];
        public LaunchpadSortMode SortMode { get; set; } = LaunchpadSortMode.Custom;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<LaunchpadEntry> _entries = [];
    private readonly List<InstalledAppEntry> _manualApps = [];
    private readonly HashSet<string> _hiddenIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forceShownIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lockedIds = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LaunchpadEntry> Entries => _entries;
    public IReadOnlyList<InstalledAppEntry> ManualApps => _manualApps;
    public IReadOnlyCollection<string> HiddenAppIds => _hiddenIds;
    public IReadOnlyCollection<string> LockedAppIds => _lockedIds;
    public LaunchpadSortMode SortMode { get; set; } = LaunchpadSortMode.Custom;

    public static bool IsAuxiliaryUtility(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        foreach (string kw in AuxiliaryKeywords)
        {
            if (kw == "ice")
            {
                // Match standalone "ICE" token (e.g. "知识融合ICE", "ZeroC ICE") without matching "Office" or "Voice"!
                if (title.Contains("知识融合", StringComparison.OrdinalIgnoreCase)
                    || title.EndsWith("ICE", StringComparison.Ordinal)
                    || title.Contains(" ICE", StringComparison.Ordinal))
                {
                    return true;
                }
                continue;
            }

            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public bool IsLocked(string entryId) => !string.IsNullOrWhiteSpace(entryId) && _lockedIds.Contains(entryId);

    public bool IsManualApp(string entryId)
        => !string.IsNullOrWhiteSpace(entryId) && _manualApps.Any(m => string.Equals(m.Id, entryId, StringComparison.OrdinalIgnoreCase));

    public bool AddManualApp(InstalledAppEntry app)
    {
        if (string.IsNullOrWhiteSpace(app.Id) || string.IsNullOrWhiteSpace(app.Name)) return false;

        _manualApps.RemoveAll(m => string.Equals(m.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        _manualApps.Add(app);
        _hiddenIds.Remove(app.Id);
        _forceShownIds.Add(app.Id);

        int existingIdx = _entries.FindIndex(e => string.Equals(e.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        var entry = ToEntry(app);
        if (existingIdx >= 0)
        {
            _entries[existingIdx] = entry;
        }
        else
        {
            _entries.Insert(0, entry);
        }
        return true;
    }

    public bool RemoveManualApp(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return false;
        int removed = _manualApps.RemoveAll(m => string.Equals(m.Id, entryId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        _entries.RemoveAll(e => string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase));
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].IsFolder)
            {
                var remaining = _entries[i].Children.Where(c => !string.Equals(c.Id, entryId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (remaining.Count == 0) _entries.RemoveAt(i);
                else if (remaining.Count == 1) _entries[i] = remaining[0];
                else _entries[i] = _entries[i] with { Children = remaining };
            }
        }
        _hiddenIds.Remove(entryId);
        _forceShownIds.Remove(entryId);
        _lockedIds.Remove(entryId);
        return true;
    }

    public bool ToggleLock(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return false;
        if (!_lockedIds.Add(entryId))
        {
            _lockedIds.Remove(entryId);
            return false;
        }
        return true;
    }

    public IReadOnlyList<LaunchpadEntry> GetEntriesForFilter(
        LaunchpadFilterMode mode,
        LaunchpadSortMode? sortOverride = null)
    {
        if (mode == LaunchpadFilterMode.Hidden)
        {
            var hiddenList = new List<LaunchpadEntry>();
            foreach (var entry in _entries)
            {
                if (entry.IsFolder)
                {
                    foreach (var c in entry.Children)
                    {
                        if (_hiddenIds.Contains(c.Id) || (IsAuxiliaryUtility(c.Title) && !_forceShownIds.Contains(c.Id)))
                        {
                            hiddenList.Add(c);
                        }
                    }
                }
                else if (_hiddenIds.Contains(entry.Id) || (IsAuxiliaryUtility(entry.Title) && !_forceShownIds.Contains(entry.Id)))
                {
                    hiddenList.Add(entry);
                }
            }
            return hiddenList;
        }

        var visible = new List<LaunchpadEntry>();
        foreach (var entry in _entries)
        {
            if (entry.IsFolder)
            {
                var visibleChildren = entry.Children.Where(c => IsVisibleInMode(c, mode)).ToList();
                if (visibleChildren.Count > 0)
                {
                    visible.Add(entry with { Children = visibleChildren });
                }
            }
            else if (IsVisibleInMode(entry, mode))
            {
                visible.Add(entry);
            }
        }

        var activeSort = sortOverride ?? SortMode;
        if (activeSort == LaunchpadSortMode.Custom)
        {
            return visible;
        }

        return ApplySortPreservingLocked(visible, activeSort);
    }

    private List<LaunchpadEntry> ApplySortPreservingLocked(List<LaunchpadEntry> source, LaunchpadSortMode sortMode)
    {
        var unlocked = source.Where(e => !IsLocked(e.Id)).ToList();
        unlocked = sortMode switch
        {
            LaunchpadSortMode.NameAsc => unlocked.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            LaunchpadSortMode.NameDesc => unlocked.OrderByDescending(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            LaunchpadSortMode.Pinyin => unlocked.OrderBy(e => string.IsNullOrEmpty(e.PinyinInitials) ? e.Title : e.PinyinInitials, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => unlocked
        };

        var result = new List<LaunchpadEntry>(source.Count);
        int uIdx = 0;
        foreach (var entry in source)
        {
            result.Add(IsLocked(entry.Id) ? entry : unlocked[uIdx++]);
        }
        return result;
    }

    private bool IsVisibleInMode(LaunchpadEntry entry, LaunchpadFilterMode mode)
    {
        if (_hiddenIds.Contains(entry.Id)) return false;
        if (mode == LaunchpadFilterMode.Primary && IsAuxiliaryUtility(entry.Title) && !_forceShownIds.Contains(entry.Id))
        {
            return false;
        }
        return true;
    }

    public int PageCount => GetPageCount(LaunchpadFilterMode.All);

    public int GetPageCount(LaunchpadFilterMode mode)
    {
        int count = GetEntriesForFilter(mode).Count;
        return Math.Max(1, (int)Math.Ceiling(count / (double)ItemsPerPage));
    }

    public int ClampPage(int pageIndex, LaunchpadFilterMode mode = LaunchpadFilterMode.All)
        => Math.Clamp(pageIndex, 0, GetPageCount(mode) - 1);

    public IReadOnlyList<LaunchpadEntry> GetPage(int pageIndex, LaunchpadFilterMode mode = LaunchpadFilterMode.All)
    {
        var items = GetEntriesForFilter(mode);
        if (items.Count == 0) return [];
        int safe = Math.Clamp(pageIndex, 0, Math.Max(0, GetPageCount(mode) - 1));
        return items.Skip(safe * ItemsPerPage).Take(ItemsPerPage).ToList();
    }

    public bool HideApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        _forceShownIds.Remove(appId);
        return _hiddenIds.Add(appId);
    }

    public bool RestoreApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        bool removed = _hiddenIds.Remove(appId);
        bool forced = _forceShownIds.Add(appId);
        return removed || forced;
    }

    public void SyncWithInstalledApps(IReadOnlyList<InstalledAppEntry> installed)
    {
        var byId = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var manual in _manualApps)
        {
            if (!string.IsNullOrWhiteSpace(manual.Id))
            {
                byId.TryAdd(manual.Id, manual);
            }
        }
        foreach (var app in installed)
        {
            if (!string.IsNullOrWhiteSpace(app.Id))
            {
                byId.TryAdd(app.Id, app);
            }
        }

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

        foreach (var manual in _manualApps)
        {
            if (seen.Add(manual.Id))
            {
                synced.Add(ToEntry(manual));
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

    public IReadOnlyList<LaunchpadEntry> Filter(string query, LaunchpadFilterMode mode = LaunchpadFilterMode.All)
    {
        var scope = GetEntriesForFilter(mode);
        if (string.IsNullOrWhiteSpace(query)) return scope;

        var matches = new List<(LaunchpadEntry Entry, int Score)>();
        foreach (var entry in scope)
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
        if (IsLocked(sourceId)) return false;

        // If user starts dragging while in an auto-sort mode, snapshot the sorted order into _entries first
        // so switching to Custom order never causes unrelated icons to jump!
        if (SortMode != LaunchpadSortMode.Custom)
        {
            var materialized = ApplySortPreservingLocked(_entries, SortMode);
            _entries.Clear();
            _entries.AddRange(materialized);
            SortMode = LaunchpadSortMode.Custom;
        }

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
        if (IsLocked(sourceId)) return false;

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
            var state = new PersistedState
            {
                Entries = _entries,
                ManualApps = _manualApps,
                HiddenAppIds = _hiddenIds.ToList(),
                ForceShownAppIds = _forceShownIds.ToList(),
                LockedAppIds = _lockedIds.ToList(),
                SortMode = SortMode
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch { }
    }

    public void Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            string json = File.ReadAllText(filePath).TrimStart();
            if (json.StartsWith('['))
            {
                var legacy = JsonSerializer.Deserialize<List<LaunchpadEntry>>(json);
                if (legacy is not null)
                {
                    _entries.Clear();
                    _entries.AddRange(legacy);
                }
                return;
            }

            var state = JsonSerializer.Deserialize<PersistedState>(json);
            if (state is not null)
            {
                _entries.Clear();
                _entries.AddRange(state.Entries);
                _manualApps.Clear();
                _manualApps.AddRange(state.ManualApps);
                _hiddenIds.Clear();
                foreach (var id in state.HiddenAppIds) _hiddenIds.Add(id);
                _forceShownIds.Clear();
                foreach (var id in state.ForceShownAppIds) _forceShownIds.Add(id);
                _lockedIds.Clear();
                foreach (var id in state.LockedAppIds) _lockedIds.Add(id);
                SortMode = state.SortMode;
            }
        }
        catch { }
    }

    private static LaunchpadEntry ToEntry(InstalledAppEntry app)
        => LaunchpadEntry.CreateApp(app.Id, app.Name, app.LaunchTarget, app.IconPath, app.PinyinInitials);
}
