# HDR Switcher

A minimal Windows 11 system tray app that toggles HDR on your displays with a single click.

![HDR On](docs/img/tray-on.png) ![HDR Off](docs/img/tray-off.png)

---

## Features

- **Left-click** the tray icon to toggle HDR on the primary display
- **Right-click** for a context menu with per-monitor controls
- Icon reflects current HDR state — filled sun (on) / outlined sun (off)
- Reacts instantly when HDR is changed externally (Windows Settings, other apps)
- Optional autostart with Windows
- Single self-contained `.exe`, no installer, no runtime required

---

## Installation

1. Download `HdrSwitcher.exe` from [Releases](../../releases)
2. Run it — the sun icon appears in the system tray
3. Optionally right-click → **Start with Windows** to enable autostart

To uninstall: right-click → **Exit**, then delete the `.exe`. If autostart was enabled, disable it first (or delete the registry value at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\HdrSwitcher`).

---

## Usage

| Action | Result |
|--------|--------|
| Left-click tray icon | Toggle HDR on primary display |
| Right-click → display name | Toggle HDR on that specific display |
| Right-click → Start with Windows | Enable / disable autostart |
| Right-click → Exit | Quit |

### Icon states

| Icon | Meaning |
|------|---------|
| ☀ (filled sun) | HDR is **on** |
| ☀ (outlined sun) | HDR is **off** |
| ☀ (dim filled sun) | **Mixed** — some displays on, some off |

---

## Requirements

- Windows 11 (or Windows 10 with HDR-capable display)
- A monitor that supports HDR (`DISPLAYCONFIG_ADVANCED_COLOR_INFO` reports `advancedColorSupported`)
- No .NET runtime needed — the runtime is bundled

---

## Building from source

```bash
git clone https://github.com/your-username/hdr-switcher
cd hdr-switcher
dotnet publish src/HdrSwitcher/HdrSwitcher.csproj -c Release -o publish/
```

Output: `publish/HdrSwitcher.exe` (~50 MB, self-contained)

### Requirements

- .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`)

### Running tests

```bash
dotnet test
```

---

## How it works

See [docs/how-it-works.md](docs/how-it-works.md) for a full technical walkthrough.

---

## License

MIT
