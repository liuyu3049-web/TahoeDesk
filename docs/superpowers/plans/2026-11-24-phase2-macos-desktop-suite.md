# TahoeDesk Phase 2 (Complete macOS Desktop Suite) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver Phase 2 of TahoeDesk — single-press `Win` key Launchpad, Spotlight floating search (apps + Pinyin initials + live calculator + commands + local files), Liquid Glass Bento Control Center (Wi-Fi/Bluetooth/Volume/Brightness/WinRT Media/`Win+V`), and Dock Folder Stacks + Pre-minimize window snapshot previews.

**Architecture:** All pure logic (Win-key state machine, Pinyin/fuzzy/calculator search engine, Launchpad pagination/folder model, folder stack scanner, CoreAudio/DDC-CI/WinRT control adapters, minimized snapshot LRU cache) lives in `TahoeDesk.Core` with zero WPF dependency and full xUnit test coverage. All Liquid Glass overlays (`LaunchpadWindow`, `SpotlightWindow`, `ControlCenterPopup`, `FolderStackPopup`) live in `TahoeDesk.UI`, reusing our Phase 1 `DxgiBackdropSampler`, `LiquidGlassSurface`, `OutsideClickDismissHook`, and `WDA_EXCLUDEFROMCAPTURE` foundation.

**Tech Stack:** C# / .NET 8 (`net8.0-windows10.0.22621.0`), WPF, built-in WinRT SDK projections (`Windows.Media.Control`, `Windows.Devices.Radios`), Win32 P/Invoke & COM (`WH_KEYBOARD_LL`, `RegisterHotKey`, CoreAudio `IMMDeviceEnumerator`/`IAudioEndpointVolume`, `dxva2.dll` DDC/CI, `wlanapi.dll`, `PrintWindow`), xUnit 2.9.2.

**Spec:** `docs/superpowers/specs/2026-11-23-liquid-glass-desktop-design.md`

## Global Constraints

- Target framework: `net8.0-windows10.0.22621.0`; build with local SDK (`d:\Antigravity\Mac\os\.tools\dotnet\dotnet.exe build TahoeDesk.sln --no-restore -m:1 /nr:false`) and verify tests (`dotnet test tests\TahoeDesk.Core.Tests --no-build --no-restore`).
- Zero DLL injection, zero admin/UAC requirement (standard Medium Integrity user), zero undocumented WNF writes.
- Zero new external NuGet packages required (use built-in .NET 8 + Windows 10.0.22621 WinRT/COM/Win32 APIs).
- Opinionated macOS Tahoe Liquid Glass aesthetics; keep Windows-localized branding (`Win11 Logo`, `资源管理器/此电脑`, `回收站`, `Ctrl/Alt/Shift/Win` shortcut labels).
- Every TahoeDesk overlay/popup MUST apply `WDA_EXCLUDEFROMCAPTURE` to prevent DXGI self-capture feedback and use `OutsideClickDismissHook` (or `Esc`/click-empty dismissal) for reliable outside-click closing.
- Anti-Goal red line: intercepting single-press `Win` key MUST NEVER break `Win+E`, `Win+D`, `Win+L`, `Win+V`, `Win+Shift+S`, `Win+Tab`, `Win+N`, or `Win+Arrow` combos, and MUST NOT trigger during fullscreen games/videos.

## Review Focus

