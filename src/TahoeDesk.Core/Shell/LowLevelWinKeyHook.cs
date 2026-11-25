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
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_UNASSIGNED_SUPPRESS = 0xFF;

    private readonly WinKeyStateMachine _stateMachine = new();
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;

    private nint _keyboardHook;
    private nint _mouseHook;
    private bool _disposed;

    public LowLevelWinKeyHook()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public event Action? SingleWinPressed;

    public bool Enabled { get; set; } = true;
    public bool IsFullscreenActive { get; set; }

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
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                _stateMachine.OnMouseButtonDown();
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
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
}
