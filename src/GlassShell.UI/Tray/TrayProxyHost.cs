using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassShell.Core.Shell;
using GlassShell.Core.Tray;

namespace GlassShell.UI.Tray;

public sealed record TrayDisplayItem(TrayIconRecord Record, BitmapSource? IconBitmap);

/// <summary>
/// Zero-injection, standard-user-privilege Win32 Shell_TrayWnd proxy host.
/// Intercepts Shell_NotifyIcon (dwData=1) and Shell_NotifyIconGetRect (dwData=3) via WM_COPYDATA,
/// forwards all non-tray shell messages (e.g. SHAppBarMessage dwData=0) transparently to Explorer,
/// and cleanly restores all icons to Explorer via TaskbarCreated on Dispose.
/// </summary>
public sealed class TrayProxyHost : IDisposable
{
    private const string TrayClassName = "Shell_TrayWnd";
    private const string NotifyClassName = "TrayNotifyWnd";
    private const uint WM_COPYDATA = 0x004A;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MSGFLT_ALLOW = 1;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_BROADCAST = new(0xFFFF);

    private readonly TrayIconRegistry _registry = new();
    private readonly Dictionary<TrayIconKey, (long Revision, BitmapSource? Bitmap)> _bitmapCache = new();
    private readonly Dictionary<TrayIconKey, long> _lastIconModifyTicks = new();
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly DispatcherTimer _pruneTimer;
    private readonly uint _taskbarCreatedMsg;

    private nint _explorerTrayHwnd;
    private nint _proxyTrayHwnd;
    private nint _proxyNotifyHwnd;
    private bool _registeredTrayClass;
    private bool _registeredNotifyClass;
    private bool _started;
    private bool _disposed;
    private long _selfBroadcastUntilTicks;

    public TrayProxyHost()
    {
        _wndProcDelegate = ProxyWndProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pruneTimer.Tick += (_, _) => PruneDeadIcons();
    }

    public event Action<IReadOnlyList<TrayDisplayItem>>? IconsChanged;
    public event Action<uint>? TrayIconFlashingDetected;

    /// <summary>
    /// True briefly while GlassShell itself broadcasts TaskbarCreated so MenuBarWindow doesn't mistake it for an Explorer crash.
    /// </summary>
    public bool IsSelfBroadcastActive => Environment.TickCount64 < _selfBroadcastUntilTicks;

    public void Start()
    {
        if (_started || _disposed) return;

        _explorerTrayHwnd = Win32TaskbarNative.FindExplorerPrimaryTaskbar();

        nint hInstance = GetModuleHandleW(null);
        _registeredTrayClass = RegisterWindowClass(TrayClassName, hInstance, _wndProcDelegate);
        _registeredNotifyClass = RegisterWindowClass(NotifyClassName, hInstance, DefWindowProcW);

        _proxyTrayHwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            TrayClassName,
            string.Empty,
            WS_POPUP,
            0, 0, 0, 0,
            0, 0, hInstance, 0);

        if (_proxyTrayHwnd == 0)
        {
            return;
        }

        _proxyNotifyHwnd = CreateWindowExW(
            0,
            NotifyClassName,
            string.Empty,
            WS_CHILD,
            0, 0, 0, 0,
            _proxyTrayHwnd, 0, hInstance, 0);

        // Allow elevated apps to deliver WM_COPYDATA tray updates to our standard-user proxy
        ChangeWindowMessageFilterEx(_proxyTrayHwnd, WM_COPYDATA, MSGFLT_ALLOW, 0);

        // Ensure our proxy Shell_TrayWnd sits ahead of Explorer's Shell_TrayWnd in Z-order
        // so shell32!FindWindow("Shell_TrayWnd") routes Shell_NotifyIcon messages here first.
        SetWindowPos(_proxyTrayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);

        _started = true;
        _pruneTimer.Start();

