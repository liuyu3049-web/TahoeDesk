using System.Collections.Generic;
using TahoeDesk.Core.Shell;

namespace TahoeDesk.Core.Discovery;

public enum DockSection
{
    PinnedApps,
    RunningOrRecent,
    FoldersAndTrash
}

public enum DockItemKind
{
    Explorer,
    Launchpad,
    Application,
    Folder,
    MinimizedWindow,
    RecycleBin
}

public sealed record WindowInfo(
    nint Hwnd,
    uint ProcessId,
    string ExecutablePath,
    string AppDisplayName,
    string Title,
    string ClassName,
    bool IsMinimized,
    bool IsFlashing,
    bool IsMaximized = false,
    ScreenRect Bounds = default);

public sealed record DockItemModel(
    string Id,
    string Title,
    string? TargetPath,
    DockItemKind Kind,
    DockSection Section,
    bool IsPinned,
    bool IsRunning,
    IReadOnlyList<nint> OpenWindowHandles,
    bool IsFlashing,
    bool IsRecycleBinEmpty = true);

public sealed record RecycleBinStatus(bool IsEmpty, long ItemCount, long TotalBytes);

public sealed record RawIconBitmap(int Width, int Height, byte[] BgraPixels);
