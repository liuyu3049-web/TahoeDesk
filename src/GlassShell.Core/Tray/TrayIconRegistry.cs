using System;
using System.Collections.Generic;
using System.Linq;
using GlassShell.Core.Shell;

namespace GlassShell.Core.Tray;

/// <summary>
/// Thread-safe registry of intercepted system tray icons, managing Add/Modify/Delete/SetVersion,
/// screen-rect tracking for Shell_NotifyIconGetRect, stale window pruning, and MenuBar vs Overflow partitioning.
/// </summary>
public sealed class TrayIconRegistry
{
    private readonly object _gate = new();
    private readonly List<TrayIconRecord> _icons = [];
    private int _nextOrder;
    private long _nextIconRevision;

    public bool ApplyCommand(ParsedTrayCommand cmd, out TrayIconRecord? updatedRecord)
    {
        updatedRecord = null;
        if (cmd.OwnerHwnd == 0 && cmd.GuidItem == Guid.Empty)
        {
            return false;
        }

        var key = new TrayIconKey(cmd.OwnerHwnd, cmd.IconId, cmd.GuidItem);

        lock (_gate)
        {
            int idx = FindIndexUnlocked(key);

            switch (cmd.Kind)
            {
                case TrayCommandKind.Add:
                {
                    bool isHidden = (cmd.Flags & NotifyIconFlags.State) != 0
                        && (cmd.StateMask & TrayWireProtocol.NisHidden) != 0
                        && (cmd.State & TrayWireProtocol.NisHidden) != 0;

                    if (idx >= 0)
                    {
                        var existing = _icons[idx];
                        updatedRecord = MergeUpdate(existing, cmd);
                        _icons[idx] = updatedRecord;
                    }
                    else
                    {
                        updatedRecord = new TrayIconRecord(
                            Key: key,
                            CallbackMessage: (cmd.Flags & NotifyIconFlags.Message) != 0 ? cmd.CallbackMessage : 0,
                            Version: 0,
                            Tooltip: cmd.Tooltip ?? string.Empty,
                            IconHandle: (cmd.Flags & NotifyIconFlags.Icon) != 0 ? cmd.IconHandle : 0,
                            IsHidden: isHidden,
                            OrderIndex: _nextOrder++,
                            IconRevision: ++_nextIconRevision,
                            LastScreenRect: default);
                        _icons.Add(updatedRecord);
                    }
                    return true;
                }

                case TrayCommandKind.Modify:
                {
                    if (idx < 0)
                    {
                        // Some apps send NIM_MODIFY before NIM_ADD after TaskbarCreated; treat gracefully as Add.
                        bool isHidden = (cmd.Flags & NotifyIconFlags.State) != 0
                            && (cmd.StateMask & TrayWireProtocol.NisHidden) != 0
                            && (cmd.State & TrayWireProtocol.NisHidden) != 0;
                        updatedRecord = new TrayIconRecord(
                            Key: key,
                            CallbackMessage: (cmd.Flags & NotifyIconFlags.Message) != 0 ? cmd.CallbackMessage : 0,
                            Version: 0,
                            Tooltip: cmd.Tooltip ?? string.Empty,
                            IconHandle: (cmd.Flags & NotifyIconFlags.Icon) != 0 ? cmd.IconHandle : 0,
                            IsHidden: isHidden,
                            OrderIndex: _nextOrder++,
                            IconRevision: ++_nextIconRevision,
                            LastScreenRect: default);
                        _icons.Add(updatedRecord);
                        return true;
                    }

                    updatedRecord = MergeUpdate(_icons[idx], cmd);
                    _icons[idx] = updatedRecord;
                    return true;
                }

                case TrayCommandKind.Delete:
                {
                    if (idx >= 0)
                    {
                        updatedRecord = _icons[idx];
                        _icons.RemoveAt(idx);
                        return true;
                    }
                    return false;
                }

                case TrayCommandKind.SetVersion:
                {
                    if (idx >= 0)
                    {
                        updatedRecord = _icons[idx] with { Version = cmd.Version };
                        _icons[idx] = updatedRecord;
                        return true;
                    }
                    return false;
                }

                case TrayCommandKind.SetFocus:
                    return idx >= 0;

                default:
                    return false;
            }
        }
    }

    public bool UpdateScreenRect(TrayIconKey key, ScreenRect rect)
    {
        lock (_gate)
        {
            int idx = FindIndexUnlocked(key);
            if (idx < 0) return false;
            _icons[idx] = _icons[idx] with { LastScreenRect = rect };
            return true;
        }
    }

