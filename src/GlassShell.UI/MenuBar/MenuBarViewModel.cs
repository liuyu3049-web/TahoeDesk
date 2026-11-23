using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GlassShell.Core.Config;
using GlassShell.Core.MenuEngine;
using GlassShell.Core.SystemStatus;

namespace GlassShell.UI.MenuBar;

public sealed partial class MenuBarViewModel : ObservableObject
{
    [ObservableProperty] private string _activeAppName = "桌面";
    [ObservableProperty] private nint _activeTargetHwnd;
    [ObservableProperty] private string _inputMethodLabel = "拼";
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private int _batteryPercent = 100;
    [ObservableProperty] private bool _isCharging;
    [ObservableProperty] private bool _isNetworkConnected = true;
    [ObservableProperty] private string _formattedClock = ClockFormatter.FormatMacZhCn(DateTime.Now);
    [ObservableProperty] private bool _showMenuBarBackground;
    [ObservableProperty] private bool _isDarkMode = true;
    [ObservableProperty] private GlassOpacityPreset _opacityPreset = GlassOpacityPreset.Clear;
    [ObservableProperty] private bool _isTrayPeekActive;

    public ObservableCollection<MenuNode> TopLevelMenus { get; } = [];

    public void ApplyMenuSnapshot(MenuBarSnapshot snapshot)
    {
        ActiveAppName = string.IsNullOrWhiteSpace(snapshot.AppDisplayName) ? "桌面" : snapshot.AppDisplayName;
        ActiveTargetHwnd = snapshot.TargetHwnd;
        TopLevelMenus.Clear();
        foreach (var node in snapshot.TopLevelMenus)
        {
            TopLevelMenus.Add(node);
        }
    }

    public void ApplySystemStatus(SystemStatusSnapshot status)
    {
        InputMethodLabel = status.InputMethodLabel;
        HasBattery = status.HasBattery;
        BatteryPercent = status.BatteryPercent;
        IsCharging = status.IsCharging;
        IsNetworkConnected = status.IsNetworkConnected;
        FormattedClock = status.FormattedClock;
    }

    public void ApplySettings(GlassSettings settings, bool resolvedDarkMode)
    {
        ShowMenuBarBackground = settings.ShowMenuBarBackground;
        OpacityPreset = settings.OpacityPreset;
        IsDarkMode = resolvedDarkMode;
    }
}
