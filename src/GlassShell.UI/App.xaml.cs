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
using Microsoft.Win32;

namespace GlassShell.UI;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _cleanExitEvent;
    private JsonSettingsStore? _settingsStore;
    private GlassSettings _settings = GlassSettings.Default;
    private TaskbarController? _taskbarController;
    private Win32TaskbarNative? _taskbarNative;
    private WindowTracker? _windowTracker;
    private ForegroundMenuCoordinator? _menuCoordinator;
    private DxgiBackdropSampler? _backdropSampler;
    private RecycleBinService? _recycleBinService;
    private MenuBarWindow? _menuBarWindow;
    private DockWindow? _dockWindow;
    private QuickSettingsWindow? _settingsWindow;
    private MonitorContext? _primaryMonitor;
    private DispatcherTimer? _periodicRefreshTimer;
    private readonly LinkedList<string> _recentApps = new();
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

        _primaryMonitor = MonitorContext.GetPrimaryMonitor();
        bool isDark = ResolveIsDarkMode(_settings);

        _taskbarNative = new Win32TaskbarNative();
        _taskbarController = new TaskbarController(_taskbarNative);
        _windowTracker = new WindowTracker();
        _recycleBinService = new RecycleBinService();
        _backdropSampler = new DxgiBackdropSampler();

        var extractors = new IMenuExtractor[]
        {
            new Win32GetMenuExtractor(),
            new StaticUiaMenuExtractor()
        };
        _menuCoordinator = new ForegroundMenuCoordinator(
            extractors,
            JsonProfileMenuProvider.CreateBuiltIn(),
            (uint)Environment.ProcessId);

        _menuBarWindow = new MenuBarWindow(_primaryMonitor, _taskbarController, _windowTracker, _backdropSampler, _settings, isDark);
        _dockWindow = new DockWindow(_primaryMonitor, _backdropSampler, _settings, isDark);

        _menuBarWindow.SettingsRequested += OpenQuickSettings;
        _menuBarWindow.RestoreNativeTaskbarRequested += () =>
        {
            CleanupSafe();
            Shutdown(0);
        };
        _menuBarWindow.SpotlightRequested += () => ShowPhase2Notice("聚焦搜索 (Spotlight) 将在阶段二启用");
        _menuBarWindow.ControlCenterRequested += () => ShowPhase2Notice("控制中心 (Control Center) 将在阶段二启用");

        _dockWindow.LaunchpadRequested += () => ShowPhase2Notice("启动台 (Launchpad) 将在阶段二启用");
        _dockWindow.SettingsUpdated += ApplyUpdatedSettings;

        _menuCoordinator.SnapshotChanged += snap => _menuBarWindow.UpdateMenuSnapshot(snap);

        _taskbarController.BarsVisibilityChanged += visible => Dispatcher.InvokeAsync(() =>
        {
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            if (_menuBarWindow is not null) _menuBarWindow.Visibility = vis;
            if (_dockWindow is not null) _dockWindow.Visibility = vis;
        });
        _taskbarController.DockYieldForTrayChanged += yield => Dispatcher.InvokeAsync(() => _dockWindow?.SetYieldForTray(yield));

        _windowTracker.TaskbarShownDetected += () => _taskbarController.HandleTaskbarShownEvent();
        _windowTracker.ForegroundChanged += OnForegroundChanged;
        _windowTracker.WindowsChanged += () => Dispatcher.InvokeAsync(RefreshDockItems);

        _menuBarWindow.Show();
        _dockWindow.Show();

        _taskbarController.Acquire(_menuBarWindow.Hwnd, _primaryMonitor, topBarDip: 28);
        _windowTracker.Start();

        RefreshDockItems();
        OnForegroundChanged(_windowTracker.GetForegroundWindowInfo());

        _periodicRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _periodicRefreshTimer.Tick += (_, _) => RefreshDockItems();
        _periodicRefreshTimer.Start();
    }

    private void OnForegroundChanged(ForegroundWindowInfo fg)
    {
        if (_primaryMonitor is null || _taskbarController is null || _taskbarNative is null) return;

        bool isFs = FullscreenDetector.IsFullscreen(fg, _primaryMonitor, _taskbarNative.IsD3DFullscreenActive());
        _taskbarController.UpdateFullscreenState(isFs);

        if (_dockWindow is not null && fg.Hwnd != 0 && !fg.IsMinimized && !fg.IsCloaked)
        {
            bool overlapsBottom = fg.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd")
                && fg.Bounds.Bottom >= _primaryMonitor.Bounds.Bottom - (int)(84 * _primaryMonitor.DpiScale);
            Dispatcher.InvokeAsync(() => _dockWindow.SetBottomOverlap(overlapsBottom));
        }

        if (_menuCoordinator is not null && fg.Hwnd != 0)
        {
            string displayName = string.IsNullOrWhiteSpace(fg.ProcessName) ? "桌面" : fg.ProcessName;
            _ = _menuCoordinator.OnForegroundChangedAsync(fg.Hwnd, 0, fg.ProcessName, displayName);
        }
    }

    private void RefreshDockItems()
    {
        if (_windowTracker is null || _recycleBinService is null || _dockWindow is null) return;

        var windows = _windowTracker.GetTopLevelWindows();
        foreach (var w in windows)
        {
            if (string.IsNullOrWhiteSpace(w.ExecutablePath)) continue;
            if (!_recentApps.Any(p => string.Equals(p, w.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                _recentApps.AddFirst(w.ExecutablePath);
                while (_recentApps.Count > 12) _recentApps.RemoveLast();
            }
        }

        var rb = _recycleBinService.Query();
        var items = DockModelCoordinator.BuildDockItems(_settings, windows, _recentApps.ToList(), rb);
        _dockWindow.UpdateItems(items);
    }

    private void ApplyUpdatedSettings(GlassSettings updated)
    {
        _settings = updated;
        _settingsStore?.Save(updated);
        bool isDark = ResolveIsDarkMode(updated);
        _menuBarWindow?.ApplySettings(updated, isDark);
        _dockWindow?.ApplySettings(updated, isDark);
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
        try { _windowTracker?.Dispose(); } catch { }
        try { _menuCoordinator?.Dispose(); } catch { }
        try { _taskbarController?.Release(); } catch { }
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
