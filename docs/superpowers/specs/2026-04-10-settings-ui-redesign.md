# Settings UI Redesign

**Date:** 2026-04-10  
**Branch:** feat/auto-hdr-on-game-launch  
**Status:** Approved — ready for implementation

---

## Problem

The Settings window has three visual and usability issues:

1. **Broken dark mode** — `TabControl` tab strip renders with mixed light/dark colors; buttons appear as invisible outlines on dark background; `CheckBox` and `TextBox` controls partially themed.
2. **Fixed size** — 800×640 is too small to comfortably read game names, paths, and the Manually Added tab. No resize handle.
3. **DPI scaling disabled** — `AutoScaleMode.None` means controls don't scale at 125%/150% DPI.

---

## Decisions

| Topic | Decision |
|-------|----------|
| Dark theme approach | `Application.SetColorMode(SystemColorMode.System)` (.NET 9+) |
| Resize | Freely resizable with `MinimumSize` |
| Window state persistence | `hdr-switcher.json` via `SettingsManager` |
| Registry for window state | Deferred — consider later |

---

## Section 1: Dark Theme

### Program.cs

Add one call after `EnableVisualStyles()`:

```csharp
Application.EnableVisualStyles();
Application.SetColorMode(SystemColorMode.System);   // ← add this
Application.SetCompatibleTextRenderingDefault(false);
```

This enables native Windows 11 dark mode for all standard WinForms controls: `TabControl`, `Button`, `TextBox`, `CheckBox`, `Panel`. Controls automatically follow the system light/dark preference and update live if the user switches themes.

### SettingsForm.cs — remove manual theming

Delete entirely:
- `static readonly Color DarkBg`, `DarkFg`, `LightBg`, `LightFg`
- `ApplyTheme()`, `ApplyPanelTheme()`, `ApplyControlTheme()`
- `ApplyListViewTheme()`, `ApplyListBoxTheme()`, `ApplyTextBoxTheme()`
- All call sites of the above (~60 lines total)
- `ApplyTheme()` call inside `OnVisibleChanged`

### ListView / ListBox fallback

`SetColorMode` may not fully darken `ListView` and `ListBox` on all Windows versions. Keep manual `BackColor` for those two controls only:

```csharp
private void ApplyListTheme(Control c)
{
    bool dark = ThemeHelper.IsDarkMode;
    c.BackColor = dark ? Color.FromArgb(40, 40, 40) : Color.White;
    c.ForeColor = dark ? Color.White : Color.Black;
}
```

Apply at construction time to `_gamesListView`, `_blacklistBox`, `_manualListView`. No need to re-apply on show — `SetColorMode` handles live theme changes for everything else.

### ThemeHelper

No changes. Still used by `TrayApplicationContext` (tray icon rendering, context menu).

---

## Section 2: Resize + Position Persistence

### SettingsForm — window properties

```csharp
// Before:
Size            = new Size(800, 640);
FormBorderStyle = FormBorderStyle.FixedSingle;
MaximizeBox     = false;
MinimizeBox     = false;
AutoScaleMode   = AutoScaleMode.None;

// After:
Size            = new Size(960, 680);
MinimumSize     = new Size(800, 560);
FormBorderStyle = FormBorderStyle.Sizable;
MaximizeBox     = true;
MinimizeBox     = false;
AutoScaleMode   = AutoScaleMode.Dpi;
```

### Game column auto-width

The Game column in `_gamesListView` is currently hardcoded at 420px. Make it fill remaining width when the ListView resizes:

```csharp
_gamesListView.Resize += (_, _) => ResizeGameColumn();

private void ResizeGameColumn()
{
    const int storeCol   = 90;
    const int runningCol = 70;
    const int scrollbar  = 20;
    int gameCol = Math.Max(200, _gamesListView.ClientSize.Width - storeCol - runningCol - scrollbar);
    if (_gamesListView.Columns.Count > 0)
        _gamesListView.Columns[0].Width = gameCol;
}
```

### HideForm helper

Extract a helper used by all three hide paths (Save button, Cancel button, X intercept):

```csharp
private void HideForm()
{
    _coordinator.Settings.SaveWindowBounds(Bounds);
    Hide();
}
```

Replace the three existing `Hide()` calls:
- `_cancelButton.Click` → `HideForm()`
- `OnSave` → `HideForm()`
- `OnFormClosing` X intercept → `HideForm()`

### Restore bounds on show

Remove `StartPosition = FormStartPosition.CenterScreen` from `BuildUI()` — it is now set dynamically on each show.

In `OnVisibleChanged`, before loading tabs:

```csharp
var saved = _coordinator.Settings.WindowBounds;
if (saved is not null && IsBoundsOnScreen(saved))
{
    StartPosition = FormStartPosition.Manual;   // must set before Bounds on first show
    Bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
}
else
{
    StartPosition = FormStartPosition.CenterScreen;
}
```

```csharp
private static bool IsBoundsOnScreen(WindowBoundsDto b)
{
    var r = new Rectangle(b.X, b.Y, b.Width, b.Height);
    return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r));
}
```

---

## Section 3: SettingsManager Changes

### New type

```csharp
public record WindowBoundsDto(int X, int Y, int Width, int Height);
```

### SettingsDto

```csharp
private sealed class SettingsDto
{
    public List<string>?      Blacklist     { get; set; }
    public List<ManualGame>?  ManualGames   { get; set; }
    public WindowBoundsDto?   WindowBounds  { get; set; }
}
```

### Public surface

```csharp
public WindowBoundsDto? WindowBounds { get; private set; }
```

Populate in `Load()`:
```csharp
WindowBounds = dto.WindowBounds;
```

### SaveWindowBounds method

Separate from `Save()` — game settings and window state are independent operations:

```csharp
public void SaveWindowBounds(Rectangle bounds)
{
    // Read current in-memory values to avoid overwriting game settings
    var dto = new SettingsDto
    {
        Blacklist    = [..Blacklist],
        ManualGames  = [..ManualGames],
        WindowBounds = new WindowBoundsDto(bounds.X, bounds.Y, bounds.Width, bounds.Height),
    };
    var json = JsonSerializer.Serialize(dto, JsonOpts);
    var tmp  = ConfigPath + ".tmp";
    File.WriteAllText(tmp, json);
    File.Move(tmp, ConfigPath, overwrite: true);
    WindowBounds = dto.WindowBounds;
}
```

**Error handling:** if `SaveWindowBounds` throws (disk full, permissions), swallow silently — window state loss is not fatal. Wrap the call in `HideForm()` with `try/catch`.

---

## Files Changed

| File | Change |
|------|--------|
| `Program.cs` | Add `SetColorMode` call |
| `SettingsForm.cs` | Remove theme methods, fix window props, add `HideForm()`, add resize handler, restore bounds |
| `SettingsManager.cs` | Add `WindowBoundsDto`, `WindowBounds` property, `SaveWindowBounds()` method |

## Files Unchanged

`ThemeHelper.cs`, `TrayApplicationContext.cs`, `GameCoordinator.cs`, `HdrController.cs`, all others.

---

## Out of Scope

- Registry-based window state persistence (deferred)
- Custom accent colors or theme tokens
- Per-tab column width persistence