1. **Held `Win` key auto-repeat & chords (`Win+E`, `Win+Shift+S`, `Win+L`, `Win+MouseClick`)**: holding `Win` fires multiple `WM_KEYDOWN` repeats before `WM_KEYUP`; any other key or mouse click during hold MUST mark the press as a chord and never trigger Launchpad or leave `Win` stuck down. (Pinned in Task 1 tests.)
2. **Calculator false positives on plain numeric search queries**: typing `"2026"` or `"123"` in Spotlight must NOT show a bogus calculator card (`= 2026`); calculator triggers only when an operator or function is present, and division-by-zero / malformed expressions return `null` safely without throwing. (Pinned in Task 2 tests.)
3. **Unsupported display brightness or absent Bluetooth/Wi-Fi hardware**: desktop monitors without DDC/CI or PCs without Bluetooth/Wi-Fi radios must degrade gracefully (slider disabled/hidden, radio row grayed out) instead of crashing Control Center. (Pinned in Task 3 tests.)
4. **Minimized windows that closed or were already minimized before TahoeDesk launched**: `WindowSnapshotCache` must never leak GDI/bitmap entries after close, and Dock minimized items without a pre-minimize snapshot must gracefully fall back to the app's high-res icon + window title. (Pinned in Task 4 tests.)
5. **Launchpad folder creation/emptying & pagination boundary**: removing the last app from a Launchpad folder must dissolve the empty folder cleanly, and deleting items on the last page must clamp `CurrentPage` within valid bounds. (Pinned in Task 5 tests.)

---

### Task 1: Single-Press `Win` Key State Machine & Low-Level Hook + Global Hotkey

**Files:**
- Create: `src/TahoeDesk.Core/Shell/WinKeyStateMachine.cs`
- Create: `src/TahoeDesk.Core/Shell/LowLevelWinKeyHook.cs`
- Create: `src/TahoeDesk.Core/Shell/GlobalHotkeyManager.cs`
- Modify: `src/TahoeDesk.Core/Config/GlassSettings.cs` (add `SpotlightHotkey` default `"Alt+Space"`)
- Test: `tests/TahoeDesk.Core.Tests/Shell/WinKeyStateMachineTests.cs`

**Interfaces:**
- Produces:
  - `WinKeyAction { PassThrough, SuppressStartAndTrigger }`
  - `WinKeyStateMachine.OnKeyDown(int vkCode, bool isInjected, bool enabled, bool isFullscreenActive) -> WinKeyAction`
  - `WinKeyStateMachine.OnKeyUp(int vkCode, bool isInjected, bool enabled, bool isFullscreenActive) -> WinKeyAction`
  - `WinKeyStateMachine.OnMouseButtonDown()`
  - `LowLevelWinKeyHook` (`event Action? SingleWinPressed`, `bool Enabled`, `bool IsFullscreenActive`, `Start()`, `Dispose()`)
  - `GlobalHotkeyManager` (`Register(nint hwnd, int id, uint modifiers, uint vk)`, `ProcessWndProc(int msg, nint wParam)`, `Dispose()`)

- [ ] **Step 1: Write failing tests in `tests/TahoeDesk.Core.Tests/Shell/WinKeyStateMachineTests.cs`**

