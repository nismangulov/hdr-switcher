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

    public void LogGameStarted(GameInfo game, bool hdrWasOn)
    {
        Write($"STARTED [{game.Source}] {game.Name}");
        if (hdrWasOn)
            Write($"  → HDR is already ON — would do nothing");
        else
            Write($"  → HDR is OFF — would enable HDR (saving state: OFF)");
    }

    public void LogGameExited(GameInfo game, bool hdrWasOn)
    {
        Write($"EXITED  [{game.Source}] {game.Name}");
        if (hdrWasOn)
            Write($"  → HDR was already ON before game — would do nothing");
        else
            Write($"  → HDR was OFF before game — would disable HDR (restoring state: OFF)");
    }

    public void LogLibrary(IReadOnlyList<GameInfo> games)
    {
        var bySteam = games.Count(g => g.Source == "Steam");
        var byEpic  = games.Count(g => g.Source == "Epic");
        var byXbox  = games.Count(g => g.Source == "Xbox");
        Write($"Library loaded — {games.Count} games (Steam: {bySteam}, Epic: {byEpic}, Xbox: {byXbox})");
        foreach (var game in games.OrderBy(g => g.Source).ThenBy(g => g.Name))
            Write($"  [{game.Source}] {game.Name}  →  {game.InstallPath}");
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
