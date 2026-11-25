using System;
using System.IO;
using TahoeDesk.Core.Config;
using Xunit;

namespace TahoeDesk.Core.Tests.Config;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"TahoeDeskTests_{Guid.NewGuid():N}");

    [Fact]
    public void Load_WhenFileMissing_ReturnsSpecDefaults()
    {
        var store = new JsonSettingsStore(Path.Combine(_tempDir, "settings.json"));
        var s = store.Load();
        Assert.Equal(ThemeMode.System, s.Theme);
        Assert.Equal(GlassOpacityPreset.Clear, s.OpacityPreset);
        Assert.False(s.ShowMenuBarBackground);
        Assert.Equal(52, s.DockIconSize);
        Assert.True(s.DockMagnificationEnabled);
        Assert.Equal(1.6, s.DockMagnificationScale);
        Assert.Equal(DockAutoHideMode.Smart, s.DockAutoHide);
        Assert.False(s.ShowMenuBarWifiIcon);
        Assert.True(s.ShowRecentApps);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(path);
        var updated = GlassSettings.Default with
        {
            ShowMenuBarBackground = true,
            DockIconSize = 64,
            PinnedAppPaths = [@"C:\Windows\notepad.exe"]
        };
        store.Save(updated);
        var loaded = store.Load();
        Assert.True(loaded.ShowMenuBarBackground);
        Assert.Equal(64, loaded.DockIconSize);
        Assert.Equal([@"C:\Windows\notepad.exe"], loaded.PinnedAppPaths);
    }

    [Fact]
    public void Load_WhenJsonCorrupted_ReturnsDefaultsWithoutThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, "{ broken json");
        var loaded = new JsonSettingsStore(path).Load();
        Assert.Equal(GlassSettings.Default.Theme, loaded.Theme);
        Assert.Equal(GlassSettings.Default.DockIconSize, loaded.DockIconSize);
        Assert.Equal(GlassSettings.Default.ShowMenuBarBackground, loaded.ShowMenuBarBackground);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
