using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HdrSwitcher;

public record GameInfo(string Name, string InstallPath, string Source);

public partial class GameLibraryScanner
{
    private readonly AppLogger? _logger;

    public GameLibraryScanner(AppLogger? logger = null) => _logger = logger;

    // Steam install directory names that belong to tools or benchmarks rather than games.
    // Steam sometimes sets type="game" in their ACF, so the type filter alone is not enough.
    // Add new entries here when they appear in the LIBRARY log and are not actual games.
    private static readonly HashSet<string> SteamExcludedInstallDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steamworks Shared", // Steam redistributables package
        "3DMark",            // Benchmark tool
        "OCCT",              // Benchmark / stress test tool
    };

    public List<GameInfo> ScanAll()
    {
        var games = new List<GameInfo>();
        Scan("Steam", ScanSteam, games);
        Scan("Epic",  ScanEpic,  games);
        Scan("Xbox",  ScanXbox,  games);
        return games;
    }

    private void Scan(string source, Func<IEnumerable<GameInfo>> scanner, List<GameInfo> target)
    {
        try { target.AddRange(scanner()); }
        catch (Exception ex) { _logger?.LogScanError(source, ex); }
    }

    private static IEnumerable<GameInfo> ScanSteam()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steamPath) yield break;

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        foreach (var root in ParseLibraryRoots(File.ReadAllText(vdfPath)))
        {
            var appsDir = Path.Combine(root, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (var acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
            {
                var content = File.ReadAllText(acf);

                // Skip non-game entries only when "type" is explicitly present and not "game".
                // Most ACF files omit the field entirely — absence means game.
                var type = AcfTypeRegex().Match(content).Groups[1].Value;
                if (!string.IsNullOrEmpty(type) &&
                    !string.Equals(type, "game", StringComparison.OrdinalIgnoreCase)) continue;

                var parsed = ParseAppManifest(content);
                if (parsed is null) continue;

                // Skip known non-game Steam entries by install directory name.
                // These are tools/benchmarks that Steam classifies as "game" in their ACF
                // but should not be treated as games for HDR purposes.
                if (SteamExcludedInstallDirs.Contains(parsed.Value.installDir)) continue;

                var fullPath = Path.Combine(appsDir, "common", parsed.Value.installDir);
                if (Directory.Exists(fullPath))
                    yield return new GameInfo(parsed.Value.name, fullPath, "Steam");
            }
        }
    }

    [GeneratedRegex(@"""path""\s+""([^""]+)""")]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex(@"""name""\s+""([^""]+)""")]
    private static partial Regex AcfNameRegex();

    [GeneratedRegex(@"""installdir""\s+""([^""]+)""")]
    private static partial Regex AcfInstallDirRegex();

    [GeneratedRegex(@"""type""\s+""([^""]+)""")]
    private static partial Regex AcfTypeRegex();

    /// <summary>
    /// Parses a <c>libraryfolders.vdf</c> and returns all unique library root paths.
    /// Public for unit testing.
    /// </summary>
    public static IReadOnlyList<string> ParseLibraryRoots(string vdfContent)
    {
        // The VDF already lists all library roots including the Steam install dir itself —
        // no need to prepend steamPath separately (avoids duplicating the default library)
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPathRegex().Matches(vdfContent))
            roots.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        return [.. roots];
    }

    /// <summary>
    /// Parses a Steam <c>appmanifest_*.acf</c> file and returns (name, installDir),
    /// or null if either field is missing. Public for unit testing.
    /// </summary>
    public static (string name, string installDir)? ParseAppManifest(string content)
    {
        var name       = AcfNameRegex().Match(content).Groups[1].Value;
        var installDir = AcfInstallDirRegex().Match(content).Groups[1].Value;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir)) return null;
        return (name, installDir);
    }

    private static IEnumerable<GameInfo> ScanEpic()
    {
        var manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestsDir)) yield break;

        foreach (var item in Directory.GetFiles(manifestsDir, "*.item"))
        {
            GameInfo? game = null;
            try { game = ParseEpicManifest(File.ReadAllText(item)); }
            catch { }
            // Skip stale manifests left behind by uninstalled games (matches
            // the Directory.Exists guards in ScanSteam and ScanXbox)
            if (game is not null && Directory.Exists(game.InstallPath))
                yield return game;
        }
    }

    /// <summary>
    /// Parses an Epic Games <c>.item</c> manifest JSON and returns a <see cref="GameInfo"/>,
    /// or null if required fields are missing. Path existence is NOT checked here.
    /// Public for unit testing.
    /// </summary>
    public static GameInfo? ParseEpicManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("DisplayName",    out var nameProp)) return null;
        if (!root.TryGetProperty("InstallLocation", out var pathProp)) return null;
        var name = nameProp.GetString();
        var path = pathProp.GetString();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return null;
        return new GameInfo(name, path, "Epic");
    }

    private static IEnumerable<GameInfo> ScanXbox()
    {
        // HKLM\SOFTWARE\Microsoft\GamingServices\GameConfig has one subkey per installed
        // Xbox / Game Pass game; the subkey name is the MSIX package full name.
        // Install path: C:\Program Files\WindowsApps\{packageFullName}
        // Display name: read from AppxManifest.xml inside that directory.
        using var configKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\GamingServices\GameConfig");
        if (configKey is null) yield break;

        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");

        foreach (var packageFullName in configKey.GetSubKeyNames())
        {
            var installPath  = Path.Combine(windowsApps, packageFullName);
            var manifestPath = Path.Combine(installPath, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) continue;

            var name = ReadDisplayNameFromManifest(manifestPath) ?? FriendlyNameFromPackage(packageFullName);
            yield return new GameInfo(name, installPath, "Xbox");
        }
    }

    public static string? ReadDisplayNameFromManifest(string manifestPath)
    {
        try
        {
            var doc  = System.Xml.Linq.XDocument.Load(manifestPath);
            var ns   = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
            var name = doc.Root
                ?.Element(ns + "Properties")
                ?.Element(ns + "DisplayName")
                ?.Value;

            // Many MSIX manifests use localized resource references (ms-resource:AppName)
            // rather than plain text. Fall back to the package name in that case.
            if (name is null || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return null;

            return name;
        }
        catch { return null; }
    }

    /// <summary>
    /// Derives a human-readable name from an MSIX package full name
    /// by stripping the publisher prefix and version/arch/hash suffix.
    /// e.g. "Atari.TotalChaos_1.2.2.0_x64__xka83p2csqhz2" → "TotalChaos"
    /// </summary>
    public static string FriendlyNameFromPackage(string packageFullName)
    {
        // Format: Publisher.Name_Version_Arch_ResourceId_PublisherId
        var withoutSuffix = packageFullName.Split('_')[0]; // "Publisher.Name"
        var dotIdx = withoutSuffix.IndexOf('.');
        return dotIdx >= 0 ? withoutSuffix[(dotIdx + 1)..] : withoutSuffix;
    }
}
