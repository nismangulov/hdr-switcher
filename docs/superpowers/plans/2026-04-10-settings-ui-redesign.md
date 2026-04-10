# Settings UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the Settings window dark theme (broken tab/button rendering), make it freely resizable with position persistence, and fix DPI scaling.

**Architecture:** Three targeted changes — one line in `Program.cs` to enable native dark mode, SettingsManager gains a `WindowBoundsDto` + `SaveWindowBounds()` method, and SettingsForm gains resize support + sheds ~60 lines of manual theming code.

**Tech Stack:** C# .NET 10, WinForms, xUnit, `Application.SetColorMode` (.NET 9+), `System.Drawing.Rectangle`, `Screen.AllScreens`

**Spec:** `docs/superpowers/specs/2026-04-10-settings-ui-redesign.md`

---

## Files

| File | Change |
|------|--------|
| `src/HdrSwitcher/Program.cs` | Add `SetColorMode` call |
| `src/HdrSwitcher/SettingsManager.cs` | Add `WindowBoundsDto`, `WindowBounds` property, `SaveWindowBounds()` |
| `src/HdrSwitcher/SettingsForm.cs` | Remove manual theming, add resize + DPI + bounds save/restore |
| `tests/HdrSwitcher.Tests/SettingsManagerTests.cs` | Add 4 tests for `SaveWindowBounds` |

---

## Task 1: SettingsManager — window bounds persistence

**Files:**
- Modify: `src/HdrSwitcher/SettingsManager.cs`
- Test: `tests/HdrSwitcher.Tests/SettingsManagerTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to the bottom of `tests/HdrSwitcher.Tests/SettingsManagerTests.cs`, inside the `SettingsManagerTests` class (before the `Dispose` method):

```csharp
[Fact]
public void WindowBounds_null_on_fresh_config()
{
    var mgr = new SettingsManager(Path.Combine(_dir, "fresh.json"));
    Assert.Null(mgr.WindowBounds);
}

[Fact]
public void SaveWindowBounds_roundtrips_bounds()
{
    var path = Path.Combine(_dir, "config.json");
    var mgr  = new SettingsManager(path);

    mgr.SaveWindowBounds(new System.Drawing.Rectangle(100, 200, 960, 680));

    var mgr2 = new SettingsManager(path);
    Assert.NotNull(mgr2.WindowBounds);
    Assert.Equal(100,  mgr2.WindowBounds!.X);
    Assert.Equal(200,  mgr2.WindowBounds.Y);
    Assert.Equal(960,  mgr2.WindowBounds.Width);
    Assert.Equal(680,  mgr2.WindowBounds.Height);
}

[Fact]
public void SaveWindowBounds_preserves_existing_game_settings()
{
    var path = Path.Combine(_dir, "config.json");
    var mgr  = new SettingsManager(path);
    mgr.Save(["tool.exe"], [new ManualGame("Dolphin", @"C:\Dolphin\Dolphin.exe")]);

    mgr.SaveWindowBounds(new System.Drawing.Rectangle(0, 0, 960, 680));

    var mgr2 = new SettingsManager(path);
    Assert.Single(mgr2.Blacklist);
    Assert.Equal("tool.exe", mgr2.Blacklist[0]);
    Assert.Single(mgr2.ManualGames);
    Assert.NotNull(mgr2.WindowBounds);
}

