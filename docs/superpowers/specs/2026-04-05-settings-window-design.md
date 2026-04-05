# Settings Window Design

**Date:** 2026-04-05
**Branch:** feat/auto-hdr-on-game-launch

---

## Overview

A `SettingsForm` window (opened from the tray context menu) with three tabs — Games, Blacklist, Manually Added — backed by a persistent `hdr-switcher.json` config file. A new `GameFilter` class centralises all game-matching and exclusion logic, replacing the current hardcoded `HashSet` fields scattered across `GameLibraryScanner` and `GameProcessMonitor`.

---

## New Files

### `SettingsManager.cs`

Owns the JSON config file (`hdr-switcher.json`, same directory as the exe and log). Responsible for loading and saving the two user-editable lists:

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

- **`blacklist`** — install paths or exe names/filenames the user has explicitly excluded. Populated when the user clicks "Blacklist" in the Games tab or manually types an entry in the Blacklist tab.
- **`manualGames`** — user-defined exe paths to watch for HDR toggling. Each entry has a display `name` and an absolute `exePath`.
- `Load()` — reads and deserialises the JSON file; returns defaults if the file does not exist.
- `Save(IReadOnlyList<string> blacklist, IReadOnlyList<ManualGame> manualGames)` — serialises and writes atomically (write to `.tmp`, rename).
- Exposes `IReadOnlyList<string> Blacklist` and `IReadOnlyList<ManualGame> ManualGames` properties.

### `GameFilter.cs`

Single source of truth for all "should this count as a game?" decisions. Constructed from a `SettingsManager` instance. Contains the built-in hardcoded sets that currently live in `GameLibraryScanner` and `GameProcessMonitor`.

```csharp
public sealed class GameFilter
{
    // Scanner uses this
    public bool IsExcludedInstallDir(string dirName) → bool

    // Process monitor uses these
    public bool IsBlockedExe(string exePath)  → bool
    public bool IsManualGame(string exePath)  → bool
    public IReadOnlyList<GameInfo> GetManualGames() → list
}
```

Logic:
- `IsExcludedInstallDir` — returns true if `dirName` matches any entry in the built-in `SteamExcludedInstallDirs` set OR any blacklist entry whose value equals the dir name (case-insensitive).
- `IsBlockedExe` — returns true if `Path.GetFileName(exePath)` matches the built-in `KnownNonGameExes` set, OR if any blacklist entry matches either the full path or the filename.
- `IsManualGame` — returns true if `exePath` matches any `manualGame.exePath` (case-insensitive, full path).
- `GetManualGames` — returns `manualGames` as `GameInfo` objects with `Source = "Manual"`.

### `SettingsForm.cs`

Non-modal `Form`. Single instance — created lazily on first open by `TrayApplicationContext`, hidden on close (not destroyed), so reopening is instant.

**Constructor receives:**
- `SettingsManager` — for reading current config and saving edits
- `Func<IReadOnlyList<GameInfo>>` — callback to get the current scanned games list
- `Func<IReadOnlySet<int>>` — callback to get the set of active game PIDs (for Running indicator)

**Tabs:**

*Games*
- `ListView` (full-width, columns: Name · Store · Running)
- Running column: green dot (●) when the game's process is in the active PID set; blank otherwise. Snapshot is taken when the form is shown and when Refresh is clicked — not continuously polled.
- **Refresh** button — invokes `RescanCallback` (an `Action` injected by `TrayApplicationContext`), then re-reads the games list via the `Func<IReadOnlyList<GameInfo>>` callback and reloads the ListView
- **Blacklist** button (enabled when a row is selected) — moves selected game's install path to the in-memory blacklist list; removes it from the Games list

*Blacklist*
- `ListBox` showing user-added entries only (built-in hardcoded entries are not shown)
- `TextBox` + **Add** button — accepts exe filename or full path
- **Remove** button (enabled when an item is selected)

*Manually Added*
- `ListView` (columns: Name · Path)
- Name `TextBox` + Path `TextBox` + **Browse…** button (opens `OpenFileDialog` filtered to `*.exe`) + **Add** button
- **Remove** button (enabled when a row is selected)

**Buttons (bottom of form):**
- **Save** — validates inputs (no empty entries, no duplicate paths), writes via `SettingsManager.Save()`, invokes `RescanCallback`, hides the form
- **Cancel** / window **X** — discards all in-memory edits, hides the form

Form size: 560 × 480 (fixed, non-resizable). Background and foreground colours set from `ThemeHelper.IsDarkMode` at open time to match the system dark/light theme.

---

## Modified Files

### `TrayApplicationContext.cs`

- Creates `SettingsManager` at the top of the constructor, before `GameLibraryScanner`
- Creates `GameFilter` from `SettingsManager` after the initial scan
- Adds **"Settings…"** `ToolStripMenuItem` to `RebuildMenu()` (above the separator before Exit)
- Clicking "Settings…" shows the `SettingsForm` (creates it lazily on first click)
- `RescanLibrary()` recreates `GameFilter` from the (now-updated) `SettingsManager` before passing to `UpdateGames()`, so blacklist and manual game changes take effect immediately

### `GameLibraryScanner.cs`

- Constructor gains an optional `GameFilter? filter` parameter
- Replaces the inline `SteamExcludedInstallDirs` HashSet check with `filter?.IsExcludedInstallDir(installDir) ?? false`
- `SteamExcludedInstallDirs` moves into `GameFilter` and is removed from this file

### `GameProcessMonitor.cs`

- Constructor gains a `GameFilter filter` parameter (required)
- Replaces `KnownNonGameExes.Contains(...)` check with `filter.IsBlockedExe(exePath)`
- `UpdateGames(List<GameInfo> games, GameFilter filter)` signature updated to accept a new filter; merges `filter.GetManualGames()` into the provided games list and replaces the stored filter reference
- Seed scan includes manual games
- `KnownNonGameExes` moves into `GameFilter` and is removed from this file

---

## Data Flow on Save

```
SettingsForm.Save()
  → SettingsManager.Save(blacklist, manualGames)         ← writes hdr-switcher.json
  → TrayApplicationContext.RescanLibrary()
      → new GameFilter(settingsManager)                  ← picks up updated lists
      → new GameLibraryScanner(filter, logger).ScanAll() ← excludes newly blacklisted installs
      → _gameMonitor.UpdateGames(games, filter)          ← adds manual games, updates block list
      → LogRescan(...)                                   ← logs changes as usual
```

---

## Files Untouched

`AppLogger`, `HdrManager`, `IHdrManager`, `IconRenderer`, `Win11MenuRenderer`, `ThemeHelper`, `AutostartManager`, `Program` — no changes.

---

## Testing

- `GameFilterTests` — unit tests for `IsExcludedInstallDir`, `IsBlockedExe`, `IsManualGame` with built-in and user-supplied entries
- `SettingsManagerTests` — round-trip load/save, missing file returns defaults, atomic write (`.tmp` rename)
- `SettingsForm` — no unit tests (WinForms UI); manual verification
