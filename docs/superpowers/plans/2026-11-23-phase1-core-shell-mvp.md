# GlassShell Phase 1 (Core Shell MVP) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver Phase 1 (Daily-Driver MVP) of GlassShell — a Windows 11 macOS Tahoe Liquid Glass desktop shell featuring safe native taskbar hiding with crash watchdog recovery, top 28 DIP AppBar reservation, DXGI/D3D11 real-time optical refraction Liquid Glass rendering, an absolute-silent 3-tier foreground app Menu Bar with mini calendar card, and a Gaussian-magnified Bottom Dock with bounce physics, tooltips, drag-and-drop, Recycle Bin, and fullscreen auto-yield.

**Architecture:** Clean 3-project `.NET 8` (`net8.0-windows10.0.22621.0`) solution separating pure Windows interop/domain logic (`GlassShell.Core`, zero WPF dependencies, unit-tested via `xUnit`) from the WPF + DXGI/D3D11 HLSL presentation layer (`GlassShell.UI`) and an isolated zero-CPU crash recovery process (`GlassShell.Watchdog`). Phase 2 (Win-key Launchpad, Spotlight, Control Center, Dock Stacks & pre-minimize snapshots) and Phase 3 (Wallpaper luminance auto-ink, Icon Squircle harmonizer, full Settings UI & portable packaging) will follow in dedicated plans once Phase 1 runs reliably on Windows 11.

**Tech Stack:** C# 12, .NET 8 SDK (`net8.0-windows10.0.22621.0`), WPF, `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.D3DCompiler`, `Interop.UIAutomationClient` (COM UIA), `CommunityToolkit.Mvvm`, `xUnit`.

**Spec:** `docs/superpowers/specs/2026-11-23-liquid-glass-desktop-design.md`

---

## Global Constraints

