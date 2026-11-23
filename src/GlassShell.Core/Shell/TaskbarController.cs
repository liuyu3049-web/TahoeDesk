using System;

namespace GlassShell.Core.Shell;

public sealed class TaskbarController
{
    private readonly ITaskbarNative _native;
    private readonly object _gate = new();

    private nint _taskbarHwnd;
    private nint _appBarHwnd;
    private MonitorContext? _monitor;
    private int _topBarDip = 28;

    public TaskbarController(ITaskbarNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public bool IsAcquired { get; private set; }
    public bool IsTrayPeekActive { get; private set; }
    public bool IsFullscreenYieldActive { get; private set; }

    public event Action<bool>? BarsVisibilityChanged;
    public event Action<bool>? DockYieldForTrayChanged;

    public void Acquire(nint appBarHwnd, MonitorContext primaryMonitor, int topBarDip = 28)
    {
        lock (_gate)
        {
            _appBarHwnd = appBarHwnd;
            _monitor = primaryMonitor;
            _topBarDip = topBarDip;
            _taskbarHwnd = _native.FindPrimaryTaskbar();

            if (_taskbarHwnd != 0)
            {
                _native.SetWindowVisible(_taskbarHwnd, false);
            }

            int heightPx = (int)Math.Round(topBarDip * primaryMonitor.DpiScale);
            if (_appBarHwnd != 0)
            {
                _native.RegisterTopAppBar(_appBarHwnd, heightPx, primaryMonitor.Bounds);
            }

            IsAcquired = true;
            IsTrayPeekActive = false;
            IsFullscreenYieldActive = false;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (!IsAcquired)
            {
                return;
            }

            if (_appBarHwnd != 0)
            {
                _native.UnregisterAppBar(_appBarHwnd);
            }

            var hwnd = _native.FindPrimaryTaskbar();
            if (hwnd == 0) hwnd = _taskbarHwnd;
            if (hwnd != 0)
            {
                _native.SetWindowVisible(hwnd, true);
            }

            IsAcquired = false;
            IsTrayPeekActive = false;
            IsFullscreenYieldActive = false;
        }
    }

    public void BeginTrayPeek()
    {
        bool changed = false;
        lock (_gate)
        {
            if (!IsAcquired || IsFullscreenYieldActive || IsTrayPeekActive)
            {
                return;
            }

            _taskbarHwnd = _native.FindPrimaryTaskbar();
            if (_taskbarHwnd != 0)
            {
                _native.SetWindowVisible(_taskbarHwnd, true);
            }

            IsTrayPeekActive = true;
            changed = true;
        }

        if (changed)
        {
            DockYieldForTrayChanged?.Invoke(true);
        }
    }

    public void EndTrayPeek()
    {
        bool changed = false;
        lock (_gate)
        {
            if (!IsAcquired || !IsTrayPeekActive)
            {
                return;
            }

            _taskbarHwnd = _native.FindPrimaryTaskbar();
            if (_taskbarHwnd != 0)
            {
                _native.SetWindowVisible(_taskbarHwnd, false);
            }

            IsTrayPeekActive = false;
            changed = true;
        }

        if (changed)
        {
            DockYieldForTrayChanged?.Invoke(false);
        }
    }

    public void HandleExplorerRestart()
    {
        lock (_gate)
        {
            if (!IsAcquired) return;

            _taskbarHwnd = _native.FindPrimaryTaskbar();
            if (!IsTrayPeekActive && _taskbarHwnd != 0)
            {
                _native.SetWindowVisible(_taskbarHwnd, false);
            }

            if (_appBarHwnd != 0 && _monitor is not null)
            {
                int heightPx = (int)Math.Round(_topBarDip * _monitor.DpiScale);
                _native.RegisterTopAppBar(_appBarHwnd, heightPx, _monitor.Bounds);
            }
        }
    }

    public void HandleTaskbarShownEvent()
    {
        lock (_gate)
        {
            if (!IsAcquired || IsTrayPeekActive) return;

            _taskbarHwnd = _native.FindPrimaryTaskbar();
            if (_taskbarHwnd != 0)
            {
                _native.SetWindowVisible(_taskbarHwnd, false);
            }
        }
    }

    public void UpdateFullscreenState(bool isFullscreen)
    {
        bool? notifyBarsVisible = null;
        bool trayCancelled = false;

        lock (_gate)
        {
            if (!IsAcquired || IsFullscreenYieldActive == isFullscreen)
            {
                return;
            }

            IsFullscreenYieldActive = isFullscreen;
            if (isFullscreen)
            {
                if (IsTrayPeekActive)
                {
                    IsTrayPeekActive = false;
                    trayCancelled = true;
                }

                _taskbarHwnd = _native.FindPrimaryTaskbar();
                if (_taskbarHwnd != 0)
                {
                    _native.SetWindowVisible(_taskbarHwnd, false);
                }

                notifyBarsVisible = false;
            }
            else
            {
                notifyBarsVisible = true;
            }
        }

        if (trayCancelled)
        {
            DockYieldForTrayChanged?.Invoke(false);
        }

        if (notifyBarsVisible.HasValue)
        {
            BarsVisibilityChanged?.Invoke(notifyBarsVisible.Value);
        }
    }
}