Cover:
1. `SingleLWinPressAndRelease_TriggersLaunchpad` (down `0x5B` -> `PassThrough`, up `0x5B` -> `SuppressStartAndTrigger`)
2. `SingleRWinPressWithKeyRepeats_StillTriggersLaunchpad` (down `0x5C`, down `0x5C`, up `0x5C` -> `SuppressStartAndTrigger`)
3. `WinPlusOtherKeyChord_NeverTriggersLaunchpad` (down `0x5B`, down `'E'`, up `'E'`, up `0x5B` -> all `PassThrough`; also `Win+Shift+S` and `Win+L`)
4. `WinHeldWithMouseClick_NeverTriggersLaunchpad` (down `0x5B`, `OnMouseButtonDown()`, up `0x5B` -> `PassThrough`)
5. `DisabledOrFullscreen_AlwaysPassesThrough` (when `enabled: false` or `isFullscreenActive: true` -> `PassThrough`)
6. `InjectedKeyEvents_IgnoredWithoutCorruptingState` (`isInjected: true` -> `PassThrough`)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build TahoeDesk.sln --no-restore -m:1 /nr:false`
Expected: FAIL (`WinKeyStateMachine` not found).

- [ ] **Step 3: Implement `WinKeyStateMachine.cs`, `LowLevelWinKeyHook.cs`, `GlobalHotkeyManager.cs`, and add `SpotlightHotkey` to `GlassSettings.cs`**

Key Win32 detail for `LowLevelWinKeyHook`:
- Install `WH_KEYBOARD_LL` (13) and `WH_MOUSE_LL` (14, mouse down notifies `OnMouseButtonDown()`).
- Always pass `LWin`/`RWin` key-down through so native combos (`Win+L`, `Win+E`, `Win+D`, `Win+V`, `Win+Shift+S`) are 100% untouched.
- When `OnKeyUp` returns `SuppressStartAndTrigger`: inject a harmless unassigned virtual key `0xFF` (`SendInput` key-down + key-up) immediately BEFORE returning `CallNextHookEx` for the `Win` key-up so Windows suppresses the native Start Menu cleanly while releasing the `Win` key modifier state, and fire `SingleWinPressed` asynchronously via `ThreadPool` / dispatcher (never block the hook callback!).

- [ ] **Step 4: Build & run tests**

Run: `dotnet build` + `dotnet test`
Expected: 0 warnings, 0 errors, all tests PASS.

- [ ] **Step 5: Commit**

Commit message: `feat(shell): add single-press Win key state machine, LL hook, and global hotkey manager`

---

### Task 2: Installed App Scanner, Offline Pinyin Initials, Instant Calculator & Spotlight Search Engine

**Files:**
- Create: `src/TahoeDesk.Core/Discovery/InstalledAppScanner.cs`
- Create: `src/TahoeDesk.Core/Search/PinyinInitials.cs`
- Create: `src/TahoeDesk.Core/Search/InstantCalculator.cs`
- Create: `src/TahoeDesk.Core/Search/SpotlightModels.cs`
- Create: `src/TahoeDesk.Core/Search/SpotlightSearchEngine.cs`
- Test: `tests/TahoeDesk.Core.Tests/Search/SpotlightSearchTests.cs`

**Interfaces:**
- Produces:
  - `InstalledAppEntry(string Id, string Name, string LaunchTarget, string? IconPath, bool IsUwp, string PinyinInitials)`
  - `InstalledAppScanner.ScanInstalledApps() -> IReadOnlyList<InstalledAppEntry>` (Start Menu `.lnk` resolved via COM `IShellLinkW` + deduplicated)
  - `PinyinInitials.GetInitials(string text) -> string` (offline GB2312 code-point range mapper + ASCII pass-through)
  - `InstantCalculator.TryEvaluate(string expression, out double value, out string formatted) -> bool`
  - `SpotlightResultKind { Calculator, Application, SystemCommand, File }`
  - `SpotlightResult(SpotlightResultKind Kind, string Id, string Title, string Subtitle, string Payload, int Score)`
  - `SpotlightSearchEngine.Search(string query, IReadOnlyList<InstalledAppEntry> apps, IReadOnlyList<IndexedFileEntry> files) -> IReadOnlyList<SpotlightResult>`

- [ ] **Step 1: Write failing tests in `tests/TahoeDesk.Core.Tests/Search/SpotlightSearchTests.cs`**

Cover:
1. `PinyinInitials_MapsCommonChineseAppNames` (`"微信"` -> `"wx"`, `"设置"` -> `"sz"`, `"计算器"` -> `"jsq"`, `"VS Code"` -> `"vsc"` / `"vscode"`)
2. `InstantCalculator_EvaluatesArithmeticAndPrecedence` (`"2 + 3 * 4"` -> `14`, `"(10 - 2) / 4"` -> `2`, `"2^8"` -> `256`, `"sqrt(81)"` -> `9`)
3. `InstantCalculator_RejectsPlainNumbersAndInvalidSyntax` (`"2026"` -> `false`, `"1 / 0"` -> `false`, `"abc"` -> `false`)
4. `SpotlightSearchEngine_RanksCalculatorAppsCommandsAndFiles` (query `"wx"` matches `"微信"` via Pinyin; query `"12*5"` puts Calculator first; query `"锁屏"` or `"lock"` matches SystemCommand `"锁定屏幕"`; query `"report"` matches indexed file).

- [ ] **Step 2: Run build to verify RED**
- [ ] **Step 3: Implement `PinyinInitials.cs`, `InstantCalculator.cs`, `InstalledAppScanner.cs`, `SpotlightModels.cs`, `SpotlightSearchEngine.cs`**
- [ ] **Step 4: Build & run tests (GREEN)**
- [ ] **Step 5: Commit**

Commit message: `feat(search): add offline Pinyin matcher, instant calculator, app scanner, and Spotlight engine`

---

### Task 3: System Control Adapters (CoreAudio Volume, Brightness DDC/CI+LCD, WinRT Wi-Fi/Bluetooth & SMTC Media)

**Files:**
- Create: `src/TahoeDesk.Core/SystemControl/SystemControlModels.cs`
- Create: `src/TahoeDesk.Core/SystemControl/AudioControlService.cs`
- Create: `src/TahoeDesk.Core/SystemControl/BrightnessControlService.cs`
- Create: `src/TahoeDesk.Core/SystemControl/RadioControlService.cs`
- Create: `src/TahoeDesk.Core/SystemControl/MediaSessionService.cs`
- Test: `tests/TahoeDesk.Core.Tests/SystemControl/SystemControlTests.cs`

**Interfaces:**
- Produces:
  - `AudioEndpointInfo(string Id, string FriendlyName, bool IsDefault)`
  - `AudioState(int VolumePercent, bool IsMuted, IReadOnlyList<AudioEndpointInfo> Devices)`
  - `AudioControlService`: `GetState()`, `SetVolumePercent(int percent)`, `ToggleMute()`
  - `BrightnessControlService`: `TryGetBrightness(out int percent)`, `TrySetBrightness(int percent)` (DDC/CI `dxva2.dll` + laptop `\\.\LCD` IOCTL, returns `false` cleanly when unsupported)
  - `RadioState(bool HasWifi, bool WifiOn, string? WifiSsid, bool HasBluetooth, bool BluetoothOn)`
  - `RadioControlService`: `GetStateAsync()`, `SetWifiEnabledAsync(bool on)`, `SetBluetoothEnabledAsync(bool on)` (WinRT `Windows.Devices.Radios.Radio` + `wlanapi.dll` SSID)
  - `MediaSessionInfo(bool HasMedia, string Title, string Artist, bool IsPlaying, byte[]? ThumbnailBytes)`
  - `MediaSessionService`: `GetCurrentMediaAsync()`, `TogglePlayPauseAsync()`, `NextTrackAsync()`, `PreviousTrackAsync()` (WinRT `GlobalSystemMediaTransportControlsSessionManager`)

- [ ] **Step 1: Write failing tests in `tests/TahoeDesk.Core.Tests/SystemControl/SystemControlTests.cs`**

Cover:
1. `ClampPercent_EnforcesZeroToHundredBounds`
2. `FormatMediaSubtitle_HandlesMissingArtistAndEmptyTitle`
3. `RadioState_GracefulDefaultsWhenRadiosUnavailable`
4. `BrightnessFallback_ReportsUnsupportedWithoutThrowing`

- [ ] **Step 2: Run build to verify RED**
- [ ] **Step 3: Implement `SystemControlModels.cs`, `AudioControlService.cs`, `BrightnessControlService.cs`, `RadioControlService.cs`, `MediaSessionService.cs`**
- [ ] **Step 4: Build & run tests (GREEN)**
- [ ] **Step 5: Commit**

Commit message: `feat(control): add CoreAudio, DDC/CI brightness, WinRT radio, and SMTC media control services`

---

### Task 4: Dock Folder Stacks & Pre-Minimize Window Snapshot Cache

**Files:**
- Create: `src/TahoeDesk.Core/Discovery/FolderStackScanner.cs`
- Create: `src/TahoeDesk.Core/Discovery/WindowSnapshotCache.cs`
- Create: `src/TahoeDesk.UI/Dock/FolderStackPopup.xaml`
- Create: `src/TahoeDesk.UI/Dock/FolderStackPopup.xaml.cs`
- Create: `src/TahoeDesk.UI/Dock/WindowThumbnailCapturer.cs`
- Modify: `src/TahoeDesk.Core/Discovery/WindowModels.cs` (add `DockItemKind.MinimizedWindow`, `WindowHandle` on `DockItemModel`)
- Modify: `src/TahoeDesk.Core/Discovery/DockModelCoordinator.cs` (add minimized windows to `RightStorage` section)
- Modify: `src/TahoeDesk.Core/Discovery/IWindowTracker.cs` & `WindowTracker.cs` (hook `EVENT_SYSTEM_MINIMIZESTART` `0x0016` -> `event Action<nint>? WindowMinimizing`)
- Modify: `src/TahoeDesk.UI/Dock/DockWindow.xaml.cs` (open `FolderStackPopup` on folder click, render cached snapshot thumbnails on `MinimizedWindow` items, click to restore)
- Test: `tests/TahoeDesk.Core.Tests/Discovery/FolderStackAndSnapshotTests.cs`

**Interfaces:**
- Produces:
  - `FolderStackItem(string Name, string FullPath, bool IsDirectory, DateTime LastModifiedUtc)`
  - `FolderStackScanner.Scan(string folderPath, int maxItems = 24) -> IReadOnlyList<FolderStackItem>`
  - `WindowSnapshotCache<TBitmap>` (`Put(nint hwnd, TBitmap bmp)`, `TryGet(nint hwnd, out TBitmap? bmp)`, `Remove(nint hwnd)`, `Prune(IReadOnlySet<nint> aliveHwnds)`)
  - `FolderStackPopup` (Tahoe Liquid Glass 4-column grid popover with file icons, click-to-open, `"在资源管理器中打开"` footer, `OutsideClickDismissHook`)

- [ ] **Step 1: Write failing tests in `tests/TahoeDesk.Core.Tests/Discovery/FolderStackAndSnapshotTests.cs`**

Cover:
1. `FolderStackScanner_SortsNewestFirstAndRespectsMaxItems` (temp dir with files of different timestamps + nonexistent dir returns empty)
2. `WindowSnapshotCache_EnforcesLruCapacityAndPrunesClosedWindows`
3. `DockModelCoordinator_PlacesMinimizedWindowsInRightStorageBeforeRecycleBin`

- [ ] **Step 2: Run build to verify RED**
- [ ] **Step 3: Implement Core scanner/cache/coordinator updates + `WindowTracker` minimize hook + `WindowThumbnailCapturer` + `FolderStackPopup` + `DockWindow` wiring**
- [ ] **Step 4: Build & run tests (GREEN)**
- [ ] **Step 5: Commit**

Commit message: `feat(dock): add Liquid Glass folder stacks and pre-minimize window snapshot thumbnails`

---

### Task 5: Launchpad Model, Pagination/Folders & Fullscreen Blurred UI

**Files:**
- Create: `src/TahoeDesk.Core/Launchpad/LaunchpadModels.cs`
- Create: `src/TahoeDesk.Core/Launchpad/LaunchpadLayoutManager.cs`
- Create: `src/TahoeDesk.UI/Launchpad/LaunchpadWindow.xaml`
- Create: `src/TahoeDesk.UI/Launchpad/LaunchpadWindow.xaml.cs`
- Test: `tests/TahoeDesk.Core.Tests/Launchpad/LaunchpadLayoutTests.cs`

**Interfaces:**
- Produces:
  - `LaunchpadEntry(string Id, string Title, bool IsFolder, string? LaunchTarget, string? IconPath, IReadOnlyList<LaunchpadEntry> Children)`
  - `LaunchpadLayoutManager` (`ItemsPerPage = 35` 7×5 grid, `SyncWithInstalledApps`, `GetPage`, `PageCount`, `Filter(query)`, `MergeIntoFolder(sourceId, targetId)`, `RemoveFromFolder(folderId, childId)`, `Reorder(sourceId, targetId)`, JSON persistence `%LocalAppData%\TahoeDesk\launchpad.json`)
  - `LaunchpadWindow` (`ToggleOnMonitor(MonitorContext, IReadOnlyList<InstalledAppEntry>)`, smooth scale+fade animation, DXGI full-screen blur backdrop, centered search pill, 7×5 paginated grid, scroll/arrow page switch, page dots, drag-to-reorder & drag-to-folder, inline Liquid Glass folder popup, click-empty/`Esc`/`Win` dismiss)

- [ ] **Step 1: Write failing tests in `tests/TahoeDesk.Core.Tests/Launchpad/LaunchpadLayoutTests.cs`**

Cover:
1. `Pagination_ComputesExact7x5PagesAndClampsIndex` (72 apps -> 3 pages: 35, 35, 2)
2. `Filter_MatchesFuzzyAndPinyinInitialsAcrossFolders`
3. `MergeIntoFolder_CreatesFolderAndRemovesSourceFromRoot`
4. `RemoveFromFolder_DissolvesFolderWhenLastChildRemoved`
5. `SaveAndLoad_PreservesFolderStructureAndOrder`

- [ ] **Step 2: Run build to verify RED**
- [ ] **Step 3: Implement `LaunchpadModels.cs`, `LaunchpadLayoutManager.cs`, `LaunchpadWindow.xaml`, `LaunchpadWindow.xaml.cs`**
- [ ] **Step 4: Build & run tests (GREEN)**
- [ ] **Step 5: Commit**

Commit message: `feat(launchpad): add paginated 7x5 fullscreen blurred Launchpad with search and folder grouping`

---

### Task 6: Spotlight Centered Floating Liquid Glass Search UI

**Files:**
- Create: `src/TahoeDesk.UI/Spotlight/SpotlightWindow.xaml`
- Create: `src/TahoeDesk.UI/Spotlight/SpotlightWindow.xaml.cs`

**Interfaces:**
- Consumes: `SpotlightSearchEngine`, `InstalledAppEntry`, `DxgiBackdropSampler`, `GlassSettings`
- Produces:
  - `SpotlightWindow` (`ToggleOnMonitor(MonitorContext)`, `event Action<string>? SystemCommandRequested`, `ApplySettings(GlassSettings, bool isDarkMode)`)

- [ ] **Step 1: Build `SpotlightWindow.xaml` & `SpotlightWindow.xaml.cs`**
  - Upper-third (`~24%` screen height) centered Liquid Glass floating capsule (`LiquidGlassSurface`, `CornerRadius="18"`, `IsOverlayCard="True"`).
  - Smoothly expands dual-pane results when query is non-empty: left grouped result list (`计算器` / `应用程序` / `系统命令` / `本地文件`), right live preview pane (large calculator value + copy hint, app icon + path, file metadata, or command description).
  - Full keyboard control: `Up`/`Down` move selection, `Enter` launch/copy/execute, `Ctrl+Enter` reveal file/app in Explorer, `Esc` or `Deactivated` dismisses cleanly.
- [ ] **Step 2: Build & verify 0 warnings, 0 errors**
- [ ] **Step 3: Commit**

Commit message: `feat(spotlight): add centered Liquid Glass Spotlight search with dual-pane preview`

---

### Task 7: Control Center Liquid Glass Bento Popup

**Files:**
- Create: `src/TahoeDesk.UI/ControlCenter/ControlCenterPopup.xaml`
- Create: `src/TahoeDesk.UI/ControlCenter/ControlCenterPopup.xaml.cs`

**Interfaces:**
- Consumes: `AudioControlService`, `BrightnessControlService`, `RadioControlService`, `MediaSessionService`, `DxgiBackdropSampler`, `OutsideClickDismissHook`
- Produces:
  - `ControlCenterPopup` (`ConfigureVisuals(...)`, `OwnerAnchorHwnd`, `event Action<ThemeMode>? ThemeChangeRequested`, `RefreshLiveStateAsync()`)

- [ ] **Step 1: Build `ControlCenterPopup.xaml` & `ControlCenterPopup.xaml.cs`**
  - Tahoe Liquid Glass Bento grid (`CornerRadius="18"`, `ShadowPadding="16"`, `IsOverlayCard="True"`):
    - Connectivity Bento tile: Wi-Fi toggle pill + live SSID + `ms-settings:network-wifi`; Bluetooth toggle pill + `ms-settings:bluetooth`; Clipboard (`Win+V`) shortcut.
    - Status Bento tiles: Focus shortcut (`ms-settings:quiethours`) + Appearance toggle (`浅色` / `深色` / `自动`).
    - Display Brightness rounded pill slider (gracefully disabled/collapsed if hardware unsupported).
    - Sound Volume rounded pill slider + mute toggle + dynamic icon.
    - Now Playing WinRT SMTC media card (album art, title, artist, Prev / Play-Pause / Next).
- [ ] **Step 2: Build & verify 0 warnings, 0 errors**
- [ ] **Step 3: Commit**

Commit message: `feat(control-center): add Liquid Glass Bento Control Center with Wi-Fi, Bluetooth, audio, brightness, and media`

---

### Task 8: Full Phase 2 Shell Integration, QuickSettings Toggle & Final Verification

**Files:**
- Modify: `src/TahoeDesk.UI/App.xaml.cs` (wire `LowLevelWinKeyHook`, `GlobalHotkeyManager` `Alt+Space`, `InstalledAppScanner` background index, `LaunchpadWindow`, `SpotlightWindow`, `ControlCenterPopup`, `WindowSnapshotCache`, system commands from Spotlight, clean unhooking on exit)
- Modify: `src/TahoeDesk.UI/MenuBar/MenuBarWindow.xaml.cs` (wire Control Center button directly to `ControlCenterPopup`, dismiss Control Center with other popups, register `Alt+Space` hotkey on primary `MenuBarWindow.Hwnd`)
- Modify: `src/TahoeDesk.UI/Settings/QuickSettingsWindow.xaml` & `.cs` (add `"单按 Win 键呼出启动台"` toggle bound to `InterceptWinKeyForLaunchpad`)

- [ ] **Step 1: Integrate all Phase 2 subsystems into `App.xaml.cs`, `MenuBarWindow.xaml.cs`, and `QuickSettingsWindow`**
  - Remove Phase 1 `ShowPhase2Notice` placeholder calls.
  - Ensure `LowLevelWinKeyHook.IsFullscreenActive` tracks `TaskbarController` fullscreen state so `Win` key is never intercepted during fullscreen games/videos.
  - Ensure `CleanupSafe()` unhooks `LowLevelWinKeyHook` and unregisters `GlobalHotkeyManager` before exiting.
- [ ] **Step 2: Run full build & full test suite**
  - Run: `dotnet build TahoeDesk.sln --no-restore -m:1 /nr:false --verbosity minimal` (0 warnings, 0 errors)
  - Run: `dotnet test tests\TahoeDesk.Core.Tests --no-build --no-restore --verbosity minimal` (all Phase 1 + Phase 2 tests PASS)
- [ ] **Step 3: Commit**

Commit message: `feat(shell): integrate Launchpad, Spotlight, Control Center, and Dock stacks into TahoeDesk`
