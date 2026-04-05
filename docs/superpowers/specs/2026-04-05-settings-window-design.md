# Settings Window Design

**Date:** 2026-04-05
**Branch:** feat/auto-hdr-on-game-launch

---

## Overview

A `SettingsForm` window (opened from the tray context menu) with three tabs — Games, Blacklist, Manually Added — plus an Autostart toggle. Backed by a persistent `hdr-switcher.json` config file.

This feature introduces two new coordinator classes (`HdrController`, `GameCoordinator`) that extract all business logic out of `TrayApplicationContext`, leaving it responsible only for the tray icon and context menu. `Program.cs` becomes the composition root that wires the object graph together.

---

## Revised Architecture

```
Program.cs  (composition root)
├── AppLogger
├── AutostartManager
├── SettingsManager
├── HdrController        ← IHdrManager + display events + state tracking
├── GameCoordinator      ← scanning, monitoring, game filter, logging
├── SettingsForm         ← GameCoordinator + AutostartManager
└── TrayApplicationContext
      ← HdrController    (StateChanged → RefreshIcon; Toggle on click)
      ← GameCoordinator  (GameStarted/GameExited → RefreshIcon)
      ← Action openSettings
```

---

## New Files

### `HdrController.cs`

Wraps `IHdrManager` and owns all HDR state coordination.

**Responsibilities:**
- `Toggle(uint displayId, bool enabled)` — sets `_ownedDisplayChange`, calls `IHdrManager.SetHdr`, fires `StateChanged`
- `GetDisplays()` — delegates to `IHdrManager`
- Subscribes to `SystemEvents.DisplaySettingsChanged`; suppresses own-triggered events via `_ownedDisplayChange`; fires `StateChanged` only when HDR state actually changed (compared against last known state)
- `event Action<IReadOnlyList<DisplayInfo>> StateChanged`
- Logs HDR changes via `AppLogger` (`LogHdrStatus`)
- Disposes display event subscription on `Dispose()`

`TrayApplicationContext` and `GameCoordinator` both subscribe to `StateChanged`. Neither touches `IHdrManager` directly.

### `GameCoordinator.cs`

Owns all game-related logic.

**Responsibilities:**
- Creates and holds `GameFilter` (from `SettingsManager`)
- Runs `GameLibraryScanner` at startup and on rescan
- Creates and holds `GameProcessMonitor`
- Handles `OnGameStart` / `OnGameExited` (snapshot, restore — currently logging phase)
- `Rescan()` — public; recreates `GameFilter`, reruns scanner, calls `_monitor.UpdateGames(games, filter)`, logs diff
- `GetCurrentGames()` → `IReadOnlyList<GameInfo>`
- `GetActiveGamePids()` → `IReadOnlySet<int>`
- `event Action GameLibraryChanged` — fires after rescan; `TrayApplicationContext` subscribes to refresh the menu
- `event Action<GameInfo> GameStarted` / `event Action<GameInfo> GameExited` — `TrayApplicationContext` subscribes to call `RefreshIcon()` (once auto-toggle is live, HDR state will change on these events)
- Disposes monitor and timer on `Dispose()`

### `GameFilter.cs`

Single source of truth for all "does this count as a game?" decisions. Immutable after construction — recreated when settings change.

```csharp
public sealed class GameFilter
{
    public bool IsExcludedInstallDir(string dirName) → bool
    public bool IsBlockedExe(string exePath)         → bool
    public bool IsManualGame(string exePath)         → bool
    public IReadOnlyList<GameInfo> GetManualGames()  → list
}
```

- Built-in `KnownNonGameExes` and `SteamExcludedInstallDirs` sets live here (moved from `GameProcessMonitor` and `GameLibraryScanner`)
- User blacklist and manual games from `SettingsManager` are merged with built-in sets at construction time

### `SettingsManager.cs`

Owns `hdr-switcher.json` (same directory as exe and log).

```json
{
  "blacklist": [
    "C:\\SteamLibrary\\steamapps\\common\\SomeGame",
    "custom-launcher.exe"
  ],
  "manualGames": [
    { "name": "Dolphin", "exePath": "C:\\Dolphin\\Dolphin.exe" }
  ]
}
```

- `Load()` — reads and deserialises; returns empty defaults if file absent
- `Save(IReadOnlyList<string> blacklist, IReadOnlyList<ManualGame> manualGames)` — writes atomically (`.tmp` then rename)
- `IReadOnlyList<string> Blacklist` and `IReadOnlyList<ManualGame> ManualGames` properties

### `SettingsForm.cs`

Non-modal `Form`. Single instance created in `Program.cs`, hidden on close, shown via the `Action openSettings` callback.

**Constructor receives:** `GameCoordinator`, `AutostartManager`

**Tabs:**