- Target OS: Windows 11 Build 26200+; target framework `net8.0-windows10.0.22621.0` (`.NET 8 SDK` installed in Task 1).
- Zero DLL injection into third-party processes; use strictly public Win32, COM (`IUIAutomation`), DXGI/D3D11, DWM, and WinRT APIs.
- Standard user privilege (Medium Integrity) only; never require UAC; elevated foreground windows degrade gracefully without throwing.
- Top Menu Bar: `28 DIP` height, default fully transparent (`ShowMenuBarBackground = false`), `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `WDA_EXCLUDEFROMCAPTURE`.
- Bottom Dock: `6 DIP` gap from bottom screen edge, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `WDA_EXCLUDEFROMCAPTURE`, 3 sections separated by `1px` vertical translucent dividers (`此电脑` + `启动台` leftmost, `回收站` rightmost).
- MenuEngine strictly silent: NEVER call `ExpandCollapsePattern.Expand()` on any target window; background MTA thread; `200ms` single-probe timeout; immediate cancellation on foreground focus change.
- All user-facing UI labels in Simplified Chinese with Windows-localized branding (Win11 logo top-left, `此电脑`, `回收站`, `Ctrl`/`Alt`/`Shift`/`Win` shortcuts).
- Target resident RAM: `60–100 MB`.

## Review Focus

1. **Explorer restart (`TaskbarCreated`) or Windows notification popping `Shell_TrayWnd` back up** — `TaskbarController` must automatically re-hide `Shell_TrayWnd` whenever `IsTrayPeekActive == false`, never re-hide while `IsTrayPeekActive == true`, and idempotently restore on release. *(Pinned in Task 2)*
2. **Rapid `Alt+Tab` storms, hung apps (>200ms UIA stall), or elevated Admin foreground windows (UIPI `E_ACCESSDENIED`)** — `ForegroundMenuCoordinator` must cancel stale probes immediately, trip the 200ms timeout fuse, catch UIPI exceptions, and cleanly return Tier 3 fallback menus. *(Pinned in Task 4)*
3. **DXGI `DXGI_ERROR_ACCESS_LOST` on UAC secure desktop, lock screen (`Win+L`), RDP, or display/DPI change** — `DxgiBackdropSampler` must degrade gracefully to translucent tint fallback without crashing and auto-reacquire desktop duplication with backoff. *(Pinned in Task 5)*
4. **Menu Bar / Dock focus steal** — Clicking GlassShell's own top menu bar or Dock must never overwrite the tracked foreground app HWND with GlassShell's own process or prevent dispatching menu commands to the previous foreground app. *(Pinned in Task 4 & Task 6)*
5. **Fullscreen game/video detection vs. desktop wallpaper windows (`Progman`/`WorkerW`)** — `FullscreenDetector` must never treat desktop windows as fullscreen, must hide both bars on real fullscreen apps, and must cancel active tray peek on fullscreen entry. *(Pinned in Task 2)*

---

## File Structure Overview

- `GlassShell.sln`, `.gitignore` — Solution & Git hygiene
- `src/GlassShell.Core/Config/` — `GlassSettings.cs`, `JsonSettingsStore.cs`
- `src/GlassShell.Core/Shell/` — `ITaskbarNative.cs`, `Win32TaskbarNative.cs`, `TaskbarController.cs`, `FullscreenDetector.cs`, `MonitorContext.cs`
- `src/GlassShell.Core/Discovery/` — `WindowModels.cs`, `IWindowTracker.cs`, `WindowTracker.cs`, `IconExtractor.cs`, `RecycleBinService.cs`, `DockModelCoordinator.cs`
- `src/GlassShell.Core/MenuEngine/` — `MenuModels.cs`, `IMenuExtractor.cs`, `Win32GetMenuExtractor.cs`, `StaticUiaMenuExtractor.cs`, `JsonProfileMenuProvider.cs`, `ForegroundMenuCoordinator.cs`, `Assets/MenuProfiles/*.json`
- `src/GlassShell.Core/DockPhysics/` — `DockMagnificationCalculator.cs`, `SpringBounceAnimator.cs`, `DockAutoHideController.cs`
- `src/GlassShell.Core/SystemStatus/` — `SystemStatusModels.cs`, `SystemStatusProvider.cs` (IME, Battery, Network, Chinese clock formatting)
- `src/GlassShell.Watchdog/` — `Program.cs` (parent-PID monitor + `--restore-taskbar` recovery)
- `src/GlassShell.UI/Glass/` — `GlassRenderParams.cs`, `IBackdropSampler.cs`, `DxgiBackdropSampler.cs`, `LiquidGlassRenderer.cs`, `Shaders/LiquidGlass.hlsl`, `LiquidGlassSurface.cs`, `WindowInteropHelperEx.cs`
- `src/GlassShell.UI/MenuBar/` — `MenuBarWindow.xaml(.cs)`, `MenuBarViewModel.cs`, `LiquidGlassMenuPopup.xaml(.cs)`, `CalendarPopup.xaml(.cs)`, `AboutMacDialog.xaml(.cs)`, `ForceQuitWindow.xaml(.cs)`
- `src/GlassShell.UI/Dock/` — `DockWindow.xaml(.cs)`, `DockViewModel.cs`, `DockTooltipPopup.xaml(.cs)`, `DockContextMenu.cs`
- `src/GlassShell.UI/` — `App.xaml(.cs)`, `Themes/LiquidGlassStyles.xaml`
- `tests/GlassShell.Core.Tests/` — Unit tests per Core subsystem

---

### Task 1: Toolchain Prerequisite, Solution Scaffolding & Settings Store

**Files:**
- Create: `.gitignore`
- Create: `GlassShell.sln`
- Create: `src/GlassShell.Core/GlassShell.Core.csproj`
- Create: `src/GlassShell.UI/GlassShell.UI.csproj`
- Create: `src/GlassShell.Watchdog/GlassShell.Watchdog.csproj`
- Create: `tests/GlassShell.Core.Tests/GlassShell.Core.Tests.csproj`
- Create: `src/GlassShell.Core/Config/GlassSettings.cs`
- Create: `src/GlassShell.Core/Config/JsonSettingsStore.cs`
- Test: `tests/GlassShell.Core.Tests/Config/JsonSettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `enum ThemeMode { System, Light, Dark }`
  - `enum GlassOpacityPreset { Clear, Balanced, Frosted }`
  - `enum DockAutoHideMode { Off, Always, Smart }`
  - `sealed record GlassSettings` (defaults: `Theme = ThemeMode.System`, `OpacityPreset = GlassOpacityPreset.Clear`, `ShowMenuBarBackground = false`, `DockIconSize = 52`, `DockMagnificationEnabled = true`, `DockMagnificationScale = 1.6`, `DockAutoHide = DockAutoHideMode.Smart`, `ShowRecentApps = true`, `PinnedAppPaths = []`, `PinnedFolderPaths = []`)
  - `sealed class JsonSettingsStore(string filePath)` with `GlassSettings Load()` and `void Save(GlassSettings settings)`

- [ ] **Step 1: Verify or install `.NET 8 SDK` and initialize Git**

Run: `dotnet --list-sdks`
If no `8.x` SDK is listed, install via `winget install --id Microsoft.DotNet.SDK.8 --exact --accept-source-agreements --accept-package-agreements` (outside sandbox if network needed), then re-verify `dotnet --list-sdks` prints `8.0.xxx`.
Run: `git init` and create `.gitignore` ignoring `bin/`, `obj/`, `.vs/`, `*.user`.

- [ ] **Step 2: Scaffold solution and 4 projects**

Create `GlassShell.sln` and:
- `src/GlassShell.Core/GlassShell.Core.csproj`: `TargetFramework=net8.0-windows10.0.22621.0`, `Nullable=enable`, `AllowUnsafeBlocks=true`.
- `src/GlassShell.UI/GlassShell.UI.csproj`: `OutputType=WinExe`, `TargetFramework=net8.0-windows10.0.22621.0`, `UseWPF=true`, `Nullable=enable`, `AllowUnsafeBlocks=true`, references `GlassShell.Core`, packages `CommunityToolkit.Mvvm`, `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.D3DCompiler`.
- `src/GlassShell.Watchdog/GlassShell.Watchdog.csproj`: `OutputType=WinExe`, `TargetFramework=net8.0-windows10.0.22621.0`, `Nullable=enable`.
- `tests/GlassShell.Core.Tests/GlassShell.Core.Tests.csproj`: `TargetFramework=net8.0-windows10.0.22621.0`, `xUnit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, references `GlassShell.Core`.

- [ ] **Step 3: Write the failing settings store tests**

Create `tests/GlassShell.Core.Tests/Config/JsonSettingsStoreTests.cs`:
```csharp
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"GlassShellTests_{Guid.NewGuid():N}");

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
        Assert.True(s.ShowRecentApps);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var store = new JsonSettingsStore(path);
        var updated = GlassSettings.Default with { ShowMenuBarBackground = true, DockIconSize = 64, PinnedAppPaths = [@"C:\Windows\notepad.exe"] };
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
        Assert.Equal(GlassSettings.Default, loaded);
    }

    public void Dispose() { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test tests/GlassShell.Core.Tests --filter JsonSettingsStoreTests`
Expected: FAIL with missing type `GlassSettings` / `JsonSettingsStore`.

- [ ] **Step 5: Implement `GlassSettings` and `JsonSettingsStore`**

- In `src/GlassShell.Core/Config/GlassSettings.cs`: define `ThemeMode`, `GlassOpacityPreset`, `DockAutoHideMode`, and `GlassSettings` record with `static GlassSettings Default => new();` matching spec defaults.
- In `src/GlassShell.Core/Config/JsonSettingsStore.cs`: atomic write via temp file + `File.Move(..., overwrite: true)`, indented camelCase `System.Text.Json`, fallback to `GlassSettings.Default` on missing/corrupt JSON, default path helper `%LocalAppData%\GlassShell\settings.json`.

- [ ] **Step 6: Run tests and verify pass**

Run: `dotnet test tests/GlassShell.Core.Tests --filter JsonSettingsStoreTests`
Expected: PASS (3/3 tests).

- [ ] **Step 7: Commit**

```bash
git add .gitignore GlassShell.sln src/ tests/ docs/
git commit -m "chore: scaffold GlassShell .NET 8 solution and settings store"
```

---

### Task 2: Safe Taskbar Suppression, Top AppBar, Fullscreen Detector & Crash Watchdog

**Files:**
- Create: `src/GlassShell.Core/Shell/MonitorContext.cs`
- Create: `src/GlassShell.Core/Shell/ITaskbarNative.cs`
- Create: `src/GlassShell.Core/Shell/Win32TaskbarNative.cs`
- Create: `src/GlassShell.Core/Shell/FullscreenDetector.cs`
- Create: `src/GlassShell.Core/Shell/TaskbarController.cs`
- Modify: `src/GlassShell.Watchdog/Program.cs`
- Test: `tests/GlassShell.Core.Tests/Shell/TaskbarControllerTests.cs`
- Test: `tests/GlassShell.Core.Tests/Shell/FullscreenDetectorTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)`
  - `sealed record MonitorContext(nint MonitorHandle, ScreenRect Bounds, double DpiScale, bool IsPrimary)`
  - `readonly record struct ForegroundWindowInfo(nint Hwnd, string ClassName, string ProcessName, ScreenRect Bounds, bool IsMinimized, bool IsCloaked)`
  - `interface ITaskbarNative`: `nint FindPrimaryTaskbar()`, `bool SetWindowVisible(nint hwnd, bool visible)`, `bool RegisterTopAppBar(nint appBarHwnd, int heightPx, ScreenRect monitorBounds)`, `void UnregisterAppBar(nint appBarHwnd)`, `bool IsD3DFullscreenActive()`
  - `static class FullscreenDetector`: `static bool IsFullscreen(ForegroundWindowInfo window, MonitorContext monitor, bool isD3DFullscreen)`
  - `sealed class TaskbarController(ITaskbarNative native)`: `bool IsAcquired`, `bool IsTrayPeekActive`, `bool IsFullscreenYieldActive`, `event Action<bool>? BarsVisibilityChanged`, `void Acquire(nint appBarHwnd, MonitorContext primaryMonitor, int topBarDip = 28)`, `void Release()`, `void BeginTrayPeek()`, `void EndTrayPeek()`, `void HandleExplorerRestart()`, `void HandleTaskbarShownEvent()`, `void UpdateFullscreenState(bool isFullscreen)`
  - `GlassShell.Watchdog` CLI: `--parent-pid <int> --clean-event <string>` and `--restore-taskbar`

- [ ] **Step 1: Write failing tests for `FullscreenDetector` and `TaskbarController` (covering Review Focus #1 & #5)**

Create `tests/GlassShell.Core.Tests/Shell/FullscreenDetectorTests.cs`:
```csharp
public sealed class FullscreenDetectorTests
{
    private readonly MonitorContext _monitor = new(1, new(0, 0, 2560, 1440), 1.5, true);

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    public void DesktopAndShellWindows_AreNeverFullscreen(string className)
    {
        var win = new ForegroundWindowInfo(100, className, "explorer", _monitor.Bounds, false, false);
        Assert.False(FullscreenDetector.IsFullscreen(win, _monitor, isD3DFullscreen: false));
    }

    [Fact]
    public void BorderlessWindowCoveringEntireMonitor_IsFullscreen()
    {
        var win = new ForegroundWindowInfo(200, "UnrealWindow", "Game", _monitor.Bounds, false, false);
        Assert.True(FullscreenDetector.IsFullscreen(win, _monitor, isD3DFullscreen: false));
    }

    [Fact]
    public void MaximizedWindowBelowTopBar_IsNotFullscreen()
    {
        var win = new ForegroundWindowInfo(201, "Chrome_WidgetWin_1", "chrome", new(0, 42, 2560, 1440), false, false);
        Assert.False(FullscreenDetector.IsFullscreen(win, _monitor, isD3DFullscreen: false));
    }
}
```

Create `tests/GlassShell.Core.Tests/Shell/TaskbarControllerTests.cs` using an in-memory `FakeTaskbarNative`:
```csharp
public sealed class TaskbarControllerTests
{
    [Fact]
    public void Acquire_HidesTaskbarAndRegisters28DipTopAppBar()
    {
        var fake = new FakeTaskbarNative(taskbarHwnd: 42);
        var ctrl = new TaskbarController(fake);
        ctrl.Acquire(appBarHwnd: 99, new(1, new(0, 0, 1920, 1080), 1.5, true), topBarDip: 28);
        Assert.False(fake.TaskbarVisible);
        Assert.Equal(42, fake.RegisteredHeightPx); // 28 * 1.5
    }

    [Fact]
    public void HandleTaskbarShownEvent_ReHidesWhenNotPeeking_KeepsVisibleWhenPeeking()
    {
        var fake = new FakeTaskbarNative(42);
        var ctrl = new TaskbarController(fake);
        ctrl.Acquire(99, new(1, new(0, 0, 1920, 1080), 1.0, true));

        fake.TaskbarVisible = true;
        ctrl.HandleTaskbarShownEvent();
        Assert.False(fake.TaskbarVisible);

        ctrl.BeginTrayPeek();
        Assert.True(fake.TaskbarVisible);
        ctrl.HandleTaskbarShownEvent();
        Assert.True(fake.TaskbarVisible);

        ctrl.EndTrayPeek();
        Assert.False(fake.TaskbarVisible);
    }

    [Fact]
    public void FullscreenEntry_CancelsTrayPeekAndHidesBars_ExitRestores()
    {
        var fake = new FakeTaskbarNative(42);
        var ctrl = new TaskbarController(fake);
        bool? lastBarsVisible = null;
        ctrl.BarsVisibilityChanged += v => lastBarsVisible = v;
        ctrl.Acquire(99, new(1, new(0, 0, 1920, 1080), 1.0, true));

        ctrl.BeginTrayPeek();
        ctrl.UpdateFullscreenState(true);
        Assert.False(ctrl.IsTrayPeekActive);
        Assert.False(fake.TaskbarVisible);
        Assert.False(lastBarsVisible);

        ctrl.UpdateFullscreenState(false);
        Assert.True(lastBarsVisible);
    }

    [Fact]
    public void Release_IsIdempotentAndRestoresTaskbarAndUnregistersAppBar()
    {
        var fake = new FakeTaskbarNative(42);
        var ctrl = new TaskbarController(fake);
        ctrl.Acquire(99, new(1, new(0, 0, 1920, 1080), 1.0, true));
        ctrl.Release();
        ctrl.Release();
        Assert.True(fake.TaskbarVisible);
        Assert.False(fake.AppBarRegistered);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/GlassShell.Core.Tests --filter "TaskbarControllerTests|FullscreenDetectorTests"`
Expected: FAIL with missing types.

- [ ] **Step 3: Implement `Shell` classes and `GlassShell.Watchdog`**

- `MonitorContext.cs`: `ScreenRect`, `MonitorContext`, static `GetPrimaryMonitor()` using `MonitorFromPoint` + `GetMonitorInfo` + `GetDpiForMonitor`.
- `ITaskbarNative.cs` & `Win32TaskbarNative.cs`: P/Invoke for `FindWindowW("Shell_TrayWnd", null)`, `ShowWindow(SW_HIDE / SW_SHOW)`, `SHAppBarMessage(ABM_NEW / ABM_QUERYPOS / ABM_SETPOS / ABM_REMOVE)`, `SHQueryUserNotificationState` (`QUNS_RUNNING_D3D_FULL_SCREEN`).
- `FullscreenDetector.cs`: Ignore `Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, minimized/cloaked windows; return `true` if `isD3DFullscreen` or window bounds cover `monitor.Bounds` within ±1px tolerance.
- `TaskbarController.cs`: Implement exact state machine with thread-safe lock, `Acquire`, `Release`, `BeginTrayPeek`, `EndTrayPeek`, `HandleExplorerRestart` (re-resolves `Shell_TrayWnd` and re-applies hide if acquired & not peeking), `HandleTaskbarShownEvent`, `UpdateFullscreenState`.
- `src/GlassShell.Watchdog/Program.cs`:
  - If `--restore-taskbar`: `FindWindowW("Shell_TrayWnd", null)` → `ShowWindow(SW_SHOW)` and exit 0.
  - If `--parent-pid <pid> --clean-event <name>`: open parent process handle (`SYNCHRONIZE`) and named manual-reset Event; wait for parent exit via `WaitForSingleObject(parentHandle, INFINITE)`; check if clean-exit event is signaled (`WaitForSingleObject(eventHandle, 0) == WAIT_OBJECT_0`); if NOT signaled, immediately call `ShowWindow(FindWindowW("Shell_TrayWnd", null), SW_SHOW)`.

- [ ] **Step 4: Run tests and verify pass**

Run: `dotnet test tests/GlassShell.Core.Tests --filter "TaskbarControllerTests|FullscreenDetectorTests"`
Expected: PASS (all tests pass).

- [ ] **Step 5: Commit**

```bash
git add src/GlassShell.Core/Shell/ src/GlassShell.Watchdog/ tests/GlassShell.Core.Tests/Shell/
git commit -m "feat(shell): add taskbar suppression, AppBar, fullscreen detector, and crash watchdog"
```

---

### Task 3: Window Tracking, High-Res Icon Extraction, Recycle Bin & Dock Model Coordinator

**Files:**
- Create: `src/GlassShell.Core/Discovery/WindowModels.cs`
- Create: `src/GlassShell.Core/Discovery/IWindowTracker.cs`
- Create: `src/GlassShell.Core/Discovery/WindowTracker.cs`
- Create: `src/GlassShell.Core/Discovery/IconExtractor.cs`
- Create: `src/GlassShell.Core/Discovery/RecycleBinService.cs`
- Create: `src/GlassShell.Core/Discovery/DockModelCoordinator.cs`
- Test: `tests/GlassShell.Core.Tests/Discovery/DockModelCoordinatorTests.cs`

**Interfaces:**
- Consumes: `GlassSettings` (Task 1), `ForegroundWindowInfo` (Task 2)
- Produces:
  - `enum DockSection { PinnedApps, RunningOrRecent, FoldersAndTrash }`
  - `enum DockItemKind { Explorer, Launchpad, Application, Folder, RecycleBin }`
  - `sealed record WindowInfo(nint Hwnd, uint ProcessId, string ExecutablePath, string AppDisplayName, string Title, string ClassName, bool IsMinimized, bool IsFlashing)`
  - `sealed record DockItemModel(string Id, string Title, string? TargetPath, DockItemKind Kind, DockSection Section, bool IsPinned, bool IsRunning, IReadOnlyList<nint> OpenWindowHandles, bool IsFlashing)`
  - `sealed record RecycleBinStatus(bool IsEmpty, long ItemCount, long TotalBytes)`
  - `interface IWindowTracker`: `IReadOnlyList<WindowInfo> GetTopLevelWindows()`, `event Action? WindowsChanged`, `event Action<ForegroundWindowInfo>? ForegroundChanged`, `void Start()`, `void Stop()`
  - `sealed class IconExtractor`: `byte[]? ExtractHighResPng(string filePath, int targetPx = 128)` (uses COM `IShellItemImageFactory` with `SIIGBF_BIGGERSIZEOK`, falls back to `SHGetFileInfo` / `ExtractIconEx`)
  - `sealed class RecycleBinService`: `RecycleBinStatus Query()`, `bool Empty(nint ownerHwnd)`, `void Open()`
  - `static class DockModelCoordinator`: `static IReadOnlyList<DockItemModel> BuildDockItems(GlassSettings settings, IReadOnlyList<WindowInfo> runningWindows, IReadOnlyList<string> recentAppPaths, RecycleBinStatus recycleBin)`

- [ ] **Step 1: Write failing tests for `DockModelCoordinator`**

Create `tests/GlassShell.Core.Tests/Discovery/DockModelCoordinatorTests.cs`:
```csharp
public sealed class DockModelCoordinatorTests
{
    [Fact]
    public void BuildDockItems_OrdersThreeSections_ExplorerAndLaunchpadLeft_RecycleBinRight()
    {
        var settings = GlassSettings.Default with { PinnedAppPaths = [@"C:\App\Code.exe"], PinnedFolderPaths = [@"C:\Users\Me\Downloads"] };
        var running = new[]
        {
            new WindowInfo(10, 100, @"C:\App\Code.exe", "VS Code", "main.cs", "Chrome_WidgetWin_1", false, false),
            new WindowInfo(20, 200, @"C:\App\Chrome.exe", "Chrome", "Tab", "Chrome_WidgetWin_1", false, false)
        };
        var items = DockModelCoordinator.BuildDockItems(settings, running, [], new RecycleBinStatus(false, 3, 1024));

        Assert.Equal(DockItemKind.Explorer, items[0].Kind);
        Assert.Equal(DockItemKind.Launchpad, items[1].Kind);
        Assert.Equal(DockItemKind.RecycleBin, items[^1].Kind);

        var code = items.Single(i => i.TargetPath == @"C:\App\Code.exe");
        Assert.Equal(DockSection.PinnedApps, code.Section);
        Assert.True(code.IsRunning);
        Assert.Single(code.OpenWindowHandles);

        var chrome = items.Single(i => i.TargetPath == @"C:\App\Chrome.exe");
        Assert.Equal(DockSection.RunningOrRecent, chrome.Section);
        Assert.False(chrome.IsPinned);
    }

    [Fact]
    public void BuildDockItems_LimitsRecentUnpinnedAppsToThree_WhenShowRecentAppsTrue()
    {
        var settings = GlassSettings.Default with { ShowRecentApps = true };
        var recents = new[] { @"C:\a.exe", @"C:\b.exe", @"C:\c.exe", @"C:\d.exe" };
        var items = DockModelCoordinator.BuildDockItems(settings, [], recents, new RecycleBinStatus(true, 0, 0));
        var middle = items.Where(i => i.Section == DockSection.RunningOrRecent).ToList();
        Assert.Equal(3, middle.Count);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/GlassShell.Core.Tests --filter DockModelCoordinatorTests`
Expected: FAIL with missing types.

- [ ] **Step 3: Implement `Discovery` classes**

- `WindowTracker.cs`: Enumerate visible top-level non-tool windows (`WS_VISIBLE`, not `WS_EX_TOOLWINDOW`, no owner unless `WS_EX_APPWINDOW`, `DwmGetWindowAttribute(DWMWA_CLOAKED) == 0`, exclude own `Environment.ProcessId`); subscribe to `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_CREATE`, `EVENT_OBJECT_DESTROY`, `EVENT_OBJECT_NAMECHANGE`, `EVENT_SYSTEM_MINIMIZESTART`, `EVENT_SYSTEM_MINIMIZEEND`).
- `IconExtractor.cs`: Extract 128–256px PNG bytes via `SHCreateItemFromParsingName` → `IShellItemImageFactory.GetImage`, cached in memory by normalized path.
- `RecycleBinService.cs`: `SHQueryRecycleBinW`, `SHEmptyRecycleBinW`, `Process.Start("explorer.exe", "shell:RecycleBinFolder")`.
- `DockModelCoordinator.cs`: Pure function assembling the 3 sections (`此电脑`, `启动台`, pinned apps | running unpinned + up to 3 non-duplicate recent apps when `ShowRecentApps` | pinned folders + `回收站`).

- [ ] **Step 4: Run tests and verify pass**

Run: `dotnet test tests/GlassShell.Core.Tests --filter DockModelCoordinatorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GlassShell.Core/Discovery/ tests/GlassShell.Core.Tests/Discovery/
git commit -m "feat(discovery): add window tracker, icon extractor, recycle bin, and 3-section Dock coordinator"
```

---

### Task 4: Absolute-Silent 3-Tier Foreground Application Menu Engine

**Files:**
- Create: `src/GlassShell.Core/MenuEngine/MenuModels.cs`
- Create: `src/GlassShell.Core/MenuEngine/IMenuExtractor.cs`
- Create: `src/GlassShell.Core/MenuEngine/Win32GetMenuExtractor.cs`
- Create: `src/GlassShell.Core/MenuEngine/StaticUiaMenuExtractor.cs`
- Create: `src/GlassShell.Core/MenuEngine/JsonProfileMenuProvider.cs`
- Create: `src/GlassShell.Core/MenuEngine/MenuCommandDispatcher.cs`
- Create: `src/GlassShell.Core/MenuEngine/ForegroundMenuCoordinator.cs`
- Create: `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/_generic.json`
- Create: `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/chrome.json`
- Create: `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/msedge.json`
- Create: `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/code.json`
- Create: `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/explorer.json`
- Create: `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/notepad.json`
- Create: `src/GlassShell.Core/MenuEngine/Assets/MenuProfiles/windowsterminal.json`
- Test: `tests/GlassShell.Core.Tests/MenuEngine/ForegroundMenuCoordinatorTests.cs`

**Interfaces:**
- Produces:
  - `enum MenuSource { Win32GetMenu, StaticUia, AppProfile, GenericFallback }`
  - `enum MenuActionKind { Win32Command, UiaInvoke, KeyboardShortcut, WindowCommand }`
  - `sealed record MenuInvokeAction(MenuActionKind Kind, uint CommandId = 0, string? Shortcut = null, string? WindowOp = null, object? UiaElementRef = null)`
  - `sealed record MenuNode(string Id, string Title, string? ShortcutText, bool IsEnabled, bool IsChecked, bool IsSeparator, MenuInvokeAction? Action, IReadOnlyList<MenuNode> Children)`
  - `sealed record MenuBarSnapshot(nint TargetHwnd, string ProcessName, string AppDisplayName, IReadOnlyList<MenuNode> TopLevelMenus, MenuSource Source)`
  - `interface IMenuExtractor`: `MenuSource Source { get; }`, `Task<IReadOnlyList<MenuNode>?> TryExtractAsync(nint hwnd, string processName, CancellationToken ct)`
  - `sealed class ForegroundMenuCoordinator(IEnumerable<IMenuExtractor> extractors, JsonProfileMenuProvider profileProvider, uint selfProcessId, TimeSpan? probeTimeout = null)`: `MenuBarSnapshot CurrentSnapshot { get; }`, `event Action<MenuBarSnapshot>? SnapshotChanged`, `Task OnForegroundChangedAsync(nint hwnd, uint processId, string processName, string appDisplayName)`
  - `sealed class MenuCommandDispatcher`: `bool Dispatch(nint targetHwnd, MenuInvokeAction action)`

- [ ] **Step 1: Write failing tests covering silent 3-tier fallback, 200ms timeout, UIPI error, rapid switch cancellation, and self-HWND ignore (Review Focus #2 & #4)**

Create `tests/GlassShell.Core.Tests/MenuEngine/ForegroundMenuCoordinatorTests.cs`:
```csharp
public sealed class ForegroundMenuCoordinatorTests
{
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
    public async Task OnForegroundChanged_WhenTier1StallsPast200Ms_TimesOutAndFallsBackToProfile()
    {
        var slowExtractor = new StubMenuExtractor(MenuSource.Win32GetMenu, async ct =>
        {
            await Task.Delay(2000, ct);
            return [new MenuNode("x", "Slow", null, true, false, false, null, [])];
        });
        var coord = new ForegroundMenuCoordinator([slowExtractor], JsonProfileMenuProvider.CreateBuiltIn(), selfProcessId: 999, TimeSpan.FromMilliseconds(150));

        await coord.OnForegroundChangedAsync(100, 10, "chrome", "Google Chrome");

        Assert.Equal(MenuSource.AppProfile, coord.CurrentSnapshot.Source);
        Assert.Contains(coord.CurrentSnapshot.TopLevelMenus, m => m.Title == "文件");
    }

    [Fact]
    public async Task OnForegroundChanged_WhenExtractorThrowsUipiUnauthorized_DegradesGracefully()
    {
        var uipiExtractor = new StubMenuExtractor(MenuSource.StaticUia, _ => throw new UnauthorizedAccessException("UIPI"));
        var coord = new ForegroundMenuCoordinator([uipiExtractor], JsonProfileMenuProvider.CreateBuiltIn(), selfProcessId: 999);

        await coord.OnForegroundChangedAsync(300, 50, "Taskmgr", "任务管理器");

        Assert.Equal(MenuSource.GenericFallback, coord.CurrentSnapshot.Source);
        Assert.NotEmpty(coord.CurrentSnapshot.TopLevelMenus);
    }

    [Fact]
    public async Task OnForegroundChanged_RapidSwitchCancelsEarlierProbe()
    {
        bool firstCancelled = false;
        var extractor = new StubMenuExtractor(MenuSource.StaticUia, async ct =>
        {
            try { await Task.Delay(120, ct); }
            catch (OperationCanceledException) { firstCancelled = true; throw; }
            return null;
        });
        var coord = new ForegroundMenuCoordinator([extractor], JsonProfileMenuProvider.CreateBuiltIn(), selfProcessId: 999);

        var t1 = coord.OnForegroundChangedAsync(100, 10, "unknown1", "App1");
        await Task.Delay(15);
        var t2 = coord.OnForegroundChangedAsync(200, 20, "code", "Visual Studio Code");
        await Task.WhenAll(t1, t2);

        Assert.True(firstCancelled);
        Assert.Equal((nint)200, coord.CurrentSnapshot.TargetHwnd);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/GlassShell.Core.Tests --filter ForegroundMenuCoordinatorTests`
Expected: FAIL with missing types.

- [ ] **Step 3: Implement silent 3-tier `MenuEngine` and JSON profiles**

- `Win32GetMenuExtractor.cs`: Silent recursive `GetMenu` / `GetMenuItemCount` / `GetMenuItemInfoW` / `GetSubMenu` (strips `&` mnemonics, splits `\t` into `Title` and `ShortcutText`, preserves `MF_GRAYED` & `MF_CHECKED`).
- `StaticUiaMenuExtractor.cs`: Dedicated single MTA worker / `Task.Run`, finds `ControlType.MenuBar` via COM `IUIAutomation` (using `TreeScope_Children` / max depth 2), reads **ONLY already-populated static children** (NEVER calls `Expand()`). Discards result if top-level items have 0 children so Tier 3 handles lazy apps cleanly.
- `JsonProfileMenuProvider.cs`: Loads embedded JSON profiles from `Assets/MenuProfiles/` (`_generic`, `chrome`, `msedge`, `code`, `explorer`, `notepad`, `windowsterminal`) plus optional user override folder `%LocalAppData%\GlassShell\MenuProfiles\`. Mark `.json` files as `<EmbeddedResource>` in `GlassShell.Core.csproj`.
- `MenuCommandDispatcher.cs`: Restores `SetForegroundWindow(targetHwnd)` if needed, then dispatches `Win32Command` (`PostMessage(WM_COMMAND)`), `UiaInvoke` (`InvokePattern.Invoke()`), `KeyboardShortcut` (`SendInput` key chord), or `WindowCommand` (`Minimize`/`Maximize`/`Close`/`SnapLeft`/`SnapRight`/`ToggleTopMost`). Swallows UIPI `E_ACCESSDENIED` cleanly returning `false`.
- `ForegroundMenuCoordinator.cs`: Enforces `200ms` per-probe `CancellationTokenSource.CreateLinkedTokenSource`, cancels previous focus task on every switch, ignores `processId == selfProcessId`, caches negative UIA probes per `(ProcessName, WindowClass)`.

- [ ] **Step 4: Run tests and verify pass**

Run: `dotnet test tests/GlassShell.Core.Tests --filter ForegroundMenuCoordinatorTests`
Expected: PASS (4/4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/GlassShell.Core/MenuEngine/ src/GlassShell.Core/GlassShell.Core.csproj tests/GlassShell.Core.Tests/MenuEngine/
git commit -m "feat(menu-engine): add silent 3-tier foreground app menu extraction and JSON profiles"
```

---

### Task 5: DXGI Local Desktop Sampler & D3D11 HLSL Liquid Glass Engine

**Files:**
- Create: `src/GlassShell.Core/Glass/GlassRenderParams.cs`
- Create: `src/GlassShell.Core/Glass/IBackdropSampler.cs`
- Create: `src/GlassShell.Core/Glass/DxgiSamplerStateMachine.cs`
- Create: `src/GlassShell.UI/Glass/WindowInteropHelperEx.cs`
- Create: `src/GlassShell.UI/Glass/DxgiBackdropSampler.cs`
- Create: `src/GlassShell.UI/Glass/Shaders/LiquidGlass.hlsl`
- Create: `src/GlassShell.UI/Glass/LiquidGlassRenderer.cs`
- Create: `src/GlassShell.UI/Glass/LiquidGlassSurface.cs`
- Test: `tests/GlassShell.Core.Tests/Glass/DxgiSamplerStateMachineTests.cs`

**Interfaces:**
- Consumes: `GlassOpacityPreset` (Task 1), `ScreenRect` (Task 2)
- Produces:
  - `readonly record struct GlassRenderParams(float CornerRadiusPx, float RefractionBandPx, float RefractionStrength, float BlurRadius, float TintAlpha, float SpecularStrength, bool IsDarkMode)` + `static GlassRenderParams FromPreset(GlassOpacityPreset preset, bool isDarkMode, float cornerRadiusPx)`
  - `enum SamplerHealth { Ready, FallbackDegraded }`
  - `sealed class DxgiSamplerStateMachine`: tracks `DXGI_ERROR_ACCESS_LOST` / `DXGI_ERROR_DEVICE_REMOVED` transitions with exponential retry backoff (`250ms` → `500ms` → `1000ms` cap) and reports `SamplerHealth`
  - `static class WindowInteropHelperEx`: `void ApplyShellOverlayStyles(nint hwnd)` (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` + `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`)
  - `sealed class LiquidGlassSurface : FrameworkElement` (WPF control with DependencyProperties `CornerRadius`, `OpacityPreset`, `IsDarkMode`, `GlassBounds` rendering D3D11 HLSL Squircle optical refraction + specular rim + outer shadow via `D3DImage`, gracefully degrading to translucent WPF Squircle tint when `SamplerHealth == FallbackDegraded`)

- [ ] **Step 1: Write failing tests for `GlassRenderParams` and `DxgiSamplerStateMachine` (Review Focus #3)**

Create `tests/GlassShell.Core.Tests/Glass/DxgiSamplerStateMachineTests.cs`:
```csharp
public sealed class DxgiSamplerStateMachineTests
{
    [Fact]
    public void AccessLost_SwitchesToFallbackDegraded_AndRespectsBackoffBeforeRetry()
    {
        var now = DateTimeOffset.UtcNow;
        var sm = new DxgiSamplerStateMachine(() => now);
        sm.MarkAcquired();
        Assert.Equal(SamplerHealth.Ready, sm.Health);

        sm.ReportAccessLost();
        Assert.Equal(SamplerHealth.FallbackDegraded, sm.Health);
        Assert.False(sm.ShouldRetryAcquire());

        now = now.AddMilliseconds(260);
        Assert.True(sm.ShouldRetryAcquire());

        sm.ReportAccessLost(); // second failure -> 500ms backoff
        now = now.AddMilliseconds(300);
        Assert.False(sm.ShouldRetryAcquire());
        now = now.AddMilliseconds(250);
        Assert.True(sm.ShouldRetryAcquire());
    }

    [Fact]
    public void FromPreset_ProducesIncreasingTintAlphaFromClearToFrosted()
    {
        var clear = GlassRenderParams.FromPreset(GlassOpacityPreset.Clear, false, 20f);
        var balanced = GlassRenderParams.FromPreset(GlassOpacityPreset.Balanced, false, 20f);
        var frosted = GlassRenderParams.FromPreset(GlassOpacityPreset.Frosted, false, 20f);
        Assert.True(clear.TintAlpha < balanced.TintAlpha && balanced.TintAlpha < frosted.TintAlpha);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/GlassShell.Core.Tests --filter DxgiSamplerStateMachineTests`
Expected: FAIL with missing types.

- [ ] **Step 3: Implement DXGI sampler state machine, HLSL Liquid Glass shader, and WPF `LiquidGlassSurface`**

- `GlassRenderParams.cs` & `DxgiSamplerStateMachine.cs` in `GlassShell.Core/Glass/`.
- `WindowInteropHelperEx.cs`: P/Invoke `SetWindowLongPtr` (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`) and `SetWindowDisplayAffinity(hwnd, 0x00000011 /* WDA_EXCLUDEFROMCAPTURE */)`.
- `DxgiBackdropSampler.cs`: Uses `Vortice.DXGI` `IDXGIOutput1.DuplicateOutput` on primary output, `AcquireNextFrame(0)` non-blocking dirty-rect copy of only the requested `ScreenRect` sub-regions into a shared `ID3D11Texture2D`, driving `DxgiSamplerStateMachine` on `DXGI_ERROR_ACCESS_LOST` / `DXGI_ERROR_DEVICE_REMOVED`.
- `Shaders/LiquidGlass.hlsl`: Multi-tap Dual-Kawase blur pass + SDF continuous-curvature Squircle mask + edge-normal UV optical refraction offset along `RefractionBandPx` + top-left directional specular rim highlight + soft ambient outer shadow + dark/light adaptive tint.
- `LiquidGlassRenderer.cs` & `LiquidGlassSurface.cs`: Runs D3D11 passes into a `D3D9Ex` shared surface bound to WPF `D3DImage` (`AllowsTransparency=True` compatible, zero pixel-copy CPU roundtrip), falls back cleanly to crisp WPF translucent Squircle brush if DXGI is degraded.

- [ ] **Step 4: Run build and tests to verify pass**

Run: `dotnet build GlassShell.sln && dotnet test tests/GlassShell.Core.Tests --filter DxgiSamplerStateMachineTests`
Expected: Build succeeds (0 errors) and tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GlassShell.Core/Glass/ src/GlassShell.UI/Glass/ tests/GlassShell.Core.Tests/Glass/
git commit -m "feat(glass): add DXGI local desktop sampler and D3D11 HLSL Liquid Glass engine"
```

