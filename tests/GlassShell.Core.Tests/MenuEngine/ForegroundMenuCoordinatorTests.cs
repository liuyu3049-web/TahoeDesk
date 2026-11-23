using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GlassShell.Core.MenuEngine;
using Xunit;

namespace GlassShell.Core.Tests.MenuEngine;

public sealed class ForegroundMenuCoordinatorTests
{
    private sealed class StubMenuExtractor(
        MenuSource source,
        Func<CancellationToken, Task<IReadOnlyList<MenuNode>?>> handler) : IMenuExtractor
    {
        public MenuSource Source => source;
        public Task<IReadOnlyList<MenuNode>?> TryExtractAsync(nint hwnd, string processName, CancellationToken ct)
            => handler(ct);
    }

    [Fact]
    public async Task OnForegroundChanged_IgnoresOwnProcessHwnd_PreservesPreviousTarget()
    {
        var profiles = JsonProfileMenuProvider.CreateBuiltIn();
        var coord = new ForegroundMenuCoordinator([], profiles, selfProcessId: 999);

        await coord.OnForegroundChangedAsync(100, 10, "chrome", "Google Chrome");
        await coord.OnForegroundChangedAsync(200, 999, "GlassShell.UI", "GlassShell");

        Assert.Equal((nint)100, coord.CurrentSnapshot.TargetHwnd);
        Assert.Equal("Google Chrome", coord.CurrentSnapshot.AppDisplayName);
    }

    [Fact]
    public async Task OnForegroundChanged_PrefersTier1Win32Menu_WhenAvailable()
    {
        var win32Menus = new[]
        {
            new MenuNode("m1", "文件", null, true, false, false, null, [
                new MenuNode("m1_1", "新建", "Ctrl+N", true, false, false, new MenuInvokeAction(MenuActionKind.Win32Command, CommandId: 101), [])
            ])
        };
        var tier1 = new StubMenuExtractor(MenuSource.Win32GetMenu, _ => Task.FromResult<IReadOnlyList<MenuNode>?>(win32Menus));
        var coord = new ForegroundMenuCoordinator([tier1], JsonProfileMenuProvider.CreateBuiltIn(), selfProcessId: 999);

        await coord.OnForegroundChangedAsync(100, 10, "oldapp", "Old App");

        Assert.Equal(MenuSource.Win32GetMenu, coord.CurrentSnapshot.Source);
        Assert.Single(coord.CurrentSnapshot.TopLevelMenus);
        Assert.Equal("文件", coord.CurrentSnapshot.TopLevelMenus[0].Title);
    }

    [Fact]
    public async Task OnForegroundChanged_WhenTier1StallsPastTimeout_TimesOutAndFallsBackToProfile()
    {
        var slowExtractor = new StubMenuExtractor(MenuSource.Win32GetMenu, async ct =>
        {
            await Task.Delay(2000, ct);
            return [new MenuNode("x", "Slow", null, true, false, false, null, [])];
        });
        var coord = new ForegroundMenuCoordinator(
            [slowExtractor],
            JsonProfileMenuProvider.CreateBuiltIn(),
            selfProcessId: 999,
            probeTimeout: TimeSpan.FromMilliseconds(150));

        await coord.OnForegroundChangedAsync(100, 10, "chrome", "Google Chrome");

        Assert.Equal(MenuSource.AppProfile, coord.CurrentSnapshot.Source);
        Assert.Contains(coord.CurrentSnapshot.TopLevelMenus, m => m.Title == "文件");
        Assert.Contains(coord.CurrentSnapshot.TopLevelMenus, m => m.Title == "标签页");
    }

    [Fact]
    public async Task OnForegroundChanged_WhenExtractorThrowsUipiUnauthorized_DegradesGracefully()
    {
        var uipiExtractor = new StubMenuExtractor(MenuSource.StaticUia, _ => throw new UnauthorizedAccessException("UIPI"));
        var coord = new ForegroundMenuCoordinator([uipiExtractor], JsonProfileMenuProvider.CreateBuiltIn(), selfProcessId: 999);

        await coord.OnForegroundChangedAsync(300, 50, "Taskmgr", "任务管理器");

        Assert.Equal(MenuSource.GenericFallback, coord.CurrentSnapshot.Source);
        Assert.Contains(coord.CurrentSnapshot.TopLevelMenus, m => m.Title == "窗口");
    }

    [Fact]
    public async Task OnForegroundChanged_RapidSwitchCancelsEarlierProbe()
    {
        bool firstCancelled = false;
        var extractor = new StubMenuExtractor(MenuSource.StaticUia, async ct =>
        {
            try
            {
                await Task.Delay(180, ct);
            }
            catch (OperationCanceledException)
            {
                firstCancelled = true;
                throw;
            }
            return null;
        });
        var coord = new ForegroundMenuCoordinator([extractor], JsonProfileMenuProvider.CreateBuiltIn(), selfProcessId: 999);

        var t1 = coord.OnForegroundChangedAsync(100, 10, "unknown1", "App1");
        await Task.Delay(25);
        var t2 = coord.OnForegroundChangedAsync(200, 20, "code", "Visual Studio Code");
        await Task.WhenAll(t1, t2);

        Assert.True(firstCancelled);
        Assert.Equal((nint)200, coord.CurrentSnapshot.TargetHwnd);
        Assert.Equal("Visual Studio Code", coord.CurrentSnapshot.AppDisplayName);
        Assert.Equal(MenuSource.AppProfile, coord.CurrentSnapshot.Source);
    }
}
