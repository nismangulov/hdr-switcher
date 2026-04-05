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

    // pid → game, so we can identify the game when the process exits
    private readonly Dictionary<int, GameInfo> _activeGames = new();
    private readonly object _lock = new();

    public GameProcessMonitor(
        IReadOnlyList<GameInfo> games,
        Action<GameInfo> onGameStart,
        Action<GameInfo> onGameExit)
    {
        _games      = games;
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

            var pid = Convert.ToInt32(proc["ProcessId"]);
            lock (_lock) _activeGames[pid] = game;
            _onGameStart(game);
        }
        catch { }
    }

    private void OnProcessDeleted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var proc = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid  = Convert.ToInt32(proc["ProcessId"]);

            GameInfo? game;
            lock (_lock)
            {
                if (!_activeGames.TryGetValue(pid, out game)) return;
                _activeGames.Remove(pid);
            }
            _onGameExit(game);
        }
        catch { }
    }

    private GameInfo? Match(string exePath) =>
        _games.FirstOrDefault(g =>
            !string.IsNullOrEmpty(g.InstallPath) &&
            exePath.StartsWith(g.InstallPath, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _startWatcher.Stop();
        _startWatcher.Dispose();
        _stopWatcher.Stop();
        _stopWatcher.Dispose();
    }
}