        BroadcastTaskbarCreated(isSelf: true);
    }

    public void HandleExternalExplorerRestart()
    {
        if (!_started || _disposed) return;
        _explorerTrayHwnd = Win32TaskbarNative.FindExplorerPrimaryTaskbar();
        if (_proxyTrayHwnd != 0)
        {
            SetWindowPos(_proxyTrayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
            BroadcastTaskbarCreated(isSelf: true);
        }
    }

    public void UpdateIconScreenRect(TrayIconKey key, ScreenRect rectPx)
    {
        _registry.UpdateScreenRect(key, rectPx);
    }

    public IReadOnlyList<TrayDisplayItem> GetCurrentItems()
    {
        var visible = _registry.GetVisibleIcons();
        var result = new List<TrayDisplayItem>(visible.Count);
        foreach (var rec in visible)
        {
            BitmapSource? bmp = null;
            if (_bitmapCache.TryGetValue(rec.Key, out var cached) && cached.Revision == rec.IconRevision)
            {
                bmp = cached.Bitmap;
            }
            else
            {
                bmp = TrayIconBitmapConverter.ConvertFromHIcon(rec.IconHandle, rec.Key.OwnerHwnd);
                _bitmapCache[rec.Key] = (rec.IconRevision, bmp);
            }
            result.Add(new TrayDisplayItem(rec, bmp));
        }
        return result;
    }

    public void DispatchMouseAction(TrayIconRecord icon, TrayMouseAction action, int screenX, int screenY)
    {
        if (icon.Key.OwnerHwnd == 0 || !IsWindow(icon.Key.OwnerHwnd))
        {
            PruneDeadIcons();
            return;
        }

        // Grant foreground activation so the target app's native context menu opens and auto-dismisses properly
        GetWindowThreadProcessId(icon.Key.OwnerHwnd, out uint pid);
        if (pid != 0)
        {
            AllowSetForegroundWindow(pid);
        }
        SetForegroundWindow(icon.Key.OwnerHwnd);

        var packets = TrayWireProtocol.BuildCallbackPackets(icon, action, screenX, screenY);
        foreach (var pkt in packets)
        {
            PostMessageW(icon.Key.OwnerHwnd, pkt.Message, pkt.WParam, pkt.LParam);
        }
    }

    private nint ProxyWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_COPYDATA && lParam != 0)
        {
            return HandleCopyData(wParam, lParam);
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint HandleCopyData(nint wParam, nint lParam)
    {
        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        ulong dwData = unchecked((ulong)cds.dwData);

        if (dwData == TrayWireProtocol.CopyDataNotifyIcon && cds.lpData != 0 && cds.cbData >= 32 && cds.cbData <= 16384)
        {
            byte[] buffer = new byte[cds.cbData];
            Marshal.Copy(cds.lpData, buffer, 0, cds.cbData);

            if (TrayWireProtocol.TryParseNotifyIconCommand(buffer, out var cmd) && cmd is not null)
            {
                bool changed = _registry.ApplyCommand(cmd, out var updated);
                if (changed)
                {
                    if (cmd.Kind == TrayCommandKind.Delete && updated is not null)
                    {
                        _bitmapCache.Remove(updated.Key);
                    }
                    else if (updated is not null)
                    {
                        // Eagerly capture HICON synchronously inside WM_COPYDATA while the sender's HICON is guaranteed valid!
                        if (!_bitmapCache.TryGetValue(updated.Key, out var cached) || cached.Revision != updated.IconRevision)
                        {
                            var bmp = TrayIconBitmapConverter.ConvertFromHIcon(updated.IconHandle, updated.Key.OwnerHwnd);
                            _bitmapCache[updated.Key] = (updated.IconRevision, bmp);

                            // Detect rapid tray icon blink (e.g. WeChat/QQ swapping icon every ~400ms on new message!)
                            if (cmd.Kind == TrayCommandKind.Modify && (cmd.Flags & NotifyIconFlags.Icon) != 0 && cmd.OwnerHwnd != 0)
                            {
                                long nowTicks = Environment.TickCount64;
                                if (_lastIconModifyTicks.TryGetValue(updated.Key, out long prevTicks) && (nowTicks - prevTicks) <= 1500)
                                {
                                    GetWindowThreadProcessId(cmd.OwnerHwnd, out uint ownerPid);
                                    if (ownerPid != 0) TrayIconFlashingDetected?.Invoke(ownerPid);
                                }
                                _lastIconModifyTicks[updated.Key] = nowTicks;
                            }
                        }
                    }

                    RaiseIconsChanged();
                }

                // Mirror to Explorer in the background (with short abort-if-hung timeout) so Windows toast
                // notifications (NIF_INFO) and emergency native taskbar fallback stay 100% intact.
                ForwardToExplorer(WM_COPYDATA, wParam, lParam);
                return 1;
            }
        }
        else if (dwData == TrayWireProtocol.CopyDataIconRect && cds.lpData != 0 && cds.cbData >= 36 && cds.cbData <= 1024)
        {
            byte[] buffer = new byte[cds.cbData];
            Marshal.Copy(cds.lpData, buffer, 0, cds.cbData);

            if (TrayWireProtocol.TryParseRectQuery(buffer, out var query) && query is not null
                && _registry.TryGetIconRect(query.OwnerHwnd, query.IconId, query.GuidItem, out var rect))
            {
                return query.QueryKind == 1
                    ? TrayWireProtocol.PackScreenPoint(rect.Left, rect.Top)
                    : TrayWireProtocol.PackScreenPoint(rect.Right, rect.Bottom);
            }
        }

        // Transparently forward SHAppBarMessage (dwData == 0) and any unknown shell IPC directly to Explorer
        return ForwardToExplorer(WM_COPYDATA, wParam, lParam);
    }

    private nint ForwardToExplorer(uint msg, nint wParam, nint lParam)
    {
        if (_explorerTrayHwnd == 0 || !IsWindow(_explorerTrayHwnd))
        {
            _explorerTrayHwnd = Win32TaskbarNative.FindExplorerPrimaryTaskbar();
        }

        if (_explorerTrayHwnd == 0 || _explorerTrayHwnd == _proxyTrayHwnd)
        {
            return 0;
        }

        return SendMessageTimeoutW(_explorerTrayHwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 120, out nint result) != 0
            ? result
            : 0;
    }

    private void PruneDeadIcons()
    {
        if (_disposed) return;
        if (_registry.PruneDeadWindows(hwnd => IsWindow(hwnd)))
        {
            var alive = new HashSet<TrayIconKey>();
            foreach (var rec in _registry.GetVisibleIcons())
            {
                alive.Add(rec.Key);
            }
            var staleKeys = new List<TrayIconKey>();
            foreach (var key in _bitmapCache.Keys)
            {
                if (!alive.Contains(key)) staleKeys.Add(key);
            }
            foreach (var k in staleKeys) _bitmapCache.Remove(k);

            RaiseIconsChanged();
        }
    }

    private void RaiseIconsChanged()
    {
        IconsChanged?.Invoke(GetCurrentItems());
    }

    private void BroadcastTaskbarCreated(bool isSelf)
    {
        if (_taskbarCreatedMsg == 0) return;
        if (isSelf)
        {
            _selfBroadcastUntilTicks = Environment.TickCount64 + 600;
        }
        SendNotifyMessageW(HWND_BROADCAST, _taskbarCreatedMsg, 0, 0);
    }

    private static bool RegisterWindowClass(string className, nint hInstance, WndProcDelegate wndProc)
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            lpszClassName = className
        };
        return RegisterClassExW(ref wc) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _pruneTimer.Stop(); } catch { }

        if (_proxyNotifyHwnd != 0)
        {
            DestroyWindow(_proxyNotifyHwnd);
            _proxyNotifyHwnd = 0;
        }
        if (_proxyTrayHwnd != 0)
        {
            DestroyWindow(_proxyTrayHwnd);
            _proxyTrayHwnd = 0;
        }

        nint hInstance = GetModuleHandleW(null);
        if (_registeredNotifyClass) UnregisterClassW(NotifyClassName, hInstance);
        if (_registeredTrayClass) UnregisterClassW(TrayClassName, hInstance);

        if (_started)
        {
            // Ask all running apps to immediately re-register onto Explorer's restored native taskbar
            BroadcastTaskbarCreated(isSelf: false);
        }
    }

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nint dwData;
        public int cbData;
        public nint lpData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint hWnd, uint message, uint action, nint pChangeFilterStruct);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
