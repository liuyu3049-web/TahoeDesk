using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using GlassShell.Core.MenuEngine;
using GlassShell.Core.Shell;
using GlassShell.Core.SystemStatus;
using GlassShell.Core.Tray;
using GlassShell.UI.ControlCenter;
using GlassShell.UI.Glass;
using GlassShell.UI.Tray;

namespace GlassShell.UI.MenuBar;

public partial class MenuBarWindow : Window
{
    private readonly MenuBarViewModel _vm = new();
    private readonly SystemStatusProvider _statusProvider = new();
    private readonly MenuCommandDispatcher _dispatcher = new();
    private readonly TaskbarController _taskbarController;
    private readonly IWindowTracker _windowTracker;
    private readonly DxgiBackdropSampler _sampler;
    private readonly TrayProxyHost _trayProxyHost;
    private readonly GlobalHotkeyManager? _hotkeyManager;
    private readonly LiquidGlassMenuPopup _menuPopup = new();
    private readonly CalendarPopup _calendarPopup = new();
    private readonly TrayOverflowPopup _trayOverflowPopup = new();
    private readonly ControlCenterPopup _controlCenterPopup = new();
    private readonly DispatcherTimer _statusTimer;

    private MonitorContext _monitor;
    private GlassSettings _settings;
    private IReadOnlyList<TrayDisplayItem> _lastTrayItems = [];
    private Button? _activeMenuButton;
    private uint _taskbarCreatedMsg;

