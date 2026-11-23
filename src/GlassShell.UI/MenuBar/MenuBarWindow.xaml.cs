using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using GlassShell.Core.MenuEngine;
using GlassShell.Core.Shell;
using GlassShell.Core.SystemStatus;
using GlassShell.UI.Glass;

namespace GlassShell.UI.MenuBar;

public partial class MenuBarWindow : Window
{
    private readonly MenuBarViewModel _vm = new();
    private readonly SystemStatusProvider _statusProvider = new();
    private readonly MenuCommandDispatcher _dispatcher = new();
    private readonly TaskbarController _taskbarController;
    private readonly IWindowTracker _windowTracker;
    private readonly DxgiBackdropSampler _sampler;
    private readonly LiquidGlassMenuPopup _menuPopup = new();
    private readonly CalendarPopup _calendarPopup = new();
    private readonly DispatcherTimer _statusTimer;

    private MonitorContext _monitor;
    private Button? _activeMenuButton;
    private uint _taskbarCreatedMsg;

    public MenuBarWindow(
        MonitorContext monitor,
        TaskbarController taskbarController,
        IWindowTracker windowTracker,
        DxgiBackdropSampler sampler,
        GlassSettings settings,
        bool isDarkMode)
    {
        InitializeComponent();
        _monitor = monitor;
        _taskbarController = taskbarController;
        _windowTracker = windowTracker;
        _sampler = sampler;
        DataContext = _vm;

        BarGlassBackdrop.AttachSampler(sampler);
        _menuPopup.Closed += (_, _) => ClearActiveMenuButton();
        ApplySettings(settings, isDarkMode);
        PositionToMonitor(monitor);

        SourceInitialized += OnSourceInitialized;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshSystemStatus();
        _statusTimer.Start();
        RefreshSystemStatus();
    }

    public nint Hwnd => new WindowInteropHelper(this).Handle;

    public event Action? SpotlightRequested;
    public event Action? ControlCenterRequested;
    public event Action? SettingsRequested;
    public event Action? RestoreNativeTaskbarRequested;

    public void ApplySettings(GlassSettings settings, bool isDarkMode)
    {
        _vm.ApplySettings(settings, isDarkMode);
        BarGlassBackdrop.OpacityPreset = settings.OpacityPreset;
        BarGlassBackdrop.IsDarkMode = isDarkMode;
        BarGlassBackdrop.Visibility = settings.ShowMenuBarBackground ? Visibility.Visible : Visibility.Collapsed;
        _menuPopup.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        _calendarPopup.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        UpdateTopBarInk(settings.ShowMenuBarBackground, isDarkMode);
    }

