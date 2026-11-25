using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassShell.Core.Shell;
using GlassShell.Core.Tray;

namespace GlassShell.UI.Tray;

public sealed record TrayDisplayItem(
    TrayIconRecord Record,
    BitmapSource? IconBitmap,
    bool IsFlashing = false,
    bool IsBlinkOffPhase = false);

/// <summary>
/// Zero-injection, standard-user-privilege Win32 Shell_TrayWnd proxy host.
/// Intercepts Shell_NotifyIcon (dwData=1) and Shell_NotifyIconGetRect (dwData=3) via WM_COPYDATA,
/// forwards mirrored notifications asynchronously off the UI thread (preventing cross-process deadlocks with Wallpaper Engine / Explorer),
/// preserves last valid icon bitmaps across blank blink frames (preventing stuck black dots),
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
    private readonly Dictionary<TrayIconKey, BitmapSource> _lastValidBitmaps = new();
    private readonly Dictionary<TrayIconKey, long> _lastIconModifyTicks = new();
    private readonly Dictionary<TrayIconKey, long> _flashingUntilTicks = new();
    private readonly HashSet<TrayIconKey> _blinkOffKeys = new();
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly DispatcherTimer _maintenanceTimer;
    private readonly uint _taskbarCreatedMsg;

    private nint _explorerTrayHwnd;
    private nint _proxyTrayHwnd;
    private nint _proxyNotifyHwnd;
    private bool _registeredTrayClass;
    private bool _registeredNotifyClass;
    private bool _started;
    private bool _proxyDestroyed;
    private bool _disposed;
    private long _selfBroadcastUntilTicks;
    private int _tickCounter;

    public TrayProxyHost()
    {
        _wndProcDelegate = ProxyWndProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        _maintenanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _maintenanceTimer.Tick += (_, _) => RunMaintenanceTick();
    }

    public event Action<IReadOnlyList<TrayDisplayItem>>? IconsChanged;
    public event Action<uint>? TrayIconFlashingDetected;

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

        ChangeWindowMessageFilterEx(_proxyTrayHwnd, WM_COPYDATA, MSGFLT_ALLOW, 0);
        SetWindowPos(_proxyTrayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);

        _started = true;
        _maintenanceTimer.Start();

        BroadcastTaskbarCreated(isSelf: true);

        // Delayed second sync (+2.5s) catches boot-time autostart apps (like Wallpaper Engine)
        // that finish creating their tray windows slightly after GlassShell launches!
        var delayedSync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        delayedSync.Tick += (_, _) =>
        {
            delayedSync.Stop();
            if (!_disposed && !_proxyDestroyed)
            {
                ResyncAllTrayIcons();
            }
        };
        delayedSync.Start();
    }

    /// <summary>
    /// Ensures GlassShell's proxy Shell_TrayWnd stays strictly ahead of Explorer's Shell_TrayWnd in Z-order
    /// so shell32!FindWindow("Shell_TrayWnd") ALWAYS delivers new/modified tray icons (WeChat, Wallpaper Engine, etc.) here first.
    /// </summary>
    public void EnsureProxyFirstInZOrder()
    {
        if (!_started || _disposed || _proxyDestroyed || _proxyTrayHwnd == 0) return;
        nint first = FindWindowExW(0, 0, TrayClassName, null);
        if (first != _proxyTrayHwnd)
        {
            SetWindowPos(_proxyTrayHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        }
    }

    public void ResyncAllTrayIcons()
    {
        if (!_started || _disposed || _proxyDestroyed || _proxyTrayHwnd == 0) return;
        EnsureProxyFirstInZOrder();
        BroadcastTaskbarCreated(isSelf: true);
    }

    public IReadOnlySet<string> GetActiveTrayExecutablePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in _registry.GetVisibleIcons())
        {
            string? path = TryResolveOwnerExePath(rec.Key.OwnerHwnd);
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary>
    /// Restores a background tray-resident app (such as WeChat closed with X) by simulating a tray click
    /// on its registered tray icon instead of launching a duplicate process/window.
    /// </summary>
    public bool TryActivateTrayAppByExePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        string targetNorm = exePath.Trim().TrimEnd('\\');

        TrayIconRecord? match = null;
        foreach (var rec in _registry.GetVisibleIcons())
        {
            string? ownerExe = TryResolveOwnerExePath(rec.Key.OwnerHwnd);
            if (!string.IsNullOrWhiteSpace(ownerExe)
                && string.Equals(ownerExe.Trim().TrimEnd('\\'), targetNorm, StringComparison.OrdinalIgnoreCase))
            {
                match = rec;
                break;
            }
        }

        if (match is null) return false;

        int sx = match.LastScreenRect.Width > 0 ? (match.LastScreenRect.Left + match.LastScreenRect.Right) / 2 : 1200;
        int sy = match.LastScreenRect.Height > 0 ? match.LastScreenRect.Bottom : 28;
        DispatchMouseAction(match, TrayMouseAction.LeftClick, sx, sy);
        return true;
    }

    private static string? TryResolveOwnerExePath(nint ownerHwnd)
    {
        if (ownerHwnd == 0 || !IsWindow(ownerHwnd)) return null;
        GetWindowThreadProcessId(ownerHwnd, out uint pid);
        if (pid == 0) return null;

        nint hProc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (hProc == 0) return null;
        try
        {
            var sb = new System.Text.StringBuilder(520);
            int cap = sb.Capacity;
            return QueryFullProcessImageNameW(hProc, 0, sb, ref cap) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    public void HandleExternalExplorerRestart()
    {
        if (!_started || _disposed || _proxyDestroyed) return;
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
        long now = Environment.TickCount64;
        var visible = _registry.GetVisibleIcons();
        var result = new List<TrayDisplayItem>(visible.Count);
        foreach (var rec in visible)
        {
            BitmapSource? bmp = null;
            if (_bitmapCache.TryGetValue(rec.Key, out var cached) && cached.Revision == rec.IconRevision && cached.Bitmap is not null)
            {
                bmp = cached.Bitmap;
            }
            else if (_lastValidBitmaps.TryGetValue(rec.Key, out var valid))
            {
                bmp = valid;
            }
            else
            {
                bmp = TrayIconBitmapConverter.ConvertFromHIcon(rec.IconHandle, rec.Key.OwnerHwnd);
                if (bmp is not null)
                {
                    _bitmapCache[rec.Key] = (rec.IconRevision, bmp);
                    _lastValidBitmaps[rec.Key] = bmp;
                }
            }

            bool isFlashing = _flashingUntilTicks.TryGetValue(rec.Key, out long until) && now < until;
            bool blinkOff = isFlashing && _blinkOffKeys.Contains(rec.Key);
            result.Add(new TrayDisplayItem(rec, bmp, isFlashing, blinkOff));
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

        // User interacted with this tray icon -> immediately clear any active blink state
        _flashingUntilTicks.Remove(icon.Key);
        _blinkOffKeys.Remove(icon.Key);

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
                        _lastValidBitmaps.Remove(updated.Key);
                        _lastIconModifyTicks.Remove(updated.Key);
                        _flashingUntilTicks.Remove(updated.Key);
                        _blinkOffKeys.Remove(updated.Key);
                    }
                    else if (updated is not null)
                    {
                        long nowTicks = Environment.TickCount64;
                        bool iconFlagSet = (cmd.Flags & NotifyIconFlags.Icon) != 0;

                        if (iconFlagSet || !_lastValidBitmaps.ContainsKey(updated.Key))
                        {
                            var bmp = TrayIconBitmapConverter.ConvertFromHIcon(
                                updated.IconHandle,
                                updated.Key.OwnerHwnd,
                                out bool isBlankFrame);

                            if (bmp is not null)
                            {
                                _bitmapCache[updated.Key] = (updated.IconRevision, bmp);
                                _lastValidBitmaps[updated.Key] = bmp;
                                _blinkOffKeys.Remove(updated.Key);
                            }
                            else if (isBlankFrame && _lastValidBitmaps.TryGetValue(updated.Key, out var kept))
                            {
                                // App swapped to blank/transparent icon to blink (e.g. WeChat/QQ new message):
                                // keep the valid icon (NEVER turn into a black dot!) and show subtle blink phase!
                                _bitmapCache[updated.Key] = (updated.IconRevision, kept);
                                _blinkOffKeys.Add(updated.Key);
                                _flashingUntilTicks[updated.Key] = nowTicks + 1600;
                                NotifyOwnerPidFlashing(cmd.OwnerHwnd);
                            }

                            if (cmd.Kind == TrayCommandKind.Modify && iconFlagSet && cmd.OwnerHwnd != 0)
                            {
                                if (_lastIconModifyTicks.TryGetValue(updated.Key, out long prevTicks) && (nowTicks - prevTicks) <= 1500)
                                {
                                    _flashingUntilTicks[updated.Key] = nowTicks + 1600;
                                    if (bmp is not null && !isBlankFrame)
                                    {
                                        // App alternates between two non-blank icons -> toggle gentle blink indicator
                                        if (!_blinkOffKeys.Add(updated.Key)) _blinkOffKeys.Remove(updated.Key);
                                    }
                                    NotifyOwnerPidFlashing(cmd.OwnerHwnd);
                                }
                                _lastIconModifyTicks[updated.Key] = nowTicks;
                            }
                        }
                    }

                    RaiseIconsChanged();
                }

                // Mirror asynchronously on background thread so ProxyWndProc returns in <0.1ms and NEVER deadlocks Wallpaper Engine / Explorer!
                ForwardCopyDataAsync(cds.dwData, wParam, buffer);
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

    private void NotifyOwnerPidFlashing(nint ownerHwnd)
    {
        if (ownerHwnd == 0) return;
        GetWindowThreadProcessId(ownerHwnd, out uint ownerPid);
        if (ownerPid != 0)
        {
            TrayIconFlashingDetected?.Invoke(ownerPid);
        }
    }

    private void ForwardCopyDataAsync(nint dwData, nint senderWParam, byte[] copiedPayload)
    {
        if (_disposed || _proxyDestroyed) return;
        nint target = _explorerTrayHwnd;
        if (target == 0 || target == _proxyTrayHwnd) return;

        ThreadPool.QueueUserWorkItem(_state =>
        {
            if (_disposed || !IsWindow(target)) return;
            var handle = GCHandle.Alloc(copiedPayload, GCHandleType.Pinned);
            try
            {
                var copyCds = new COPYDATASTRUCT
                {
                    dwData = dwData,
                    cbData = copiedPayload.Length,
                    lpData = handle.AddrOfPinnedObject()
                };
                SendMessageTimeoutCopyData(target, WM_COPYDATA, senderWParam, ref copyCds, SMTO_ABORTIFHUNG, 50, out _);
            }
            catch { }
            finally
            {
                handle.Free();
            }
        });
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

        return SendMessageTimeoutW(_explorerTrayHwnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 80, out nint result) != 0
            ? result
            : 0;
    }

    private void RunMaintenanceTick()
    {
        if (_disposed) return;

        EnsureProxyFirstInZOrder();

        bool uiNeedsRefresh = false;
        long now = Environment.TickCount64;

        // Expire any finished message-blink animations cleanly so icons ALWAYS return to full opacity!
        if (_flashingUntilTicks.Count > 0)
        {
            var expired = new List<TrayIconKey>();
            foreach (var (key, until) in _flashingUntilTicks)
            {
                if (now >= until) expired.Add(key);
            }
            foreach (var k in expired)
            {
                _flashingUntilTicks.Remove(k);
                _blinkOffKeys.Remove(k);
                uiNeedsRefresh = true;
            }
        }

        // Every ~3s (every 4 ticks), prune closed tray owners
        if ((++_tickCounter % 4) == 0)
        {
            if (PruneDeadIconsInternal())
            {
                uiNeedsRefresh = true;
            }
        }

        if (uiNeedsRefresh)
        {
            RaiseIconsChanged();
        }
    }

    private void PruneDeadIcons()
    {
        if (_disposed) return;
        if (PruneDeadIconsInternal())
        {
            RaiseIconsChanged();
        }
    }

    private bool PruneDeadIconsInternal()
    {
        if (!_registry.PruneDeadWindows(hwnd => IsWindow(hwnd))) return false;

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
        foreach (var k in staleKeys)
        {
            _bitmapCache.Remove(k);
            _lastValidBitmaps.Remove(k);
            _lastIconModifyTicks.Remove(k);
            _flashingUntilTicks.Remove(k);
            _blinkOffKeys.Remove(k);
        }
        return true;
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

    /// <summary>
    /// Destroys the proxy Shell_TrayWnd BEFORE restoring Explorer's taskbar so no app (such as Wallpaper Engine)
    /// ever sends messages to a half-disposed proxy during shutdown.
    /// </summary>
    public void DestroyProxyWindowOnly()
    {
        if (_proxyDestroyed) return;
        _proxyDestroyed = true;

        try { _maintenanceTimer.Stop(); } catch { }

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
        if (_registeredNotifyClass) { UnregisterClassW(NotifyClassName, hInstance); _registeredNotifyClass = false; }
        if (_registeredTrayClass) { UnregisterClassW(TrayClassName, hInstance); _registeredTrayClass = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DestroyProxyWindowOnly();

        if (_started)
        {
            // Ask all running apps to cleanly re-register onto Explorer's already-restored native taskbar
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeoutCopyData(nint hWnd, uint msg, nint wParam, ref COPYDATASTRUCT lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint hwndParent, nint hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, int dwFlags, System.Text.StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
