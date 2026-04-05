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
