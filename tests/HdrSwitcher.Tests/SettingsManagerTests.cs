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
