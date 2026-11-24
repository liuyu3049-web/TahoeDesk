using System;
using GlassShell.Core.Tray;
using Xunit;

namespace GlassShell.Core.Tests.Tray;

public sealed class TraySystemIconFilterTests
{
    private static TrayIconRecord MakeIcon(uint id, string tooltip, Guid? guid = null) =>
        new(
            Key: new TrayIconKey(new nint(1000 + id), id, guid ?? Guid.Empty),
            CallbackMessage: 0x8000,
            Version: 4,
            Tooltip: tooltip,
            IconHandle: 0,
            IsHidden: false,
            OrderIndex: (int)id,
            IconRevision: 1,
            LastScreenRect: default);

    [Fact]
    public void PartitionForMenuBar_FiltersRedundantSystemPowerAndAudioIconsByGuidAndTooltip()
    {
        var icons = new[]
        {
            MakeIcon(1, "微信"),
            MakeIcon(2, "电池状态: 剩余 86% (已接通电源)"),
            MakeIcon(3, "扬声器: 35%"),
            MakeIcon(4, "Speakers (Realtek): 50%"),
            MakeIcon(5, "Unknown", new Guid("7820ae75-23e3-4229-82c1-e41cb67d5b9c")), // Power GUID
            MakeIcon(6, "Unknown", new Guid("7820ae73-23e3-4229-82c1-e41cb67d5b9c")), // Audio GUID
            MakeIcon(7, "Steam")
        };

        var (bar, overflow) = TrayIconRegistry.PartitionForMenuBar(icons, pinnedKeys: null, defaultInlineCount: 1);

        Assert.Single(bar);
        Assert.Equal("微信", bar[0].Tooltip);
        Assert.Single(overflow);
        Assert.Equal("Steam", overflow[0].Tooltip);
    }
}
