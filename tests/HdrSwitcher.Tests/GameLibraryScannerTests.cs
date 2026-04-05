namespace HdrSwitcher.Tests;

public class GameLibraryScannerTests
{
    // ── ParseLibraryRoots ───────────────────────────────────────────────────────

    [Fact]
    public void ParseLibraryRoots_extracts_single_path()
    {
        var vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"    "C:\\Program Files (x86)\\Steam"
                }
            }
            """;

        var roots = GameLibraryScanner.ParseLibraryRoots(vdf);

        Assert.Single(roots);
        Assert.Contains(@"C:\Program Files (x86)\Steam", roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseLibraryRoots_extracts_multiple_paths()
    {
        var vdf = """
            "libraryfolders"
            {
                "0" { "path"    "C:\\Program Files (x86)\\Steam" }
                "1" { "path"    "D:\\SteamLibrary" }
                "2" { "path"    "E:\\Games\\Steam" }
            }
            """;

        var roots = GameLibraryScanner.ParseLibraryRoots(vdf);

        Assert.Equal(3, roots.Count);
        Assert.Contains(@"D:\SteamLibrary", roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseLibraryRoots_deduplicates_identical_paths()
    {
        var vdf = """
            "libraryfolders"
            {
                "0" { "path"    "C:\\Steam" }
                "1" { "path"    "C:\\Steam" }
            }
            """;

        var roots = GameLibraryScanner.ParseLibraryRoots(vdf);

        Assert.Single(roots);
    }

    [Fact]
    public void ParseLibraryRoots_unescapes_double_backslash()
    {
        // VDF files use \\ as path separator
        var vdf = "\"path\"\t\"D:\\\\SteamLibrary\"";

        var roots = GameLibraryScanner.ParseLibraryRoots(vdf);

        Assert.Contains(@"D:\SteamLibrary", roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseLibraryRoots_returns_empty_for_empty_vdf()
    {
        var roots = GameLibraryScanner.ParseLibraryRoots(string.Empty);
        Assert.Empty(roots);
    }

    // ── ParseAppManifest ────────────────────────────────────────────────────────

    [Fact]
    public void ParseAppManifest_returns_name_and_installdir()
    {
        var acf = """
            "AppState"
            {
                "appid"       "620"
                "name"        "Portal 2"
                "installdir"  "Portal 2"
            }
            """;

        var result = GameLibraryScanner.ParseAppManifest(acf);

        Assert.NotNull(result);
        Assert.Equal("Portal 2", result.Value.name);
        Assert.Equal("Portal 2", result.Value.installDir);
    }

    [Fact]
    public void ParseAppManifest_returns_null_when_name_missing()
    {
        var acf = "\"AppState\" { \"installdir\" \"Portal 2\" }";
        Assert.Null(GameLibraryScanner.ParseAppManifest(acf));
    }

    [Fact]
    public void ParseAppManifest_returns_null_when_installdir_missing()
    {
        var acf = "\"AppState\" { \"name\" \"Portal 2\" }";
        Assert.Null(GameLibraryScanner.ParseAppManifest(acf));
    }

    [Fact]
    public void ParseAppManifest_returns_null_for_empty_content()
    {
        Assert.Null(GameLibraryScanner.ParseAppManifest(string.Empty));
    }

    // ── ParseEpicManifest ───────────────────────────────────────────────────────

    [Fact]
    public void ParseEpicManifest_extracts_name_and_path()
    {
        var json = """
            {
                "DisplayName": "Fortnite",
                "InstallLocation": "C:\\Epic\\Fortnite"
            }
            """;

        var game = GameLibraryScanner.ParseEpicManifest(json);

        Assert.NotNull(game);
        Assert.Equal("Fortnite", game!.Name);
        Assert.Equal(@"C:\Epic\Fortnite", game.InstallPath);
        Assert.Equal("Epic", game.Source);
    }

    [Fact]
    public void ParseEpicManifest_returns_null_when_DisplayName_empty()
    {
        var json = """{ "DisplayName": "", "InstallLocation": "C:\\Games\\Test" }""";
        Assert.Null(GameLibraryScanner.ParseEpicManifest(json));
    }

    [Fact]
    public void ParseEpicManifest_returns_null_when_InstallLocation_empty()
    {
        var json = """{ "DisplayName": "Game", "InstallLocation": "" }""";
        Assert.Null(GameLibraryScanner.ParseEpicManifest(json));
    }

    [Fact]
    public void ParseEpicManifest_returns_null_when_DisplayName_key_missing()
    {
        var json = """{ "InstallLocation": "C:\\Games\\Test" }""";
        Assert.Null(GameLibraryScanner.ParseEpicManifest(json));
    }

    [Fact]
    public void ParseEpicManifest_returns_null_when_InstallLocation_key_missing()
    {
        var json = """{ "DisplayName": "Game" }""";
        Assert.Null(GameLibraryScanner.ParseEpicManifest(json));
    }

    [Fact]
    public void ParseEpicManifest_throws_on_invalid_json()
    {
        Assert.ThrowsAny<Exception>(() => GameLibraryScanner.ParseEpicManifest("not-json"));
    }

    // ── ReadDisplayNameFromManifest ─────────────────────────────────────────────

    [Fact]
    public void ReadDisplayNameFromManifest_reads_plain_text_name()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Properties>
                <DisplayName>Total Chaos</DisplayName>
              </Properties>
            </Package>
            """;
        var path = WriteTemp(xml, ".xml");
        try
        {
            var name = GameLibraryScanner.ReadDisplayNameFromManifest(path);
            Assert.Equal("Total Chaos", name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadDisplayNameFromManifest_returns_null_for_ms_resource_values()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Properties>
                <DisplayName>ms-resource:AppName</DisplayName>
              </Properties>
            </Package>
            """;
        var path = WriteTemp(xml, ".xml");
        try
        {
            Assert.Null(GameLibraryScanner.ReadDisplayNameFromManifest(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadDisplayNameFromManifest_returns_null_for_missing_file()
    {
        Assert.Null(GameLibraryScanner.ReadDisplayNameFromManifest(@"C:\nonexistent\path\AppxManifest.xml"));
    }

    // ── FriendlyNameFromPackage ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Atari.TotalChaos_1.2.2.0_x64__xka83p2csqhz2", "TotalChaos")]
    [InlineData("Microsoft.XboxApp_48.49.31001.0_x64__8wekyb3d8bbwe", "XboxApp")]
    [InlineData("Publisher.My Game Title_1.0.0_x64__abc123", "My Game Title")]
    [InlineData("NoDot_1.0.0_x64__abc123", "NoDot")]  // no publisher dot — returns full first segment
    public void FriendlyNameFromPackage_strips_publisher_and_version(string packageName, string expected)
        => Assert.Equal(expected, GameLibraryScanner.FriendlyNameFromPackage(packageName));

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string WriteTemp(string content, string extension)
    {
        // GetRandomFileName does not create a file, avoiding the orphaned .tmp
        // file that GetTempFileName() would leave behind.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + extension);
        File.WriteAllText(path, content);
        return path;
    }
}