[Fact]
public void SaveWindowBounds_updates_in_memory_property_immediately()
{
    var path = Path.Combine(_dir, "config.json");
    var mgr  = new SettingsManager(path);

    mgr.SaveWindowBounds(new System.Drawing.Rectangle(50, 60, 800, 600));

    Assert.NotNull(mgr.WindowBounds);
    Assert.Equal(50, mgr.WindowBounds!.X);
    Assert.Equal(60, mgr.WindowBounds.Y);
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj --filter "FullyQualifiedName~SettingsManagerTests" -v normal
```

Expected: 4 new tests fail with compile errors (`WindowBounds` and `SaveWindowBounds` don't exist yet). Existing 5 tests still pass.

- [ ] **Step 3: Implement the changes in SettingsManager.cs**

Replace `src/HdrSwitcher/SettingsManager.cs` entirely with:

```csharp
using System.Drawing;
using System.Text.Json;

namespace HdrSwitcher;

public record ManualGame(string Name, string ExePath);
public record WindowBoundsDto(int X, int Y, int Width, int Height);

public class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
    };

    public string ConfigPath { get; }
    public IReadOnlyList<string>     Blacklist    { get; private set; } = [];
    public IReadOnlyList<ManualGame> ManualGames  { get; private set; } = [];
    public WindowBoundsDto?          WindowBounds { get; private set; }

    public SettingsManager() : this(Path.Combine(AppContext.BaseDirectory, "hdr-switcher.json")) { }

    public SettingsManager(string configPath)
    {
        ConfigPath = configPath;
        Load();
    }

    private void Load()
    {
        try { File.Delete(ConfigPath + ".tmp"); } catch { }

        if (!File.Exists(ConfigPath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(ConfigPath), JsonOpts);
            if (dto is null) return;
            Blacklist    = dto.Blacklist   ?? [];
            ManualGames  = dto.ManualGames ?? [];
            WindowBounds = dto.WindowBounds;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { /* corrupt or unreadable config — silently use defaults */ }
    }

    public void Save(IReadOnlyList<string> blacklist, IReadOnlyList<ManualGame> manualGames)
    {
        var dto = new SettingsDto
        {
            Blacklist    = [..blacklist],
            ManualGames  = [..manualGames],
            WindowBounds = WindowBounds,   // preserve existing window state
        };
        WriteDto(dto);
        Blacklist   = dto.Blacklist!;
        ManualGames = dto.ManualGames!;
    }

    public void SaveWindowBounds(Rectangle bounds)
    {
        var dto = new SettingsDto
        {
            Blacklist    = [..Blacklist],
            ManualGames  = [..ManualGames],
            WindowBounds = new WindowBoundsDto(bounds.X, bounds.Y, bounds.Width, bounds.Height),
        };
        WriteDto(dto);
        WindowBounds = dto.WindowBounds;
    }

    private void WriteDto(SettingsDto dto)
    {
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp  = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private sealed class SettingsDto
    {
        public List<string>?     Blacklist    { get; set; }
        public List<ManualGame>? ManualGames  { get; set; }
        public WindowBoundsDto?  WindowBounds { get; set; }
    }
}
```

- [ ] **Step 4: Run all SettingsManager tests**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj --filter "FullyQualifiedName~SettingsManagerTests" -v normal
```

Expected: all 9 tests pass.

- [ ] **Step 5: Run full test suite**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj -v normal
```

Expected: all tests pass, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/HdrSwitcher/SettingsManager.cs tests/HdrSwitcher.Tests/SettingsManagerTests.cs
git commit -m "feat: add WindowBoundsDto and SaveWindowBounds to SettingsManager"
```

---

## Task 2: Enable native dark mode in Program.cs

**Files:**
- Modify: `src/HdrSwitcher/Program.cs`

No new tests — `SetColorMode` is a framework call with no testable surface.

- [ ] **Step 1: Add SetColorMode call**

In `src/HdrSwitcher/Program.cs`, the current bootstrap block is:

```csharp
Application.SetHighDpiMode(HighDpiMode.SystemAware);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
```

Change it to:

```csharp
Application.SetHighDpiMode(HighDpiMode.SystemAware);
Application.EnableVisualStyles();
Application.SetColorMode(SystemColorMode.System);
Application.SetCompatibleTextRenderingDefault(false);
```

- [ ] **Step 2: Build to confirm it compiles**

```bash
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -c Debug
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Commit**

```bash
git add src/HdrSwitcher/Program.cs
git commit -m "feat: enable native dark mode via Application.SetColorMode"
```

---

## Task 3: SettingsForm — remove manual theming, add resize and bounds persistence

**Files:**
- Modify: `src/HdrSwitcher/SettingsForm.cs`

No unit tests — WinForms form behavior is verified by running the app. Build verification + manual smoke test.

- [ ] **Step 1: Fix form properties in constructor and BuildUI()**

`AutoScaleMode` is set in the **constructor** (not `BuildUI`). Change it there:

```csharp
// Before:
public SettingsForm(GameCoordinator coordinator, AutostartManager autostart)
{
    _coordinator  = coordinator;
    _autostart    = autostart;
    AutoScaleMode = AutoScaleMode.None;
    BuildUI();
}

// After:
public SettingsForm(GameCoordinator coordinator, AutostartManager autostart)
{
    _coordinator  = coordinator;
    _autostart    = autostart;
    AutoScaleMode = AutoScaleMode.Dpi;
    BuildUI();
}
```

In `BuildUI()`, replace the property block at the top of the method:

```csharp
// Before:
Text            = "HDR Switcher — Settings";
Size            = new Size(800, 640);
FormBorderStyle = FormBorderStyle.FixedSingle;
MaximizeBox     = false;
MinimizeBox     = false;
ShowInTaskbar   = false;
StartPosition   = FormStartPosition.CenterScreen;

ApplyTheme();
```

```csharp
// After:
Text            = "HDR Switcher — Settings";
Size            = new Size(960, 680);
MinimumSize     = new Size(800, 560);
FormBorderStyle = FormBorderStyle.Sizable;
MaximizeBox     = true;
MinimizeBox     = false;
ShowInTaskbar   = false;
// StartPosition is set dynamically in OnVisibleChanged
```

- [ ] **Step 2: Remove manual theme methods and their call sites**

Delete the entire `// ── Theme helpers` section at the bottom of `SettingsForm.cs` (lines ~472–518):

```csharp
// DELETE all of this:
private static readonly Color DarkBg  = ColorTranslator.FromHtml("#1F1F1F");
private static readonly Color DarkFg  = Color.White;
private static readonly Color LightBg = ColorTranslator.FromHtml("#F3F3F3");
private static readonly Color LightFg = Color.Black;

private void ApplyTheme() { ... }
private void ApplyPanelTheme(Control c) { ... }
private void ApplyControlTheme(Control c) { ... }
private void ApplyListViewTheme(ListView lv) { ... }
private void ApplyListBoxTheme(ListBox lb) { ... }
private void ApplyTextBoxTheme(TextBox tb) { ... }
```

Remove every call site:
- `ApplyPanelTheme(bottomPanel)` — in `BuildUI()`
- `ApplyPanelTheme(page)` — in `BuildGamesTab()`, `BuildBlacklistTab()`, `BuildManualTab()`
- `ApplyPanelTheme(topBar)` — in `BuildGamesTab()`
- `ApplyListViewTheme(_gamesListView)` — in `BuildGamesTab()`
- `ApplyPanelTheme(bottomBar)` — in `BuildBlacklistTab()`, `BuildManualTab()`
- `ApplyTextBoxTheme(_blacklistInput)` — in `BuildBlacklistTab()`
- `ApplyListBoxTheme(_blacklistBox)` — in `BuildBlacklistTab()`
- `ApplyControlTheme(nameLabel)`, `ApplyControlTheme(pathLabel)` — in `BuildManualTab()`
- `ApplyTextBoxTheme(_manualNameInput)`, `ApplyTextBoxTheme(_manualPathInput)` — in `BuildManualTab()`
- `ApplyListViewTheme(_manualListView)` — in `BuildManualTab()`
- `ApplyControlTheme(_autostartCheckbox)` — in `BuildUI()`
- `ApplyTheme()` — in `OnVisibleChanged()`

- [ ] **Step 3: Add the minimal list-control theme helper**

Add this private method at the bottom of the class (replacing the deleted theme section):

```csharp
// ── Theme helpers ─────────────────────────────────────────────────────────

// SetColorMode handles most controls natively. ListView and ListBox need
// manual BackColor as a fallback on some Windows configurations.
private static void ApplyListTheme(Control c)
{
    bool dark = ThemeHelper.IsDarkMode;
    c.BackColor = dark ? Color.FromArgb(40, 40, 40) : Color.White;
    c.ForeColor = dark ? Color.White : Color.Black;
}
```

- [ ] **Step 4: Apply ApplyListTheme to the three list controls**

In `BuildGamesTab()`, after creating `_gamesListView` and its columns, add:
```csharp
ApplyListTheme(_gamesListView);
```

In `BuildBlacklistTab()`, after creating `_blacklistBox`, add:
```csharp
ApplyListTheme(_blacklistBox);
```

In `BuildManualTab()`, after creating `_manualListView` and its columns, add:
```csharp
ApplyListTheme(_manualListView);
```

- [ ] **Step 5: Add Game column auto-resize**

In `BuildGamesTab()`, after the three `_gamesListView.Columns.Add(...)` calls, add:

```csharp
_gamesListView.Resize += (_, _) => ResizeGameColumn();
```

Add this method at the bottom of the class, just before `ApplyListTheme`:

```csharp
private void ResizeGameColumn()
{
    const int storeCol   = 90;
    const int runningCol = 70;
    const int scrollbar  = 20;
    int gameCol = Math.Max(200,
        _gamesListView.ClientSize.Width - storeCol - runningCol - scrollbar);
    if (_gamesListView.Columns.Count > 0)
        _gamesListView.Columns[0].Width = gameCol;
}
```

- [ ] **Step 6: Add HideForm() helper and update all three hide paths**

Add this method to the `// ── Save / Cancel / Close` section:

```csharp
private void HideForm()
{
    try { _coordinator.Settings.SaveWindowBounds(Bounds); } catch { /* best-effort */ }
    Hide();
}
```

Then replace the three `Hide()` calls:

In `_cancelButton.Click` handler (in `BuildUI()`):
```csharp
// Before:
_cancelButton.Click += (_, _) => { DiscardEdits(); Hide(); };
// After:
_cancelButton.Click += (_, _) => { DiscardEdits(); HideForm(); };
```

In `OnSave`:
```csharp
// Before:
private void OnSave(object? sender, EventArgs e)
{
    _coordinator.Settings.Save(_pendingBlacklist, _pendingManualGames);
    _coordinator.Rescan();
    Hide();
}
// After:
private void OnSave(object? sender, EventArgs e)
{
    _coordinator.Settings.Save(_pendingBlacklist, _pendingManualGames);
    _coordinator.Rescan();
    HideForm();
}
```

In `OnFormClosing`:
```csharp
// Before:
if (e.CloseReason == CloseReason.UserClosing)
{
    e.Cancel = true;
    DiscardEdits();
    Hide();
}
// After:
if (e.CloseReason == CloseReason.UserClosing)
{
    e.Cancel = true;
    DiscardEdits();
    HideForm();
}
```

- [ ] **Step 7: Add bounds restore to OnVisibleChanged**

In `OnVisibleChanged`, the current visible block is:

```csharp
protected override void OnVisibleChanged(EventArgs e)
{
    base.OnVisibleChanged(e);
    if (!Visible) return;

    DiscardEdits();
    ApplyTheme();          // ← was already removed in Step 2
    LoadGamesTab();
    LoadBlacklistTab();
    LoadManualTab();

    _autostartCheckbox.CheckedChanged -= OnAutostartChanged;
    _autostartCheckbox.Checked = _autostart.IsEnabled();
    _autostartCheckbox.CheckedChanged += OnAutostartChanged;
}
```

Replace with:

```csharp
protected override void OnVisibleChanged(EventArgs e)
{
    base.OnVisibleChanged(e);
    if (!Visible) return;

    // Restore last window position/size, or center if no saved state or monitor gone
    var saved = _coordinator.Settings.WindowBounds;
    if (saved is not null && IsBoundsOnScreen(saved))
    {
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
    }
    else
    {
        StartPosition = FormStartPosition.CenterScreen;
    }

    DiscardEdits();
    LoadGamesTab();
    LoadBlacklistTab();
    LoadManualTab();

    _autostartCheckbox.CheckedChanged -= OnAutostartChanged;
    _autostartCheckbox.Checked = _autostart.IsEnabled();
    _autostartCheckbox.CheckedChanged += OnAutostartChanged;
}

private static bool IsBoundsOnScreen(WindowBoundsDto b)
{
    var r = new Rectangle(b.X, b.Y, b.Width, b.Height);
    return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r));
}
```

- [ ] **Step 8: Build to confirm no errors**

```bash
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -c Debug
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 9: Run full test suite**

```bash
dotnet test tests/HdrSwitcher.Tests/HdrSwitcher.Tests.csproj -v normal
```

Expected: all tests pass.

- [ ] **Step 10: Smoke test — launch the app**

```bash
dotnet run --project src/HdrSwitcher/HdrSwitcher.csproj
```

Verify:
- Tray icon appears
- Right-click → Settings… opens the window
- Window is resizable (drag corner)
- Tabs render correctly in current system theme (no mixed light/dark)
- Buttons (Blacklist, Refresh, Save, Cancel) are clearly visible and filled
- Close the window, reopen — it appears at the same position and size
- Unplug/change monitor scenario is hard to test manually; trust the `IsBoundsOnScreen` guard

- [ ] **Step 11: Commit**

```bash
git add src/HdrSwitcher/SettingsForm.cs
git commit -m "feat: resizable settings window, native dark mode, DPI fix, bounds persistence"
```

---

## Task 4: Push

- [ ] **Step 1: Push branch**

```bash
git push
```
