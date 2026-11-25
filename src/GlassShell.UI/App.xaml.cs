using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassShell.Core.Config;
using GlassShell.Core.Diagnostics;
using GlassShell.Core.Discovery;
using GlassShell.Core.MenuEngine;
using GlassShell.Core.Search;
using GlassShell.Core.Shell;
using GlassShell.UI.Dock;
using GlassShell.UI.Glass;
using GlassShell.UI.Launchpad;
using GlassShell.UI.MenuBar;
using GlassShell.UI.Settings;
using GlassShell.UI.Spotlight;
using GlassShell.UI.Tray;
using Microsoft.Win32;

namespace GlassShell.UI;

public partial class App : Application
{
    private sealed record DisplayShellPair(MonitorContext Monitor, MenuBarWindow MenuBar, DockWindow Dock);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _cleanExitEvent;
    private JsonSettingsStore? _settingsStore;
    private GlassSettings _settings = GlassSettings.Default;
    private TaskbarController? _taskbarController;
    private Win32TaskbarNative? _taskbarNative;
    private WindowTracker? _windowTracker;
    private ForegroundMenuCoordinator? _menuCoordinator;
    private DxgiBackdropSampler? _backdropSampler;
    private TrayProxyHost? _trayProxyHost;
    private LowLevelWinKeyHook? _winKeyHook;
    private GlobalHotkeyManager? _hotkeyManager;
    private RecycleBinService? _recycleBinService;
    private readonly WindowSnapshotCache<BitmapSource> _snapshotCache = new(16);
    private readonly List<DisplayShellPair> _shells = [];
    private LaunchpadWindow? _launchpadWindow;
    private SpotlightWindow? _spotlightWindow;
    private QuickSettingsWindow? _settingsWindow;
    private IReadOnlyList<InstalledAppEntry> _cachedApps = [];
    private IReadOnlyList<IndexedFileEntry> _cachedFiles = [];
    private DispatcherTimer? _periodicRefreshTimer;
    private DispatcherTimer? _foregroundReconcileTimer;
    private DispatcherTimer? _indexRefreshTimer;
    private DispatcherTimer? _displayRebuildDebounceTimer;
    private nint _lastForegroundHwnd;
    private string _lastForegroundTitle = string.Empty;
    private readonly LinkedList<string> _recentApps = new();
    private readonly List<string> _stableRunningOrder = [];
    private readonly object _dockStateGate = new();
    private DispatcherTimer? _dockDebounceTimer;
    private int _dockRefreshInFlight;
    private int? _originalTaskbarAutoHideState;
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            Win32TaskbarNative.RestorePrimaryTaskbarNow(restoreState: 0);
            Shutdown(0);
            return;
        }

        _singleInstanceMutex = new Mutex(true, "Local\\GlassShell_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("AppDomain", args.ExceptionObject as Exception);
            CleanupSafe();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            CleanupSafe();
            args.Handled = false;
        };
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Enable taskbar auto-hide BEFORE acquiring work area so Windows releases the bottom ~48px reserved strip
        // on EVERY monitor (primary + external), allowing maximized windows to truly fill the full screen!
        // Original state is passed to GlassShell.Watchdog so even a crash/force-kill restores it 100%.
        int origState = Win32TaskbarNative.GetTaskbarAutoHideState();
        if ((origState & Win32TaskbarNative.ABS_AUTOHIDE) == 0)
        {
            _originalTaskbarAutoHideState = origState;
            Win32TaskbarNative.SetTaskbarAutoHideState(origState | Win32TaskbarNative.ABS_AUTOHIDE);
        }

        LaunchWatchdog(_originalTaskbarAutoHideState);

        _settingsStore = new JsonSettingsStore();
        bool firstRun = !File.Exists(_settingsStore.FilePath);
        _settings = _settingsStore.Load();
        if (firstRun)
        {
            _settings = SeedDefaultPins(_settings);
            _settingsStore.Save(_settings);
        }
        else if (_settings.SettingsSchemaVersion < 3)
        {
            // One-time v3 defaults migration: hide Dock on maximized/fullscreen (NOT floating overlap), hide redundant MenuBar Wi-Fi
            _settings = _settings with
            {
                SettingsSchemaVersion = 3,
                DockAutoHide = DockAutoHideMode.Smart,
                ShowMenuBarWifiIcon = false,
                ShowMinimizedWindowsInDock = false
            };
            _settingsStore.Save(_settings);
        }

        EnsureNativeMinimizeAnimationEnabled();

        var monitors = MonitorContext.GetAllMonitors();
        bool isDark = ResolveIsDarkMode(_settings);

        _taskbarNative = new Win32TaskbarNative();
        _taskbarController = new TaskbarController(_taskbarNative);
        _windowTracker = new WindowTracker();
        _recycleBinService = new RecycleBinService();
        _backdropSampler = new DxgiBackdropSampler();
        _trayProxyHost = new TrayProxyHost();
        _winKeyHook = new LowLevelWinKeyHook { Enabled = _settings.InterceptWinKeyForLaunchpad };
        _hotkeyManager = new GlobalHotkeyManager();

        _launchpadWindow = new LaunchpadWindow(_backdropSampler);
        _launchpadWindow.ApplySettings(_settings, isDark);
        _launchpadWindow.RescanRequested += () => _ = RefreshSearchAndLaunchpadIndexAsync();
        _launchpadWindow.IsPinnedToDockCheck = path =>
        {
            string resolved = InstalledAppScanner.ResolveShortcutTargetOrSelf(path);
            return _settings.PinnedAppPaths.Any(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, resolved, StringComparison.OrdinalIgnoreCase));
        };
        _launchpadWindow.PinToDockToggled += ToggleAppPinnedToDock;
        _spotlightWindow = new SpotlightWindow(_backdropSampler, _settings, isDark);
        _spotlightWindow.SystemCommandRequested += HandleSpotlightSystemCommand;

        var extractors = new IMenuExtractor[]
        {
            new Win32GetMenuExtractor(),
            new StaticUiaMenuExtractor()
        };
        _menuCoordinator = new ForegroundMenuCoordinator(
            extractors,
            JsonProfileMenuProvider.CreateBuiltIn(),
            (uint)Environment.ProcessId);

        foreach (var monitor in monitors)
        {
            var menuBar = new MenuBarWindow(monitor, _taskbarController, _windowTracker, _backdropSampler, _trayProxyHost, _settings, isDark, _hotkeyManager);
            var dock = new DockWindow(monitor, _backdropSampler, _settings, isDark, _snapshotCache);

            menuBar.SettingsRequested += OpenQuickSettings;
            menuBar.SettingsUpdated += ApplyUpdatedSettings;
            menuBar.RestoreNativeTaskbarRequested += () =>
            {
                CleanupSafe();
                Shutdown(0);
            };
            menuBar.SpotlightRequested += () => ToggleSpotlight(monitor);
            menuBar.WindowFlashDetected += hwnd => _windowTracker.MarkWindowFlashing(hwnd);

            dock.LaunchpadRequested += () => ToggleLaunchpad(monitor);
            dock.SettingsUpdated += ApplyUpdatedSettings;
            dock.RemoveRecentAppRequested += RemoveRecentApp;
            dock.ActivateTrayAppRequested += HandleActivateTrayApp;

            menuBar.Show();
            dock.Show();

            _shells.Add(new DisplayShellPair(monitor, menuBar, dock));
        }

        _menuCoordinator.SnapshotChanged += snap =>
        {
            foreach (var s in _shells) s.MenuBar.UpdateMenuSnapshot(snap);
        };

        _taskbarController.BarsVisibilityChanged += visible => Dispatcher.InvokeAsync(() =>
        {
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            foreach (var s in _shells)
            {
                s.MenuBar.Visibility = vis;
                s.Dock.Visibility = vis;
            }
        });
        _taskbarController.DockYieldForTrayChanged += yield => Dispatcher.InvokeAsync(() =>
        {
            foreach (var s in _shells) s.Dock.SetYieldForTray(yield);
        });

        _windowTracker.TaskbarShownDetected += () => _taskbarController.HandleTaskbarShownEvent();
        _windowTracker.ForegroundChanged += OnForegroundChanged;
        _windowTracker.WindowMinimizing += OnWindowMinimizing;
        _windowTracker.WindowsChanged += ScheduleDockRefresh;

        // Acquire taskbar suppression + primary top AppBar matching MenuBar height 1:1
        var primary = _shells.FirstOrDefault(s => s.Monitor.IsPrimary) ?? _shells[0];
        _taskbarController.Acquire(primary.MenuBar.Hwnd, primary.Monitor, topBarDip: (int)Math.Round(primary.MenuBar.Height));
        foreach (var s in _shells)
        {
            if (s != primary)
            {
                int secHeightPx = (int)Math.Round(s.MenuBar.Height * s.Monitor.DpiScale);
                _taskbarNative.RegisterTopAppBar(s.MenuBar.Hwnd, secHeightPx, s.Monitor.Bounds);
            }
        }

        // Register global Spotlight hotkey (Alt+Space) on primary MenuBar HWND and start Win-key Launchpad hook
        _hotkeyManager.Register(primary.MenuBar.Hwnd, id: 1, _settings.SpotlightHotkey, () => ToggleSpotlight(GetCursorMonitor()));
        _winKeyHook.SingleWinPressed += () => Dispatcher.InvokeAsync(() => ToggleLaunchpad(GetCursorMonitor()));
        _winKeyHook.Start();

        _windowTracker.Start();

        // Start zero-injection Shell_TrayWnd tray proxy AFTER AppBar registration so SHAppBarMessage hits Explorer cleanly
        _trayProxyHost.IconsChanged += items => Dispatcher.InvokeAsync(() =>
        {
            foreach (var s in _shells) s.MenuBar.UpdateTrayItems(items);
        });
        _trayProxyHost.TrayIconFlashingDetected += pid => _windowTracker.MarkProcessFlashing(pid);
        _trayProxyHost.Start();

        _dockDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _dockDebounceTimer.Tick += (_, _) =>
        {
            _dockDebounceTimer.Stop();
            _ = RefreshDockItemsAsync();
        };

        _ = RefreshDockItemsAsync();
        OnForegroundChanged(_windowTracker.GetForegroundWindowInfo());
        _ = RefreshSearchAndLaunchpadIndexAsync();

        _periodicRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _periodicRefreshTimer.Tick += (_, _) => _ = RefreshDockItemsAsync();
        _periodicRefreshTimer.Start();

        _foregroundReconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _foregroundReconcileTimer.Tick += (_, _) => ReconcileForegroundAndMonitorVisibility();
        _foregroundReconcileTimer.Start();

        _displayRebuildDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _displayRebuildDebounceTimer.Tick += (_, _) =>
        {
            _displayRebuildDebounceTimer.Stop();
            RebuildShellsForCurrentMonitors();
        };

        _indexRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _indexRefreshTimer.Tick += (_, _) => _ = RefreshSearchAndLaunchpadIndexAsync();
        _indexRefreshTimer.Start();
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => CleanupSafe();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleDisplayRebuild();

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            ScheduleDisplayRebuild();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            ScheduleDisplayRebuild();
        }
    }

    private void ScheduleDisplayRebuild()
    {
        if (_cleanedUp) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_cleanedUp || _displayRebuildDebounceTimer is null) return;
            _displayRebuildDebounceTimer.Stop();
            _displayRebuildDebounceTimer.Start();
        });
    }

    private void RebuildShellsForCurrentMonitors()
    {
        if (_cleanedUp || _taskbarController is null || _taskbarNative is null || _windowTracker is null
            || _backdropSampler is null || _trayProxyHost is null || _hotkeyManager is null)
        {
            return;
        }

        try
        {
            var oldPrimary = _shells.FirstOrDefault(s => s.Monitor.IsPrimary) ?? _shells.FirstOrDefault();
            if (oldPrimary is not null)
            {
                _hotkeyManager.UnregisterAll();
            }

            foreach (var s in _shells)
            {
                if (s != oldPrimary)
                {
                    _taskbarNative.UnregisterAppBar(s.MenuBar.Hwnd);
                }
                s.MenuBar.Close();
                s.Dock.Close();
            }
            _shells.Clear();

            _backdropSampler.RefreshOutputs();

            var monitors = MonitorContext.GetAllMonitors();
            bool isDark = ResolveIsDarkMode(_settings);

            foreach (var monitor in monitors)
            {
                var menuBar = new MenuBarWindow(monitor, _taskbarController, _windowTracker, _backdropSampler, _trayProxyHost, _settings, isDark, _hotkeyManager);
                var dock = new DockWindow(monitor, _backdropSampler, _settings, isDark, _snapshotCache);

                menuBar.SettingsRequested += OpenQuickSettings;
                menuBar.SettingsUpdated += ApplyUpdatedSettings;
                menuBar.RestoreNativeTaskbarRequested += () =>
                {
                    CleanupSafe();
                    Shutdown(0);
                };
                menuBar.SpotlightRequested += () => ToggleSpotlight(monitor);
                menuBar.WindowFlashDetected += hwnd => _windowTracker.MarkWindowFlashing(hwnd);

                dock.LaunchpadRequested += () => ToggleLaunchpad(monitor);
                dock.SettingsUpdated += ApplyUpdatedSettings;
                dock.RemoveRecentAppRequested += RemoveRecentApp;
                dock.ActivateTrayAppRequested += HandleActivateTrayApp;

                menuBar.Show();
                dock.Show();

                _shells.Add(new DisplayShellPair(monitor, menuBar, dock));
            }

            var newPrimary = _shells.FirstOrDefault(s => s.Monitor.IsPrimary) ?? _shells[0];
            _taskbarController.Acquire(newPrimary.MenuBar.Hwnd, newPrimary.Monitor, topBarDip: (int)Math.Round(newPrimary.MenuBar.Height));
            foreach (var s in _shells)
            {
                if (s != newPrimary)
                {
                    int secHeightPx = (int)Math.Round(s.MenuBar.Height * s.Monitor.DpiScale);
                    _taskbarNative.RegisterTopAppBar(s.MenuBar.Hwnd, secHeightPx, s.Monitor.Bounds);
                }
            }

            _hotkeyManager.Register(newPrimary.MenuBar.Hwnd, id: 1, _settings.SpotlightHotkey, () => ToggleSpotlight(GetCursorMonitor()));

            _trayProxyHost.ResyncAllTrayIcons();
            var trayItems = _trayProxyHost.GetCurrentItems();
            foreach (var s in _shells) s.MenuBar.UpdateTrayItems(trayItems);
            _ = RefreshDockItemsAsync();
            OnForegroundChanged(_windowTracker.GetForegroundWindowInfo());
        }
        catch (Exception ex)
        {
            LogCrash("DisplayRebuild", ex);
        }
    }

    private void ReconcileForegroundAndMonitorVisibility()
    {
        if (_windowTracker is null) return;
        var fg = _windowTracker.GetForegroundWindowInfo();
        if (fg.Hwnd != 0 && fg.ProcessId != (uint)Environment.ProcessId)
        {
            if (fg.Hwnd != _lastForegroundHwnd || !string.Equals(fg.WindowTitle, _lastForegroundTitle, StringComparison.Ordinal))
            {
                OnForegroundChanged(fg);
            }
        }

        // Self-heal if Windows Explorer unexpectedly re-showed primary or secondary native taskbar!
        if (_taskbarController is { IsAcquired: true, IsTrayPeekActive: false }
            && _taskbarNative is not null
            && _taskbarNative.IsAnyNativeTaskbarVisible())
        {
            _taskbarController.HandleTaskbarShownEvent();
        }

        // Always evaluate per-monitor fullscreen & maximize states independently (catches maximize/un-maximize on same HWND!)
        EvaluatePerMonitorVisibility(fg);
    }

    private void EvaluatePerMonitorVisibility(ForegroundWindowInfo fg)
    {
        if (_windowTracker is null || _taskbarNative is null || _shells.Count == 0) return;

        bool d3dFs = _taskbarNative.IsD3DFullscreenActive();
        var visibleWindows = _windowTracker.GetVisibleWindowBounds();
        bool primaryFullscreen = false;

        foreach (var s in _shells)
        {
            var mon = s.Monitor;
            bool monFullscreen = false;
            bool monMaximized = false;

            foreach (var w in visibleWindows)
            {
                int cx = (w.Bounds.Left + w.Bounds.Right) / 2;
                int cy = (w.Bounds.Top + w.Bounds.Bottom) / 2;
                if (cx < mon.Bounds.Left || cx >= mon.Bounds.Right || cy < mon.Bounds.Top || cy >= mon.Bounds.Bottom)
                {
                    continue;
                }

                // True fullscreen covers entire monitor bounds within 2px tolerance
                if (w.Bounds.Left <= mon.Bounds.Left + 2
                    && w.Bounds.Top <= mon.Bounds.Top + 2
                    && w.Bounds.Right >= mon.Bounds.Right - 2
                    && w.Bounds.Bottom >= mon.Bounds.Bottom - 2)
                {
                    monFullscreen = true;
                }

                if (w.IsMaximized)
                {
                    monMaximized = true;
                }
            }

            if (mon.IsPrimary && d3dFs && fg.Hwnd != 0 && !fg.IsMinimized)
            {
                int fcx = (fg.Bounds.Left + fg.Bounds.Right) / 2;
                int fcy = (fg.Bounds.Top + fg.Bounds.Bottom) / 2;
                if (fcx >= mon.Bounds.Left && fcx < mon.Bounds.Right && fcy >= mon.Bounds.Top && fcy < mon.Bounds.Bottom)
                {
                    monFullscreen = true;
                }
            }

            if (mon.IsPrimary) primaryFullscreen = monFullscreen;

            var vis = monFullscreen ? Visibility.Collapsed : Visibility.Visible;
            if (s.MenuBar.Visibility != vis) s.MenuBar.Visibility = vis;
            if (s.Dock.Visibility != vis) s.Dock.Visibility = vis;
            s.Dock.SetBottomOverlap(monMaximized || monFullscreen);
        }

        if (_winKeyHook is not null)
        {
            _winKeyHook.IsFullscreenActive = primaryFullscreen;
        }
    }

    private void OnForegroundChanged(ForegroundWindowInfo fg)
    {
        if (_shells.Count == 0 || _taskbarController is null || _taskbarNative is null) return;

        uint selfPid = (uint)Environment.ProcessId;
        if (fg.ProcessId == selfPid)
        {
            return;
        }

        // User switched to this app -> clear any unread/flashing Dock badge for it!
        if (fg.ProcessId != 0)
        {
            _windowTracker?.ClearProcessFlashing(fg.ProcessId);
        }

        if (fg.Hwnd != _lastForegroundHwnd)
        {
            foreach (var s in _shells)
            {
                s.MenuBar.DismissPopups();
                s.Dock.DismissPopups();
            }
        }
        _lastForegroundHwnd = fg.Hwnd;
        _lastForegroundTitle = fg.WindowTitle;

        EvaluatePerMonitorVisibility(fg);

        if (_menuCoordinator is not null && fg.Hwnd != 0)
        {
            string displayName = FriendlyAppNameResolver.Resolve(fg.ProcessName, fg.ClassName, fg.WindowTitle);
            _ = _menuCoordinator.OnForegroundChangedAsync(fg.Hwnd, fg.ProcessId, fg.ProcessName, displayName);
        }
    }

    private void ScheduleDockRefresh()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_dockDebounceTimer is null || _cleanedUp) return;
            _dockDebounceTimer.Stop();
            _dockDebounceTimer.Start();
        });
    }

    private void RefreshDockItems() => _ = RefreshDockItemsAsync();

    private async Task RefreshDockItemsAsync()
    {
        if (_windowTracker is null || _recycleBinService is null || _shells.Count == 0 || _cleanedUp) return;
        if (Interlocked.CompareExchange(ref _dockRefreshInFlight, 1, 0) != 0) return;

        var settingsSnapshot = _settings;
        try
        {
            var items = await Task.Run(() =>
            {
                var windows = _windowTracker.GetTopLevelWindows();
                var trayExes = _trayProxyHost?.GetActiveTrayExecutablePaths();
                List<string> recentCopy;
                List<string> stableCopy;

                lock (_dockStateGate)
                {
                    var activeExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var w in windows)
                    {
                        if (string.IsNullOrWhiteSpace(w.ExecutablePath)) continue;
                        activeExes.Add(w.ExecutablePath);

                        if (!_stableRunningOrder.Any(p => string.Equals(p, w.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            _stableRunningOrder.Add(w.ExecutablePath);
                        }

                        if (!_recentApps.Any(p => string.Equals(p, w.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            _recentApps.AddFirst(w.ExecutablePath);
                            while (_recentApps.Count > 12) _recentApps.RemoveLast();
                        }
                    }

                    // Keep apps that closed to system tray (e.g. WeChat X button) in _stableRunningOrder so they stay in the Dock!
                    _stableRunningOrder.RemoveAll(p => !activeExes.Contains(p) && (trayExes is null || !trayExes.Contains(p)));
                    recentCopy = _recentApps.ToList();
                    stableCopy = _stableRunningOrder.ToList();
                }

                if (settingsSnapshot.ShowMinimizedWindowsInDock)
                {
                    var minimizedHwnds = new HashSet<nint>(windows.Where(w => w.IsMinimized).Select(w => w.Hwnd));
                    _snapshotCache.Prune(minimizedHwnds);
                }

                var rb = _recycleBinService.Query();
                return DockModelCoordinator.BuildDockItems(settingsSnapshot, windows, recentCopy, rb, stableCopy, trayExes);
            });

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var s in _shells) s.Dock.UpdateItems(items);
            });
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _dockRefreshInFlight, 0);
        }
    }

    private void ApplyUpdatedSettings(GlassSettings updated)
    {
        _settings = updated;
        _settingsStore?.Save(updated);
        bool isDark = ResolveIsDarkMode(updated);
        if (_winKeyHook is not null)
        {
            _winKeyHook.Enabled = updated.InterceptWinKeyForLaunchpad;
        }
        _spotlightWindow?.ApplySettings(updated, isDark);
        _launchpadWindow?.ApplySettings(updated, isDark);
        foreach (var s in _shells)
        {
            s.MenuBar.ApplySettings(updated, isDark);
            s.Dock.ApplySettings(updated, isDark);
        }
        RefreshDockItems();
    }

    private void ToggleAppPinnedToDock(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return;
        string resolved = InstalledAppScanner.ResolveShortcutTargetOrSelf(targetPath);
        bool exists = _settings.PinnedAppPaths.Any(p =>
            string.Equals(p, targetPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, resolved, StringComparison.OrdinalIgnoreCase));
        var nextPins = exists
            ? _settings.PinnedAppPaths.Where(p =>
                !string.Equals(p, targetPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p, resolved, StringComparison.OrdinalIgnoreCase)).ToList()
            : [.. _settings.PinnedAppPaths, resolved];
        ApplyUpdatedSettings(_settings with { PinnedAppPaths = nextPins });
    }

    private void HandleActivateTrayApp(string targetPath)
    {
        if (_trayProxyHost is not null && _trayProxyHost.TryActivateTrayAppByExePath(targetPath))
        {
            return;
        }

        // Fallback if tray icon wasn't captured yet: launch/focus target executable directly
        try
        {
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }
        catch { }
    }

    private void RemoveRecentApp(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return;
        lock (_dockStateGate)
        {
            var node = _recentApps.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    _recentApps.Remove(node);
                }
                node = next;
            }
        }
        RefreshDockItems();
    }

    private void OnWindowMinimizing(nint hwnd)
    {
        if (!_settings.ShowMinimizedWindowsInDock) return;
        var snap = WindowThumbnailCapturer.CaptureThumbnail(hwnd);
        if (snap is not null)
        {
            _snapshotCache.Put(hwnd, snap);
        }
    }

    private static void EnsureNativeMinimizeAnimationEnabled()
    {
        try
        {
            var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
            if (SystemParametersInfoW(0x0048 /* SPI_GETANIMATION */, info.cbSize, ref info, 0) && info.iMinAnimate == 0)
            {
                info.iMinAnimate = 1;
                SystemParametersInfoW(0x0049 /* SPI_SETANIMATION */, info.cbSize, ref info, 0x0002 /* SPIF_SENDCHANGE */);
            }
        }
        catch { }
    }

    private async Task RefreshSearchAndLaunchpadIndexAsync()
    {
        try
        {
            var tcs = new TaskCompletionSource<(IReadOnlyList<InstalledAppEntry>, IReadOnlyList<IndexedFileEntry>)>();
            // Dedicated STA thread so COM IShellLinkW resolves every .lnk shortcut reliably
            var thread = new Thread(() =>
            {
                try
                {
                    var apps = InstalledAppScanner.ScanInstalledApps();
                    var files = InstalledAppScanner.ScanLocalUserFiles(maxFiles: 300);
                    tcs.TrySetResult((apps, files));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            var (apps, files) = await tcs.Task;

            _cachedApps = apps;
            _cachedFiles = files;

            await Dispatcher.InvokeAsync(() =>
            {
                _spotlightWindow?.UpdateIndex(apps, files);
                _launchpadWindow?.SyncApps(apps);
            });
        }
        catch (Exception ex)
        {
            LogCrash("IndexRefresh", ex);
        }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassShell");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\r\n");
        }
        catch { }
    }

    private void ToggleLaunchpad(MonitorContext monitor)
    {
        if (_spotlightWindow is { IsVisible: true }) _spotlightWindow.Hide();
        foreach (var s in _shells)
        {
            s.MenuBar.DismissPopups();
            s.Dock.DismissPopups();
        }
        _launchpadWindow?.ToggleOnMonitor(monitor, _cachedApps);
    }

    private void ToggleSpotlight(MonitorContext monitor)
    {
        if (_launchpadWindow is { IsVisible: true }) _launchpadWindow.DismissSmooth();
        foreach (var s in _shells)
        {
            s.MenuBar.DismissPopups();
            s.Dock.DismissPopups();
        }
        _spotlightWindow?.ToggleOnMonitor(monitor);
    }

    private MonitorContext GetCursorMonitor()
    {
        if (GetCursorPos(out POINT pt))
        {
            foreach (var s in _shells)
            {
                var b = s.Monitor.Bounds;
                if (pt.X >= b.Left && pt.X < b.Right && pt.Y >= b.Top && pt.Y < b.Bottom)
                {
                    return s.Monitor;
                }
            }
        }
        return (_shells.FirstOrDefault(s => s.Monitor.IsPrimary) ?? _shells[0]).Monitor;
    }

    private void HandleSpotlightSystemCommand(string commandId)
    {
        switch (commandId)
        {
            case "cmd:lock":
                LockWorkStation();
                break;
            case "cmd:sleep":
                SetSuspendState(false, false, false);
                break;
            case "cmd:restart":
                Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                break;
            case "cmd:shutdown":
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                break;
            case "cmd:theme-dark":
                ApplyUpdatedSettings(_settings with { Theme = ThemeMode.Dark });
                break;
            case "cmd:theme-light":
                ApplyUpdatedSettings(_settings with { Theme = ThemeMode.Light });
                break;
            case "cmd:theme-system":
                ApplyUpdatedSettings(_settings with { Theme = ThemeMode.System });
                break;
            case "cmd:empty-bin":
                _recycleBinService?.Empty(0);
                RefreshDockItems();
                break;
            case "cmd:settings":
                OpenQuickSettings();
                break;
            case "cmd:win-settings":
                try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); } catch { }
                break;
            case "cmd:restore-taskbar":
                CleanupSafe();
                Shutdown(0);
                break;
        }
    }

    private void OpenQuickSettings()
    {
        if (_backdropSampler is null) return;
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new QuickSettingsWindow(
            _settings,
            _backdropSampler,
            ResolveIsDarkMode(_settings),
            ApplyUpdatedSettings,
            () =>
            {
                CleanupSafe();
                Shutdown(0);
            },
            ExportDiagnosticsReport);
        _settingsWindow.Show();
    }

    private string? ExportDiagnosticsReport()
    {
        try
        {
            var monitors = _shells.Select(s => s.Monitor).ToList();
            if (monitors.Count == 0) monitors = MonitorContext.GetAllMonitors().ToList();
            string crashLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlassShell",
                "crash.log");
            string report = DiagnosticsReportBuilder.BuildReport(
                _settings,
                monitors,
                _backdropSampler?.Health ?? GlassShell.Core.Glass.SamplerHealth.FallbackDegraded,
                crashLog);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string outPath = Path.Combine(desktop, $"GlassShell-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(outPath, report);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    private void LaunchWatchdog(int? restoreAutoHideState = null)
    {
        try
        {
            int pid = Environment.ProcessId;
            string eventName = $"Global\\GlassShell_CleanExit_{pid}";
            _cleanExitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

            string watchdogExe = Path.Combine(AppContext.BaseDirectory, "GlassShell.Watchdog.exe");
            if (File.Exists(watchdogExe))
            {
                string extra = restoreAutoHideState.HasValue ? $" --restore-autohide {restoreAutoHideState.Value}" : string.Empty;
                Process.Start(new ProcessStartInfo
                {
                    FileName = watchdogExe,
                    Arguments = $"--parent-pid {pid} --clean-event {eventName}{extra}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }
        catch { }
    }

    private static GlassSettings SeedDefaultPins(GlassSettings baseSettings)
    {
        string[] candidateApps =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
            @"C:\Windows\System32\notepad.exe"
        ];

        var pinnedApps = candidateApps.Where(File.Exists).ToList();
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var pinnedFolders = Directory.Exists(downloads) ? new List<string> { downloads } : [];

        return baseSettings with
        {
            PinnedAppPaths = pinnedApps,
            PinnedFolderPaths = pinnedFolders
        };
    }

    private static bool ResolveIsDarkMode(GlassSettings settings) => settings.Theme switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => WindowInteropHelperEx.IsSystemDarkTheme()
    };

    private void CleanupSafe()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
        try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { _displayRebuildDebounceTimer?.Stop(); } catch { }
        try { _periodicRefreshTimer?.Stop(); } catch { }
        try { _foregroundReconcileTimer?.Stop(); } catch { }
        try { _indexRefreshTimer?.Stop(); } catch { }
        try { _dockDebounceTimer?.Stop(); } catch { }
        try { _winKeyHook?.Dispose(); } catch { }
        try { _hotkeyManager?.Dispose(); } catch { }
        try { _windowTracker?.Dispose(); } catch { }
        try { _menuCoordinator?.Dispose(); } catch { }
        // Wallpaper-Engine-safe shutdown sequence:
        // 1. Destroy our proxy Shell_TrayWnd FIRST so no app can message a dying proxy window.
        // 2. Unregister AppBars, restore original auto-hide state, and show Explorer's real native taskbar.
        // 3. Broadcast TaskbarCreated LAST via _trayProxyHost.Dispose() so all apps cleanly re-bind to Explorer!
        try { _trayProxyHost?.DestroyProxyWindowOnly(); } catch { }
        try
        {
            if (_taskbarNative is not null)
            {
                foreach (var s in _shells)
                {
                    if (!s.Monitor.IsPrimary)
                    {
                        _taskbarNative.UnregisterAppBar(s.MenuBar.Hwnd);
                    }
                }
            }
        }
        catch { }
        try
        {
            if (_originalTaskbarAutoHideState.HasValue)
            {
                Win32TaskbarNative.SetTaskbarAutoHideState(_originalTaskbarAutoHideState.Value);
                _originalTaskbarAutoHideState = null;
            }
        }
        catch { }
        try { _taskbarController?.Release(); } catch { }
        try { _trayProxyHost?.Dispose(); } catch { }
        try { _backdropSampler?.Dispose(); } catch { }
        try { _cleanExitEvent?.Set(); } catch { }
        try { _cleanExitEvent?.Dispose(); } catch { }
        try { _singleInstanceMutex?.ReleaseMutex(); _singleInstanceMutex?.Dispose(); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupSafe();
        base.OnExit(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
