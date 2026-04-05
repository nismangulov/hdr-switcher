namespace HdrSwitcher;

public class GameLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public string LogPath { get; }

    public GameLogger() : this(Path.Combine(AppContext.BaseDirectory, "game-log.txt")) { }

    public GameLogger(string logPath)
    {
        LogPath = logPath;
        _writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
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

    public void LogScanError(string source, Exception ex)
        => Write($"WARN    [{source}] scan failed: {ex.GetType().Name}: {ex.Message}");

    public void LogRescan(IReadOnlyList<GameInfo> newGames)
    {
        if (newGames.Count == 0)
            Write("Rescan complete — no new games found");
        else
        {
            Write($"Rescan complete — {newGames.Count} new game(s) found:");
            foreach (var game in newGames.OrderBy(g => g.Source).ThenBy(g => g.Name))
                Write($"  [{game.Source}] {game.Name}  →  {game.InstallPath}");
        }
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
        {
            if (_disposed) return;
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === HDR Switcher stopped ===");
            _writer.Dispose();
        }
    }
}
