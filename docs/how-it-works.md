# How HDR Switcher works

## Overview

HDR Switcher is a C# .NET 10 WinForms application that runs as a system tray icon with no visible window. It reads and writes the HDR state of connected displays using the Windows Display Configuration API, monitors game launches to automate HDR toggling, and reflects the current state through a programmatically drawn icon.

---

## Architecture

```
Program.cs
└── TrayApplicationContext          ← app lifecycle, tray icon, menu, event wiring
    ├── HdrManager                  ← Win32 Display Config API (read/write HDR)
    ├── AutostartManager            ← HKCU Run registry key
    ├── IconRenderer                ← GDI+ vector sun icon (multi-resolution)
    ├── Win11MenuRenderer           ← custom ToolStrip renderer (Win11 dark/light theme)
    ├── ThemeHelper                 ← system dark/light mode detection
    ├── AppLogger                   ← append-only structured log (hdr-switcher.log)
    ├── GameLibraryScanner          ← Steam / Epic / Xbox install path discovery
    └── GameProcessMonitor          ← SetWinEventHook launch + WMI exit detection
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

## HDR state saving and restore

When `OnGameStart` fires:
1. `GetDisplays()` captures the full per-display state (name, ID, HDR on/off, primary flag)
2. The snapshot is stored in `_preGameHdrState[game.InstallPath]`
3. *(Auto-toggle not yet enabled — currently logging phase only)*

When `OnGameExit` fires:
1. The snapshot is retrieved and removed from `_preGameHdrState`
2. Each display is restored to its pre-game HDR state
3. If no snapshot exists (game was running at app startup), restore is skipped

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
| `Program.cs` | Entry point, single-instance mutex, WinForms bootstrap |
| `IHdrManager.cs` | `IHdrManager` interface + `DisplayInfo` record |
| `HdrManager.cs` | Win32 P/Invoke, display enumeration, HDR read/write, monitor name lookup |
| `TrayApplicationContext.cs` | App lifecycle, tray icon, menu, event wiring, game callbacks |
| `AppLogger.cs` | Append-only structured log with fixed-width tags |
| `GameLibraryScanner.cs` | Steam VDF/ACF, Epic JSON, Xbox manifest parsing |
| `GameProcessMonitor.cs` | `SetWinEventHook` launch detection, WMI exit detection, seed scan |
| `IconRenderer.cs` | GDI+ sun icon rendering, multi-size ICO, app icon, render cache |
| `Win11MenuRenderer.cs` | Custom dark/light ToolStrip renderer + DWM rounded corners |
| `ThemeHelper.cs` | `IsDarkMode` registry read |
| `AutostartManager.cs` | HKCU Run key read/write |
