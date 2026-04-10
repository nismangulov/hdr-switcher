using System.Drawing;

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

        mgr.SaveWindowBounds(new Rectangle(100, 200, 960, 680));

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

        mgr.SaveWindowBounds(new Rectangle(0, 0, 960, 680));

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

        mgr.SaveWindowBounds(new Rectangle(50, 60, 800, 600));

        Assert.NotNull(mgr.WindowBounds);
        Assert.Equal(50, mgr.WindowBounds!.X);
        Assert.Equal(60, mgr.WindowBounds.Y);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
