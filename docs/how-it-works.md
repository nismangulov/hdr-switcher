# How HDR Switcher works

## Overview

HDR Switcher is a C# .NET 10 WinForms application that runs as a system tray icon with no visible window. It reads and writes the HDR state of connected displays using the Windows Display Configuration API, monitors game launches to automate HDR toggling, and reflects the current state through a programmatically drawn icon.

---

## Architecture

```
Program.cs                          ← composition root, single-instance mutex
├── HdrController                   ← HDR state coordination, owns DisplaySettingsChanged
│   └── HdrManager                  ← Win32 Display Config API (read/write HDR)
├── GameCoordinator                 ← game library, process monitoring, HDR snapshots
│   ├── SettingsManager             ← JSON settings (blacklist, manual games)
│   ├── GameFilter                  ← game/exe filter rules (built-in + user)
│   ├── GameLibraryScanner          ← Steam / Epic / Xbox install path discovery
│   └── GameProcessMonitor          ← SetWinEventHook launch + WMI exit detection
├── AppLogger                       ← append-only structured log (hdr-switcher.log)
├── WinFormsDispatcher              ← UI-thread dispatch (implements IDispatcher)
├── TrayApplicationContext          ← tray icon, context menu, event wiring
│   ├── IconRenderer                ← GDI+ vector sun icon (multi-resolution)
│   ├── Win11MenuRenderer           ← custom ToolStrip renderer (Win11 dark/light theme)
│   └── ThemeHelper                 ← system dark/light mode detection
├── SettingsForm                    ← settings window (Games, Blacklist, Manually Added tabs)
└── AutostartManager                ← HKCU Run registry key
```

---

## HDR state detection and toggling

Windows exposes display colour capabilities through the **Display Configuration API** (`user32.dll`):

```
GetDisplayConfigBufferSizes  →  determine array sizes needed
QueryDisplayConfig           →  enumerate all active display paths
DisplayConfigGetDeviceInfo   →  read HDR capability, state, and friendly name per display
DisplayConfigSetDeviceInfo   →  write new HDR state
```

### Reading state

`DisplayConfigGetDeviceInfo` with type `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO` (9) returns a `value` bitmask:

| Bit | Meaning |
|-----|---------|
| 0 | `advancedColorSupported` — display can do HDR |
| 1 | `advancedColorEnabled` — advanced colour pipeline active |
| 2 | `wideColorEnforced` — WCG-only mode (HDR off, WCG on) |

**Key insight:** bit 1 (`advancedColorEnabled`) is always `1` on HDR-capable displays — it stays set even when "Use HDR" is off in Windows Settings, because the display stays in the wide-colour pipeline. The actual "Use HDR" toggle is tracked by bit 2:

- `value = 0x3` (bits 0+1, bit 2 clear) → **HDR on**
- `value = 0x7` (bits 0+1+2) → **HDR off** (WCG-only mode)

So the correct check is:
```csharp
bool hdrEnabled = (value & 0x02) != 0 && (value & 0x04) == 0;
```

These are extracted as `HdrManager.IsHdrSupported(uint)` and `HdrManager.IsHdrEnabled(uint)` for testability.

### Reading display names

`DisplayConfigGetDeviceInfo` with type `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_FRIENDLY_NAME` (2) returns the EDID-supplied monitor name (e.g. `"LG OLED C3"`). Falls back to `"Display N"` if the API returns an empty string.

### Writing state

`DisplayConfigSetDeviceInfo` with type `DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE` (10) accepts a single bit:

- `value = 1` → enable advanced colour (HDR on)
- `value = 0` → disable advanced colour (reverts to WCG or SDR)

### Reliability

`GetDisplayConfigBufferSizes` and `QueryDisplayConfig` are called as two separate API calls. If display topology changes between them (e.g. a Thunderbolt dock is connected), `QueryDisplayConfig` returns `ERROR_INSUFFICIENT_BUFFER` (122). `QueryPaths()` retries the pair in a loop until it succeeds:

```csharp
do {
    GetDisplayConfigBufferSizes(...);
    err = QueryDisplayConfig(...);
} while (err == ERROR_INSUFFICIENT_BUFFER);
```

If `DisplayConfigGetDeviceInfo` fails for a single display (e.g. mid-hotplug), that display is skipped and the rest are still returned.

