using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GlassShell.Core.Config;
using GlassShell.Core.MenuEngine;
using GlassShell.Core.SystemStatus;

namespace GlassShell.UI.MenuBar;

public sealed class MenuBarViewModel : INotifyPropertyChanged
{
    private string _activeAppName = "桌面";
    private nint _activeTargetHwnd;
    private string _inputMethodLabel = "中";
    private bool _hasBattery;
    private int _batteryPercent = 100;
    private bool _isCharging;
    private bool _isNetworkConnected = true;
    private string _formattedClock = ClockFormatter.FormatMacZhCn(DateTime.Now);
    private bool _showMenuBarBackground;
    private bool _isDarkMode = true;
    private GlassOpacityPreset _opacityPreset = GlassOpacityPreset.Clear;
    private bool _isTrayPeekActive;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ActiveAppName { get => _activeAppName; set => SetField(ref _activeAppName, value); }
    public nint ActiveTargetHwnd { get => _activeTargetHwnd; set => SetField(ref _activeTargetHwnd, value); }
    public string InputMethodLabel { get => _inputMethodLabel; set => SetField(ref _inputMethodLabel, value); }
    public bool HasBattery { get => _hasBattery; set => SetField(ref _hasBattery, value); }
    public int BatteryPercent { get => _batteryPercent; set => SetField(ref _batteryPercent, value); }
    public bool IsCharging { get => _isCharging; set => SetField(ref _isCharging, value); }
    public bool IsNetworkConnected { get => _isNetworkConnected; set => SetField(ref _isNetworkConnected, value); }
    public string FormattedClock { get => _formattedClock; set => SetField(ref _formattedClock, value); }
    public bool ShowMenuBarBackground { get => _showMenuBarBackground; set => SetField(ref _showMenuBarBackground, value); }
    public bool IsDarkMode { get => _isDarkMode; set => SetField(ref _isDarkMode, value); }
    public GlassOpacityPreset OpacityPreset { get => _opacityPreset; set => SetField(ref _opacityPreset, value); }
    public bool IsTrayPeekActive { get => _isTrayPeekActive; set => SetField(ref _isTrayPeekActive, value); }

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

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