---

### Task 6: Top Menu Bar Window, Cascading Liquid Glass Menus & Mini Calendar Card

**Files:**
- Create: `src/GlassShell.Core/SystemStatus/SystemStatusModels.cs`
- Create: `src/GlassShell.Core/SystemStatus/SystemStatusProvider.cs`
- Create: `src/GlassShell.UI/Themes/LiquidGlassStyles.xaml`
- Create: `src/GlassShell.UI/MenuBar/MenuBarViewModel.cs`
- Create: `src/GlassShell.UI/MenuBar/MenuBarWindow.xaml`
- Create: `src/GlassShell.UI/MenuBar/MenuBarWindow.xaml.cs`
- Create: `src/GlassShell.UI/MenuBar/LiquidGlassMenuPopup.xaml`
- Create: `src/GlassShell.UI/MenuBar/LiquidGlassMenuPopup.xaml.cs`
- Create: `src/GlassShell.UI/MenuBar/CalendarPopup.xaml`
- Create: `src/GlassShell.UI/MenuBar/CalendarPopup.xaml.cs`
- Create: `src/GlassShell.UI/MenuBar/AboutPcDialog.xaml`
- Create: `src/GlassShell.UI/MenuBar/AboutPcDialog.xaml.cs`
- Create: `src/GlassShell.UI/MenuBar/ForceQuitWindow.xaml`
- Create: `src/GlassShell.UI/MenuBar/ForceQuitWindow.xaml.cs`
- Test: `tests/GlassShell.Core.Tests/SystemStatus/SystemStatusProviderTests.cs`

