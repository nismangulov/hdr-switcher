# How HDR Switcher works

## Overview

HDR Switcher is a C# .NET 10 WinForms application that runs as a system tray icon with no visible window. It reads and writes the HDR state of connected displays using the Windows Display Configuration API, and reflects the current state through a programmatically drawn icon.

---

## Architecture

```
Program.cs
└── TrayApplicationContext          ← application lifecycle, tray icon, menu
    ├── HdrManager                  ← Win32 Display Config API (read/write HDR)
    ├── AutostartManager            ← HKCU Run registry key
    ├── IconRenderer                ← GDI+ vector sun icon (multi-resolution)
    ├── Win11MenuRenderer           ← custom ToolStrip renderer (Win11 dark theme)
    ├── ThemeHelper                 ← system dark/light mode + DWM accent colour
    └── DisplayChangeListener       ← hidden message window (WM_DISPLAYCHANGE)
```

---

## HDR state detection and toggling

Windows exposes display colour capabilities through the **Display Configuration API** (`user32.dll`):

```
GetDisplayConfigBufferSizes  →  determine array sizes needed
QueryDisplayConfig           →  enumerate all active display paths
DisplayConfigGetDeviceInfo   →  read HDR capability and state per display
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

---

## Reacting to external changes

A hidden **message-only window** (`NativeWindow` with `HWND_MESSAGE` parent) listens for `WM_DISPLAYCHANGE` (0x007E). Windows sends this message whenever display configuration changes — including when the user toggles HDR in Windows Settings, connects or disconnects a monitor, or another app changes the HDR state. On receipt, `RefreshIcon()` is called to re-read and reflect the new state.

Additionally, `SystemEvents.UserPreferenceChanged` is subscribed to detect dark/light mode switches and accent colour changes, which affect icon and menu rendering.

---

## Icon rendering

The tray icon is drawn at runtime using **GDI+ (System.Drawing)** rather than loaded from a static file. This allows it to:

- Scale to any DPI without blurriness
- Adapt to the system's dark/light mode
- Show three distinct states from a single code path

### States

| State | Rendering |
|-------|-----------|
| AllOn | Filled circle + line rays — solid white |
| AllOff | Outlined circle + line rays — white strokes only |
| Mixed | Filled circle + line rays — white at 63% opacity |

The sun geometry (circle radius, ray inner/outer radii, ray count) is identical for all states. The only difference is `FillEllipse` vs `DrawEllipse` for the circle body. Rays are always `DrawLine` with `LineCap.Round`.

### Multi-resolution packaging

`RenderMultiSize()` renders the icon at 16, 20, 24, and 32 pixels, then packs them into an ICO stream (PNG blobs in ICO container, Vista+ format). The shell automatically picks the best size for the current DPI. At 200% scaling on a 4K display, the 32px variant is used — equivalent to a crisp 64px icon on a 1080p screen.

### Application icon

The `icon.ico` embedded in the `.exe` (shown in Explorer, Task Manager) is a separate golden/amber version of the same sun geometry at 16, 32, 48, and 256px. It was generated once using `IconRenderer.RenderAppIconBytes()` and committed to the repository.

---

## Context menu styling

The `ContextMenuStrip` uses a custom `Win11MenuRenderer` (subclass of `ToolStripProfessionalRenderer`) that overrides:

- **Background** — dark `#1F1F1F` or light `#F3F3F3` depending on system theme
- **Item hover** — rounded rectangle highlight via `GraphicsPath`
- **Checkmarks** — custom white tick drawn with `DrawLines`
- **Separators** — thin single-pixel lines
- **Border** — 1px subtle border

On first show, `DwmSetWindowAttribute` with `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND` applies Win11 rounded corners to the popup window via DWM.

Colours are read from `ThemeHelper.IsDarkMode` at paint time so they update automatically if the user switches between dark and light mode without restarting the app.

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
| `HdrManager.cs` | Win32 P/Invoke, display enumeration, HDR read/write |
| `TrayApplicationContext.cs` | App lifecycle, tray icon, menu, event wiring |
| `IconRenderer.cs` | GDI+ sun icon rendering, multi-size ICO, app icon |
| `Win11MenuRenderer.cs` | Custom dark/light ToolStrip renderer + DWM rounded corners |
| `ThemeHelper.cs` | `IsDarkMode` (registry) + `AccentColor` (DWM) |
| `AutostartManager.cs` | HKCU Run key read/write |
| `app.manifest` | `supportedOS` declaration for Windows 10/11 |