    public bool TryGetIconRect(nint ownerHwnd, uint iconId, Guid guidItem, out ScreenRect rect)
    {
        var key = new TrayIconKey(ownerHwnd, iconId, guidItem);
        lock (_gate)
        {
            int idx = FindIndexUnlocked(key);
            if (idx >= 0 && _icons[idx].LastScreenRect.Width > 0 && _icons[idx].LastScreenRect.Height > 0)
            {
                rect = _icons[idx].LastScreenRect;
                return true;
            }
        }

        rect = default;
        return false;
    }

    public bool PruneDeadWindows(Func<nint, bool> isWindowAlive)
    {
        lock (_gate)
        {
            int removed = _icons.RemoveAll(icon => icon.Key.OwnerHwnd != 0 && !isWindowAlive(icon.Key.OwnerHwnd));
            return removed > 0;
        }
    }

    public IReadOnlyList<TrayIconRecord> GetVisibleIcons()
    {
        lock (_gate)
        {
            return _icons
                .Where(i => !i.IsHidden)
                .OrderBy(i => i.OrderIndex)
                .ToList();
        }
    }

    /// <summary>
    /// Partitions visible tray icons into icons shown directly in the top Menu Bar
    /// and icons neatly tucked inside the Bartender-style Liquid Glass overflow capsule.
    /// </summary>
    public static (IReadOnlyList<TrayIconRecord> BarIcons, IReadOnlyList<TrayIconRecord> OverflowIcons) PartitionForMenuBar(
        IReadOnlyList<TrayIconRecord> visibleIcons,
        IReadOnlyCollection<string>? pinnedKeys,
        int defaultInlineCount = 4,
        IReadOnlyList<string>? overflowOrderKeys = null)
    {
        if (visibleIcons.Count == 0)
        {
            return ([], []);
        }

        var pinnedSet = pinnedKeys is { Count: > 0 }
            ? new HashSet<string>(pinnedKeys, StringComparer.OrdinalIgnoreCase)
            : null;

        var bar = new List<TrayIconRecord>();
        var overflow = new List<TrayIconRecord>();

        if (pinnedSet is not null)
        {
            foreach (var icon in visibleIcons)
            {
                if (pinnedSet.Contains(icon.PreferenceKey) || pinnedSet.Contains(icon.Key.StablePreferenceId))
                {
                    bar.Add(icon);
                }
                else
                {
                    overflow.Add(icon);
                }
            }
        }
        else
        {
            int limit = Math.Max(0, defaultInlineCount);
            for (int i = 0; i < visibleIcons.Count; i++)
            {
                if (i < limit) bar.Add(visibleIcons[i]);
                else overflow.Add(visibleIcons[i]);
            }
        }

        if (overflowOrderKeys is { Count: > 0 } && overflow.Count > 1)
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < overflowOrderKeys.Count; i++)
            {
                rank.TryAdd(overflowOrderKeys[i], i);
            }

            overflow = overflow
                .OrderBy(ic =>
                {
                    if (rank.TryGetValue(ic.PreferenceKey, out int r1)) return r1;
                    if (rank.TryGetValue(ic.Key.StablePreferenceId, out int r2)) return r2;
                    return int.MaxValue;
                })
                .ThenBy(ic => ic.OrderIndex)
                .ToList();
        }

        return (bar, overflow);
    }

    private TrayIconRecord MergeUpdate(TrayIconRecord existing, ParsedTrayCommand cmd)
    {
        uint callback = (cmd.Flags & NotifyIconFlags.Message) != 0 ? cmd.CallbackMessage : existing.CallbackMessage;
        string tip = (cmd.Flags & NotifyIconFlags.Tip) != 0 && cmd.Tooltip is not null ? cmd.Tooltip : existing.Tooltip;
        bool iconChanged = (cmd.Flags & NotifyIconFlags.Icon) != 0;
        nint hIcon = iconChanged ? cmd.IconHandle : existing.IconHandle;

        bool hidden = existing.IsHidden;
        if ((cmd.Flags & NotifyIconFlags.State) != 0 && (cmd.StateMask & TrayWireProtocol.NisHidden) != 0)
        {
            hidden = (cmd.State & TrayWireProtocol.NisHidden) != 0;
        }

        var mergedKey = existing.Key;
        if (cmd.GuidItem != Guid.Empty && !mergedKey.HasGuid)
        {
            mergedKey = mergedKey with { GuidItem = cmd.GuidItem };
        }

        return existing with
        {
            Key = mergedKey,
            CallbackMessage = callback,
            Tooltip = tip,
            IconHandle = hIcon,
            IsHidden = hidden,
            IconRevision = iconChanged ? ++_nextIconRevision : existing.IconRevision
        };
    }

    private int FindIndexUnlocked(TrayIconKey key)
    {
        for (int i = 0; i < _icons.Count; i++)
        {
            if (_icons[i].Key.Matches(key))
            {
                return i;
            }
        }
        return -1;
    }
}