**Interfaces:**
- Consumes: `GlassSettings` (Task 1), `TaskbarController` (Task 2), `ForegroundMenuCoordinator` & `MenuCommandDispatcher` (Task 4), `LiquidGlassSurface` & `WindowInteropHelperEx` (Task 5)
- Produces:
  - `sealed record SystemStatusSnapshot(string InputMethodLabel, bool HasBattery, int BatteryPercent, bool IsCharging, bool IsNetworkConnected, bool IsWifi, string FormattedClock)`
  - `static class ClockFormatter`: `string FormatMacZhCn(DateTime localTime)` → e.g. `"11月23日 周一 10:03"`
  - `sealed class MenuBarWindow : Window` exposing `event Action? SpotlightRequested`, `event Action? ControlCenterRequested`, `event Action? SettingsRequested`, `event Action? RestoreNativeTaskbarRequested`

- [ ] **Step 1: Write failing tests for Chinese macOS clock formatting and desktop battery hiding**

Create `tests/GlassShell.Core.Tests/SystemStatus/SystemStatusProviderTests.cs`:
```csharp
public sealed class SystemStatusProviderTests
{
    [Fact]
    public void FormatMacZhCn_MatchesSpecFormat()
    {
        var dt = new DateTime(2026, 11, 23, 10, 3, 0); // Monday
        Assert.Equal("11月23日 周一 10:03", ClockFormatter.FormatMacZhCn(dt));
    }

    [Fact]
    public void BatteryStatus_NoSystemBatteryFlag_SetsHasBatteryFalse()
    {
        var status = SystemStatusProvider.ParseBatteryStatus(batteryFlag: 128, lifePercent: 255, acLineStatus: 1);
        Assert.False(status.HasBattery);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/GlassShell.Core.Tests --filter SystemStatusProviderTests`
