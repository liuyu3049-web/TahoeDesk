namespace GlassShell.Core.Search;

public enum SpotlightResultKind
{
    Calculator,
    Application,
    SystemCommand,
    File
}

public sealed record InstalledAppEntry(
    string Id,
    string Name,
    string LaunchTarget,
    string? IconPath,
    bool IsUwp,
    string PinyinInitials);

public sealed record IndexedFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    string PinyinInitials);

public sealed record SpotlightResult(
    SpotlightResultKind Kind,
    string Id,
    string Title,
    string Subtitle,
    string Payload,
    string? IconPath,
    int Score);
