using System.Management;

namespace HdrSwitcher;

/// <summary>
/// Watches for game process starts and exits using WMI polling (no admin required).
/// Fires onGameStart/onGameExit when a process whose path falls inside a known game
/// install directory is detected.
/// </summary>
public class GameProcessMonitor : IDisposable
{
    private readonly IReadOnlyList<GameInfo> _games;
    private readonly Action<GameInfo> _onGameStart;
    private readonly Action<GameInfo> _onGameExit;
    private readonly ManagementEventWatcher _startWatcher;
    private readonly ManagementEventWatcher _stopWatcher;

    // pid → (game, processName) — processName guards against PID reuse
    private readonly Dictionary<int, (GameInfo Game, string ProcessName)> _activeGames = new();
    private readonly object _lock = new();

    public GameProcessMonitor(
        IReadOnlyList<GameInfo> games,
        Action<GameInfo> onGameStart,
        Action<GameInfo> onGameExit)
    {
        _games       = games;
        _onGameStart = onGameStart;
        _onGameExit  = onGameExit;

        // WITHIN 3 = WMI polls every 3 seconds; no elevated privileges needed
        _startWatcher = new ManagementEventWatcher(new WqlEventQuery(
            "SELECT * FROM __InstanceCreationEvent WITHIN 3 WHERE TargetInstance ISA 'Win32_Process'"));
        _startWatcher.EventArrived += OnProcessCreated;
        _startWatcher.Start();

        _stopWatcher = new ManagementEventWatcher(new WqlEventQuery(
            "SELECT * FROM __InstanceDeletionEvent WITHIN 3 WHERE TargetInstance ISA 'Win32_Process'"));
        _stopWatcher.EventArrived += OnProcessDeleted;
        _stopWatcher.Start();
    }

    private void OnProcessCreated(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var proc    = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var exePath = proc["ExecutablePath"]?.ToString();
            if (string.IsNullOrEmpty(exePath)) return;

            var game = Match(exePath);
            if (game is null) return;

            var pid     = Convert.ToInt32(proc["ProcessId"]);
            var procName = proc["Name"]?.ToString() ?? string.Empty;

            bool firstProcess;
            lock (_lock)
            {
                firstProcess = !_activeGames.ContainsKey(pid);
                _activeGames[pid] = (game, procName);
            }

            // Only fire the event for the first process of this game
            // (launchers and anti-cheat engines can spawn multiple tracked processes)
            if (firstProcess)
                _onGameStart(game);
        }
        catch { }
    }

    private void OnProcessDeleted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var proc     = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid      = Convert.ToInt32(proc["ProcessId"]);
            var procName = proc["Name"]?.ToString() ?? string.Empty;

            GameInfo? game;
            lock (_lock)
            {
                if (!_activeGames.TryGetValue(pid, out var entry)) return;

                // Guard against PID reuse: if the process name doesn't match
                // what we recorded at start, this is a different process
                if (!string.Equals(entry.ProcessName, procName, StringComparison.OrdinalIgnoreCase))
                    return;

                _activeGames.Remove(pid);
                game = entry.Game;
            }

            _onGameExit(game);
        }
        catch { }
    }

    /// <summary>
    /// Returns the first game whose install path is a directory ancestor of <paramref name="exePath"/>.
    /// </summary>
    public static GameInfo? Match(IReadOnlyList<GameInfo> games, string exePath) =>
        games.FirstOrDefault(g =>
            !string.IsNullOrEmpty(g.InstallPath) &&
            IsUnderDirectory(exePath, g.InstallPath));

    private GameInfo? Match(string exePath) => Match(_games, exePath);

    /// <summary>
    /// Returns true when <paramref name="exePath"/> is inside <paramref name="dirPath"/>,
    /// enforcing a directory-separator boundary to prevent false prefix matches
    /// (e.g. "Elden Ring" matching "Elden Ring GOTY").
    /// </summary>
    public static bool IsUnderDirectory(string exePath, string dirPath)
    {
        if (!exePath.StartsWith(dirPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (exePath.Length == dirPath.Length) return true;
        return exePath[dirPath.Length] is '\\' or '/';
    }

    public void Dispose()
    {
        _startWatcher.Stop();
        _startWatcher.Dispose();
        _stopWatcher.Stop();
        _stopWatcher.Dispose();
    }
}