Expected: FAIL with missing types.

- [ ] **Step 3: Implement `SystemStatusProvider` and Top Menu Bar WPF UI**

- `SystemStatusProvider.cs`: `GetSystemPowerStatus` (hides battery when `BatteryFlag == 128`), `GetKeyboardLayout` on foreground window thread (`"拼"` for `0x0804`, `"英"` for `0x0409`), `NetworkInterface.GetIsNetworkAvailable()`, and `ClockFormatter.FormatMacZhCn`.
- `MenuBarWindow.xaml(.cs)`: Fixed at `(monitor.Bounds.Left, monitor.Bounds.Top)`, full primary monitor width, height `28 DIP`, `Topmost=True`, calls `WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd)` on `SourceInitialized`. Default transparent background; displays `LiquidGlassSurface` when `ShowMenuBarBackground == true`.
  - Left: Win11 SVG vector logo button → opens `LiquidGlassMenuPopup` with `关于本机`, `系统设置…`, `Microsoft Store`, separator, `最近使用的项目`, separator, `强制退出…`, separator, `睡眠`, `重新启动…`, `关机…`, separator, `锁定屏幕`, `注销…`; Bold active app name button (`关于/隐藏/退出/强制退出`); dynamic `ItemsControl` of `TopLevelMenus` from `ForegroundMenuCoordinator`.
  - Right: Tray peek chevron (`TaskbarController.BeginTrayPeek()`), IME badge, Battery (collapsed if `!HasBattery`), Network icon, Spotlight icon, Control Center icon, Clock button (`CalendarPopup` with mini month grid + `"打开通知中心"` button sending `Win+N`).
