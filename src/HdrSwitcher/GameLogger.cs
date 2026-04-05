namespace HdrSwitcher;

public class GameLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public string LogPath { get; }

    public GameLogger()
    {
        var dir = AppContext.BaseDirectory;
        LogPath = Path.Combine(dir, "game-log.txt");
        _writer = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        Write("=== HDR Switcher started ===");
    }

    public void Log(string eventType, GameInfo game)
        => Write($"{eventType,-7} [{game.Source}] {game.Name}");

    public void LogLibrary(IReadOnlyList<GameInfo> games)
    {
        var bySteam = games.Count(g => g.Source == "Steam");
        var byEpic  = games.Count(g => g.Source == "Epic");
        Write($"Library loaded — {games.Count} games (Steam: {bySteam}, Epic: {byEpic})");
    }

    private void Write(string message)
    {
        lock (_lock)
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    }

    public void Dispose()
    {
        Write("=== HDR Switcher stopped ===");
        _writer.Dispose();
    }
}
