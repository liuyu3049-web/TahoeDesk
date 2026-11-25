using System.Collections.Generic;

namespace TahoeDesk.Core.Config;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public enum GlassOpacityPreset
{
    Clear,
    Balanced,
    Frosted
}

public enum DockAutoHideMode
{
    Off,
    Always,
    Smart
}

public sealed record GlassSettings
{
    public int SettingsSchemaVersion { get; init; } = 1;
    public ThemeMode Theme { get; init; } = ThemeMode.System;
    public GlassOpacityPreset OpacityPreset { get; init; } = GlassOpacityPreset.Clear;
    public bool ShowMenuBarBackground { get; init; } = false;
    public bool ShowMenuBarWifiIcon { get; init; } = false;
    public int DockIconSize { get; init; } = 52;
    public bool DockMagnificationEnabled { get; init; } = true;
    public double DockMagnificationScale { get; init; } = 1.6;
    public DockAutoHideMode DockAutoHide { get; init; } = DockAutoHideMode.Smart;
    public bool ShowRecentApps { get; init; } = true;
    public bool HarmonizeDockIcons { get; init; } = false;
    public bool ShowMinimizedWindowsInDock { get; init; } = false;
    public bool InterceptWinKeyForLaunchpad { get; init; } = true;
    public int LaunchpadColumns { get; init; } = 7;
    public int LaunchpadRows { get; init; } = 5;
    public string SpotlightHotkey { get; init; } = "Alt+Space";
    public IReadOnlyList<string> PinnedAppPaths { get; init; } = [];
    public IReadOnlyList<string> PinnedFolderPaths { get; init; } = [];
    public IReadOnlyList<string> PinnedTrayIconKeys { get; init; } = [];
    public IReadOnlyList<string> OverflowTrayOrderKeys { get; init; } = [];

    public static GlassSettings Default => new() { SettingsSchemaVersion = 3 };
}
