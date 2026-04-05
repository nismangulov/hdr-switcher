# Settings Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Settings window (Games / Blacklist / Manually Added tabs) backed by a JSON config file, and refactor the architecture so TrayApplicationContext is a thin tray-icon shell with all business logic extracted into HdrController and GameCoordinator.

**Architecture:** Three new classes — `SettingsManager` (JSON persistence), `GameFilter` (centralised game matching/exclusion logic), `HdrController` (HDR read/write + display event handling), and `GameCoordinator` (game scanning, monitoring, and auto-toggle coordination). `Program.cs` becomes the composition root. `TrayApplicationContext` is reduced to icon + menu only.

**Tech Stack:** C# .NET 10, WinForms, System.Text.Json, xUnit

---

## File Map

**New files:**
- `src/HdrSwitcher/SettingsManager.cs` — JSON config load/save, `ManualGame` record
- `src/HdrSwitcher/GameFilter.cs` — all game matching/exclusion logic (built-in + user)
- `src/HdrSwitcher/HdrController.cs` — wraps `IHdrManager`, owns `DisplaySettingsChanged`, fires `StateChanged`
- `src/HdrSwitcher/GameCoordinator.cs` — game scanning, monitoring, rescan timer, game events
- `src/HdrSwitcher/SettingsForm.cs` — three-tab settings window
- `tests/HdrSwitcher.Tests/SettingsManagerTests.cs`
- `tests/HdrSwitcher.Tests/GameFilterTests.cs`
- `tests/HdrSwitcher.Tests/HdrControllerTests.cs`

**Modified files:**
- `src/HdrSwitcher/GameLibraryScanner.cs` — accept `GameFilter?`, use `IsExcludedInstallDir` / `IsBlacklistedPath`
- `src/HdrSwitcher/GameProcessMonitor.cs` — accept `GameFilter`, use `IsBlockedExe`, expose `GetActiveGamePids()`, update `UpdateGames` signature
- `src/HdrSwitcher/TrayApplicationContext.cs` — stripped to icon + menu, new constructor signature
- `src/HdrSwitcher/Program.cs` — composition root with all new classes

---

## Task 1: SettingsManager

**Files:**
- Create: `src/HdrSwitcher/SettingsManager.cs`
- Create: `tests/HdrSwitcher.Tests/SettingsManagerTests.cs`

- [ ] **Step 1.1: Write failing tests**

Create `tests/HdrSwitcher.Tests/SettingsManagerTests.cs`:

```csharp
namespace HdrSwitcher.Tests;

public class SettingsManagerTests : IDisposable
{
    private readonly string _dir;

    public SettingsManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Load_returns_empty_defaults_when_file_missing()
    {
        var mgr = new SettingsManager(Path.Combine(_dir, "nonexistent.json"));
        Assert.Empty(mgr.Blacklist);
        Assert.Empty(mgr.ManualGames);
    }

    [Fact]
    public void Save_and_Load_roundtrip_blacklist_and_manual_games()
    {
        var path = Path.Combine(_dir, "config.json");
        var mgr = new SettingsManager(path);
        var bl     = new List<string> { "tool.exe", @"C:\Games\BadGame" };
        var manual = new List<ManualGame> { new("Dolphin", @"C:\Dolphin\Dolphin.exe") };

        mgr.Save(bl, manual);

        var mgr2 = new SettingsManager(path);
        Assert.Equal(2, mgr2.Blacklist.Count);
        Assert.Contains("tool.exe", mgr2.Blacklist);
        Assert.Contains(@"C:\Games\BadGame", mgr2.Blacklist);
        Assert.Single(mgr2.ManualGames);
        Assert.Equal("Dolphin", mgr2.ManualGames[0].Name);
        Assert.Equal(@"C:\Dolphin\Dolphin.exe", mgr2.ManualGames[0].ExePath);
    }

    [Fact]
    public void Save_writes_atomically_no_tmp_file_left_behind()
    {
        var path = Path.Combine(_dir, "config.json");
        var mgr = new SettingsManager(path);
        mgr.Save(["x.exe"], []);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_ignores_corrupt_json_and_returns_defaults()
    {
        var path = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(path, "{ not valid json !!");
        var mgr = new SettingsManager(path);
        Assert.Empty(mgr.Blacklist);
        Assert.Empty(mgr.ManualGames);
    }

    [Fact]
    public void Save_updates_in_memory_properties_immediately()
    {
        var path = Path.Combine(_dir, "config.json");
        var mgr = new SettingsManager(path);
        mgr.Save(["a.exe"], [new("G", @"C:\G\g.exe")]);
        Assert.Single(mgr.Blacklist);
        Assert.Single(mgr.ManualGames);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
```

- [ ] **Step 1.2: Run tests to confirm they fail**

```
dotnet test tests/HdrSwitcher.Tests --filter "ClassName=HdrSwitcher.Tests.SettingsManagerTests" -v quiet
```

Expected: compile error or all fail with type-not-found.

- [ ] **Step 1.3: Create SettingsManager.cs**

```csharp
using System.Text.Json;

namespace HdrSwitcher;

public record ManualGame(string Name, string ExePath);

public class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
    };

    public string ConfigPath { get; }
    public IReadOnlyList<string>     Blacklist   { get; private set; } = [];
    public IReadOnlyList<ManualGame> ManualGames { get; private set; } = [];

    // Default path: next to exe, same directory as the log file
    public SettingsManager() : this(Path.Combine(AppContext.BaseDirectory, "hdr-switcher.json")) { }

    public SettingsManager(string configPath)
    {
        ConfigPath = configPath;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(ConfigPath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(ConfigPath), JsonOpts);
            if (dto is null) return;
            Blacklist   = dto.Blacklist   ?? [];
            ManualGames = dto.ManualGames ?? [];
        }
        catch { /* corrupt config — silently use defaults */ }
    }

    public void Save(IReadOnlyList<string> blacklist, IReadOnlyList<ManualGame> manualGames)
    {
        Blacklist   = [..blacklist];
        ManualGames = [..manualGames];

        var dto = new SettingsDto
        {
            Blacklist   = [..blacklist],
            ManualGames = [..manualGames],
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp  = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    // Internal DTO — separate from the public record so deserialization stays simple
    private sealed class SettingsDto
    {
        public List<string>?     Blacklist   { get; set; }
        public List<ManualGame>? ManualGames { get; set; }
    }
}
```

- [ ] **Step 1.4: Run tests — expect all pass**

```
dotnet test tests/HdrSwitcher.Tests --filter "ClassName=HdrSwitcher.Tests.SettingsManagerTests" -v quiet
```

Expected: 5 passed.

- [ ] **Step 1.5: Commit**

```bash
git add src/HdrSwitcher/SettingsManager.cs tests/HdrSwitcher.Tests/SettingsManagerTests.cs
git commit -m "feat: add SettingsManager for persistent JSON config"
```

---

## Task 2: GameFilter

**Files:**
- Create: `src/HdrSwitcher/GameFilter.cs`
- Create: `tests/HdrSwitcher.Tests/GameFilterTests.cs`

- [ ] **Step 2.1: Write failing tests**

Create `tests/HdrSwitcher.Tests/GameFilterTests.cs`:

```csharp
namespace HdrSwitcher.Tests;

public class GameFilterTests
{
    // ── IsExcludedInstallDir ───────────────────────────────────────────────

    [Theory]
    [InlineData("3DMark",           true)]
    [InlineData("OCCT",             true)]
    [InlineData("Steamworks Shared",true)]
    [InlineData("Elden Ring",       false)]
    [InlineData("3dmark",           true)]   // case-insensitive
    public void IsExcludedInstallDir_builtin(string dir, bool expected)
    {
        var f = new GameFilter([], []);
        Assert.Equal(expected, f.IsExcludedInstallDir(dir));
    }

    // ── IsBlacklistedPath ─────────────────────────────────────────────────

    [Fact]
    public void IsBlacklistedPath_returns_true_for_user_added_install_path()
    {
        var f = new GameFilter([@"C:\Games\BadGame"], []);
        Assert.True(f.IsBlacklistedPath(@"C:\Games\BadGame"));
        Assert.False(f.IsBlacklistedPath(@"C:\Games\GoodGame"));
    }

    [Fact]
    public void IsBlacklistedPath_is_case_insensitive()
    {
        var f = new GameFilter([@"C:\Games\BadGame"], []);
        Assert.True(f.IsBlacklistedPath(@"c:\games\badgame"));
    }

    [Fact]
    public void IsBlacklistedPath_ignores_exe_entries()
    {
        // exe entries go to the blocked-exe set, not the path set
        var f = new GameFilter(["launcher.exe"], []);
        Assert.False(f.IsBlacklistedPath("launcher.exe"));
    }

    // ── IsBlockedExe ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Game\launcher.exe",              true)]
    [InlineData(@"C:\Game\crashpad_handler.exe",      true)]
    [InlineData(@"C:\Game\easyanticheat.exe",         true)]
    [InlineData(@"C:\Game\mygame.exe",                false)]
    public void IsBlockedExe_builtin(string exePath, bool expected)
    {
        var f = new GameFilter([], []);
        Assert.Equal(expected, f.IsBlockedExe(exePath));
    }

    [Fact]
    public void IsBlockedExe_returns_true_for_user_added_exe_name()
    {
        var f = new GameFilter(["custom-launcher.exe"], []);
        Assert.True(f.IsBlockedExe(@"C:\Games\SomeGame\custom-launcher.exe"));
        Assert.False(f.IsBlockedExe(@"C:\Games\SomeGame\game.exe"));
    }

    [Fact]
    public void IsBlockedExe_returns_true_for_user_added_full_exe_path()
    {
        var f = new GameFilter([@"C:\Games\SomeGame\tool.exe"], []);
        Assert.True(f.IsBlockedExe(@"C:\Games\SomeGame\tool.exe"));
        Assert.False(f.IsBlockedExe(@"C:\Games\Other\tool.exe"));
    }

    // ── GetManualGames ────────────────────────────────────────────────────

    [Fact]
    public void GetManualGames_returns_GameInfo_with_Manual_source()
    {
        var manuals = new List<ManualGame> { new("Dolphin", @"C:\Dolphin\Dolphin.exe") };
        var f = new GameFilter([], manuals);
        var games = f.GetManualGames();
        Assert.Single(games);
        Assert.Equal("Dolphin", games[0].Name);
        Assert.Equal("Manual", games[0].Source);
        Assert.Equal(@"C:\Dolphin", games[0].InstallPath);
    }

    [Fact]
    public void GetManualGames_skips_entries_with_empty_exe_path()
    {
        var manuals = new List<ManualGame> { new("Bad", ""), new("Good", @"C:\G\g.exe") };
        var f = new GameFilter([], manuals);
        Assert.Single(f.GetManualGames());
    }
}
```

- [ ] **Step 2.2: Run tests to confirm they fail**

```
dotnet test tests/HdrSwitcher.Tests --filter "ClassName=HdrSwitcher.Tests.GameFilterTests" -v quiet
```

Expected: compile error (GameFilter not defined).

- [ ] **Step 2.3: Create GameFilter.cs**

```csharp
namespace HdrSwitcher;

/// <summary>
/// Single source of truth for all "should this count as a game?" decisions.
/// Immutable after construction — recreate when settings change.
/// </summary>
public sealed class GameFilter
{
    // Built-in: Steam install dir basenames that are tools/benchmarks, not games
    private static readonly HashSet<string> BuiltInExcludedDirs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Steamworks Shared",
            "3DMark",
            "OCCT",
        };

    // Built-in: exe filenames that live inside game dirs but are not the game itself
    private static readonly HashSet<string> BuiltInBlockedExes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Launchers
            "launcher.exe", "gamelauncher.exe", "gamelauncherhelper.exe",
            // Crash reporters
            "crashreporter.exe", "crashpad_handler.exe", "crashhandler.exe",
            "crashhandler64.exe", "crash_reporter.exe", "sentry.exe",
            // Anti-cheat / overlays
            "easyanticheat.exe", "easyanticheat_setup.exe",
            "battleye.exe", "beclauncher.exe",
            "gameoverlayrenderer.exe", "gameoverlayrenderer64.exe",
            // Unreal Engine helpers
            "unrealcefsubprocess.exe",
            // Installers / redistributables
            "vc_redist.x64.exe", "vc_redist.x86.exe",
            "dxsetup.exe", "dxwebsetup.exe",
            "ue4prereqsetup_x64.exe", "ue4prereqsetup_x86.exe",
            "setup.exe", "install.exe", "uninstall.exe", "unins000.exe",
        };

    private readonly HashSet<string> _blockedExes;      // filenames and full paths
    private readonly HashSet<string> _blacklistedPaths; // full install paths
    private readonly List<GameInfo>  _manualGames;

    public GameFilter(SettingsManager settings)
        : this(settings.Blacklist, settings.ManualGames) { }

    // Internal constructor for tests — avoids needing a SettingsManager file
    internal GameFilter(IReadOnlyList<string> userBlacklist, IReadOnlyList<ManualGame> manualGames)
    {
        _blockedExes      = new HashSet<string>(BuiltInBlockedExes, StringComparer.OrdinalIgnoreCase);
        _blacklistedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in userBlacklist)
        {
            // Entries ending in .exe are exe filters; everything else is an install path
            if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                _blockedExes.Add(entry);
            else
                _blacklistedPaths.Add(entry);
        }

        _manualGames = manualGames
            .Where(m => !string.IsNullOrEmpty(m.ExePath))
            .Select(m => new GameInfo(
                m.Name,
                Path.GetDirectoryName(m.ExePath) ?? m.ExePath,
                "Manual"))
            .ToList();
    }

    /// <summary>True if the Steam install dir basename is a built-in excluded tool/benchmark.</summary>
    public bool IsExcludedInstallDir(string dirBaseName) =>
        BuiltInExcludedDirs.Contains(dirBaseName);

    /// <summary>True if the full install path was explicitly blacklisted by the user.</summary>
    public bool IsBlacklistedPath(string fullPath) =>
        _blacklistedPaths.Contains(fullPath);

    /// <summary>
    /// True if the exe should never trigger HDR — built-in launchers/anti-cheat
    /// plus any user-added exe names or full paths.
    /// </summary>
    public bool IsBlockedExe(string exePath) =>
        _blockedExes.Contains(Path.GetFileName(exePath)) ||
        _blockedExes.Contains(exePath);

    /// <summary>Manual games as GameInfo objects (Source = "Manual").</summary>
    public IReadOnlyList<GameInfo> GetManualGames() => _manualGames;
}
```

- [ ] **Step 2.4: Add InternalsVisibleTo so tests can use the internal constructor**

In `src/HdrSwitcher/HdrSwitcher.csproj`, add inside `<PropertyGroup>`:
```xml
<InternalsVisibleTo Include="HdrSwitcher.Tests" />
```

Wait — `InternalsVisibleTo` goes in an `<ItemGroup>`, not `<PropertyGroup>`. Add to the csproj:
```xml
<ItemGroup>
  <InternalsVisibleTo Include="HdrSwitcher.Tests" />
</ItemGroup>
```

