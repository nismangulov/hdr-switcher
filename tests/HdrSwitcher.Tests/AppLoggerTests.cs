namespace HdrSwitcher.Tests;

public class AppLoggerTests : IDisposable
{
    private readonly string _logPath;
    private readonly AppLogger _logger;

    public AppLoggerTests()
    {
        _logPath = Path.GetTempFileName();
        File.Delete(_logPath); // AppLogger creates it fresh
        _logger = new AppLogger(_logPath);
    }

    [Fact]
    public void Startup_writes_started_header()
    {
        _logger.Dispose();
        var lines = File.ReadAllLines(_logPath);
        Assert.Contains(lines, l => l.Contains("HDR Switcher started"));
    }

    [Fact]
    public void Dispose_writes_stopped_header()
    {
        _logger.Dispose();
        var lines = File.ReadAllLines(_logPath);
        Assert.Contains(lines, l => l.Contains("HDR Switcher stopped"));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        _logger.Dispose();
        var ex = Record.Exception(() => _logger.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void LogGameStarted_some_displays_off_logs_would_enable()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        var displays = new List<DisplayInfo>
        {
            new(1, "LG OLED", HdrEnabled: true,  IsPrimary: true),
            new(2, "Dell",    HdrEnabled: false, IsPrimary: false),
        };
        _logger.LogGameStarted(game, displays);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("STARTED", content);
        Assert.Contains("would enable HDR on", content);
        Assert.Contains("Dell", content);
    }

    [Fact]
    public void LogGameStarted_all_displays_on_logs_do_nothing()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        var displays = new List<DisplayInfo>
        {
            new(1, "LG OLED", HdrEnabled: true, IsPrimary: true),
        };
        _logger.LogGameStarted(game, displays);
        _logger.Dispose();

        Assert.Contains("would do nothing", File.ReadAllText(_logPath));
    }

    [Fact]
    public void LogGameExited_with_pre_game_state_logs_would_restore()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        var preGameDisplays = new List<DisplayInfo>
        {
            new(1, "LG OLED", HdrEnabled: false, IsPrimary: true),
        };
        _logger.LogGameExited(game, preGameDisplays);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("EXITED", content);
        Assert.Contains("would restore", content);
        Assert.Contains("LG OLED", content);
    }

    [Fact]
    public void LogGameExited_null_pre_game_state_logs_skip_restore()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        _logger.LogGameExited(game, null);
        _logger.Dispose();

        Assert.Contains("would skip restore", File.ReadAllText(_logPath));
    }

    [Fact]
    public void Write_after_dispose_does_not_throw()
    {
        _logger.Dispose();
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        var ex = Record.Exception(() => _logger.LogGameStarted(game, []));
        Assert.Null(ex);
    }

    [Fact]
    public void LogHdrStatus_writes_HDR_tag_and_display_state()
    {
        var displays = new List<DisplayInfo>
        {
            new(1, "LG OLED", HdrEnabled: true,  IsPrimary: true),
            new(2, "Dell",    HdrEnabled: false, IsPrimary: false),
        };
        _logger.LogHdrStatus("startup", displays);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("HDR     [startup]", content);
        Assert.Contains("LG OLED: ON (primary)", content);
        Assert.Contains("Dell: OFF", content);
    }

    [Fact]
    public void LogHdrStatus_handles_empty_display_list()
    {
        _logger.LogHdrStatus("test", []);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("HDR     [test] no displays", content);
    }

    [Fact]
    public void LogHdrStatus_marks_primary_display()
    {
        var displays = new List<DisplayInfo> { new(1, "Monitor", HdrEnabled: false, IsPrimary: true) };
        _logger.LogHdrStatus("check", displays);
        _logger.Dispose();

        Assert.Contains("(primary)", File.ReadAllText(_logPath));
    }

    [Fact]
    public void LogScanError_writes_WARN_tag_with_source_and_exception()
    {
        var ex = new InvalidOperationException("disk full");
        _logger.LogScanError("Steam", ex);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("WARN", content);
        Assert.Contains("[Steam]", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("disk full", content);
    }

    [Fact]
    public void LogRescan_no_changes_writes_no_changes()
    {
        _logger.LogRescan([]);
        _logger.Dispose();

        Assert.Contains("no changes", File.ReadAllText(_logPath));
    }

    [Fact]
    public void LogRescan_with_new_games_writes_count_and_names()
    {
        var games = new List<GameInfo>
        {
            new("Elden Ring", @"C:\Games\EldenRing", "Steam"),
            new("Fortnite",   @"C:\Games\Fortnite",  "Epic"),
        };
        _logger.LogRescan(games);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("2 new game(s) found", content);
        Assert.Contains("Elden Ring", content);
        Assert.Contains("Fortnite", content);
    }

    [Fact]
    public void LogRescan_with_removed_games_writes_removed_count_and_names()
    {
        var removed = new List<GameInfo>
        {
            new("Cyberpunk 2077", @"C:\Games\Cyberpunk", "Steam"),
        };
        _logger.LogRescan([], removed);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("1 game(s) removed", content);
        Assert.Contains("Cyberpunk 2077", content);
    }

    [Fact]
    public void LogLibrary_writes_total_count_and_per_source_breakdown()
    {
        var games = new List<GameInfo>
        {
            new("Game A", @"C:\Steam\A",  "Steam"),
            new("Game B", @"C:\Steam\B",  "Steam"),
            new("Game C", @"C:\Epic\C",   "Epic"),
            new("Game D", @"C:\Xbox\D",   "Xbox"),
        };
        _logger.LogLibrary(games);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("LIBRARY", content);
        Assert.Contains("4 games", content);
        Assert.Contains("Steam: 2", content);
        Assert.Contains("Epic: 1", content);
        Assert.Contains("Xbox: 1", content);
    }

    [Fact]
    public void LogLibrary_lists_each_game_with_source_and_path()
    {
        var games = new List<GameInfo>
        {
            new("Portal 2", @"C:\Steam\Portal2", "Steam"),
        };
        _logger.LogLibrary(games);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("[Steam]", content);
        Assert.Contains("Portal 2", content);
        Assert.Contains(@"C:\Steam\Portal2", content);
    }

    public void Dispose()
    {
        try { _logger.Dispose(); } catch { }
        if (File.Exists(_logPath)) File.Delete(_logPath);
    }
}
