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
using GlassShell.UI.StageManager;
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, ScreenRect> _lastWindowBounds = new();
    private readonly List<DisplayShellPair> _shells = [];
    private LaunchpadWindow? _launchpadWindow;
    private SpotlightWindow? _spotlightWindow;
    private StageManagerWindow? _stageManagerWindow;
    private QuickSettingsWindow? _settingsWindow;
    private int _originalMinAnimate = 1;
    private bool _suppressedMinAnimate;
    private uint _lastActiveProcessId;
    private IReadOnlyList<InstalledAppEntry> _cachedApps = [];
    private IReadOnlyList<IndexedFileEntry> _cachedFiles = [];
    private DispatcherTimer? _periodicRefreshTimer;
    private DispatcherTimer? _foregroundReconcileTimer;
    private DispatcherTimer? _indexRefreshTimer;
    private nint _lastForegroundHwnd;
    private string _lastForegroundTitle = string.Empty;
    private readonly LinkedList<string> _recentApps = new();
    private readonly List<string> _stableRunningOrder = [];
    private readonly object _dockStateGate = new();
    private DispatcherTimer? _dockDebounceTimer;
    private int _dockRefreshInFlight;
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            Win32TaskbarNative.RestorePrimaryTaskbarNow();
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
        SystemEvents.SessionEnding += (_, _) => CleanupSafe();

        LaunchWatchdog();

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

        SuppressSystemMinimizeAnimation();

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
        _spotlightWindow = new SpotlightWindow(_backdropSampler, _settings, isDark);
        _spotlightWindow.SystemCommandRequested += HandleSpotlightSystemCommand;

        var primaryMon = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        _stageManagerWindow = new StageManagerWindow(primaryMon, isDark);
        _stageManagerWindow.AppGroupSelected += OnStageManagerGroupSelected;
        if (_settings.StageManagerEnabled)
        {
            _stageManagerWindow.Show();
        }

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

            dock.LaunchpadRequested += () => ToggleLaunchpad(monitor);
            dock.SettingsUpdated += ApplyUpdatedSettings;
            dock.RestoreFromDockRequested += (hwnd, iconCenterDip) => HandleRestoreFromDock(monitor, hwnd, iconCenterDip);

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
            if (_stageManagerWindow is not null)
            {
                _stageManagerWindow.Visibility = visible && _settings.StageManagerEnabled ? Visibility.Visible : Visibility.Collapsed;
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
        _foregroundReconcileTimer.Tick += (_, _) => ReconcileForegroundWindow();
        _foregroundReconcileTimer.Start();

        _indexRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _indexRefreshTimer.Tick += (_, _) => _ = RefreshSearchAndLaunchpadIndexAsync();
        _indexRefreshTimer.Start();
    }

    private void ReconcileForegroundWindow()
    {
        if (_windowTracker is null) return;
        var fg = _windowTracker.GetForegroundWindowInfo();
        if (fg.Hwnd == 0 || fg.ProcessId == (uint)Environment.ProcessId) return;

        if (fg.Hwnd != _lastForegroundHwnd || !string.Equals(fg.WindowTitle, _lastForegroundTitle, StringComparison.Ordinal))
        {
            OnForegroundChanged(fg);
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

        // Evaluate fullscreen & maximized auto-hide per monitor (floating windows NEVER hide Dock!)
        bool anyFullscreen = false;
        bool d3dFs = _taskbarNative.IsD3DFullscreenActive();
        foreach (var s in _shells)
        {
            bool isFs = FullscreenDetector.IsFullscreen(fg, s.Monitor, d3dFs);
            if (isFs) anyFullscreen = true;

            if (fg.Hwnd != 0 && !fg.IsMinimized && !fg.IsCloaked)
            {
                bool onThisMonitor = fg.Bounds.Right > s.Monitor.Bounds.Left && fg.Bounds.Left < s.Monitor.Bounds.Right;
                bool isNormalWindow = fg.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
                bool shouldHideForMaximized = onThisMonitor && isNormalWindow && fg.IsMaximized;
                Dispatcher.InvokeAsync(() => s.Dock.SetBottomOverlap(shouldHideForMaximized));
            }
        }
        _taskbarController.UpdateFullscreenState(anyFullscreen);
        if (_winKeyHook is not null)
        {
            _winKeyHook.IsFullscreenActive = anyFullscreen;
        }

        // Record live pre-minimize window rect & fresh snapshot so Dock minimize animation ALWAYS works cleanly
        if (fg.Hwnd != 0 && !fg.IsMinimized && !fg.IsCloaked && fg.Bounds.Width > 80 && fg.Bounds.Height > 60
            && fg.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
        {
            _lastWindowBounds[fg.Hwnd] = fg.Bounds;
            _lastActiveProcessId = fg.ProcessId;
            nint captureTarget = fg.Hwnd;
            _ = Task.Run(() =>
            {
                var snap = WindowThumbnailCapturer.CaptureThumbnail(captureTarget, 420, 280);
                if (snap is not null) _snapshotCache.Put(captureTarget, snap);
            });
        }

        if (_menuCoordinator is not null && fg.Hwnd != 0)
        {
            string displayName = FriendlyAppNameResolver.Resolve(fg.ProcessName, fg.ClassName, fg.WindowTitle);
            _ = _menuCoordinator.OnForegroundChangedAsync(fg.Hwnd, fg.ProcessId, fg.ProcessName, displayName);
        }

        // macOS Stage Manager (台前调度): keep only the active application's windows visible on stage & update left strip
        if (_settings.StageManagerEnabled
            && fg.Hwnd != 0
            && fg.ProcessId != 0
            && fg.ProcessId != selfPid
            && fg.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
        {
            ApplyStageManagerFocus(fg.ProcessId);
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

                    _stableRunningOrder.RemoveAll(p => !activeExes.Contains(p));
                    recentCopy = _recentApps.ToList();
                    stableCopy = _stableRunningOrder.ToList();
                }

                if (settingsSnapshot.ShowMinimizedWindowsInDock)
                {
                    var minimizedHwnds = new HashSet<nint>(windows.Where(w => w.IsMinimized).Select(w => w.Hwnd));
                    _snapshotCache.Prune(minimizedHwnds);
                }

                var rb = _recycleBinService.Query();
                var dockItems = DockModelCoordinator.BuildDockItems(settingsSnapshot, windows, recentCopy, rb, stableCopy);

                List<StageGroupItem>? stageGroups = null;
                if (settingsSnapshot.StageManagerEnabled)
                {
                    stageGroups = BuildStageGroups(windows, _lastActiveProcessId);
                }

                return (dockItems, stageGroups);
            });

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var s in _shells) s.Dock.UpdateItems(items.dockItems);
                if (items.stageGroups is not null && _stageManagerWindow is not null)
                {
                    _stageManagerWindow.UpdateGroups(items.stageGroups);
                }
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
        bool stageToggled = _settings.StageManagerEnabled != updated.StageManagerEnabled;
        _settings = updated;
        _settingsStore?.Save(updated);
        bool isDark = ResolveIsDarkMode(updated);
        if (_winKeyHook is not null)
        {
            _winKeyHook.Enabled = updated.InterceptWinKeyForLaunchpad;
        }
        _spotlightWindow?.ApplySettings(updated, isDark);
        _stageManagerWindow?.ApplyTheme(isDark);
        if (_stageManagerWindow is not null && stageToggled)
        {
            if (updated.StageManagerEnabled)
            {
                _stageManagerWindow.Show();
                if (_lastActiveProcessId != 0)
                {
                    ApplyStageManagerFocus(_lastActiveProcessId);
                }
            }
            else
            {
                _stageManagerWindow.Hide();
            }
        }
        foreach (var s in _shells)
        {
            s.MenuBar.ApplySettings(updated, isDark);
            s.Dock.ApplySettings(updated, isDark);
        }
        RefreshDockItems();
    }

    private void OnWindowMinimizing(nint hwnd)
    {
        // Try live capture first; if Windows already marked HWND iconic during async hook delivery, reuse cached snapshot!
        var snap = WindowThumbnailCapturer.CaptureThumbnail(hwnd, 420, 280);
        if (snap is not null)
        {
            _snapshotCache.Put(hwnd, snap);
        }
        else
        {
            _snapshotCache.TryGet(hwnd, out snap);
        }

        // Resolve pre-minimize bounds from our live bounds cache first (never (-32000,-32000)!), then GetWindowPlacement
        ScreenRect? cachedPx = _lastWindowBounds.TryGetValue(hwnd, out var prevRect) ? prevRect : null;
        var shell = FindShellForWindow(hwnd, cachedPx);
        if (shell is null) return;

        Rect winRectDip;
        if (cachedPx is { Width: > 60, Height: > 48 } r)
        {
            double dpi = shell.Monitor.DpiScale;
            winRectDip = new Rect(r.Left / dpi, r.Top / dpi, r.Width / dpi, r.Height / dpi);
        }
        else if (!WindowDockAnimator.TryGetWindowRectDip(hwnd, shell.Monitor.DpiScale, out winRectDip))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            shell.Dock.TryGetIconScreenCenterDip(hwnd, exePath: null, out var iconCenterDip);
            WindowDockAnimator.PlayMinimizeToDock(snap!, winRectDip, iconCenterDip);
        });
    }

    private void HandleRestoreFromDock(MonitorContext monitor, nint hwnd, Point iconCenterDip)
    {
        Rect targetRectDip;
        if (_lastWindowBounds.TryGetValue(hwnd, out var cached) && cached.Width > 60 && cached.Height > 48)
        {
            targetRectDip = new Rect(cached.Left / monitor.DpiScale, cached.Top / monitor.DpiScale, cached.Width / monitor.DpiScale, cached.Height / monitor.DpiScale);
        }
        else if (!WindowDockAnimator.TryGetWindowRectDip(hwnd, monitor.DpiScale, out targetRectDip))
        {
            targetRectDip = new Rect((monitor.Bounds.Left / monitor.DpiScale) + 120, (monitor.Bounds.Top / monitor.DpiScale) + 80, monitor.WidthDip * 0.7, monitor.HeightDip * 0.7);
        }

        _snapshotCache.TryGet(hwnd, out var cachedSnap);
        WindowDockAnimator.PlayRestoreFromDock(cachedSnap, iconCenterDip, targetRectDip);
        WindowTracker.ActivateWindow(hwnd);
    }

    private void OnStageManagerGroupSelected(nint hwnd, uint processId)
    {
        _lastActiveProcessId = processId;
        WindowTracker.ActivateWindow(hwnd);
        ApplyStageManagerFocus(processId);
    }

    private List<StageGroupItem> BuildStageGroups(IReadOnlyList<WindowInfo> windows, uint activeProcessId)
    {
        return windows
            .Where(w => w.Hwnd != 0
                && w.ProcessId != activeProcessId
                && w.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
            .GroupBy(w => w.ProcessId)
            .Take(5)
            .Select(g =>
            {
                var rep = g.First();
                _snapshotCache.TryGet(rep.Hwnd, out var snap);
                return new StageGroupItem(
                    rep.Hwnd,
                    rep.ProcessId,
                    rep.AppDisplayName,
                    rep.Title,
                    rep.ExecutablePath,
                    g.Count(),
                    snap);
            })
            .ToList();
    }

    private void ApplyStageManagerFocus(uint activeProcessId)
    {
        if (_windowTracker is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                var windows = _windowTracker.GetTopLevelWindows();
                foreach (var w in windows)
                {
                    if (w.Hwnd == 0 || w.IsMinimized || w.ProcessId == activeProcessId) continue;
                    if (w.ClassName is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") continue;

                    var snap = WindowThumbnailCapturer.CaptureThumbnail(w.Hwnd, 320, 200);
                    if (snap is not null) _snapshotCache.Put(w.Hwnd, snap);
                    ShowWindowAsync(w.Hwnd, 6 /* SW_MINIMIZE */);
                }

                var groups = BuildStageGroups(windows, activeProcessId);
                Dispatcher.BeginInvoke(() => _stageManagerWindow?.UpdateGroups(groups));
            }
            catch { }
        });
    }

    private DisplayShellPair? FindShellForWindow(nint hwnd, ScreenRect? cachedRect = null)
    {
        if (_shells.Count == 0) return null;
        int cx = 0, cy = 0;
        bool hasPt = false;
        if (cachedRect is { Width: > 0, Height: > 0 } r)
        {
            cx = (r.Left + r.Right) / 2;
            cy = (r.Top + r.Bottom) / 2;
            hasPt = true;
        }
        else if (GetWindowRect(hwnd, out RECT rc) && rc.Left > -10000)
        {
            cx = (rc.Left + rc.Right) / 2;
            cy = (rc.Top + rc.Bottom) / 2;
            hasPt = true;
        }

        if (hasPt)
        {
            foreach (var s in _shells)
            {
                var b = s.Monitor.Bounds;
                if (cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom)
                {
                    return s;
                }
            }
        }
        return _shells.FirstOrDefault(s => s.Monitor.IsPrimary) ?? _shells[0];
    }

    private void SuppressSystemMinimizeAnimation()
    {
        try
        {
            var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
            if (SystemParametersInfoW(0x0048 /* SPI_GETANIMATION */, info.cbSize, ref info, 0))
            {
                _originalMinAnimate = info.iMinAnimate;
                if (info.iMinAnimate != 0)
                {
                    info.iMinAnimate = 0;
                    if (SystemParametersInfoW(0x0049 /* SPI_SETANIMATION */, info.cbSize, ref info, 0))
                    {
                        _suppressedMinAnimate = true;
                    }
                }
            }
        }
        catch { }
    }

    private void RestoreSystemMinimizeAnimation()
    {
        if (!_suppressedMinAnimate) return;
        _suppressedMinAnimate = false;
        try
        {
            var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = _originalMinAnimate };
            SystemParametersInfoW(0x0049 /* SPI_SETANIMATION */, info.cbSize, ref info, 0);
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
            });
        _settingsWindow.Show();
    }

    private void LaunchWatchdog()
    {
        try
        {
            int pid = Environment.ProcessId;
            string eventName = $"Global\\GlassShell_CleanExit_{pid}";
            _cleanExitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

            string watchdogExe = Path.Combine(AppContext.BaseDirectory, "GlassShell.Watchdog.exe");
            if (File.Exists(watchdogExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = watchdogExe,
                    Arguments = $"--parent-pid {pid} --clean-event {eventName}",
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

        try { _periodicRefreshTimer?.Stop(); } catch { }
        try { _foregroundReconcileTimer?.Stop(); } catch { }
        try { _indexRefreshTimer?.Stop(); } catch { }
        try { _dockDebounceTimer?.Stop(); } catch { }
        try { _winKeyHook?.Dispose(); } catch { }
        try { _hotkeyManager?.Dispose(); } catch { }
        try { _windowTracker?.Dispose(); } catch { }
        try { _menuCoordinator?.Dispose(); } catch { }
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
        // Restore native taskbar visibility FIRST, then destroy our Shell_TrayWnd proxy & broadcast TaskbarCreated
        // so all running tray apps immediately re-register onto Explorer's restored taskbar.
        try { _taskbarController?.Release(); } catch { }
        try { RestoreSystemMinimizeAnimation(); } catch { }
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
    private struct RECT { public int Left, Top, Right, Bottom; }

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
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
