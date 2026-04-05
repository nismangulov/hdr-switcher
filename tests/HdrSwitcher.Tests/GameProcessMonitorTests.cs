namespace HdrSwitcher.Tests;

public class GameProcessMonitorTests
{
    private static readonly List<GameInfo> Games =
    [
        new("Elden Ring",      @"C:\Games\Elden Ring",      "Steam"),
        new("Elden Ring GOTY", @"C:\Games\Elden Ring GOTY", "Steam"),
        new("Hades II",        @"D:\SteamLibrary\steamapps\common\Hades II", "Steam"),
    ];

    // IsUnderDirectory

    [Theory]
    [InlineData(@"C:\Games\Elden Ring\eldenring.exe",      @"C:\Games\Elden Ring",      true)]
    [InlineData(@"C:\Games\Elden Ring GOTY\eldenring.exe", @"C:\Games\Elden Ring GOTY", true)]
    [InlineData(@"C:\Games\Elden Ring GOTY\eldenring.exe", @"C:\Games\Elden Ring",      false)] // no false prefix match
    [InlineData(@"C:\Games\Elden Ring\eldenring.exe",      @"C:\Games\Elden Ring GOTY", false)]
    [InlineData(@"C:\Games\Elden Ring",                    @"C:\Games\Elden Ring",      true)]  // exact match
    [InlineData(@"D:\SteamLibrary\steamapps\common\Hades II\hades2.exe", @"D:\SteamLibrary\steamapps\common\Hades II", true)]
    public void IsUnderDirectory_boundary_check(string exePath, string dirPath, bool expected)
    {
        Assert.Equal(expected, GameProcessMonitor.IsUnderDirectory(exePath, dirPath));
    }

    // Match

    [Fact]
    public void Match_returns_correct_game_by_path()
    {
        var result = GameProcessMonitor.Match(Games, @"C:\Games\Elden Ring GOTY\eldenring.exe");
        Assert.NotNull(result);
        Assert.Equal("Elden Ring GOTY", result.Name);
    }

    [Fact]
    public void Match_does_not_confuse_similar_prefixes()
    {
        // "Elden Ring" install path must not match a process inside "Elden Ring GOTY"
        var result = GameProcessMonitor.Match(Games, @"C:\Games\Elden Ring GOTY\eldenring.exe");
        Assert.NotEqual("Elden Ring", result?.Name);
    }

    [Fact]
    public void Match_returns_null_for_unknown_process()
    {
        var result = GameProcessMonitor.Match(Games, @"C:\Windows\System32\notepad.exe");
        Assert.Null(result);
    }

    [Fact]
    public void Match_returns_null_for_empty_install_path()
    {
        var games = new List<GameInfo> { new("Ghost", string.Empty, "Steam") };
        var result = GameProcessMonitor.Match(games, @"C:\Games\Ghost\ghost.exe");
        Assert.Null(result);
    }
}