    public MenuBarWindow(
        MonitorContext monitor,
        TaskbarController taskbarController,
        IWindowTracker windowTracker,
        DxgiBackdropSampler sampler,
        TrayProxyHost trayProxyHost,
        GlassSettings settings,
        bool isDarkMode,
        GlobalHotkeyManager? hotkeyManager = null)
    {
        InitializeComponent();
        _monitor = monitor;
        _taskbarController = taskbarController;
        _windowTracker = windowTracker;
        _sampler = sampler;
        _trayProxyHost = trayProxyHost;
        _hotkeyManager = hotkeyManager;
        _settings = settings;
        DataContext = _vm;

        BarGlassBackdrop.AttachSampler(sampler);
        _menuPopup.Closed += (_, _) => ClearActiveMenuButton();
        _trayOverflowPopup.Closed += (_, _) => TrayOverflowButton.Tag = null;
        _trayOverflowPopup.TrayIconInvoked += HandleTrayIconInvoked;
        _trayOverflowPopup.TrayIconPinToggled += HandleTrayIconPinToggled;
        _trayOverflowPopup.NativeTaskbarPeekRequested += ToggleNativeTaskbarPeek;
        _controlCenterPopup.Closed += (_, _) => ControlCenterButton.Tag = null;
        _controlCenterPopup.ThemeChangeRequested += theme => SettingsUpdated?.Invoke(_settings with { Theme = theme });
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
    public event Action<GlassSettings>? SettingsUpdated;

    public void ApplySettings(GlassSettings settings, bool isDarkMode)
    {
        _settings = settings;
        _vm.ApplySettings(settings, isDarkMode);
        BarGlassBackdrop.OpacityPreset = settings.OpacityPreset;
        BarGlassBackdrop.IsDarkMode = isDarkMode;
        BarGlassBackdrop.Visibility = settings.ShowMenuBarBackground ? Visibility.Visible : Visibility.Collapsed;
        _menuPopup.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        _calendarPopup.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        _trayOverflowPopup.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        _controlCenterPopup.ConfigureVisuals(_sampler, settings, isDarkMode);
        UpdateTopBarInk(settings.ShowMenuBarBackground, isDarkMode);
        RebuildTrayVisuals();
    }

    public void UpdateTrayItems(IReadOnlyList<TrayDisplayItem> items)
    {
        _lastTrayItems = items;
        RebuildTrayVisuals();
    }

    public void DismissPopups()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _menuPopup.IsOpen = false;
            _calendarPopup.IsOpen = false;
            _trayOverflowPopup.IsOpen = false;
            _controlCenterPopup.IsOpen = false;
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

        // If the display already has a top reserved work-area strip (e.g. 32 DIP), match its exact height
        // so the Liquid Glass top menu bar sits 0px flush against maximized windows with zero gap.
        double workTopDip = monitor.WorkArea.Width > 0
            ? (monitor.WorkArea.Top - monitor.Bounds.Top) / monitor.DpiScale
            : 0;
        Height = workTopDip >= 28.0 && workTopDip <= 40.0 ? Math.Round(workTopDip) : 28.0;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd;
        WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd);
        _menuPopup.OwnerAnchorHwnd = hwnd;
        _calendarPopup.OwnerAnchorHwnd = hwnd;
        _trayOverflowPopup.OwnerAnchorHwnd = hwnd;
        _controlCenterPopup.OwnerAnchorHwnd = hwnd;

        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_hotkeyManager is not null && _hotkeyManager.ProcessWndProc(msg, wParam))
        {
            handled = true;
            return 0;
        }

        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg && !_trayProxyHost.IsSelfBroadcastActive)
        {
            _taskbarController.HandleExplorerRestart();
            if (_monitor.IsPrimary)
            {
                _trayProxyHost.HandleExternalExplorerRestart();
            }
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
            _trayOverflowPopup.IsOpen = false;
            _controlCenterPopup.IsOpen = false;
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
        _trayOverflowPopup.IsOpen = false;
        _controlCenterPopup.IsOpen = false;
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

    private void RebuildTrayVisuals()
    {
        InlineTrayPanel.Children.Clear();

        var records = _lastTrayItems.Select(i => i.Record).ToList();
        var bitmapByKey = _lastTrayItems.ToDictionary(i => i.Record.Key, i => i);
        var (barRecs, overflowRecs) = TrayIconRegistry.PartitionForMenuBar(
            records,
            _settings.PinnedTrayIconKeys,
            defaultInlineCount: 4);

        var inlineTiles = new List<(TrayDisplayItem Item, Border Tile)>(barRecs.Count);
        foreach (var rec in barRecs)
        {
            var item = bitmapByKey[rec.Key];
            var tile = BuildInlineTrayTile(item);
            InlineTrayPanel.Children.Add(tile);
            inlineTiles.Add((item, tile));
        }

        var overflowItems = overflowRecs.Select(r => bitmapByKey[r.Key]).ToList();
        _trayOverflowPopup.Populate(overflowItems);
        _trayOverflowPopup.UpdateNativePeekLabel(_taskbarController.IsTrayPeekActive);

        TrayOverflowButton.ToolTip = overflowItems.Count > 0
            ? $"已收纳 {overflowItems.Count} 个托盘图标（点击展开）"
            : "系统托盘与原生任务栏选项";

        if (_monitor.IsPrimary && inlineTiles.Count > 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                foreach (var (item, tile) in inlineTiles)
                {
                    if (!tile.IsLoaded || tile.ActualWidth < 2) continue;
                    try
                    {
                        var tl = tile.PointToScreen(new Point(0, 0));
                        var br = tile.PointToScreen(new Point(tile.ActualWidth, tile.ActualHeight));
                        _trayProxyHost.UpdateIconScreenRect(item.Record.Key, new ScreenRect(
                            (int)Math.Round(tl.X),
                            (int)Math.Round(tl.Y),
                            (int)Math.Round(br.X),
                            (int)Math.Round(br.Y)));
                    }
                    catch { }
                }
            });
        }
    }

    private Border BuildInlineTrayTile(TrayDisplayItem item)
    {
        UIElement iconEl;
        if (item.IconBitmap is not null)
        {
            var img = new Image
            {
                Source = item.IconBitmap,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconEl = img;
        }
        else
        {
            iconEl = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Resources["MenuBarForegroundBrush"] as Brush ?? Brushes.White
            };
        }

        var tile = new Border
        {
            Width = 24,
            Height = 22,
            Margin = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrWhiteSpace(item.Record.Tooltip) ? "托盘应用" : item.Record.Tooltip,
            Child = iconEl
        };

        tile.MouseEnter += (_, _) => tile.Background = Resources["MenuBarHoverBrush"] as Brush ?? Brushes.Transparent;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;

        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                HandleTrayIconPinToggled(item);
                return;
            }

            var (rect, pt) = CaptureTileMetrics(tile);
            var action = e.ClickCount >= 2 ? TrayMouseAction.LeftDoubleClick : TrayMouseAction.LeftClick;
            HandleTrayIconInvoked(item, action, rect, pt);
        };

        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var (rect, pt) = CaptureTileMetrics(tile);
            HandleTrayIconInvoked(item, TrayMouseAction.RightClick, rect, pt);
        };

        tile.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                var (rect, pt) = CaptureTileMetrics(tile);
                HandleTrayIconInvoked(item, TrayMouseAction.MiddleClick, rect, pt);
            }
        };

        return tile;
    }

    private static (ScreenRect Rect, Point AnchorPoint) CaptureTileMetrics(FrameworkElement tile)
    {
        try
        {
            var tl = tile.PointToScreen(new Point(0, 0));
            var br = tile.PointToScreen(new Point(tile.ActualWidth, tile.ActualHeight));
            var rect = new ScreenRect(
                (int)Math.Round(tl.X),
                (int)Math.Round(tl.Y),
                (int)Math.Round(br.X),
                (int)Math.Round(br.Y));
            return (rect, new Point((tl.X + br.X) * 0.5, br.Y));
        }
        catch
        {
            return (default, new Point(0, 0));
        }
    }

    private void HandleTrayIconInvoked(TrayDisplayItem item, TrayMouseAction action, ScreenRect rectPx, Point anchorPtPx)
    {
        _menuPopup.IsOpen = false;
        _calendarPopup.IsOpen = false;
        if (rectPx.Width > 0 && rectPx.Height > 0)
        {
            _trayProxyHost.UpdateIconScreenRect(item.Record.Key, rectPx);
        }
        _trayProxyHost.DispatchMouseAction(
            item.Record,
            action,
            (int)Math.Round(anchorPtPx.X),
            (int)Math.Round(anchorPtPx.Y));
    }

    private void HandleTrayIconPinToggled(TrayDisplayItem item)
    {
        var currentRecords = _lastTrayItems.Select(i => i.Record).ToList();
        var pinned = _settings.PinnedTrayIconKeys.Count > 0
            ? new List<string>(_settings.PinnedTrayIconKeys)
            : TrayIconRegistry.PartitionForMenuBar(currentRecords, null, defaultInlineCount: 4)
                .BarIcons.Select(r => r.PreferenceKey).ToList();

        string key = item.Record.PreferenceKey;
        int existingIdx = pinned.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(k, item.Record.Key.StablePreferenceId, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0)
        {
            pinned.RemoveAt(existingIdx);
        }
        else
        {
            pinned.Add(key);
        }

        SettingsUpdated?.Invoke(_settings with { PinnedTrayIconKeys = pinned });
    }

    private void OnTrayOverflowClick(object sender, RoutedEventArgs e)
    {
        _menuPopup.IsOpen = false;
        _calendarPopup.IsOpen = false;
        _controlCenterPopup.IsOpen = false;

        if (_trayOverflowPopup.IsOpen)
        {
            _trayOverflowPopup.IsOpen = false;
            return;
        }

        _trayOverflowPopup.UpdateNativePeekLabel(_taskbarController.IsTrayPeekActive);
        _trayOverflowPopup.PlacementTarget = TrayOverflowButton;
        _trayOverflowPopup.HorizontalOffset = -92;
        _trayOverflowPopup.VerticalOffset = 4;
        TrayOverflowButton.Tag = "Active";
        _trayOverflowPopup.IsOpen = true;
    }

    private void ToggleNativeTaskbarPeek()
    {
        if (_taskbarController.IsTrayPeekActive)
        {
            _taskbarController.EndTrayPeek();
        }
        else
        {
            _taskbarController.BeginTrayPeek();
        }
        _trayOverflowPopup.UpdateNativePeekLabel(_taskbarController.IsTrayPeekActive);
    }

    private void OnImeClick(object sender, RoutedEventArgs e)
    {
        _dispatcher.Dispatch(_vm.ActiveTargetHwnd, new MenuInvokeAction(MenuActionKind.KeyboardShortcut, Shortcut: "Win+Space"));
    }

    private void OnSpotlightClick(object sender, RoutedEventArgs e)
    {
        _menuPopup.IsOpen = false;
        _calendarPopup.IsOpen = false;
        _trayOverflowPopup.IsOpen = false;
        _controlCenterPopup.IsOpen = false;
        SpotlightRequested?.Invoke();
    }

    private void OnWifiBarClick(object sender, RoutedEventArgs e)
    {
        _menuPopup.IsOpen = false;
        _calendarPopup.IsOpen = false;
        _trayOverflowPopup.IsOpen = false;

        if (_controlCenterPopup.IsOpen)
        {
            _controlCenterPopup.IsOpen = false;
            return;
        }

        _controlCenterPopup.SetWifiDrawerExpanded(true);
        _controlCenterPopup.PlacementTarget = WifiBarButton;
        _controlCenterPopup.HorizontalOffset = -230;
        _controlCenterPopup.VerticalOffset = 4;
        ControlCenterButton.Tag = "Active";
        _controlCenterPopup.IsOpen = true;
    }

    private void OnControlCenterClick(object sender, RoutedEventArgs e)
    {
        _menuPopup.IsOpen = false;
        _calendarPopup.IsOpen = false;
        _trayOverflowPopup.IsOpen = false;

        if (_controlCenterPopup.IsOpen)
        {
            _controlCenterPopup.IsOpen = false;
            return;
        }

        _controlCenterPopup.SetWifiDrawerExpanded(false);
        _controlCenterPopup.PlacementTarget = ControlCenterButton;
        _controlCenterPopup.HorizontalOffset = -270;
        _controlCenterPopup.VerticalOffset = 4;
        ControlCenterButton.Tag = "Active";
        _controlCenterPopup.IsOpen = true;
        ControlCenterRequested?.Invoke();
    }

    private void OnClockClick(object sender, RoutedEventArgs e)
    {
        _menuPopup.IsOpen = false;
        _trayOverflowPopup.IsOpen = false;
        _controlCenterPopup.IsOpen = false;
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
