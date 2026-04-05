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
    public void LogGameStarted_hdr_off_logs_would_enable()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        _logger.LogGameStarted(game, hdrWasOn: false);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("STARTED", content);
        Assert.Contains("would enable HDR", content);
    }

    [Fact]
    public void LogGameStarted_hdr_on_logs_do_nothing()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        _logger.LogGameStarted(game, hdrWasOn: true);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("already ON", content);
    }

    [Fact]
    public void LogGameExited_hdr_was_off_logs_would_disable()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        _logger.LogGameExited(game, hdrWasOn: false);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("EXITED", content);
        Assert.Contains("would disable HDR", content);
    }

    [Fact]
    public void LogGameExited_hdr_was_on_logs_do_nothing()
    {
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        _logger.LogGameExited(game, hdrWasOn: true);
        _logger.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("already ON before game", content);
    }

    [Fact]
    public void Write_after_dispose_does_not_throw()
    {
        _logger.Dispose();
        var game = new GameInfo("TestGame", @"C:\Games\Test", "Steam");
        var ex = Record.Exception(() => _logger.LogGameStarted(game, false));
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

    public void Dispose()
    {
        try { _logger.Dispose(); } catch { }
        if (File.Exists(_logPath)) File.Delete(_logPath);
    }
}
