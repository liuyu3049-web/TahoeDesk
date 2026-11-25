using System;
using TahoeDesk.Core.Shell;

namespace TahoeDesk.Core.Tray;

public enum TrayCommandKind : uint
{
    Add = 0,
    Modify = 1,
    Delete = 2,
    SetFocus = 3,
    SetVersion = 4
}

[Flags]
public enum NotifyIconFlags : uint
{
    None = 0,
    Message = 0x00000001,
    Icon = 0x00000002,
    Tip = 0x00000004,
    State = 0x00000008,
    Info = 0x00000010,
    Guid = 0x00000020,
    Realtime = 0x00000040,
    ShowTip = 0x00000080
}

public enum TrayMouseAction
{
    LeftClick,
    LeftDoubleClick,
    RightClick,
    MiddleClick
}

public readonly record struct TrayIconKey(nint OwnerHwnd, uint IconId, Guid GuidItem)
{
    public bool HasGuid => GuidItem != Guid.Empty;

    public string StablePreferenceId => HasGuid
        ? $"guid:{GuidItem:D}"
        : $"id:{IconId}";

    public bool Matches(TrayIconKey other)
    {
        if (HasGuid && other.HasGuid)
        {
            return GuidItem == other.GuidItem;
        }

        return OwnerHwnd == other.OwnerHwnd && IconId == other.IconId;
    }
}

public sealed record ParsedTrayCommand(
    TrayCommandKind Kind,
    nint OwnerHwnd,
    uint IconId,
    NotifyIconFlags Flags,
    uint CallbackMessage,
    nint IconHandle,
    string? Tooltip,
    uint State,
    uint StateMask,
    uint Version,
    Guid GuidItem);

public sealed record ParsedTrayRectQuery(
    uint QueryKind, // 1 = TopLeft, 2 = BottomRight
    nint OwnerHwnd,
    uint IconId,
    Guid GuidItem);

public sealed record TrayCallbackPacket(uint Message, nint WParam, nint LParam);

public sealed record TrayIconRecord(
    TrayIconKey Key,
    uint CallbackMessage,
    uint Version,
    string Tooltip,
    nint IconHandle,
    bool IsHidden,
    int OrderIndex,
    long IconRevision,
    ScreenRect LastScreenRect)
{
    public string PreferenceKey => !string.IsNullOrWhiteSpace(Tooltip)
        ? (Key.HasGuid ? Key.StablePreferenceId : $"tip:{Tooltip.Trim().Split('\r', '\n')[0]}")
        : Key.StablePreferenceId;
}
