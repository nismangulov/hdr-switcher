using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HdrSwitcher;

public record GameInfo(string Name, string InstallPath, string Source);

public class GameLibraryScanner
{
    public List<GameInfo> ScanAll()
    {
        var games = new List<GameInfo>();
        try { games.AddRange(ScanSteam()); } catch { }
        try { games.AddRange(ScanEpic()); } catch { }
        try { games.AddRange(ScanXbox()); } catch { }
        return games;
    }

    private static IEnumerable<GameInfo> ScanSteam()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steamPath) yield break;

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        var vdf = File.ReadAllText(vdfPath);

        // Collect all library roots (Steam dir itself + additional library paths from vdf)
        var libraryRoots = new List<string> { steamPath };
        foreach (Match m in Regex.Matches(vdf, @"""path""\s+""([^""]+)"""))
            libraryRoots.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var root in libraryRoots)
        {
            var appsDir = Path.Combine(root, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (var acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
            {
                var content = File.ReadAllText(acf);
                var name       = Regex.Match(content, @"""name""\s+""([^""]+)""").Groups[1].Value;
                var installDir = Regex.Match(content, @"""installdir""\s+""([^""]+)""").Groups[1].Value;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir)) continue;

                var fullPath = Path.Combine(appsDir, "common", installDir);
                if (Directory.Exists(fullPath))
                    yield return new GameInfo(name, fullPath, "Steam");
            }
        }
    }

    private static IEnumerable<GameInfo> ScanXbox()
    {
        // Win32 PC games from Xbox app install to a user-configured root (default C:\XboxGames).
        // Each game lives in {root}\{GameName}\Content\.
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Read user-configured install root from GamingServices registry
        using var gsKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\GamingServices");
        if (gsKey?.GetValue("PackageRoot") is string regRoot && Directory.Exists(regRoot))
            roots.Add(regRoot);

        // Always check the default location as well
        roots.Add(@"C:\XboxGames");

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var gameDir in Directory.GetDirectories(root))
            {
                var name        = Path.GetFileName(gameDir);
                var contentPath = Path.Combine(gameDir, "Content");
                var installPath = Directory.Exists(contentPath) ? contentPath : gameDir;
                yield return new GameInfo(name, installPath, "Xbox");
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
}