    public void DismissPopups()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _menuPopup.IsOpen = false;
            _calendarPopup.IsOpen = false;
        });
    }

    private void UpdateTopBarInk(bool showGlassBackground, bool isDarkMode)
    {
        bool useDarkInk = showGlassBackground
            ? !isDarkMode
            : WallpaperLuminanceSampler.ShouldUseDarkInkForTopBar();

        Resources["MenuBarForegroundBrush"] = new System.Windows.Media.SolidColorBrush(
            useDarkInk
                ? System.Windows.Media.Color.FromArgb(0xF0, 0x10, 0x11, 0x16)
                : System.Windows.Media.Color.FromArgb(0xF8, 0xFF, 0xFF, 0xFF));

        Resources["MenuBarHoverBrush"] = new System.Windows.Media.SolidColorBrush(
            useDarkInk
                ? System.Windows.Media.Color.FromArgb(0x1E, 0x00, 0x00, 0x00)
                : System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        Resources["MenuBarActiveBrush"] = new System.Windows.Media.SolidColorBrush(
            useDarkInk
                ? System.Windows.Media.Color.FromArgb(0x32, 0x00, 0x00, 0x00)
                : System.Windows.Media.Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

        Resources["MenuBarInverseDotBrush"] = new System.Windows.Media.SolidColorBrush(
            useDarkInk
                ? System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF7)
                : System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1E));
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop or Microsoft.Win32.UserPreferenceCategory.General)
        {
            Dispatcher.InvokeAsync(() => UpdateTopBarInk(_vm.ShowMenuBarBackground, _vm.IsDarkMode));
        }
    }

    public void UpdateMenuSnapshot(MenuBarSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => _vm.ApplyMenuSnapshot(snapshot));
    }

    public void PositionToMonitor(MonitorContext monitor)
    {
        _monitor = monitor;
        Left = monitor.Bounds.Left / monitor.DpiScale;
        Top = monitor.Bounds.Top / monitor.DpiScale;
        Width = monitor.WidthDip;
        Height = 28;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd;
        WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd);
        _menuPopup.OwnerAnchorHwnd = hwnd;
        _calendarPopup.OwnerAnchorHwnd = hwnd;

        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            _taskbarController.HandleExplorerRestart();
        }
        return 0;
    }

    private void RefreshSystemStatus()
    {
        var status = _statusProvider.Capture(_vm.ActiveTargetHwnd);
        _vm.ApplySystemStatus(status);

        BatteryButton.Visibility = status.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (status.HasBattery)
        {
            BatteryPercentText.Text = $"{status.BatteryPercent}%";
            BatteryFillBar.Width = Math.Max(2, 16.0 * status.BatteryPercent / 100.0);
        }
    }

    private void OnWinLogoClick(object sender, RoutedEventArgs e) => ToggleMenu(WinLogoButton, BuildWinSystemMenu());

    private void OnBarBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == RootGrid)
        {
            _menuPopup.IsOpen = false;
            _calendarPopup.IsOpen = false;
        }
    }

    private void OnWinLogoHover(object sender, MouseEventArgs e)
    {
        if (_menuPopup.IsOpen && _activeMenuButton != WinLogoButton)
        {
            OpenMenu(WinLogoButton, BuildWinSystemMenu());
        }
    }

    private void OnActiveAppClick(object sender, RoutedEventArgs e) => ToggleMenu(ActiveAppButton, BuildActiveAppMenu());

    private void OnActiveAppHover(object sender, MouseEventArgs e)
    {
        if (_menuPopup.IsOpen && _activeMenuButton != ActiveAppButton)
        {
            OpenMenu(ActiveAppButton, BuildActiveAppMenu());
        }
    }

    private void OnDynamicMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is MenuNode node)
        {
            var entries = LiquidGlassMenuPopup.FromMenuNodes(node.Children, InvokeMenuNode);
            ToggleMenu(btn, entries);
        }
    }

    private void OnDynamicMenuHover(object sender, MouseEventArgs e)
    {
        if (_menuPopup.IsOpen && sender is Button btn && btn != _activeMenuButton && btn.DataContext is MenuNode node)
        {
            var entries = LiquidGlassMenuPopup.FromMenuNodes(node.Children, InvokeMenuNode);
            OpenMenu(btn, entries);
        }
    }

    private void ToggleMenu(Button button, IReadOnlyList<PopupMenuEntry> entries)
    {
        if (_menuPopup.IsOpen && _activeMenuButton == button)
        {
            _menuPopup.IsOpen = false;
            return;
        }
        OpenMenu(button, entries);
    }

    private void OpenMenu(Button button, IReadOnlyList<PopupMenuEntry> entries)
    {
        _calendarPopup.IsOpen = false;
        ClearActiveMenuButton();
        _activeMenuButton = button;
        button.Tag = "Active";

        _menuPopup.PlacementTarget = button;
        _menuPopup.HorizontalOffset = -8;
        _menuPopup.VerticalOffset = 2;
        _menuPopup.Populate(entries, _vm.IsDarkMode);
        _menuPopup.IsOpen = true;
    }

    private void ClearActiveMenuButton()
    {
        if (_activeMenuButton is not null)
        {
            _activeMenuButton.Tag = null;
            _activeMenuButton = null;
        }
    }

    private void InvokeMenuNode(MenuNode node)
    {
        if (node.Action is null) return;
        _dispatcher.Dispatch(_vm.ActiveTargetHwnd, node.Action);
    }

    private IReadOnlyList<PopupMenuEntry> BuildWinSystemMenu() =>
    [
        new("关于本机", OnClick: () => new AboutPcDialog(_sampler, _vm.OpacityPreset, _vm.IsDarkMode).Show()),
        new(string.Empty, IsSeparator: true),
        new("系统设置…", OnClick: () => SettingsRequested?.Invoke()),
        new("Windows 设置…", OnClick: () => LaunchUri("ms-settings:")),
        new("Microsoft Store…", OnClick: () => LaunchUri("ms-windows-store:")),
        new(string.Empty, IsSeparator: true),
        new("强制退出…", "Ctrl+Shift+Esc", OnClick: () => new ForceQuitWindow(_windowTracker, _sampler, _vm.OpacityPreset, _vm.IsDarkMode).Show()),
        new(string.Empty, IsSeparator: true),
        new("睡眠", OnClick: () => SetSuspendState(false, false, false)),
        new("重新启动…", OnClick: () => Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false })),
        new("关机…", OnClick: () => Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false })),
        new(string.Empty, IsSeparator: true),
        new("锁定屏幕", "Win+L", OnClick: () => LockWorkStation()),
        new("注销当前用户…", OnClick: () => ExitWindowsEx(0, 0)),
        new(string.Empty, IsSeparator: true),
        new("恢复原生任务栏并退出 GlassShell", OnClick: () => RestoreNativeTaskbarRequested?.Invoke())
    ];

    private IReadOnlyList<PopupMenuEntry> BuildActiveAppMenu() =>
    [
        new($"关于 {_vm.ActiveAppName}", IsEnabled: false),
        new(string.Empty, IsSeparator: true),
        new($"隐藏 {_vm.ActiveAppName}", OnClick: () => _dispatcher.Dispatch(_vm.ActiveTargetHwnd, new MenuInvokeAction(MenuActionKind.WindowCommand, WindowOp: "minimize"))),
        new(string.Empty, IsSeparator: true),
        new($"退出 {_vm.ActiveAppName}", "Alt+F4", OnClick: () => _dispatcher.Dispatch(_vm.ActiveTargetHwnd, new MenuInvokeAction(MenuActionKind.WindowCommand, WindowOp: "close")))
    ];

    private void OnTrayPeekClick(object sender, RoutedEventArgs e)
    {
        if (_taskbarController.IsTrayPeekActive)
        {
            _taskbarController.EndTrayPeek();
            TrayPeekButton.Tag = null;
        }
        else
        {
            _taskbarController.BeginTrayPeek();
            TrayPeekButton.Tag = "Active";
        }
    }

    private void OnImeClick(object sender, RoutedEventArgs e)
    {
        _dispatcher.Dispatch(_vm.ActiveTargetHwnd, new MenuInvokeAction(MenuActionKind.KeyboardShortcut, Shortcut: "Win+Space"));
    }

    private void OnSpotlightClick(object sender, RoutedEventArgs e) => SpotlightRequested?.Invoke();
    private void OnControlCenterClick(object sender, RoutedEventArgs e) => ControlCenterRequested?.Invoke();

    private void OnClockClick(object sender, RoutedEventArgs e)
    {
        _menuPopup.IsOpen = false;
        if (_calendarPopup.IsOpen)
        {
            _calendarPopup.IsOpen = false;
            return;
        }

        _calendarPopup.PlacementTarget = ClockButton;
        _calendarPopup.HorizontalOffset = -170;
        _calendarPopup.VerticalOffset = 4;
        _calendarPopup.IsOpen = true;
    }

    private static void LaunchUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _statusTimer.Stop();
        BarGlassBackdrop.Dispose();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