- `LiquidGlassMenuPopup.xaml(.cs)`: `Popup` with `AllowsTransparency=True`, `LiquidGlassSurface` backdrop, cascading submenu hover expansion, horizontal hover-switch across adjacent top-level menu buttons while any menu is open, macOS system-blue `#0A84FF` rounded pill highlight, disabled items at 40% opacity, right-aligned shortcut hints, invokes `MenuCommandDispatcher.Dispatch(snapshot.TargetHwnd, node.Action)` without stealing target focus.
- `AboutPcDialog.xaml(.cs)` & `ForceQuitWindow.xaml(.cs)`: Clean macOS-styled dialogs for "关于本机" (OS build, CPU, RAM) and "强制退出应用程序" (lists running windowed apps with terminate button).

- [ ] **Step 4: Run build and tests to verify pass**

Run: `dotnet build GlassShell.sln && dotnet test tests/GlassShell.Core.Tests --filter SystemStatusProviderTests`
Expected: Build succeeds and tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GlassShell.Core/SystemStatus/ src/GlassShell.UI/Themes/ src/GlassShell.UI/MenuBar/ tests/GlassShell.Core.Tests/SystemStatus/
git commit -m "feat(menubar): add Liquid Glass top menu bar, cascading menus, and mini calendar card"
```

---

### Task 7: Bottom Dock Physics Engine, Liquid Glass Dock Window, Tooltips & Drag-and-Drop

**Files:**
- Create: `src/GlassShell.Core/DockPhysics/DockMagnificationCalculator.cs`
- Create: `src/GlassShell.Core/DockPhysics/SpringBounceAnimator.cs`
- Create: `src/GlassShell.Core/DockPhysics/DockAutoHideController.cs`
- Create: `src/GlassShell.UI/Dock/DockViewModel.cs`
- Create: `src/GlassShell.UI/Dock/DockWindow.xaml`
- Create: `src/GlassShell.UI/Dock/DockWindow.xaml.cs`
- Create: `src/GlassShell.UI/Dock/DockTooltipPopup.xaml`
- Create: `src/GlassShell.UI/Dock/DockTooltipPopup.xaml.cs`
- Create: `src/GlassShell.UI/Dock/DockContextMenu.cs`
- Test: `tests/GlassShell.Core.Tests/DockPhysics/DockPhysicsTests.cs`

**Interfaces:**
- Consumes: `GlassSettings` (Task 1), `MonitorContext` (Task 2), `DockItemModel`, `DockModelCoordinator`, `IconExtractor`, `RecycleBinService` (Task 3), `LiquidGlassSurface`, `WindowInteropHelperEx` (Task 5)
- Produces:
  - `readonly record struct DockIconLayout(double CenterX, double RenderedSize, double LiftY)`
  - `static class DockMagnificationCalculator`: `IReadOnlyList<DockIconLayout> Compute(int itemCount, double baseSize, double gap, double? pointerX, bool magnificationEnabled, double maxScale, double sigmaFactor = 1.65)`
  - `sealed class SpringBounceAnimator`: `void TriggerBounce(string itemId, int hops = 2)`, `double SampleOffsetY(string itemId, double nowSeconds)`, `bool HasActiveAnimations`
  - `sealed class DockAutoHideController`: `bool ShouldHide(DockAutoHideMode mode, bool hasBottomOverlappingWindow, bool isPointerAtBottomEdge, double bottomEdgeDwellMs, bool isMenuOrDragActive)` (uses `450ms` macOS trigger delay when hiding)
  - `sealed class DockWindow : Window` exposing `event Action? LaunchpadRequested`, `event Action<GlassSettings>? SettingsChanged`

- [ ] **Step 1: Write failing tests for `DockMagnificationCalculator`, `SpringBounceAnimator`, and `DockAutoHideController`**

Create `tests/GlassShell.Core.Tests/DockPhysics/DockPhysicsTests.cs`:
```csharp
public sealed class DockPhysicsTests
{
    [Fact]
    public void Magnification_PeaksAtPointerAndDecaysSymmetrically()
    {
        var baseLayout = DockMagnificationCalculator.Compute(5, baseSize: 52, gap: 8, pointerX: null, true, 1.6);
        double centerOfMiddle = baseLayout[2].CenterX;

        var hovered = DockMagnificationCalculator.Compute(5, baseSize: 52, gap: 8, pointerX: centerOfMiddle, true, 1.6);

        Assert.Equal(52 * 1.6, hovered[2].RenderedSize, precision: 2);
        Assert.Equal(hovered[1].RenderedSize, hovered[3].RenderedSize, precision: 2);
        Assert.True(hovered[2].RenderedSize > hovered[1].RenderedSize);
        Assert.True(hovered[1].RenderedSize > hovered[0].RenderedSize);
    }

