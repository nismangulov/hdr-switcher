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
    private readonly HashSet<string> _manualExePaths;

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

        var validManuals = manualGames
            .Where(m => !string.IsNullOrEmpty(m.ExePath))
            .ToList();
        _manualGames = validManuals
            .Select(m => new GameInfo(
                m.Name,
                Path.GetDirectoryName(m.ExePath) ?? m.ExePath,
                "Manual"))
            .ToList();
        _manualExePaths = new HashSet<string>(
            validManuals.Select(m => m.ExePath),
            StringComparer.OrdinalIgnoreCase);
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

    /// <summary>True if the exe path matches a manually configured game.</summary>
    public bool IsManualGame(string exePath) =>
        _manualExePaths.Contains(exePath);
}