Alternatively, use an `AssemblyInfo.cs` attribute. The csproj approach is cleaner. Open `src/HdrSwitcher/HdrSwitcher.csproj` and add:

```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
    <_Parameter1>HdrSwitcher.Tests</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

- [ ] **Step 2.5: Run tests — expect all pass**

```
dotnet test tests/HdrSwitcher.Tests --filter "ClassName=HdrSwitcher.Tests.GameFilterTests" -v quiet
```

Expected: 12 passed.

- [ ] **Step 2.6: Commit**

```bash
git add src/HdrSwitcher/GameFilter.cs src/HdrSwitcher/HdrSwitcher.csproj tests/HdrSwitcher.Tests/GameFilterTests.cs
git commit -m "feat: add GameFilter centralising all game matching and exclusion logic"
```

---

## Task 3: Update GameLibraryScanner

**Files:**
- Modify: `src/HdrSwitcher/GameLibraryScanner.cs`

- [ ] **Step 3.1: Update constructor to accept optional GameFilter**

In `GameLibraryScanner.cs`, replace:
```csharp
public GameLibraryScanner(AppLogger? logger = null) => _logger = logger;

private static readonly HashSet<string> SteamExcludedInstallDirs = new(StringComparer.OrdinalIgnoreCase)
{
    "Steamworks Shared",
    "3DMark",
    "OCCT",
};
```

With:
```csharp
private readonly GameFilter? _filter;

public GameLibraryScanner(GameFilter? filter = null, AppLogger? logger = null)
{
    _filter = filter;
    _logger = logger;
}
```

Add `private readonly GameFilter? _filter;` as a field (above `_logger`).

- [ ] **Step 3.2: Replace SteamExcludedInstallDirs check with GameFilter calls**

In `ScanSteam()`, replace:
```csharp
if (SteamExcludedInstallDirs.Contains(parsed.Value.installDir)) continue;

var fullPath = Path.Combine(appsDir, "common", parsed.Value.installDir);
if (Directory.Exists(fullPath))
    yield return new GameInfo(parsed.Value.name, fullPath, "Steam");
```

With:
```csharp
if (_filter?.IsExcludedInstallDir(parsed.Value.installDir) ?? false) continue;

var fullPath = Path.Combine(appsDir, "common", parsed.Value.installDir);
if (!Directory.Exists(fullPath)) continue;
if (_filter?.IsBlacklistedPath(fullPath) ?? false) continue;

yield return new GameInfo(parsed.Value.name, fullPath, "Steam");
```

- [ ] **Step 3.3: Delete SteamExcludedInstallDirs field**

Remove the `private static readonly HashSet<string> SteamExcludedInstallDirs` field entirely — it is now in `GameFilter`.

- [ ] **Step 3.4: Build and run all tests**

```
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet
dotnet test tests/HdrSwitcher.Tests -v quiet
```

Expected: build succeeds, all existing tests pass.

- [ ] **Step 3.5: Commit**

```bash
git add src/HdrSwitcher/GameLibraryScanner.cs
git commit -m "refactor: GameLibraryScanner accepts GameFilter, removes hardcoded SteamExcludedInstallDirs"
```

---

## Task 4: Update GameProcessMonitor

**Files:**
- Modify: `src/HdrSwitcher/GameProcessMonitor.cs`

- [ ] **Step 4.1: Add GameFilter field and update constructor**

Replace the constructor signature:
```csharp
public GameProcessMonitor(
    List<GameInfo> games,
    Action<GameInfo> onGameStart,
    Action<GameInfo> onGameExit,
    AppLogger? logger = null)
```

With:
```csharp
private GameFilter _filter;

public GameProcessMonitor(
    List<GameInfo> games,
    GameFilter filter,
    Action<GameInfo> onGameStart,
    Action<GameInfo> onGameExit,
    AppLogger? logger = null)
{
    _games       = games;
    _filter      = filter;
    _onGameStart = onGameStart;
    _onGameExit  = onGameExit;
    _logger      = logger;
    // ... rest of constructor unchanged
```

Add `private GameFilter _filter;` as a field (after `private volatile List<GameInfo> _games;`).

- [ ] **Step 4.2: Replace KnownNonGameExes checks with filter.IsBlockedExe**

In `OnForegroundChanged`, replace:
```csharp
if (KnownNonGameExes.Contains(Path.GetFileName(exePath))) return;
```
With:
```csharp
if (_filter.IsBlockedExe(exePath)) return;
```

In `SeedRunningGames`, replace:
```csharp
if (KnownNonGameExes.Contains(process.ProcessName + ".exe")) continue;
// ...
if (KnownNonGameExes.Contains(Path.GetFileName(exePath))) continue;
```
With:
```csharp
if (_filter.IsBlockedExe(process.ProcessName + ".exe")) continue;
// ...
if (_filter.IsBlockedExe(exePath)) continue;
```

- [ ] **Step 4.3: Update UpdateGames to accept GameFilter and merge manual games**

Replace:
```csharp
public void UpdateGames(List<GameInfo> updatedGames)
{
    _games = updatedGames; // volatile write — safe reference swap
    PurgeDeadEntries();
}
```
With:
```csharp
public void UpdateGames(List<GameInfo> updatedGames, GameFilter filter)
{
    _filter = filter; // update filter atomically with game list
    var merged = new List<GameInfo>(updatedGames);
    merged.AddRange(filter.GetManualGames());
    _games = merged; // volatile write — safe reference swap
    PurgeDeadEntries();
}
```

- [ ] **Step 4.4: Add GetActiveGamePids method**

Add after `UpdateGames`:
```csharp
/// <summary>Returns a snapshot of PIDs of currently active games.</summary>
public IReadOnlySet<int> GetActiveGamePids()
{
    lock (_activeGamesLock)
        return _activeGames.Keys.ToHashSet();
}
```

- [ ] **Step 4.5: Remove KnownNonGameExes field**

Delete the entire `private static readonly HashSet<string> KnownNonGameExes = ...` block — it now lives in `GameFilter`.

- [ ] **Step 4.6: Merge manual games at construction time**

In the constructor, after assigning `_games = games;`, add:
```csharp
var withManual = new List<GameInfo>(games);
withManual.AddRange(filter.GetManualGames());
_games = withManual;
```

Replace `_games = games;` with those three lines.

- [ ] **Step 4.7: Build — note compile errors in TrayApplicationContext (expected)**

```
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet 2>&1 | grep -E "error|warning"
```

The build will fail because `TrayApplicationContext` still constructs `GameProcessMonitor` with the old signature. This is expected — it will be fixed in Task 6.

- [ ] **Step 4.8: Commit (even with build errors in TrayApplicationContext)**

```bash
git add src/HdrSwitcher/GameProcessMonitor.cs
git commit -m "refactor: GameProcessMonitor accepts GameFilter, exposes GetActiveGamePids"
```

---

## Task 5: HdrController

**Files:**
- Create: `src/HdrSwitcher/HdrController.cs`
- Create: `tests/HdrSwitcher.Tests/HdrControllerTests.cs`

- [ ] **Step 5.1: Write failing tests**

Create `tests/HdrSwitcher.Tests/HdrControllerTests.cs`:

```csharp
namespace HdrSwitcher.Tests;

// Minimal IHdrManager fake for testing HdrController in isolation
public class FakeHdrManager : IHdrManager
{
    public List<DisplayInfo> Displays { get; set; } = [];
    public List<(uint Id, bool Enabled)> Calls { get; } = [];

    public IReadOnlyList<DisplayInfo> GetDisplays() => Displays;

    public void SetHdr(uint displayId, bool enabled)
    {
        Calls.Add((displayId, enabled));
        var i = Displays.FindIndex(d => d.Id == displayId);
        if (i >= 0) Displays[i] = Displays[i] with { HdrEnabled = enabled };
    }
}

public class HdrControllerTests : IDisposable
{
    private readonly string _logPath;

    public HdrControllerTests() => _logPath = Path.GetTempFileName();

    [Fact]
    public void Toggle_calls_SetHdr_on_manager()
    {
        var fake = new FakeHdrManager();
        fake.Displays = [new(1, "Test", HdrEnabled: false, IsPrimary: true)];
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);

        ctrl.Toggle(1, true);

        Assert.Single(fake.Calls);
        Assert.Equal((1u, true), fake.Calls[0]);
    }

    [Fact]
    public void Toggle_fires_StateChanged_with_updated_display_state()
    {
        var fake = new FakeHdrManager();
        fake.Displays = [new(1, "Test", HdrEnabled: false, IsPrimary: true)];
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);

        IReadOnlyList<DisplayInfo>? received = null;
        ctrl.StateChanged += d => received = d;

        ctrl.Toggle(1, true);

        Assert.NotNull(received);
        Assert.True(received![0].HdrEnabled);
    }

    [Fact]
    public void GetDisplays_delegates_to_manager()
    {
        var fake = new FakeHdrManager();
        fake.Displays = [new(1, "Monitor", HdrEnabled: true, IsPrimary: true)];
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);

        var result = ctrl.GetDisplays();

        Assert.Single(result);
        Assert.Equal("Monitor", result[0].Name);
    }

    [Fact]
    public void Dispose_unsubscribes_from_display_events()
    {
        var fake = new FakeHdrManager();
        using var logger = new AppLogger(_logPath);
        var ctrl = new HdrController(fake, logger);
        var ex = Record.Exception(() => ctrl.Dispose());
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try { File.Delete(_logPath); } catch { }
    }
}
```

- [ ] **Step 5.2: Run tests to confirm they fail**

```
dotnet test tests/HdrSwitcher.Tests --filter "ClassName=HdrSwitcher.Tests.HdrControllerTests" -v quiet
```

Expected: compile error (HdrController not defined).

- [ ] **Step 5.3: Create HdrController.cs**

```csharp
using Microsoft.Win32;