*Games*
- `ListView` (columns: Name · Store · Running)
- Running = green dot when PID is in `coordinator.GetActiveGamePids()`; snapshot taken on show and on Refresh
- **Refresh** button — calls `coordinator.Rescan()`, reloads ListView
- **Blacklist** button (enabled on selection) — adds selected game's install path to in-memory blacklist, removes row

*Blacklist*
- `ListBox` — user-added entries only (built-in entries work silently, not shown)
- `TextBox` + **Add** button — accepts exe filename or full path
- **Remove** button (enabled on selection)

*Manually Added*
- `ListView` (columns: Name · Path)
- Name `TextBox` + Path `TextBox` + **Browse…** (`OpenFileDialog`, `*.exe`) + **Add** button
- **Remove** button (enabled on selection)

*All tabs share bottom bar:*
- **Autostart** checkbox — reads/writes `AutostartManager.IsEnabled()` / `SetEnabled()`; takes effect immediately on toggle (does not require Save)
- **Save** — validates (no empty entries, no duplicates), calls `SettingsManager.Save()`, calls `coordinator.Rescan()`, hides form
- **Cancel** / window **X** — discards in-memory edits, hides form

Form: 560 × 480, fixed size. Background/foreground set from `ThemeHelper.IsDarkMode` at show time.

---

## Modified Files

### `Program.cs`

Becomes the composition root. Constructs all objects and wires dependencies:

```csharp
var logger      = new AppLogger();
var autostart   = new AutostartManager();
var settings    = new SettingsManager();
var hdr         = new HdrController(new HdrManager(), logger);
var coordinator = new GameCoordinator(settings, hdr, logger);
var form        = new SettingsForm(coordinator, autostart);
var tray        = new TrayApplicationContext(hdr, coordinator, () => form.Show());
Application.Run(tray);
```

### `TrayApplicationContext.cs`

Reduced to tray icon + context menu only.

**Constructor receives:** `HdrController`, `GameCoordinator`, `Action openSettings`

**Keeps:**
- `NotifyIcon`, `ContextMenuStrip`
- `RefreshIcon()` / `ComputeHdrState()` / icon render cache
- `ApplyNotifyIconVersion4()`
- `SystemEvents.UserPreferenceChanged` → `RefreshIcon()` (UI theme concern, stays here)

**Subscribes to:**
- `hdrController.StateChanged` → `RefreshIcon()`
- `coordinator.GameStarted` / `coordinator.GameExited` → `RefreshIcon()`
- `coordinator.GameLibraryChanged` → rebuild menu (display names may change)

**Menu items:** HDR Primary · separator · per-display toggles · separator · Settings… · Open log · separator · Exit

**Removes:** `AutostartManager`, `SettingsManager`, `GameFilter`, `GameProcessMonitor`, `GameLibraryScanner`, `AppLogger`, all game event handlers, rescan timer, `SystemEvents.DisplaySettingsChanged`

### `GameLibraryScanner.cs`

- Constructor gains `GameFilter? filter` parameter
- Replaces `SteamExcludedInstallDirs` check with `filter?.IsExcludedInstallDir(installDir) ?? false`
- `SteamExcludedInstallDirs` removed (moved to `GameFilter`)

### `GameProcessMonitor.cs`

- Constructor gains `GameFilter filter` parameter (required)
- `UpdateGames(List<GameInfo> games, GameFilter filter)` — updates stored filter and merges `filter.GetManualGames()` into game list
- Replaces `KnownNonGameExes` check with `filter.IsBlockedExe(exePath)`
- `KnownNonGameExes` removed (moved to `GameFilter`)

---

## Data Flow on Save

```
SettingsForm.Save()
  → SettingsManager.Save(blacklist, manualGames)
  → coordinator.Rescan()
      → new GameFilter(settingsManager)
      → new GameLibraryScanner(filter, logger).ScanAll()
      → _monitor.UpdateGames(games, filter)
      → LogRescan(...)
      → fires GameLibraryChanged
          → TrayApplicationContext rebuilds menu
```

---

## Tray Menu (revised)

```
HDR: Primary     ✓
─────────────────
LG OLED C3       ✓
─────────────────
Settings…
Open log
─────────────────
Exit
```

---

## Files Untouched

`AppLogger`, `HdrManager`, `IHdrManager`, `IconRenderer`, `Win11MenuRenderer`, `ThemeHelper` — no changes.

---

## Testing

- `GameFilterTests` — `IsExcludedInstallDir`, `IsBlockedExe`, `IsManualGame` with built-in and user-supplied entries
- `SettingsManagerTests` — round-trip load/save, missing file returns defaults, atomic write
- `HdrControllerTests` — `StateChanged` fires on external change, suppressed on own toggle, no spurious fire when state unchanged
- `SettingsForm`, `TrayApplicationContext` — no unit tests (WinForms UI); manual verification