    [Fact]
    public void SpringBounce_LiftsAboveZeroAndSettlesBackToZero()
    {
        var anim = new SpringBounceAnimator();
        anim.TriggerBounce("app1", hops: 1);
        double mid = anim.SampleOffsetY("app1", 0.15);
        double settled = anim.SampleOffsetY("app1", 2.0);
        Assert.True(mid > 5.0);
        Assert.Equal(0.0, settled, precision: 3);
    }

    [Fact]
    public void AutoHide_SmartModeHidesOnOverlap_RevealsAfter450MsBottomDwell()
    {
        var ctrl = new DockAutoHideController();
        Assert.True(ctrl.ShouldHide(DockAutoHideMode.Smart, hasBottomOverlappingWindow: true, isPointerAtBottomEdge: false, bottomEdgeDwellMs: 0, isMenuOrDragActive: false));
        Assert.True(ctrl.ShouldHide(DockAutoHideMode.Smart, hasBottomOverlappingWindow: true, isPointerAtBottomEdge: true, bottomEdgeDwellMs: 200, isMenuOrDragActive: false));
        Assert.False(ctrl.ShouldHide(DockAutoHideMode.Smart, hasBottomOverlappingWindow: true, isPointerAtBottomEdge: true, bottomEdgeDwellMs: 500, isMenuOrDragActive: false));
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/GlassShell.Core.Tests --filter DockPhysicsTests`
Expected: FAIL with missing types.

- [ ] **Step 3: Implement Dock physics engine and `DockWindow`**

- `DockMagnificationCalculator.cs`: Computes Gaussian scale factor `1 + (maxScale - 1) * Math.Exp(-(dx * dx) / (2 * sigma * sigma))` with `sigma = baseSize * sigmaFactor`, preserves non-overlapping layout spacing, and sets `LiftY = (renderedSize - baseSize) * 0.5` so icons grow upward out of the capsule.
- `SpringBounceAnimator.cs`: Gravity-parabola multi-hop bounce (`abs(sin)` damped decay per hop) for launch bounce & window-flash attention bounce.
- `DockAutoHideController.cs`: Evaluates `Off` / `Always` / `Smart` with `450ms` bottom-edge dwell anti-accidental-trigger delay and keeps Dock pinned open during context menus or drag-and-drop.
- `DockWindow.xaml(.cs)`:
  - Transparent overlay centered at bottom of primary monitor (`6 DIP` bottom margin, `140 DIP` total window height so magnified icons + floating tooltips protrude freely above the `~68 DIP` `LiquidGlassSurface` Squircle capsule).
  - Applies `WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd)`.
  - Hooks `CompositionTarget.Rendering` (display VSync 60/120Hz) to smoothly lerp pointer magnification, capsule width, bounce offset, and auto-hide slide transform.
  - Renders 3 sections separated by 1px translucent vertical lines, 4px white/accent glowing running indicator dot beneath active/running icons, `DockTooltipPopup` with downward caret above hovered icon.
  - Click behavior: `Explorer` opens `shell:MyComputerFolder`; `Launchpad` raises `LaunchpadRequested`; `RecycleBin` opens Recycle Bin; `Application` restores/focuses existing window or launches `.exe` with bounce animation.
  - Right-click `DockContextMenu` (using `LiquidGlassMenuPopup`): lists open windows to activate, `新建窗口`, `在程序坞中保留` / `从程序坞中移除`, `在资源管理器中显示`, `退出`, `强制退出`; for Recycle Bin: `打开回收站`, `清倒回收站`.
  - Drag-and-drop (`AllowDrop=True`): reorder pinned items; drop `.lnk`/`.exe` to pin and persist via `JsonSettingsStore`; drag pinned icon >80px above Dock and release to unpin with fade/scale puff animation; drop external data files onto an app icon to launch `Process.Start(appPath, droppedFiles)`.

- [ ] **Step 4: Run build and tests to verify pass**

Run: `dotnet build GlassShell.sln && dotnet test tests/GlassShell.Core.Tests --filter DockPhysicsTests`
Expected: Build succeeds and tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GlassShell.Core/DockPhysics/ src/GlassShell.UI/Dock/ tests/GlassShell.Core.Tests/DockPhysics/
git commit -m "feat(dock): add Gaussian magnification physics, Liquid Glass Dock, tooltips, and drag-and-drop"
```

---

### Task 8: Application Bootstrap, Watchdog Integration & Phase 1 End-to-End Verification

**Files:**
- Modify: `src/GlassShell.UI/App.xaml`
- Modify: `src/GlassShell.UI/App.xaml.cs`
- Create: `src/GlassShell.UI/Settings/QuickSettingsWindow.xaml`
- Create: `src/GlassShell.UI/Settings/QuickSettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: All Phase 1 components from Tasks 1–7 (`JsonSettingsStore`, `TaskbarController`, `WindowTracker`, `DockModelCoordinator`, `ForegroundMenuCoordinator`, `DxgiBackdropSampler`, `MenuBarWindow`, `DockWindow`, `GlassShell.Watchdog`)

- [ ] **Step 1: Wire `App.xaml.cs` lifecycle, single-instance guard, Watchdog launch & safe cleanup**

- Handle `--restore-taskbar` argument immediately (call `Win32TaskbarNative` restore & exit 0).
- Enforce single-instance `Mutex("Local\\GlassShell_SingleInstance")`.
- Create named clean-exit `EventWaitHandle("Global\\GlassShell_CleanExit_" + pid)` and spawn `GlassShell.Watchdog.exe --parent-pid <pid> --clean-event <name>`.
- Load `GlassSettings` via `JsonSettingsStore`, create `MonitorContext.GetPrimaryMonitor()`, initialize `TaskbarController`, `WindowTracker`, `ForegroundMenuCoordinator`, `DxgiBackdropSampler`, `MenuBarWindow`, and `DockWindow`.
- Wire `SystemEvents.SessionEnding`, `AppDomain.CurrentDomain.UnhandledException`, `DispatcherUnhandledException`, and `App.OnExit` to cleanly call `TaskbarController.Release()`, signal the clean-exit event, dispose DXGI/hooks, and exit.
- Wire `QuickSettingsWindow` (macOS-styled minimal Phase 1 settings for Theme, GlassOpacityPreset, ShowMenuBarBackground, DockIconSize, Magnification, AutoHide, and "恢复 Windows 原生任务栏并退出" button) so every Phase 1 setting is immediately usable.
- For Phase 2 buttons (Spotlight, Control Center, Launchpad), show a crisp non-intrusive Liquid Glass toast `"将在阶段二启用"` until Phase 2 lands.

- [ ] **Step 2: Run full solution build and complete unit test suite**

Run: `dotnet build GlassShell.sln -c Debug && dotnet test GlassShell.sln --verbosity normal`
Expected: `Build succeeded. 0 Error(s)` and ALL unit tests PASS.

- [ ] **Step 3: Live smoke test & Phase 1 verification checklist**

Verify on Windows 11 (outside sandbox if GUI display required):
1. Native taskbar hides cleanly; maximizing Explorer stops cleanly below the `28 DIP` top bar.
2. Top Menu Bar is transparent over wallpaper; switching between Explorer, Notepad, Chrome, and VS Code updates active app & top menus silently (zero native menu flashing inside target windows); clicking a menu command executes on the target window.
3. Bottom Dock renders Liquid Glass Squircle capsule with edge refraction; hovering magnifies icons smoothly above the capsule; launching an app bounces; clicking top-right tray chevron reveals bottom native taskbar and tucks Dock away, then restores on click-away.
4. Force-killing `GlassShell.UI.exe` triggers `GlassShell.Watchdog.exe` to restore the Windows native taskbar within 1 second.

- [ ] **Step 4: Commit**

```bash
git add src/GlassShell.UI/App.xaml src/GlassShell.UI/App.xaml.cs src/GlassShell.UI/Settings/
git commit -m "feat(app): wire Phase 1 bootstrap, crash watchdog, and minimal quick settings"
```

---

## Next Plans (After Phase 1 Is Verified Running)
- **Phase 2 Plan**: `Win` key single-press hook + Launchpad (fullscreen blurred grid & folders) + Spotlight Search (apps/pinyin/calc/commands/files) + Control Center (Wi-Fi/Bluetooth/Volume/Brightness/WinRT Media/`Win+V`) + Dock Stacks & Pre-Minimize Snapshots.
- **Phase 3 Plan**: Wallpaper luminance adaptive top-bar ink + Icon Squircle harmonizer + Full Settings UI + Stability stress-testing + Portable/Installer packaging & GitHub release docs.
