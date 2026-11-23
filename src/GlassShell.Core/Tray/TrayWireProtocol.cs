using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace GlassShell.Core.Tray;

/// <summary>
/// Pure-managed parser and serializer for Windows Shell_TrayWnd WM_COPYDATA IPC packets
/// (SHELLTRAYDATA dwData=1, WINNOTIFYICONIDENTIFIER dwData=3, and V4/legacy callback messages).
/// </summary>
public static class TrayWireProtocol
{
    public const uint ShellTrayMagic = 0x34753423;
    public const uint CopyDataAppBar = 0;
    public const uint CopyDataNotifyIcon = 1;
    public const uint CopyDataIconRect = 3;

    public const uint NisHidden = 0x00000001;

    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;

    /// <summary>
    /// Parses a SHELLTRAYDATA packet sent via WM_COPYDATA (dwData == 1).
    /// Windows shell32.dll always marshals NOTIFYICONDATA with 32-bit handle slots on the wire even on x64.
    /// </summary>
    public static bool TryParseNotifyIconCommand(ReadOnlySpan<byte> payload, out ParsedTrayCommand? command)
    {
        command = null;
        // Minimum header: dwSignature(4) + dwMessage(4) + cbSize(4) + hWnd(4) + uID(4) + uFlags(4) + uCallback(4) + hIcon(4) = 32 bytes
        if (payload.Length < 32) return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        if (magic != ShellTrayMagic) return false;

        uint rawMessage = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        if (rawMessage > (uint)TrayCommandKind.SetVersion) return false;

        // NOTIFYICONDATA32 starts at offset 8
        const int nid = 8;
        uint hWnd32 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 4, 4));
        uint uId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 8, 4));
        var flags = (NotifyIconFlags)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 12, 4));
        uint callbackMsg = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 16, 4));
        uint hIcon32 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 20, 4));

        string? tooltip = null;
        if ((flags & NotifyIconFlags.Tip) != 0 && payload.Length >= nid + 24 + 2)
        {
            int maxBytes = Math.Min(256, payload.Length - (nid + 24));
            maxBytes &= ~1; // align to UTF-16 code unit
            tooltip = ReadNullTerminatedUtf16(payload.Slice(nid + 24, maxBytes));
        }

        uint state = 0;
        uint stateMask = 0;
        if (payload.Length >= nid + 288)
        {
            state = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 280, 4));
            stateMask = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 284, 4));
        }

        uint version = 0;
        if (payload.Length >= nid + 804)
        {
            version = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(nid + 800, 4));
        }

        Guid guidItem = Guid.Empty;
        if ((flags & NotifyIconFlags.Guid) != 0 && payload.Length >= nid + 952)
        {
            guidItem = new Guid(payload.Slice(nid + 936, 16));
        }

        command = new ParsedTrayCommand(
            Kind: (TrayCommandKind)rawMessage,
            OwnerHwnd: new nint(unchecked((int)hWnd32)),
            IconId: uId,
            Flags: flags,
            CallbackMessage: callbackMsg,
            IconHandle: new nint(unchecked((int)hIcon32)),
            Tooltip: tooltip,
            State: state,
            StateMask: stateMask,
            Version: version,
            GuidItem: guidItem);

        return true;
    }

    /// <summary>
    /// Parses a WINNOTIFYICONIDENTIFIER packet sent by Shell_NotifyIconGetRect via WM_COPYDATA (dwData == 3).
    /// Layout: dwMagic(4) + dwMessage(4) + cbSize(4) + hWnd32(4) + uID(4) + guidItem(16) = 36 bytes.
    /// </summary>
    public static bool TryParseRectQuery(ReadOnlySpan<byte> payload, out ParsedTrayRectQuery? query)
    {
        query = null;
        if (payload.Length < 36) return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        if (magic != ShellTrayMagic) return false;

        uint queryKind = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        if (queryKind is not (1 or 2)) return false;

        uint hWnd32 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
        uint uId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
        var guid = new Guid(payload.Slice(20, 16));

        query = new ParsedTrayRectQuery(
            QueryKind: queryKind,
            OwnerHwnd: new nint(unchecked((int)hWnd32)),
            IconId: uId,
            GuidItem: guid);

        return true;
    }

    /// <summary>
    /// Packs a signed 16-bit screen coordinate pair into an LRESULT for Shell_NotifyIconGetRect (dwData == 3).
    /// </summary>
    public static nint PackScreenPoint(int x, int y)
    {
        uint packed = ((uint)(ushort)(short)y << 16) | (ushort)(short)x;
        return unchecked((nint)(int)packed);
    }

    /// <summary>
    /// Builds the exact callback message sequence to send to a tray icon's owner HWND
    /// for both NOTIFYICON_VERSION_4 and legacy V0/V3 apps.
    /// </summary>
    public static IReadOnlyList<TrayCallbackPacket> BuildCallbackPackets(
        TrayIconRecord icon,
        TrayMouseAction action,
        int screenX,
        int screenY)
    {
        if (icon.CallbackMessage == 0)
        {
            return [];
        }

        var list = new List<TrayCallbackPacket>(3);
        bool isV4 = icon.Version >= 4;
        nint xyParam = PackScreenPoint(screenX, screenY);

        void Emit(uint mouseMsg)
        {
            if (isV4)
            {
                uint lParam32 = ((uint)(ushort)icon.Key.IconId << 16) | (ushort)mouseMsg;
                list.Add(new TrayCallbackPacket(icon.CallbackMessage, xyParam, unchecked((nint)(int)lParam32)));
            }
            else
            {
                list.Add(new TrayCallbackPacket(icon.CallbackMessage, unchecked((nint)(int)icon.Key.IconId), unchecked((nint)(int)mouseMsg)));
            }
        }

        switch (action)
        {
            case TrayMouseAction.LeftClick:
                Emit(WmLButtonDown);
                Emit(WmLButtonUp);
                if (icon.Version >= 3)
                {
                    Emit(NinSelect);
                }
                break;

            case TrayMouseAction.LeftDoubleClick:
                Emit(WmLButtonDblClk);
                Emit(WmLButtonUp);
                break;

            case TrayMouseAction.RightClick:
                Emit(WmRButtonDown);
                Emit(WmRButtonUp);
                if (icon.Version >= 3)
                {
                    Emit(WmContextMenu);
                }
                break;

            case TrayMouseAction.MiddleClick:
                Emit(WmMButtonDown);
                Emit(WmMButtonUp);
                break;
        }

        return list;
    }

    private static string ReadNullTerminatedUtf16(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.Length;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                end = i;
                break;
            }
        }
        return end == 0 ? string.Empty : Encoding.Unicode.GetString(bytes.Slice(0, end));
    }
}
