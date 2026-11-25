using System.Collections.Generic;

namespace TahoeDesk.Core.Launchpad;

public enum LaunchpadFilterMode
{
    Primary,
    All,
    Hidden
}

public enum LaunchpadSortMode
{
    Custom,
    NameAsc,
    NameDesc,
    Pinyin
}

public sealed record LaunchpadEntry(
    string Id,
    string Title,
    bool IsFolder,
    string? LaunchTarget,
    string? IconPath,
    string PinyinInitials,
    IReadOnlyList<LaunchpadEntry> Children)
{
    public static LaunchpadEntry CreateApp(string id, string title, string launchTarget, string? iconPath, string pinyinInitials)
        => new(id, title, IsFolder: false, launchTarget, iconPath, pinyinInitials, []);

    public static LaunchpadEntry CreateFolder(string id, string title, IReadOnlyList<LaunchpadEntry> children)
        => new(id, title, IsFolder: true, LaunchTarget: null, IconPath: null, PinyinInitials: string.Empty, children);
}