---

## Reacting to external changes

`SystemEvents.DisplaySettingsChanged` fires whenever display configuration changes — including when the user toggles HDR in Windows Settings, connects or disconnects a monitor, or another app changes the HDR state. On receipt, `RefreshIcon()` is called to re-read and reflect the new state.

When the user toggles HDR via the tray, a `_ownedDisplayChange` flag is set before calling `SetHdr`. The `DisplaySettingsChanged` handler checks the flag and discards the resulting event, preventing a redundant "external change" log entry.

`SystemEvents.UserPreferenceChanged` is also subscribed to detect dark/light mode switches, which affect icon and menu rendering.

---

## Icon rendering

The tray icon is drawn at runtime using **GDI+ (System.Drawing)** rather than loaded from a static file. This allows it to:

- Scale to any DPI without blurriness
- Adapt to the system's dark/light mode
- Show three distinct states from a single code path

### States

| State | Rendering |
|-------|-----------|
| AllOn | Filled circle + line rays — solid |
| AllOff | Outlined circle + line rays — strokes only |
| Mixed | Filled circle + line rays — 63% opacity |

The sun geometry (circle radius, ray inner/outer radii, ray count) is identical for all states. The only difference is `FillEllipse` vs `DrawEllipse` for the circle body.

### Multi-resolution packaging

`RenderMultiSize()` renders the icon at 16, 20, 24, and 32 pixels, then packs them into an ICO stream (PNG blobs in ICO container, Vista+ format). The shell automatically picks the best size for the current DPI.

A render cache (`_lastIconState` + `_lastIconDarkMode`) skips the GDI+ work entirely when neither the HDR state nor the system theme has changed since the last render.

### Application icon

The `icon.ico` embedded in the `.exe` (shown in Explorer, Task Manager) is a separate golden/amber version of the same sun geometry at 16, 32, 48, and 256px. It was generated once using `IconRenderer.RenderAppIconBytes()` and committed to the repository.

---

## Context menu styling

The `ContextMenuStrip` uses a custom `Win11MenuRenderer` (subclass of `ToolStripProfessionalRenderer`) that overrides:

- **Background** — dark `#1F1F1F` or light `#F3F3F3` depending on system theme
- **Item hover** — rounded rectangle highlight via `GraphicsPath`
- **Checkmarks** — custom tick drawn with `DrawLines`
- **Separators** — thin single-pixel lines
- **Border** — 1px subtle border

On first show, `DwmSetWindowAttribute` with `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND` applies Win11 rounded corners to the popup window via DWM.

Colours are read from `ThemeHelper.IsDarkMode` at paint time so they update automatically if the user switches themes without restarting.

The menu is rebuilt on every open (`_menu.Opening` event) so display names and HDR states are always current. Items from the previous open are disposed before clearing to release GDI resources.

---

## Game library scanning

`GameLibraryScanner` discovers installed games from three sources:

### Steam

1. Reads `HKCU\Software\Valve\Steam\SteamPath` for the Steam installation path
2. Parses `steamapps\libraryfolders.vdf` (source-generated regex) to find all library roots
3. For each root, reads every `appmanifest_*.acf` file
4. Skips entries where `"type"` is explicitly non-`"game"` (tools, DLC, demos)
5. Skips known non-game install directories (`Steamworks Shared`, `3DMark`, `OCCT`, etc.)

### Epic Games

Reads `%PROGRAMDATA%\Epic\EpicGamesLauncher\Data\Manifests\*.item` JSON files. Each manifest contains `DisplayName` and `InstallLocation`. Skips entries where the install path no longer exists on disk (stale manifests from uninstalled games).

### Xbox / Game Pass

1. Reads subkey names from `HKLM\SOFTWARE\Microsoft\GamingServices\GameConfig` — each subkey is an MSIX package full name
2. Resolves the install path as `C:\Program Files\WindowsApps\{packageFullName}`
3. Reads `AppxManifest.xml` for the display name; falls back to `FriendlyNameFromPackage()` when the manifest uses `ms-resource:` localization references

Libraries are rescanned every 30 minutes via `System.Threading.Timer`. The rescan logs newly installed and removed games.

---

## Game process monitoring

`GameProcessMonitor` uses two complementary mechanisms:

### Launch detection — `SetWinEventHook`

`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` with `WINEVENT_OUTOFCONTEXT` installs a hook that fires on the UI thread's message pump whenever any window comes to the foreground. For each event:

1. Early-exit if the PID is already in `_activeGames` (avoids expensive path query for known games)
2. `OpenProcess` + `QueryFullProcessImageName` to get the full exe path (reuses a field-level `StringBuilder` to avoid per-event allocation)
3. Rejects known non-game executables (launchers, crash reporters, anti-cheat) by filename
4. `Match()` checks if the path is under any known game install directory
5. If a new match is found, fires `OnGameStart` via `Task.Run` to keep the message pump unblocked

The `_games` list is a `volatile` reference so `UpdateGames()` can swap it from the rescan timer thread without locking.

### Exit detection — WMI

A `ManagementEventWatcher` subscribing to `__InstanceDeletionEvent WITHIN 1` delivers process exit events with ~1-second resolution without requiring administrator privileges. The handler verifies both PID and process name to guard against PID reuse before calling `OnGameExit`.

### Startup seed

`SeedRunningGames()` is called once after construction. It scans all currently running processes and registers any game matches in `_activeGames` without firing `OnGameStart`. This ensures that games already running when the app starts have their exit events tracked correctly.

### Stale entry cleanup

`PurgeDeadEntries()` is called on every library rescan. It uses `OpenProcess` + `GetExitCodeProcess` (checking for `STILL_ACTIVE = 259`) to identify and remove entries for processes that exited without triggering a WMI event (e.g. hard kills, crashes).

---

## Game coordinator

`GameCoordinator` is the top-level orchestrator for everything game-related. It owns:

- The initial library scan (runs on a background thread; posts results back to the UI thread via `IDispatcher`)
- Construction and lifecycle of `GameProcessMonitor` (must happen on the UI thread)
- The 30-minute rescan timer (`System.Threading.Timer`)
- The `_preGameHdrState` snapshot dictionary
- The `LibraryChanged`, `GameStarted`, and `GameExited` events consumed by `TrayApplicationContext` and `SettingsForm`

`Rescan()` is safe to call from any thread. It constructs a fresh `GameFilter`, scans all libraries, diffs against the current game list, and posts a `LibraryChanged` notification to the UI thread.

---

## Settings persistence

`SettingsManager` persists user preferences to `hdr-switcher.json` next to the exe. It stores two collections:

- **Blacklist** — install paths (or `.exe` filenames) that should never trigger HDR
- **ManualGames** — user-added games by display name + exe path

Saves are atomic: the new JSON is written to `hdr-switcher.json.tmp`, then `File.Move(..., overwrite: true)` replaces the real file. Any leftover `.tmp` from a prior crash is deleted on startup. In-memory state is updated only after a successful persist.

---

## Game filter

`GameFilter` is the single source of truth for all "is this a game?" decisions. It is immutable after construction — a new instance is created on every rescan so there is no concurrent mutation.

It combines:

- **Built-in excluded install dirs** — Steam tools/benchmarks by basename (`3DMark`, `OCCT`, `Steamworks Shared`)
- **Built-in blocked exes** — launchers, crash reporters, anti-cheat, installers, Unreal Engine helpers
- **User blacklist** — entries ending in `.exe` are added to the blocked-exe set; everything else is treated as an install path
- **Manual games** — converted to `GameInfo` objects with `Source = "Manual"` and exposed to `GameLibraryScanner`

---

## HDR controller

`HdrController` wraps `IHdrManager` and centralises all HDR state coordination:

- Subscribes to `SystemEvents.DisplaySettingsChanged` and fires `StateChanged` to subscribers
- Sets `_ownedDisplayChange = true` before each `SetHdr` call so the resulting `DisplaySettingsChanged` event is suppressed (it is already handled synchronously in `Toggle`)
- Tracks `_lastState` to skip no-op events (e.g. wake-from-sleep fires `DisplaySettingsChanged` even when HDR state was preserved)
- `ComputeHdrState()` is `internal static` so `TrayApplicationContext` can use it without re-reading displays

---

## UI dispatch

Business logic is fully decoupled from WinForms via the `IDispatcher` interface:

