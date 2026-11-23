using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GlassShell.Core.Shell;

namespace GlassShell.Core.Discovery;

public sealed class WindowTracker : IWindowTracker
{
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_CLOAKED = 14;
    private const uint GW_OWNER = 4;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly uint _selfProcessId = (uint)Environment.ProcessId;
    private readonly List<nint> _hooks = [];
    private WinEventDelegate? _hookDelegate;
    private bool _started;

    public event Action? WindowsChanged;
    public event Action<ForegroundWindowInfo>? ForegroundChanged;
    public event Action? TaskbarShownDetected;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _hookDelegate = OnWinEvent;

        RegisterHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        RegisterHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        RegisterHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        RegisterHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        foreach (var h in _hooks)
        {
            if (h != 0) UnhookWinEvent(h);
        }
        _hooks.Clear();
        _hookDelegate = null;
    }

    public void Dispose() => Stop();

    public IReadOnlyList<WindowInfo> GetTopLevelWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (TryBuildWindowInfo(hwnd, out var info) && info is not null)
            {
                result.Add(info);
            }
            return true;
        }, 0);
        return result;
    }

    public ForegroundWindowInfo GetForegroundWindowInfo()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return new ForegroundWindowInfo(0, string.Empty, string.Empty, new(0, 0, 0, 0), false, false);
        }

        string className = GetClassName(hwnd);
        GetWindowThreadProcessId(hwnd, out uint pid);
        string exePath = QueryProcessPath(pid);
        string procName = string.IsNullOrEmpty(exePath) ? string.Empty : Path.GetFileNameWithoutExtension(exePath);
        GetWindowRect(hwnd, out RECT rc);
        bool minimized = IsIconic(hwnd);
        bool cloaked = IsCloaked(hwnd);
        return new ForegroundWindowInfo(hwnd, className, procName, new(rc.Left, rc.Top, rc.Right, rc.Bottom), minimized, cloaked);
    }

    public static bool ActivateWindow(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, 9 /* SW_RESTORE */);
        }
        return SetForegroundWindow(hwnd);
    }

    private void RegisterHook(uint eventMin, uint eventMax)
    {
        var h = SetWinEventHook(eventMin, eventMax, 0, _hookDelegate!, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != 0) _hooks.Add(h);
    }

    private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == 0 || idObject != 0) return;

        if (eventType == EVENT_OBJECT_SHOW)
        {
            string cls = GetClassName(hwnd);
            if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
            {
                TaskbarShownDetected?.Invoke();
            }
            return;
        }

        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            var fg = GetForegroundWindowInfo();
            ForegroundChanged?.Invoke(fg);
            WindowsChanged?.Invoke();
            return;
        }

        WindowsChanged?.Invoke();
    }

    private bool TryBuildWindowInfo(nint hwnd, out WindowInfo? info)
    {
        info = null;
        if (!IsWindowVisible(hwnd)) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _selfProcessId) return false;

        int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 || (exStyle & WS_EX_NOACTIVATE) != 0) return false;

        bool hasOwner = GetWindow(hwnd, GW_OWNER) != 0;
        if (hasOwner && (exStyle & WS_EX_APPWINDOW) == 0) return false;

        if (IsCloaked(hwnd)) return false;

        string title = GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return false;

        string className = GetClassName(hwnd);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow")
        {
            return false;
        }

        string exePath = QueryProcessPath(pid);
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        string appName = ResolveDisplayName(exePath);
        info = new WindowInfo(hwnd, pid, exePath, appName, title, className, IsIconic(hwnd), IsFlashing: false);
        return true;
    }

    private static string ResolveDisplayName(string exePath)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
        }
        catch { }

        return Path.GetFileNameWithoutExtension(exePath);
    }

    private static bool IsCloaked(nint hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        int len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetClassNameW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string QueryProcessPath(uint pid)
    {
        nint hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == 0) return string.Empty;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(hProc, 0, sb, ref size) ? sb.ToString() : string.Empty;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLongW(nint hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