namespace HdrSwitcher;

/// <summary>
/// Wraps IHdrManager and owns all HDR state coordination:
/// display event subscription, own-change suppression, and state-change notifications.
/// </summary>
public class HdrController : IDisposable
{
    private readonly IHdrManager _hdr;
    private readonly AppLogger   _logger;

    // Set before every SetHdr call so OnDisplaySettingsChanged can suppress
    // the resulting event (which is our own change, not an external one).
    // Both Toggle and the event handler run on the UI thread — plain bool is safe.
    private bool _ownedDisplayChange;

    // Last known computed state — used to skip no-op events (e.g. wake-from-sleep
    // fires DisplaySettingsChanged even when HDR state was preserved).
    private HdrState _lastState = (HdrState)(-1);

    /// <summary>
    /// Fired when HDR state changes — either from a Toggle call or an external change.
    /// Runs on the UI thread.
    /// </summary>
    public event Action<IReadOnlyList<DisplayInfo>>? StateChanged;

    public HdrController(IHdrManager hdr, AppLogger logger)
    {
        _hdr    = hdr;
        _logger = logger;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => _hdr.GetDisplays();

    /// <summary>
    /// Toggles HDR on the specified display. Fires StateChanged with the new state.
    /// Safe to call from the UI thread only.
    /// </summary>
    public void Toggle(uint displayId, bool enabled)
    {
        _ownedDisplayChange = true;
        _hdr.SetHdr(displayId, enabled);
        var displays = _hdr.GetDisplays();
        _lastState = ComputeHdrState(displays);
        StateChanged?.Invoke(displays);
        _logger.LogHdrStatus("tray toggle", displays);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Suppress events triggered by our own Toggle call — already handled above
        if (_ownedDisplayChange) { _ownedDisplayChange = false; return; }

        try
        {
            var displays = _hdr.GetDisplays();
            var newState = ComputeHdrState(displays);

            // Skip no-op events (e.g. wake-from-sleep when HDR state was preserved)
            if (newState == _lastState) return;
            _lastState = newState;

            StateChanged?.Invoke(displays);
            _logger.LogHdrStatus("external change", displays);
        }
        catch (Exception ex) { _logger.LogScanError("DisplaySettingsChanged", ex); }
    }

    internal static HdrState ComputeHdrState(IReadOnlyList<DisplayInfo> displays) =>
        displays.Count == 0             ? HdrState.AllOff
        : displays.All(d => d.HdrEnabled)  ? HdrState.AllOn
        : displays.All(d => !d.HdrEnabled) ? HdrState.AllOff
        : HdrState.Mixed;

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
```

- [ ] **Step 5.4: Run tests — expect all pass**

```
dotnet test tests/HdrSwitcher.Tests --filter "ClassName=HdrSwitcher.Tests.HdrControllerTests" -v quiet
```

Expected: 4 passed.

- [ ] **Step 5.5: Commit**

```bash
git add src/HdrSwitcher/HdrController.cs tests/HdrSwitcher.Tests/HdrControllerTests.cs
git commit -m "feat: add HdrController extracting HDR state management from TrayApplicationContext"
```

---

## Task 6: GameCoordinator

**Files:**
- Create: `src/HdrSwitcher/GameCoordinator.cs`

- [ ] **Step 6.1: Create GameCoordinator.cs**

```csharp
namespace HdrSwitcher;

/// <summary>
/// Owns all game-related logic: library scanning, process monitoring,
/// pre-game HDR state snapshots, and the 30-minute rescan timer.
/// Must be constructed on the UI thread (GameProcessMonitor requirement).
/// </summary>
public class GameCoordinator : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly HdrController   _hdr;
    private readonly AppLogger       _logger;

    private volatile List<GameInfo> _currentGames = [];
    private GameFilter              _filter;
    private GameProcessMonitor?     _monitor;
    private System.Threading.Timer? _rescanTimer;

    // install path → display snapshot taken at game-start
    private readonly Dictionary<string, IReadOnlyList<DisplayInfo>> _preGameHdrState = new();
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Fired after every rescan (including the initial scan on startup).</summary>
    public event Action? LibraryChanged;

    /// <summary>Fired when a game process is detected.</summary>
    public event Action<GameInfo>? GameStarted;

    /// <summary>Fired when a tracked game process exits.</summary>
    public event Action<GameInfo>? GameExited;

    public GameCoordinator(SettingsManager settings, HdrController hdr, AppLogger logger)
    {
        _settings = settings;
        _hdr      = hdr;
        _logger   = logger;
        _filter   = new GameFilter(settings);

        var syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "GameCoordinator must be constructed on the UI thread.");

        // Scan game libraries on background thread; create monitor on UI thread
        Task.Run(() =>
        {
            var games = new GameLibraryScanner(_filter, _logger).ScanAll();
            _logger.LogLibrary(games);

            syncContext.Post(_ =>
            {
                var withManual = new List<GameInfo>(games);
                withManual.AddRange(_filter.GetManualGames());
                _currentGames = withManual;

                _monitor = new GameProcessMonitor(
                    withManual, _filter, OnGameStart, OnGameExit, _logger);

                var running = _monitor.SeedRunningGames();
                _logger.LogSeedGames(running);

                _rescanTimer = new System.Threading.Timer(
                    _ => Rescan(), null,
                    TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

                LibraryChanged?.Invoke();
            }, null);
        });
    }

    /// <summary>
    /// Rescans game libraries, updates the monitor, and fires LibraryChanged.
    /// Safe to call from any thread.
    /// </summary>
    public void Rescan()
    {
        if (_cts.IsCancellationRequested) return;

        _filter = new GameFilter(_settings); // pick up latest settings
        var updated = new GameLibraryScanner(_filter, _logger).ScanAll();

        var snapshot     = _currentGames;
        var existingPaths = snapshot.Select(g => g.InstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatedPaths = updated.Select(g => g.InstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newGames     = updated .Where(g => !existingPaths.Contains(g.InstallPath)).ToList();
        var removedGames = snapshot.Where(g => !updatedPaths .Contains(g.InstallPath)).ToList();
        _logger.LogRescan(newGames, removedGames);

        if (_cts.IsCancellationRequested) return;

        var withManual = new List<GameInfo>(updated);
        withManual.AddRange(_filter.GetManualGames());

        lock (_stateLock)
        {
            _currentGames = withManual;
            _monitor?.UpdateGames(withManual, _filter);
        }

        LibraryChanged?.Invoke();
    }

    public IReadOnlyList<GameInfo> GetCurrentGames() => _currentGames;

    public IReadOnlySet<int> GetActiveGamePids() =>
        _monitor?.GetActiveGamePids() ?? (IReadOnlySet<int>)new HashSet<int>();

    private void OnGameStart(GameInfo game)
    {
        var displays = _hdr.GetDisplays();
        lock (_stateLock)
        {
            if (!_preGameHdrState.ContainsKey(game.InstallPath))
                _preGameHdrState[game.InstallPath] = displays;
        }
        _logger.LogGameStarted(game, displays);
        GameStarted?.Invoke(game);
        // TODO: auto-enable HDR here
    }

    private void OnGameExit(GameInfo game)
    {
        Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DisplayInfo>? preGameDisplays;
                lock (_stateLock)
                {
                    _preGameHdrState.TryGetValue(game.InstallPath, out preGameDisplays);
                    _preGameHdrState.Remove(game.InstallPath);
                }
                _logger.LogGameExited(game, preGameDisplays);
                GameExited?.Invoke(game);
                // TODO: auto-restore HDR here
            }
            catch (Exception ex) { _logger.LogScanError("GameExit", ex); }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _rescanTimer?.Dispose();
        _monitor?.Dispose();
    }
}
```

- [ ] **Step 6.2: Build to check for compile errors**

```
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet 2>&1 | grep -E "^.*error"
```

Expected: only errors from `TrayApplicationContext.cs` (still uses old constructors). New files compile cleanly.

- [ ] **Step 6.3: Commit**

```bash
git add src/HdrSwitcher/GameCoordinator.cs
git commit -m "feat: add GameCoordinator extracting game logic from TrayApplicationContext"
```

---

## Task 7: Refactor TrayApplicationContext + Program.cs

Both files must be updated together — TrayApplicationContext gets a new constructor that requires objects Program.cs is responsible for creating.

**Files:**
- Modify: `src/HdrSwitcher/TrayApplicationContext.cs`
- Modify: `src/HdrSwitcher/Program.cs`

- [ ] **Step 7.1: Replace TrayApplicationContext entirely**

Replace the full contents of `src/HdrSwitcher/TrayApplicationContext.cs`:

```csharp
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdrSwitcher;

/// <summary>
/// Owns the system tray icon and context menu only.
/// All HDR logic is in HdrController; all game logic is in GameCoordinator.
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly HdrController  _hdr;
    private readonly GameCoordinator _coordinator;
    private readonly Action         _openSettings;
    private readonly NotifyIcon     _tray;
    private readonly ContextMenuStrip _menu;

    // Icon render cache — skip GDI+ work when neither state nor theme has changed
    private HdrState _lastIconState = (HdrState)(-1);
    private bool     _lastIconDarkMode;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint   uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string szInfoTitle;
        public uint   dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    private const uint NIM_SETVERSION      = 4;
    private const uint NOTIFYICON_VERSION_4 = 4;

    public TrayApplicationContext(
        HdrController   hdr,
        GameCoordinator coordinator,
        Action          openSettings)
    {
        _hdr          = hdr;
        _coordinator  = coordinator;
        _openSettings = openSettings;

        _menu = new ContextMenuStrip();
        Win11MenuRenderer.Apply(_menu);
        _menu.Opening += (_, _) => RebuildMenu();

        _tray = new NotifyIcon
        {
            Visible          = true,
            ContextMenuStrip = _menu,
            Text             = "HDR Switcher"
        };
        _tray.MouseClick += OnTrayClick;

        // Subscribe to state-change events
        _hdr.StateChanged          += OnHdrStateChanged;
        _coordinator.GameStarted   += _ => RefreshIcon();
        _coordinator.GameExited    += _ => RefreshIcon();
        _coordinator.LibraryChanged += RebuildMenu;

        // Re-render on theme change (dark/light mode switch)
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Initial icon render using current HDR state
        RefreshIcon(_hdr.GetDisplays());
        ApplyNotifyIconVersion4();
    }

    private void OnHdrStateChanged(IReadOnlyList<DisplayInfo> displays) => RefreshIcon(displays);

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        try
        {
            var before  = _hdr.GetDisplays();
            var primary = before.FirstOrDefault(d => d.IsPrimary) ?? before.FirstOrDefault();
            if (primary is null) return;
            _hdr.Toggle(primary.Id, !primary.HdrEnabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            try { RefreshIcon(); }
            catch (Exception ex) { /* best-effort */ _ = ex; }
        }
    }

    private void RefreshIcon() => RefreshIcon(_hdr.GetDisplays());

    private void RefreshIcon(IReadOnlyList<DisplayInfo> displays)
    {
        var state = ComputeHdrState(displays);

        _tray.Text = state switch
        {
            HdrState.AllOn  => "HDR Switcher — All On",
            HdrState.AllOff => "HDR Switcher — All Off",
            HdrState.Mixed  => "HDR Switcher — Mixed",
            _               => "HDR Switcher"
        };

        bool dark = ThemeHelper.IsDarkMode;
        if (state == _lastIconState && dark == _lastIconDarkMode) return;
        _lastIconState    = state;
        _lastIconDarkMode = dark;

        var oldIcon = _tray.Icon;
        _tray.Icon = IconRenderer.RenderMultiSize(state);
        oldIcon?.Dispose();
    }

    private static HdrState ComputeHdrState(IReadOnlyList<DisplayInfo> displays) =>
        displays.Count == 0             ? HdrState.AllOff
        : displays.All(d => d.HdrEnabled)  ? HdrState.AllOn
        : displays.All(d => !d.HdrEnabled) ? HdrState.AllOff
        : HdrState.Mixed;

    private void RebuildMenu()
    {
        foreach (var item in _menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _menu.Items.Clear();

        var displays = _hdr.GetDisplays();
        var primary  = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        var primaryItem = new ToolStripMenuItem("HDR: Primary")
        {
            Checked      = primary?.HdrEnabled ?? false,
            CheckOnClick = false
        };
        if (primary is not null)
        {
            var cap = primary;
            primaryItem.Click += (_, _) =>
            {
                try { _hdr.Toggle(cap.Id, !cap.HdrEnabled); }
                catch (Exception ex)
                {
                    MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }
        _menu.Items.Add(primaryItem);
        _menu.Items.Add(new ToolStripSeparator());

        foreach (var display in displays)
        {
            var item = new ToolStripMenuItem(display.Name)
            {
                Checked      = display.HdrEnabled,
                CheckOnClick = false
            };
            var cap = display;
            item.Click += (_, _) =>
            {
                try { _hdr.Toggle(cap.Id, !cap.HdrEnabled); }
                catch (Exception ex)
                {
                    MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => _openSettings();
        _menu.Items.Add(settingsItem);

        var logItem = new ToolStripMenuItem("Open log");
        // AppLogger.LogPath is accessible via the coordinator's logger — pass path via closure
        logItem.Click += (_, _) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "hdr-switcher.log");
            if (File.Exists(path))
                System.Diagnostics.Process.Start("notepad.exe", path);
        };
        _menu.Items.Add(logItem);
        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        };
        _menu.Items.Add(exitItem);
    }

    private void ApplyNotifyIconVersion4()
    {
        try
        {
            var flags  = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var window = typeof(NotifyIcon).GetField("_window", flags)?.GetValue(_tray) as NativeWindow;
            var idObj  = (typeof(NotifyIcon).GetField("_id", flags)
                       ?? typeof(NotifyIcon).GetField("id",  flags))?.GetValue(_tray);
            uint id    = idObj is int i ? (uint)i : 0u;

            if (window?.Handle is IntPtr hwnd && hwnd != IntPtr.Zero)
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize   = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd     = hwnd,
                    uID      = id,
                    uVersion = NOTIFYICON_VERSION_4
                };
                Shell_NotifyIcon(NIM_SETVERSION, ref nid);
            }
        }
        catch { /* Optional — degrade gracefully */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _hdr.StateChanged                  -= OnHdrStateChanged;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 7.2: Update Program.cs**

Replace the full contents of `src/HdrSwitcher/Program.cs`:

```csharp
using HdrSwitcher;

// Prevent multiple instances — two tray icons would be confusing
using var mutex = new Mutex(true, "Global\\HdrSwitcher-3F8A1C2D-9E4B-4F7A-8C1D-2E5F6A7B8C9D", out bool createdNew);
if (!createdNew) return;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var logger      = new AppLogger();
var autostart   = new AutostartManager();
var settings    = new SettingsManager();
var hdr         = new HdrController(new HdrManager(), logger);
var coordinator = new GameCoordinator(settings, hdr, logger);
var form        = new SettingsForm(coordinator, autostart);
var tray        = new TrayApplicationContext(hdr, coordinator, () => form.Show());

Application.Run(tray);
```

- [ ] **Step 7.3: Build and verify**

```
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet 2>&1 | grep -E "error|Error"
```

Expected: one error — `SettingsForm` does not exist yet. All others resolved.

- [ ] **Step 7.4: Create a stub SettingsForm so the project compiles**

Create `src/HdrSwitcher/SettingsForm.cs` with a minimal stub:

```csharp
namespace HdrSwitcher;

// Stub — full implementation in Task 9-11
public class SettingsForm : Form
{
    public SettingsForm(GameCoordinator coordinator, AutostartManager autostart) { }
}
```

- [ ] **Step 7.5: Build and run all tests**

```
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet
dotnet test tests/HdrSwitcher.Tests -v quiet
```

Expected: build succeeds, all tests pass.

- [ ] **Step 7.6: Commit**

```bash
git add src/HdrSwitcher/TrayApplicationContext.cs src/HdrSwitcher/Program.cs src/HdrSwitcher/SettingsForm.cs
git commit -m "refactor: TrayApplicationContext reduced to tray icon + menu; Program.cs is now composition root"
```

---

## Task 8: Run the App and Verify

Before building the settings UI, verify the refactored app runs correctly end-to-end.

- [ ] **Step 8.1: Publish and run**

```
dotnet publish src/HdrSwitcher/HdrSwitcher.csproj -c Release -o publish/ -v quiet
start publish/HdrSwitcher.exe
```

- [ ] **Step 8.2: Check log**

Open `publish/hdr-switcher.log`. Confirm:
- `=== HDR Switcher started ===` present
- `HDR     [startup]` with display name and state
- `LIBRARY N games (Steam: X, Epic: Y, Xbox: Z)` present
- `SEED    ...` present
- No error lines (no `WARN` unexpectedly)

- [ ] **Step 8.3: Verify tray menu**

Right-click tray icon. Confirm menu shows: display toggles, `Settings…` item (clicking does nothing useful yet — stub form), `Open log`, `Exit`.

- [ ] **Step 8.4: Commit (publish artifact only — no source changes)**

No commit needed if no source changes. Proceed to Task 9.

---

## Task 9: SettingsForm — Games Tab

**Files:**
- Modify: `src/HdrSwitcher/SettingsForm.cs` (replace stub)

- [ ] **Step 9.1: Add required APIs to GameCoordinator and GameProcessMonitor**

These APIs are used by SettingsForm — add them before creating the form.

In `GameProcessMonitor.cs`, add after `GetActiveGamePids()`:
```csharp
/// <summary>Returns a snapshot of currently tracked game entries.</summary>
public IReadOnlyList<GameInfo> GetActiveGames()
{
    lock (_activeGamesLock)
        return _activeGames.Values.Select(v => v.Game).ToList();
}
```

In `GameCoordinator.cs`, add after `GetActiveGamePids()`:
```csharp
/// <summary>Exposes settings for SettingsForm reads and saves.</summary>
public SettingsManager Settings => _settings;

/// <summary>Install paths of games currently running, for the Games tab Running column.</summary>
public IReadOnlySet<string> GetActiveGameInstallPaths() =>
    (_monitor?.GetActiveGames() ?? (IReadOnlyList<GameInfo>)[])
    .Select(g => g.InstallPath)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
```

Build to confirm no errors:
```
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet 2>&1 | grep error
```

- [ ] **Step 9.2: Replace SettingsForm stub with full implementation**

Replace the full contents of `src/HdrSwitcher/SettingsForm.cs`:

```csharp
using System.Drawing;

namespace HdrSwitcher;

/// <summary>
/// Settings window with three tabs: Games, Blacklist, Manually Added.
/// Single instance — created in Program.cs, shown/hidden via TrayApplicationContext callback.
/// </summary>
public class SettingsForm : Form
{
    private readonly GameCoordinator  _coordinator;
    private readonly AutostartManager _autostart;

    // In-memory edits — applied on Save, discarded on Cancel / close
    private List<string>     _pendingBlacklist  = [];
    private List<ManualGame> _pendingManualGames = [];

    // Controls
    private TabControl  _tabs          = null!;
    private ListView    _gamesListView = null!;
    private Button      _refreshButton = null!;
    private Button      _blacklistButton = null!;

    private ListBox _blacklistBox    = null!;
    private TextBox _blacklistInput  = null!;
    private Button  _blAddButton     = null!;
    private Button  _blRemoveButton  = null!;

    private ListView _manualListView   = null!;
    private TextBox  _manualNameInput  = null!;
    private TextBox  _manualPathInput  = null!;
    private Button   _manualBrowseButton = null!;
    private Button   _manualAddButton  = null!;
    private Button   _manualRemoveButton = null!;

    private CheckBox _autostartCheckbox = null!;
    private Button   _saveButton        = null!;
    private Button   _cancelButton      = null!;

    public SettingsForm(GameCoordinator coordinator, AutostartManager autostart)
    {
        _coordinator = coordinator;
        _autostart   = autostart;
        BuildUI();
    }

    private void BuildUI()
    {
        Text             = "HDR Switcher — Settings";
        Size             = new Size(560, 480);
        FormBorderStyle  = FormBorderStyle.FixedSingle;
        MaximizeBox      = false;
        MinimizeBox      = false;
        ShowInTaskbar    = false;
        StartPosition    = FormStartPosition.CenterScreen;

        ApplyTheme();

        // ── Tab control ──────────────────────────────────────────────────────
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildGamesTab());
        _tabs.TabPages.Add(BuildBlacklistTab());
        _tabs.TabPages.Add(BuildManualTab());

        // ── Bottom panel (Autostart + Save/Cancel) ───────────────────────────
        var bottomPanel = new Panel
        {
            Dock   = DockStyle.Bottom,
            Height = 48,
        };
        ApplyPanelTheme(bottomPanel);

        _autostartCheckbox = new CheckBox
        {
            Text     = "Start with Windows",
            AutoSize = true,
            Location = new Point(12, 14),
        };
        ApplyControlTheme(_autostartCheckbox);

        _saveButton = new Button
        {
            Text     = "Save",
            Size     = new Size(80, 28),
            Location = new Point(460 - 80 - 88, 10),
            Anchor   = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _saveButton.Click += OnSave;

        _cancelButton = new Button
        {
            Text     = "Cancel",
            Size     = new Size(80, 28),
            Location = new Point(460 - 80, 10),
            Anchor   = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _cancelButton.Click += (_, _) => { DiscardEdits(); Hide(); };

        bottomPanel.Controls.AddRange([_autostartCheckbox, _saveButton, _cancelButton]);

        Controls.Add(_tabs);
        Controls.Add(bottomPanel); // added after tabs so it renders on top
    }

    // ── Games tab ────────────────────────────────────────────────────────────

    private TabPage BuildGamesTab()
    {
        var page = new TabPage("Games");
        ApplyPanelTheme(page);

        _gamesListView = new ListView
        {
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = false,
            MultiSelect   = false,
            Dock          = DockStyle.None,
            Location      = new Point(8, 38),
            Size          = new Size(520, 318),
        };
        _gamesListView.Columns.Add("Game",    280);
        _gamesListView.Columns.Add("Store",    70);
        _gamesListView.Columns.Add("Running",  60);
        _gamesListView.SelectedIndexChanged += (_, _) =>
            _blacklistButton.Enabled = _gamesListView.SelectedItems.Count > 0;
        ApplyListViewTheme(_gamesListView);

        _refreshButton = new Button
        {
            Text     = "↺ Refresh",
            Size     = new Size(88, 26),
            Location = new Point(440, 8),
        };
        _refreshButton.Click += (_, _) =>
        {
            _coordinator.Rescan();
            LoadGamesTab();
        };

        _blacklistButton = new Button
        {
            Text    = "Blacklist",
            Size    = new Size(80, 26),
            Location = new Point(348, 8),
            Enabled = false,
        };
        _blacklistButton.Click += OnBlacklistSelectedGame;

        page.Controls.AddRange([_gamesListView, _refreshButton, _blacklistButton]);
        return page;
    }

    private void LoadGamesTab()
    {
        var activePaths = _coordinator.GetActiveGameInstallPaths();
        var games       = _coordinator.GetCurrentGames();
        var bl          = _pendingBlacklist.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _gamesListView.BeginUpdate();
        _gamesListView.Items.Clear();
        foreach (var g in games.OrderBy(g => g.Name))
        {
            if (bl.Contains(g.InstallPath)) continue; // hide already-blacklisted entries
            var item = new ListViewItem(g.Name);
            item.SubItems.Add(g.Source);
            item.SubItems.Add(activePaths.Contains(g.InstallPath) ? "●" : "");
            item.Tag = g;
            _gamesListView.Items.Add(item);
        }
        _gamesListView.EndUpdate();
        _blacklistButton.Enabled = false;
    }

    private void OnBlacklistSelectedGame(object? sender, EventArgs e)
    {
        if (_gamesListView.SelectedItems.Count == 0) return;
        var game = (GameInfo)_gamesListView.SelectedItems[0].Tag!;
        if (!_pendingBlacklist.Contains(game.InstallPath, StringComparer.OrdinalIgnoreCase))
            _pendingBlacklist.Add(game.InstallPath);
        _gamesListView.Items.Remove(_gamesListView.SelectedItems[0]);
        _blacklistButton.Enabled = false;
        // Refresh blacklist tab list if it is currently shown
        LoadBlacklistTab();
    }

    // ── Blacklist tab ─────────────────────────────────────────────────────────

    private TabPage BuildBlacklistTab()
    {
        var page = new TabPage("Blacklist");
        ApplyPanelTheme(page);

        _blacklistBox = new ListBox
        {
            Location      = new Point(8, 8),
            Size          = new Size(520, 310),
            SelectionMode = SelectionMode.One,
        };
        _blacklistBox.SelectedIndexChanged += (_, _) =>
            _blRemoveButton.Enabled = _blacklistBox.SelectedIndex >= 0;
        ApplyListBoxTheme(_blacklistBox);

        _blacklistInput = new TextBox
        {
            Location    = new Point(8, 326),
            Size        = new Size(380, 23),
            PlaceholderText = "Executable name (launcher.exe) or full install path…",
        };
        ApplyTextBoxTheme(_blacklistInput);

        _blAddButton = new Button
        {
            Text     = "+ Add",
            Size     = new Size(60, 26),
            Location = new Point(396, 324),
        };
        _blAddButton.Click += OnBlacklistAdd;

        _blRemoveButton = new Button
        {
            Text     = "Remove",
            Size     = new Size(66, 26),
            Location = new Point(462, 324),
            Enabled  = false,
        };
        _blRemoveButton.Click += OnBlacklistRemove;

        page.Controls.AddRange([_blacklistBox, _blacklistInput, _blAddButton, _blRemoveButton]);
        return page;
    }

    private void LoadBlacklistTab()
    {
        _blacklistBox.BeginUpdate();
        _blacklistBox.Items.Clear();
        foreach (var entry in _pendingBlacklist)
            _blacklistBox.Items.Add(entry);
        _blacklistBox.EndUpdate();
        _blRemoveButton.Enabled = false;
    }

    private void OnBlacklistAdd(object? sender, EventArgs e)
    {
        var entry = _blacklistInput.Text.Trim();
        if (string.IsNullOrEmpty(entry)) return;
        if (_pendingBlacklist.Contains(entry, StringComparer.OrdinalIgnoreCase)) return;
        _pendingBlacklist.Add(entry);
        _blacklistInput.Clear();
        LoadBlacklistTab();
    }

    private void OnBlacklistRemove(object? sender, EventArgs e)
    {
        if (_blacklistBox.SelectedIndex < 0) return;
        _pendingBlacklist.RemoveAt(_blacklistBox.SelectedIndex);
        LoadBlacklistTab();
    }

    // ── Manually Added tab ────────────────────────────────────────────────────

    private TabPage BuildManualTab()
    {
        var page = new TabPage("Manually Added");
        ApplyPanelTheme(page);

        _manualListView = new ListView
        {
            View          = View.Details,
            FullRowSelect = true,
            MultiSelect   = false,
            Location      = new Point(8, 8),
            Size          = new Size(520, 280),
        };
        _manualListView.Columns.Add("Name", 180);
        _manualListView.Columns.Add("Path", 330);
        _manualListView.SelectedIndexChanged += (_, _) =>
            _manualRemoveButton.Enabled = _manualListView.SelectedItems.Count > 0;
        ApplyListViewTheme(_manualListView);

        var nameLabel = new Label { Text = "Name:", Location = new Point(8,  298), AutoSize = true };
        var pathLabel = new Label { Text = "Path:", Location = new Point(8,  326), AutoSize = true };
        ApplyControlTheme(nameLabel);
        ApplyControlTheme(pathLabel);

        _manualNameInput = new TextBox
        {
            Location = new Point(50, 295),
            Size     = new Size(160, 23),
            PlaceholderText = "Display name…",
        };
        ApplyTextBoxTheme(_manualNameInput);

        _manualPathInput = new TextBox
        {
            Location = new Point(50, 323),
            Size     = new Size(320, 23),
            PlaceholderText = "Full path to .exe…",
        };
        ApplyTextBoxTheme(_manualPathInput);

        _manualBrowseButton = new Button
        {
            Text     = "Browse…",
            Size     = new Size(68, 26),
            Location = new Point(376, 322),
        };
        _manualBrowseButton.Click += OnManualBrowse;

        _manualAddButton = new Button
        {
            Text     = "+ Add",
            Size     = new Size(56, 26),
            Location = new Point(450, 295),
        };
        _manualAddButton.Click += OnManualAdd;

        _manualRemoveButton = new Button
        {
            Text     = "Remove",
            Size     = new Size(66, 26),
            Location = new Point(450, 322),
            Enabled  = false,
        };
        _manualRemoveButton.Click += OnManualRemove;

        page.Controls.AddRange([
            _manualListView,
            nameLabel, pathLabel,
            _manualNameInput, _manualPathInput,
            _manualBrowseButton, _manualAddButton, _manualRemoveButton]);
        return page;
    }

    private void LoadManualTab()
    {
        _manualListView.BeginUpdate();
        _manualListView.Items.Clear();
        foreach (var g in _pendingManualGames)
        {
            var item = new ListViewItem(g.Name);
            item.SubItems.Add(g.ExePath);
            item.Tag = g;
            _manualListView.Items.Add(item);
        }
        _manualListView.EndUpdate();
        _manualRemoveButton.Enabled = false;
    }

    private void OnManualBrowse(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Select game executable",
            Filter = "Executables (*.exe)|*.exe",
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            _manualPathInput.Text = dlg.FileName;
    }

    private void OnManualAdd(object? sender, EventArgs e)
    {
        var name = _manualNameInput.Text.Trim();
        var path = _manualPathInput.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path)) { MessageBox.Show("File not found.", "HDR Switcher"); return; }
        if (_pendingManualGames.Any(g => string.Equals(g.ExePath, path, StringComparison.OrdinalIgnoreCase))) return;
        _pendingManualGames.Add(new ManualGame(name, path));
        _manualNameInput.Clear();
        _manualPathInput.Clear();
        LoadManualTab();
    }

    private void OnManualRemove(object? sender, EventArgs e)
    {
        if (_manualListView.SelectedItems.Count == 0) return;
        var game = (ManualGame)_manualListView.SelectedItems[0].Tag!;
        _pendingManualGames.Remove(game);
        LoadManualTab();
    }

    // ── Save / Cancel / Close ─────────────────────────────────────────────────

    private void OnSave(object? sender, EventArgs e)
    {
        _coordinator.Settings.Save(_pendingBlacklist, _pendingManualGames);
        _coordinator.Rescan();
        Hide();
    }

    private void DiscardEdits()
    {
        _pendingBlacklist   = [.._coordinator.Settings.Blacklist];
        _pendingManualGames = [.._coordinator.Settings.ManualGames];
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            DiscardEdits();
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        DiscardEdits(); // reset to current saved state each time the form is shown
        ApplyTheme();   // re-apply in case theme changed since last open
        LoadGamesTab();
        LoadBlacklistTab();
        LoadManualTab();
        _autostartCheckbox.Checked = _autostart.IsEnabled();
    }

    // ── Theme helpers ─────────────────────────────────────────────────────────

    private static readonly Color DarkBg   = ColorTranslator.FromHtml("#1F1F1F");
    private static readonly Color DarkFg   = Color.White;
    private static readonly Color LightBg  = ColorTranslator.FromHtml("#F3F3F3");
    private static readonly Color LightFg  = Color.Black;

    private void ApplyTheme()
    {
        bool dark    = ThemeHelper.IsDarkMode;
        BackColor    = dark ? DarkBg : LightBg;
        ForeColor    = dark ? DarkFg : LightFg;
    }

    private void ApplyPanelTheme(Control c)
    {
        bool dark   = ThemeHelper.IsDarkMode;
        c.BackColor = dark ? DarkBg : LightBg;
        c.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyControlTheme(Control c)
    {
        bool dark   = ThemeHelper.IsDarkMode;
        c.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyListViewTheme(ListView lv)
    {
        bool dark    = ThemeHelper.IsDarkMode;
        lv.BackColor = dark ? Color.FromArgb(40, 40, 40)  : Color.White;
        lv.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyListBoxTheme(ListBox lb)
    {
        bool dark    = ThemeHelper.IsDarkMode;
        lb.BackColor = dark ? Color.FromArgb(40, 40, 40) : Color.White;
        lb.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyTextBoxTheme(TextBox tb)
    {
        bool dark    = ThemeHelper.IsDarkMode;
        tb.BackColor = dark ? Color.FromArgb(40, 40, 40) : Color.White;
        tb.ForeColor = dark ? DarkFg : LightFg;
    }
}
```

- [ ] **Step 9.3: Build**

```
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet 2>&1 | grep -E "error"
```

Expected: build succeeds (0 errors).

- [ ] **Step 9.4: Publish and smoke test**

```
dotnet publish src/HdrSwitcher/HdrSwitcher.csproj -c Release -o publish/ -v quiet
start publish/HdrSwitcher.exe
```

Right-click tray → Settings… → verify window opens with all three tabs. Games tab should list scanned games. Click Cancel → window closes.

- [ ] **Step 9.5: Commit**

```bash
git add src/HdrSwitcher/SettingsForm.cs src/HdrSwitcher/GameCoordinator.cs src/HdrSwitcher/GameProcessMonitor.cs
git commit -m "feat: add SettingsForm with Games, Blacklist, and Manually Added tabs"
```

---

## Task 10: Wire Save + Verify End-to-End

- [ ] **Step 10.1: Test Save flow manually**

1. Open Settings → Blacklist tab
2. Type `test-launcher.exe` → click Add → confirm it appears in the list
3. Click Save
4. Open `hdr-switcher.json` next to exe — confirm `"blacklist": ["test-launcher.exe"]` is present
5. Reopen Settings → Blacklist tab — confirm `test-launcher.exe` is still listed

- [ ] **Step 10.2: Test Manually Added flow**

1. Open Settings → Manually Added tab
2. Enter a name + click Browse to pick any `.exe`
3. Click Add → confirm it appears in the list
4. Click Save
5. Check `hdr-switcher.json` — confirm `manualGames` array is populated

- [ ] **Step 10.3: Test Blacklist from Games tab**

1. Open Settings → Games tab
2. Select a game → click Blacklist → confirm it disappears from Games list
3. Click Blacklist tab — confirm the game's install path appears
4. Click Save
5. Check log — next rescan should no longer list that game

- [ ] **Step 10.4: Test Cancel discards edits**

1. Open Settings → add items to Blacklist tab
2. Click Cancel
3. Reopen Settings → Blacklist tab should be unchanged from before

- [ ] **Step 10.5: Run all tests**

```
dotnet test tests/HdrSwitcher.Tests -v quiet
```

Expected: all pass.

- [ ] **Step 10.6: Final commit**

```bash
git add -A
git commit -m "feat: complete settings window with persistent blacklist and manual games"
```

---

## Running Tests at Any Point

```bash
# All tests
dotnet test tests/HdrSwitcher.Tests -v quiet

# Specific class
dotnet test tests/HdrSwitcher.Tests --filter "ClassName=HdrSwitcher.Tests.GameFilterTests" -v quiet

# Build only
dotnet build src/HdrSwitcher/HdrSwitcher.csproj -v quiet
```
