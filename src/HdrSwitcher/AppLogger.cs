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

    public void LogGameStarted(GameInfo game, IReadOnlyList<DisplayInfo> displays)
    {
        Write($"STARTED [{game.Source}] {game.Name}");
        var summary = DisplaySummary(displays);
        var toEnable = displays.Where(d => !d.HdrEnabled).Select(d => d.Name).ToList();
        if (toEnable.Count == 0)
            Write($"  → {summary} — HDR already ON on all, would do nothing");
        else
            Write($"  → {summary} — would enable HDR on: {string.Join(", ", toEnable)}");
    }

    public void LogGameExited(GameInfo game, IReadOnlyList<DisplayInfo>? preGameDisplays)
    {
        Write($"EXITED  [{game.Source}] {game.Name}");
        if (preGameDisplays is null)
            Write($"  → no pre-game state recorded (game was running at startup) — would skip restore");
        else if (preGameDisplays.All(d => d.HdrEnabled))
            Write($"  → HDR was already ON before game — would do nothing");
        else
            Write($"  → would restore: {DisplaySummary(preGameDisplays)}");
    }

    public void LogSeedGames(IReadOnlyList<(GameInfo Game, int Pid)> runningGames)
    {
        if (runningGames.Count == 0)
        {
            Write("SEED    no games already running");
            return;
        }
        Write($"SEED    {runningGames.Count} game(s) already running at startup:");
        foreach (var (game, pid) in runningGames.OrderBy(x => x.Game.Source).ThenBy(x => x.Game.Name))
            Write($"  [{game.Source}] {game.Name}  (pid {pid})");
    }

    private static string DisplaySummary(IReadOnlyList<DisplayInfo> displays) =>
        string.Join(", ", displays.Select(
            d => $"{d.Name}: {(d.HdrEnabled ? "ON" : "OFF")}{(d.IsPrimary ? " (primary)" : "")}"));

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