```csharp
public interface IDispatcher
{
    void Post(Action action);
}
```

`WinFormsDispatcher` implements it using the `SynchronizationContext` captured on the UI thread at startup. `GameCoordinator` uses it to marshal game events and library-changed notifications back to the UI thread before subscribers touch WinForms controls.

---

## Settings window

`SettingsForm` is a single-instance WinForms dialog created in `Program.cs` and shown/hidden via the tray "Settings…" menu item. It never truly closes — `FormClosingEventArgs.Cancel = true` intercepts the X button and calls `Hide()` instead.

It has three tabs:

| Tab | Contents |
|-----|---------|
| Games | Read-only list of all discovered games; "Blacklist" button moves selected game to the Blacklist tab |
| Blacklist | User-managed list of blocked install paths and exe names; supports manual text entry |
| Manually Added | User-managed list of games by display name + exe path, with a Browse button |

Edits are held in `_pendingBlacklist` and `_pendingManualGames` and only committed to `SettingsManager` when Save is clicked. Cancel and X both call `DiscardEdits()` to reset pending state from the current saved values.

`OnVisibleChanged` reloads all three tabs on every `Show()`, so the form always reflects the latest library scan and saved settings.

---

## HDR state saving and restore

`GameCoordinator` coordinates HDR snapshots alongside game events:

When `OnGameStart` fires:
1. `HdrController.GetDisplays()` captures the full per-display state (name, ID, HDR on/off, primary flag)
2. The snapshot is stored in `_preGameHdrState[game.InstallPath]` (guarded by `_stateLock`)
3. The event is forwarded to subscribers via `GameStarted`
4. *(Auto-enable HDR not yet implemented — snapshot is taken but HDR is not changed)*

When `OnGameExit` fires (on a thread-pool thread via `Task.Run`):
1. The snapshot is retrieved and removed from `_preGameHdrState`
2. The event is forwarded to subscribers via `GameExited`
3. If no snapshot exists (game was running at app startup), restore is skipped
4. *(Auto-restore HDR not yet implemented — snapshot is available but not applied)*

Using `InstallPath` as the dictionary key (rather than game name) ensures uniqueness across stores — two games from different platforms can share a display name but never the same install path.

---

## Autostart

Autostart is implemented by writing the fully-qualified exe path to:

```
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
Value: HdrSwitcher
```

This is the standard user-level autostart mechanism on Windows — no elevation required, and no scheduled tasks or services.

---

## Single instance

A named global mutex (`Global\\HdrSwitcher-{guid}`) prevents more than one instance from running. If a second instance is launched, it exits immediately.

---

## Key files

| File | Responsibility |
|------|---------------|
| `Program.cs` | Composition root, single-instance mutex, WinForms bootstrap |
| `IHdrManager.cs` | `IHdrManager` interface + `DisplayInfo` / `HdrState` types |
| `HdrManager.cs` | Win32 P/Invoke, display enumeration, HDR read/write, monitor name lookup |
| `HdrController.cs` | HDR state coordination, owned-change suppression, `StateChanged` event |
| `IDispatcher.cs` | `IDispatcher` interface + `WinFormsDispatcher` implementation |
| `SettingsManager.cs` | JSON config persistence (blacklist, manual games), atomic write-via-tmp |
| `GameFilter.cs` | Built-in + user exe/dir exclusions, manual game list |
| `GameCoordinator.cs` | Library scan orchestration, process monitoring, HDR snapshot, rescan timer |
| `GameLibraryScanner.cs` | Steam VDF/ACF, Epic JSON, Xbox manifest parsing |
| `GameProcessMonitor.cs` | `SetWinEventHook` launch detection, WMI exit detection, seed scan |
| `TrayApplicationContext.cs` | Tray icon, context menu, event wiring |
| `SettingsForm.cs` | WinForms settings window — Games, Blacklist, Manually Added tabs |
| `AppLogger.cs` | Append-only structured log with fixed-width tags |
| `IconRenderer.cs` | GDI+ sun icon rendering, multi-size ICO, app icon, render cache |
| `Win11MenuRenderer.cs` | Custom dark/light ToolStrip renderer + DWM rounded corners |
| `ThemeHelper.cs` | `IsDarkMode` registry read |
| `AutostartManager.cs` | HKCU Run key read/write |
