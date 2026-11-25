using System;
using System.Buffers.Binary;
using System.Text;
using TahoeDesk.Core.Shell;
using TahoeDesk.Core.Tray;
using Xunit;

namespace TahoeDesk.Core.Tests.Tray;

public sealed class TrayProtocolAndRegistryTests
{
    private static byte[] BuildShellTrayPacket(
        TrayCommandKind kind,
        uint hwnd,
        uint iconId,
        NotifyIconFlags flags,
        uint callbackMsg = 0x8001,
        uint hIcon = 0x1234,
        string? tip = null,
        uint state = 0,
        uint stateMask = 0,
        uint version = 0,
        Guid guid = default)
    {
        var buf = new byte[8 + 956];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), TrayWireProtocol.ShellTrayMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)kind);

        const int nid = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 0, 4), 956);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 4, 4), hwnd);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 8, 4), iconId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 12, 4), (uint)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 16, 4), callbackMsg);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 20, 4), hIcon);

        if (tip is not null)
        {
            var bytes = Encoding.Unicode.GetBytes(tip);
            bytes.AsSpan(0, Math.Min(bytes.Length, 254)).CopyTo(buf.AsSpan(nid + 24));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 280, 4), state);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 284, 4), stateMask);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(nid + 800, 4), version);
        guid.TryWriteBytes(buf.AsSpan(nid + 936, 16));
        return buf;
    }

    [Fact]
    public void TryParseNotifyIconCommand_ParsesValidShellTrayPacket()
    {
        var guid = Guid.NewGuid();
        var buf = BuildShellTrayPacket(
            TrayCommandKind.Add,
            hwnd: 0x000A1234,
            iconId: 7,
            flags: NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip | NotifyIconFlags.Guid,
            callbackMsg: 0x0401,
            hIcon: 0x0055AA11,
            tip: "微信 - 3条新消息",
            guid: guid);

        Assert.True(TrayWireProtocol.TryParseNotifyIconCommand(buf, out var cmd));
        Assert.NotNull(cmd);
        Assert.Equal(TrayCommandKind.Add, cmd!.Kind);
        Assert.Equal((nint)0x000A1234, cmd.OwnerHwnd);
        Assert.Equal(7u, cmd.IconId);
        Assert.Equal(0x0401u, cmd.CallbackMessage);
        Assert.Equal((nint)0x0055AA11, cmd.IconHandle);
        Assert.Equal("微信 - 3条新消息", cmd.Tooltip);
        Assert.Equal(guid, cmd.GuidItem);
    }

    [Fact]
    public void TryParseNotifyIconCommand_RejectsInvalidMagicOrShortPayload()
    {
        var shortBuf = new byte[16];
        Assert.False(TrayWireProtocol.TryParseNotifyIconCommand(shortBuf, out _));

        var badMagic = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(badMagic, 0xDEADBEEF);
        Assert.False(TrayWireProtocol.TryParseNotifyIconCommand(badMagic, out _));
    }

    [Fact]
    public void TryParseRectQuery_And_PackScreenPoint_WorkForMultiMonitorCoords()
    {
        var guid = Guid.NewGuid();
        var buf = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), TrayWireProtocol.ShellTrayMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 1); // TopLeft
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), 0x00123456);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 42);
        guid.TryWriteBytes(buf.AsSpan(20, 16));

        Assert.True(TrayWireProtocol.TryParseRectQuery(buf, out var query));
        Assert.Equal(1u, query!.QueryKind);
        Assert.Equal((nint)0x00123456, query.OwnerHwnd);
        Assert.Equal(42u, query.IconId);
        Assert.Equal(guid, query.GuidItem);

        // Negative X coordinate (left secondary monitor) packs as signed 16-bit low word
        nint packed = TrayWireProtocol.PackScreenPoint(-120, 18);
        int raw = unchecked((int)packed);
        short unpackedX = unchecked((short)(raw & 0xFFFF));
        short unpackedY = unchecked((short)((raw >> 16) & 0xFFFF));
        Assert.Equal(-120, unpackedX);
        Assert.Equal(18, unpackedY);
    }

    [Fact]
    public void BuildCallbackPackets_FormatsV4AndLegacyMessagesAccurately()
    {
        var v4Icon = new TrayIconRecord(
            new TrayIconKey(0x100, 9, Guid.Empty),
            CallbackMessage: 0x8001,
            Version: 4,
            Tooltip: "Clash",
            IconHandle: 0x200,
            IsHidden: false,
            OrderIndex: 0,
            IconRevision: 1,
            LastScreenRect: default);

        var v4Right = TrayWireProtocol.BuildCallbackPackets(v4Icon, TrayMouseAction.RightClick, 1800, 14);
        Assert.Equal(3, v4Right.Count); // RBUTTONDOWN, RBUTTONUP, WM_CONTEXTMENU
        Assert.Equal(TrayWireProtocol.PackScreenPoint(1800, 14), v4Right[2].WParam);
        Assert.Equal(unchecked((nint)(int)((9u << 16) | 0x007Bu)), v4Right[2].LParam);

        var legacyIcon = v4Icon with { Version = 0 };
        var legacyRight = TrayWireProtocol.BuildCallbackPackets(legacyIcon, TrayMouseAction.RightClick, 1800, 14);
        Assert.Equal(2, legacyRight.Count); // RBUTTONDOWN, RBUTTONUP only (no duplicate WM_CONTEXTMENU)
        Assert.Equal((nint)9, legacyRight[1].WParam);
        Assert.Equal((nint)0x0205, legacyRight[1].LParam);
    }

    [Fact]
    public void Registry_TracksAddModifyHideVersionRectAndPrune()
    {
        var reg = new TrayIconRegistry();

        var addPacket = BuildShellTrayPacket(
            TrayCommandKind.Add, 0x1000, 1,
            NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip,
            callbackMsg: 0x0401, hIcon: 0xAAA, tip: "WeChat");
        Assert.True(TrayWireProtocol.TryParseNotifyIconCommand(addPacket, out var addCmd));
        Assert.True(reg.ApplyCommand(addCmd!, out var added));
        Assert.Single(reg.GetVisibleIcons());
        long rev1 = added!.IconRevision;

        // Modify tooltip only -> preserves hIcon and does not bump IconRevision
        var modTip = BuildShellTrayPacket(
            TrayCommandKind.Modify, 0x1000, 1,
            NotifyIconFlags.Tip, tip: "WeChat (2)");
        TrayWireProtocol.TryParseNotifyIconCommand(modTip, out var modTipCmd);
        reg.ApplyCommand(modTipCmd!, out var afterTip);
        Assert.Equal("WeChat (2)", afterTip!.Tooltip);
        Assert.Equal((nint)0xAAA, afterTip.IconHandle);
        Assert.Equal(rev1, afterTip.IconRevision);

        // SetVersion 4
        var setVer = BuildShellTrayPacket(TrayCommandKind.SetVersion, 0x1000, 1, NotifyIconFlags.None, version: 4);
        TrayWireProtocol.TryParseNotifyIconCommand(setVer, out var setVerCmd);
        reg.ApplyCommand(setVerCmd!, out var afterVer);
        Assert.Equal(4u, afterVer!.Version);

        // Screen rect tracking
        var rect = new ScreenRect(1700, 2, 1724, 30);
        Assert.True(reg.UpdateScreenRect(added.Key, rect));
        Assert.True(reg.TryGetIconRect(0x1000, 1, Guid.Empty, out var queried));
        Assert.Equal(rect, queried);

        // Hide via NIS_HIDDEN
        var hidePkt = BuildShellTrayPacket(
            TrayCommandKind.Modify, 0x1000, 1,
            NotifyIconFlags.State, state: TrayWireProtocol.NisHidden, stateMask: TrayWireProtocol.NisHidden);
        TrayWireProtocol.TryParseNotifyIconCommand(hidePkt, out var hideCmd);
        reg.ApplyCommand(hideCmd!, out _);
        Assert.Empty(reg.GetVisibleIcons());

        // Prune dead window
        Assert.True(reg.PruneDeadWindows(hwnd => hwnd != 0x1000));
        Assert.False(reg.TryGetIconRect(0x1000, 1, Guid.Empty, out _));
    }

    [Fact]
    public void PartitionForMenuBar_SplitsDefaultAndPinnedIconsCleanly()
    {
        var icons = new TrayIconRecord[6];
        for (int i = 0; i < 6; i++)
        {
            icons[i] = new TrayIconRecord(
                new TrayIconKey(0x2000 + i, 1, Guid.Empty),
                0x8000, 4, $"App{i}", 0x100 + i, false, i, 1, default);
        }

        // Default: first 4 inline, last 2 overflow
        var (barDefault, overflowDefault) = TrayIconRegistry.PartitionForMenuBar(icons, pinnedKeys: null, defaultInlineCount: 4);
        Assert.Equal(4, barDefault.Count);
        Assert.Equal(2, overflowDefault.Count);

        // Explicit pinned keys: only pinned stay on bar
        var (barPinned, overflowPinned) = TrayIconRegistry.PartitionForMenuBar(icons, pinnedKeys: ["tip:App1", "tip:App5"]);
        Assert.Equal(2, barPinned.Count);
        Assert.Equal("App1", barPinned[0].Tooltip);
        Assert.Equal("App5", barPinned[1].Tooltip);
        Assert.Equal(4, overflowPinned.Count);

        // Custom overflow drag-reorder persists order cleanly
        var (_, reordered) = TrayIconRegistry.PartitionForMenuBar(
            icons,
            pinnedKeys: ["tip:App1", "tip:App5"],
            overflowOrderKeys: ["tip:App4", "tip:App2", "tip:App0", "tip:App3"]);
        Assert.Equal("App4", reordered[0].Tooltip);
        Assert.Equal("App2", reordered[1].Tooltip);
    }
}
