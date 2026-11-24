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
        else if (_settings.SettingsSchemaVersion < 2)
        {
            // One-time v2 defaults migration: keep Dock visible unless fullscreen & hide redundant MenuBar Wi-Fi icon
            _settings = _settings with
            {
                SettingsSchemaVersion = 2,
                DockAutoHide = DockAutoHideMode.Off,
                ShowMenuBarWifiIcon = false,
                ShowMinimizedWindowsInDock = false
            };
            _settingsStore.Save(_settings);
        }

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

        // Evaluate fullscreen & bottom overlap per monitor
        bool anyFullscreen = false;
        bool d3dFs = _taskbarNative.IsD3DFullscreenActive();
        foreach (var s in _shells)
        {
            bool isFs = FullscreenDetector.IsFullscreen(fg, s.Monitor, d3dFs);
            if (isFs) anyFullscreen = true;

            if (fg.Hwnd != 0 && !fg.IsMinimized && !fg.IsCloaked)
            {
                bool onThisMonitor = fg.Bounds.Right > s.Monitor.Bounds.Left && fg.Bounds.Left < s.Monitor.Bounds.Right;
                bool overlapsBottom = onThisMonitor
                    && fg.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                    && fg.Bounds.Bottom >= s.Monitor.Bounds.Bottom - (int)(84 * s.Monitor.DpiScale);
                Dispatcher.InvokeAsync(() => s.Dock.SetBottomOverlap(overlapsBottom));
            }
        }
        _taskbarController.UpdateFullscreenState(anyFullscreen);
        if (_winKeyHook is not null)
        {
            _winKeyHook.IsFullscreenActive = anyFullscreen;
        }

        if (_menuCoordinator is not null && fg.Hwnd != 0)
        {
            string displayName = FriendlyAppNameResolver.Resolve(fg.ProcessName, fg.ClassName, fg.WindowTitle);
            _ = _menuCoordinator.OnForegroundChangedAsync(fg.Hwnd, fg.ProcessId, fg.ProcessName, displayName);
        }

        // macOS Stage Manager (台前调度): keep only the active application's windows visible on stage
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
                return DockModelCoordinator.BuildDockItems(settingsSnapshot, windows, recentCopy, rb, stableCopy);
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
        foreach (var s in _shells)
        {
            s.MenuBar.ApplySettings(updated, isDark);
            s.Dock.ApplySettings(updated, isDark);
        }
        RefreshDockItems();
    }

    private void OnWindowMinimizing(nint hwnd)
    {
        var snap = WindowThumbnailCapturer.CaptureThumbnail(hwnd, 480, 320);
        if (snap is null) return;

        _snapshotCache.Put(hwnd, snap);

        // Play macOS-style scale-down-into-Dock-icon transition on the monitor hosting this window
        var shell = FindShellForWindow(hwnd);
        if (shell is null) return;

        if (WindowDockAnimator.TryGetWindowRectDip(hwnd, shell.Monitor.DpiScale, out var winRectDip))
        {
            Dispatcher.BeginInvoke(() =>
            {
                shell.Dock.TryGetIconScreenCenterDip(hwnd, exePath: null, out var iconCenterDip);
                WindowDockAnimator.PlayMinimizeToDock(snap, winRectDip, iconCenterDip);
            });
        }
    }

    private void HandleRestoreFromDock(MonitorContext monitor, nint hwnd, Point iconCenterDip)
    {
        if (WindowDockAnimator.TryGetWindowRectDip(hwnd, monitor.DpiScale, out var targetRectDip))
        {
            _snapshotCache.TryGet(hwnd, out var cachedSnap);
            WindowDockAnimator.PlayRestoreFromDock(cachedSnap, iconCenterDip, targetRectDip);
        }
        WindowTracker.ActivateWindow(hwnd);
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
                    ShowWindowAsync(w.Hwnd, 6 /* SW_MINIMIZE */);
                }
            }
            catch { }
        });
    }

    private DisplayShellPair? FindShellForWindow(nint hwnd)
    {
        if (_shells.Count == 0) return null;
        if (GetWindowRect(hwnd, out RECT rc))
        {
            int cx = (rc.Left + rc.Right) / 2;
            int cy = (rc.Top + rc.Bottom) / 2;
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
