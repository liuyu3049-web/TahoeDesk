using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TahoeDesk.Core.Shell;

/// <summary>
/// Installs standard user-mode WH_KEYBOARD_LL + WH_MOUSE_LL hooks to intercept clean single-press Win key taps
/// for Launchpad while keeping all Win+* combinations (Win+E, Win+D, Win+L, Win+V, Win+Shift+S, etc.) intact.
/// </summary>
public sealed class LowLevelWinKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 2;
    private const uint GA_ROOT = 2;
    private const int GWL_STYLE = -16;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_UNASSIGNED_SUPPRESS = 0xFF;

    private readonly WinKeyStateMachine _stateMachine = new();
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly uint _selfProcessId = (uint)Environment.ProcessId;

    private nint _keyboardHook;
    private nint _mouseHook;
    private long _lastLeftDownTick;
    private int _lastLeftDownX;
    private int _lastLeftDownY;
    private bool _swallowNextLeftUp;
    private bool _disposed;

    public LowLevelWinKeyHook()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public event Action? SingleWinPressed;
    public event Action<nint>? TitleBarDoubleClicked;

    public bool Enabled { get; set; } = true;
    public bool IsFullscreenActive { get; set; }
    public bool TitleBarDoubleClickEnabled { get; set; } = true;

    public void Start()
    {
        if (_disposed || _keyboardHook != 0) return;

        nint hMod = GetModuleHandleW(null);
        _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool isInjected = (info.flags & LLKHF_INJECTED) != 0;
            int msg = (int)wParam;

            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                _stateMachine.OnKeyDown((int)info.vkCode, isInjected, Enabled, IsFullscreenActive);
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                var action = _stateMachine.OnKeyUp((int)info.vkCode, isInjected, Enabled, IsFullscreenActive);
                if (action == WinKeyAction.SuppressStartAndTrigger)
                {
                    InjectDummySuppressionKey();
                    ThreadPool.QueueUserWorkItem(_ => SingleWinPressed?.Invoke());
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != 0)
        {
            int msg = (int)wParam;
            if (msg == WM_LBUTTONUP && _swallowNextLeftUp)
            {
                _swallowNextLeftUp = false;
                return (nint)1;
            }

            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                _stateMachine.OnMouseButtonDown();

                if (msg == WM_LBUTTONDOWN && TitleBarDoubleClickEnabled && !IsFullscreenActive)
                {
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if ((ms.flags & LLMHF_INJECTED) == 0 && TryInterceptTitleBarDoubleClick(ms.pt))
                    {
                        _swallowNextLeftUp = true;
                        return (nint)1;
                    }
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool TryInterceptTitleBarDoubleClick(POINT pt)
    {
        long now = Environment.TickCount64;
        uint dblMs = Math.Clamp(GetDoubleClickTime(), 200u, 800u);
        bool isDoubleClick = _lastLeftDownTick != 0
            && (now - _lastLeftDownTick) <= dblMs
            && Math.Abs(pt.X - _lastLeftDownX) <= 6
            && Math.Abs(pt.Y - _lastLeftDownY) <= 6;

        _lastLeftDownTick = now;
        _lastLeftDownX = pt.X;
        _lastLeftDownY = pt.Y;

        if (!isDoubleClick) return false;

        nint hitWnd = WindowFromPoint(pt);
        if (hitWnd == 0) return false;
        nint root = GetAncestor(hitWnd, GA_ROOT);
        if (root == 0 || !IsWindowVisible(root) || IsZoomed(root)) return false;

        GetWindowThreadProcessId(root, out uint pid);
        if (pid == 0 || pid == _selfProcessId) return false;

        int style = GetWindowLongW(root, GWL_STYLE);
        if ((style & WS_THICKFRAME) == 0) return false;

        if (!GetWindowRect(root, out RECT rc)) return false;
        // Fast geometric gate: must be within the top titlebar band (~96px max on 200% DPI)
        // and clearly outside the top-right Min/Max/Close button cluster (DPI-safe up to 200%).
        int buttonZonePx = Math.Min(210, Math.Max(140, (rc.Right - rc.Left) / 3));
        if (pt.Y < rc.Top || pt.Y > rc.Top + 96) return false;
        if (pt.X > rc.Right - buttonZonePx) return false;

        nint lp = (nint)((pt.Y << 16) | (pt.X & 0xFFFF));
        if (SendMessageTimeoutW(root, WM_NCHITTEST, 0, lp, SMTO_BLOCK | SMTO_ABORTIFHUNG, 30, out nint hitResult) == 0)
        {
            return false;
        }

        if ((int)hitResult != HTCAPTION) return false;

        // Reset double-click sequence so a fast triple-click does not double-trigger
        _lastLeftDownTick = 0;
        nint targetHwnd = root;
        ThreadPool.QueueUserWorkItem(_ => TitleBarDoubleClicked?.Invoke(targetHwnd));
        return true;
    }

    private static void InjectDummySuppressionKey()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_UNASSIGNED_SUPPRESS;
        inputs[0].u.ki.dwFlags = 0;

        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_UNASSIGNED_SUPPRESS;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        _stateMachine.Reset();
    }

    private delegate nint LowLevelProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, LowLevelProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLongW(nint hwnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);
}
