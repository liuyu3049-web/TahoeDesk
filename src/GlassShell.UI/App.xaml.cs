using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using GlassShell.Core.MenuEngine;
using GlassShell.Core.Shell;
using GlassShell.UI.Dock;
using GlassShell.UI.Glass;
using GlassShell.UI.MenuBar;
using GlassShell.UI.Settings;
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
    private RecycleBinService? _recycleBinService;
    private readonly List<DisplayShellPair> _shells = [];
    private QuickSettingsWindow? _settingsWindow;
    private DispatcherTimer? _periodicRefreshTimer;
    private DispatcherTimer? _foregroundReconcileTimer;
    private nint _lastForegroundHwnd;
    private string _lastForegroundTitle = string.Empty;
    private readonly LinkedList<string> _recentApps = new();
    private readonly List<string> _stableRunningOrder = [];
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

        AppDomain.CurrentDomain.UnhandledException += (_, _) => CleanupSafe();
        DispatcherUnhandledException += (_, args) =>
        {
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

        var monitors = MonitorContext.GetAllMonitors();
        bool isDark = ResolveIsDarkMode(_settings);

        _taskbarNative = new Win32TaskbarNative();
        _taskbarController = new TaskbarController(_taskbarNative);
        _windowTracker = new WindowTracker();
        _recycleBinService = new RecycleBinService();
        _backdropSampler = new DxgiBackdropSampler();
        _trayProxyHost = new TrayProxyHost();

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
            var menuBar = new MenuBarWindow(monitor, _taskbarController, _windowTracker, _backdropSampler, _trayProxyHost, _settings, isDark);
            var dock = new DockWindow(monitor, _backdropSampler, _settings, isDark);

            menuBar.SettingsRequested += OpenQuickSettings;
            menuBar.SettingsUpdated += ApplyUpdatedSettings;
            menuBar.RestoreNativeTaskbarRequested += () =>
            {
                CleanupSafe();
                Shutdown(0);
            };
            menuBar.SpotlightRequested += () => ShowPhase2Notice("聚焦搜索 (Spotlight) 将在阶段二启用");
            menuBar.ControlCenterRequested += () => ShowPhase2Notice("控制中心 (Control Center) 将在阶段二启用");

            dock.LaunchpadRequested += () => ShowPhase2Notice("启动台 (Launchpad) 将在阶段二启用");
            dock.SettingsUpdated += ApplyUpdatedSettings;

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
        _windowTracker.WindowsChanged += () => Dispatcher.InvokeAsync(RefreshDockItems);

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

        _windowTracker.Start();

        // Start zero-injection Shell_TrayWnd tray proxy AFTER AppBar registration so SHAppBarMessage hits Explorer cleanly
        _trayProxyHost.IconsChanged += items => Dispatcher.InvokeAsync(() =>
        {
            foreach (var s in _shells) s.MenuBar.UpdateTrayItems(items);
        });
        _trayProxyHost.Start();

        RefreshDockItems();
        OnForegroundChanged(_windowTracker.GetForegroundWindowInfo());

        _periodicRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _periodicRefreshTimer.Tick += (_, _) => RefreshDockItems();
        _periodicRefreshTimer.Start();

        _foregroundReconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _foregroundReconcileTimer.Tick += (_, _) => ReconcileForegroundWindow();
        _foregroundReconcileTimer.Start();
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

        if (_menuCoordinator is not null && fg.Hwnd != 0)
        {
            string displayName = FriendlyAppNameResolver.Resolve(fg.ProcessName, fg.ClassName, fg.WindowTitle);
            _ = _menuCoordinator.OnForegroundChangedAsync(fg.Hwnd, fg.ProcessId, fg.ProcessName, displayName);
        }
    }

    private void RefreshDockItems()
    {
        if (_windowTracker is null || _recycleBinService is null || _shells.Count == 0) return;

        var windows = _windowTracker.GetTopLevelWindows();
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

        // Remove apps from stable order only once all their windows have closed
        _stableRunningOrder.RemoveAll(p => !activeExes.Contains(p));

        var rb = _recycleBinService.Query();
        var items = DockModelCoordinator.BuildDockItems(_settings, windows, _recentApps.ToList(), rb, _stableRunningOrder);
        foreach (var s in _shells)
        {
            s.Dock.UpdateItems(items);
        }
    }

    private void ApplyUpdatedSettings(GlassSettings updated)
    {
        _settings = updated;
        _settingsStore?.Save(updated);
        bool isDark = ResolveIsDarkMode(updated);
        foreach (var s in _shells)
        {
            s.MenuBar.ApplySettings(updated, isDark);
            s.Dock.ApplySettings(updated, isDark);
        }
        RefreshDockItems();
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

    private static void ShowPhase2Notice(string message)
    {
        MessageBox.Show(message, "GlassShell", MessageBoxButton.OK, MessageBoxImage.Information);
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
}
