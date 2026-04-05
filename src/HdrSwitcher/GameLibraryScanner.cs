using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HdrSwitcher;

public record GameInfo(string Name, string InstallPath, string Source);

public class GameLibraryScanner
{
    private readonly AppLogger? _logger;

    public GameLibraryScanner(AppLogger? logger = null) => _logger = logger;

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

        var vdf = File.ReadAllText(vdfPath);

        // The VDF already lists all library roots including the Steam install dir itself —
        // no need to prepend steamPath separately (avoids duplicating the default library)
        var libraryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(vdf, @"""path""\s+""([^""]+)"""))
            libraryRoots.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var root in libraryRoots)
        {
            var appsDir = Path.Combine(root, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (var acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
            {
                var content    = File.ReadAllText(acf);
                var name       = Regex.Match(content, @"""name""\s+""([^""]+)""").Groups[1].Value;
                var installDir = Regex.Match(content, @"""installdir""\s+""([^""]+)""").Groups[1].Value;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir)) continue;

                var fullPath = Path.Combine(appsDir, "common", installDir);
                if (Directory.Exists(fullPath))
                    yield return new GameInfo(name, fullPath, "Steam");
            }
        }
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
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;
                var name = root.GetProperty("DisplayName").GetString();
                var path = root.GetProperty("InstallLocation").GetString();
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path) && Directory.Exists(path))
                    game = new GameInfo(name, path, "Epic");
            }
            catch { }
            if (game is not null) yield return game;
        }
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

    private static string? ReadDisplayNameFromManifest(string manifestPath)
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
    private static string FriendlyNameFromPackage(string packageFullName)
    {
        // Format: Publisher.Name_Version_Arch_ResourceId_PublisherId
        var withoutSuffix = packageFullName.Split('_')[0]; // "Publisher.Name"
        var dotIdx = withoutSuffix.IndexOf('.');
        return dotIdx >= 0 ? withoutSuffix[(dotIdx + 1)..] : withoutSuffix;
    }
}
