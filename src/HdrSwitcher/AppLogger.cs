namespace HdrSwitcher;

public class AppLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public string LogPath { get; }

    public AppLogger() : this(Path.Combine(AppContext.BaseDirectory, "hdr-switcher.log")) { }

    public AppLogger(string logPath)
    {
        LogPath = logPath;
        _writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Write("=== HDR Switcher started ===");
    }

    public void LogHdrStatus(string trigger, IReadOnlyList<DisplayInfo> displays)
    {
        var summary = displays.Count == 0
            ? "no displays"
            : string.Join(", ", displays.Select(
                d => $"{d.Name}: {(d.HdrEnabled ? "ON" : "OFF")}{(d.IsPrimary ? " (primary)" : "")}"));
        Write($"HDR     [{trigger}] {summary}");
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

    public void LogRescan(IReadOnlyList<GameInfo> newGames, IReadOnlyList<GameInfo>? removedGames = null)
    {
        if (newGames.Count == 0 && (removedGames is null || removedGames.Count == 0))
        {
            Write("RESCAN  no changes");
            return;
        }
        if (newGames.Count > 0)
        {
            Write($"RESCAN  {newGames.Count} new game(s) found:");
            foreach (var game in newGames.OrderBy(g => g.Source).ThenBy(g => g.Name))
                Write($"  [{game.Source}] {game.Name}  →  {game.InstallPath}");
        }
        if (removedGames is { Count: > 0 })
        {
            Write($"RESCAN  {removedGames.Count} game(s) removed:");
            foreach (var game in removedGames.OrderBy(g => g.Source).ThenBy(g => g.Name))
                Write($"  [{game.Source}] {game.Name}  →  {game.InstallPath}");
        }
    }

    public void LogLibrary(IReadOnlyList<GameInfo> games)
    {
        var bySteam = games.Count(g => g.Source == "Steam");
        var byEpic  = games.Count(g => g.Source == "Epic");
        var byXbox  = games.Count(g => g.Source == "Xbox");
        Write($"LIBRARY {games.Count} games (Steam: {bySteam}, Epic: {byEpic}, Xbox: {byXbox})");
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
