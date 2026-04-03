# HDR Switcher — Design Document

**Date:** 2026-04-03

---

## Goal

A Windows 11 system tray app that lets the user toggle HDR on/off per monitor, with a tray icon reflecting current state, autostart support, and no visible window.

---

## Architecture

**C# .NET 8 WinForms** (`ApplicationContext`, no main form). `NotifyIcon` drives the tray presence. The app runs a standard Windows message loop — no background threads needed for state polling; HDR state is read on demand (on icon click or menu open).

HDR read/write is done via Win32 `SetDisplayConfig` / `QueryDisplayConfig` P/Invoke calls. No third-party dependencies.

Published as a **self-contained single `.exe`** — no runtime installation needed on the target machine.

---

## Components

### 1. Program entry point (`Program.cs`)
- Sets DPI awareness to `PerMonitorV2` via manifest or API call
- Launches `ApplicationContext`

### 2. TrayApplicationContext (`TrayApplicationContext.cs`)
- Owns the `NotifyIcon` instance
- Builds and rebuilds the context menu on open
- Handles left-click → toggle primary display HDR
- Handles autostart read/write

### 3. HdrManager (`HdrManager.cs`)
- P/Invoke declarations for `QueryDisplayConfig` and `SetDisplayConfig`
- `GetDisplays()` → returns list of `DisplayInfo { Id, Name, HdrEnabled }`
- `SetHdr(displayId, bool enabled)` → toggles HDR for one display
- `GetPrimaryDisplayId()` → identifies primary display

### 4. IconRenderer (`IconRenderer.cs`)
- `RenderIcon(HdrState state, int sizePx)` → returns `Icon`
- Draws using GDI+ `Graphics` + `GraphicsPath` (vector paths, no bitmaps)
- Three states: `AllOn`, `AllOff`, `Mixed`
- Size determined at runtime from `SystemInformation.SmallIconSize` scaled by DPI

### 5. AutostartManager (`AutostartManager.cs`)
- Read/write `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- `IsEnabled()` / `SetEnabled(bool)`

---

## Tray Icon States

| State | Appearance |
|-------|-----------|
| All HDR on | Bright sun (filled, high contrast) |
| All HDR off | Dim sun with diagonal slash |
| Mixed | Half-filled sun |

Icons are drawn programmatically at the DPI-appropriate size (16px @ 100%, 32px @ 200%, 48px @ 300%) using GDI+ vector paths. Respects light/dark taskbar via `SystemParameters` or theme detection.

---

## Context Menu Structure

```
[checkmark] HDR: Primary     ← toggles primary display, checked if HDR on
─────────────────────────────
  [✓] Dell U2722D            ← per-monitor toggles, checked if HDR on
  [ ] LG 27GP950             ← unchecked = HDR off
─────────────────────────────
  [checkmark] Start with Windows
─────────────────────────────
  Exit
```

- Left-clicking the tray icon toggles HDR on the primary display
- Right-clicking opens the context menu
- Menu is rebuilt each time it opens to reflect current state

---

## HDR Toggle Implementation

Uses Win32 `SetDisplayConfig` with `DISPLAYCONFIG_DEVICE_INFO_SET_TARGET_BASE_TYPE` or the `DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE` path (undocumented but standard in Windows 11 HDR apps). Falls back to the documented `SetDisplayConfig` flags approach.

Read current state via `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO`.

---

## Autostart

Registry key: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`  
Value name: `HdrSwitcher`  
Value: full path to the `.exe`

No UAC elevation required (user-level registry key).

---

## Build & Distribution

- Target: `net8.0-windows`
- Publish: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`
- Output: single `HdrSwitcher.exe`, ~10–20 MB

---

## Out of Scope

- Multi-language / localization
- Per-display color profiles or brightness
- Linux / macOS support
- Settings UI beyond the tray menu
