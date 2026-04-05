namespace HdrSwitcher;

/// <summary>
/// Owns all game-related logic: library scanning, process monitoring,
/// pre-game HDR state snapshots, and the 30-minute rescan timer.
/// Must be constructed on the UI thread (GameProcessMonitor requirement).
/// </summary>
public class GameCoordinator : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly HdrController   _hdr;
    private readonly AppLogger       _logger;

    private volatile List<GameInfo> _currentGames = [];
    private GameFilter              _filter;
    private GameProcessMonitor?     _monitor;
    private System.Threading.Timer? _rescanTimer;

    // install path → display snapshot taken at game-start
    private readonly Dictionary<string, IReadOnlyList<DisplayInfo>> _preGameHdrState = new();
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Fired after every rescan (including the initial scan on startup).</summary>
    public event Action? LibraryChanged;

    /// <summary>Fired when a game process is detected.</summary>
    public event Action<GameInfo>? GameStarted;

    /// <summary>Fired when a tracked game process exits.</summary>
    public event Action<GameInfo>? GameExited;

    public GameCoordinator(SettingsManager settings, HdrController hdr, AppLogger logger)
    {
        _settings = settings;
        _hdr      = hdr;
        _logger   = logger;
        _filter   = new GameFilter(settings);

        var syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "GameCoordinator must be constructed on the UI thread.");

        // Scan game libraries on background thread; create monitor on UI thread
        Task.Run(() =>
        {
            var games = new GameLibraryScanner(_logger, _filter).ScanAll();
            _logger.LogLibrary(games);

            syncContext.Post(_ =>
            {
                _currentGames = games;

                _monitor = new GameProcessMonitor(
                    games, _filter, OnGameStart, OnGameExit, _logger);

                var running = _monitor.SeedRunningGames();
                _logger.LogSeedGames(running);

                _rescanTimer = new System.Threading.Timer(
                    _ => Rescan(), null,
                    TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

                LibraryChanged?.Invoke();
            }, null);
        });
    }

    /// <summary>
    /// Rescans game libraries, updates the monitor, and fires LibraryChanged.
    /// Safe to call from any thread.
    /// </summary>
    public void Rescan()
    {
        if (_cts.IsCancellationRequested) return;

        _filter = new GameFilter(_settings); // pick up latest settings
        var updated = new GameLibraryScanner(_logger, _filter).ScanAll();

        var snapshot      = _currentGames;
        var existingPaths = snapshot.Select(g => g.InstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatedPaths  = updated.Select(g => g.InstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newGames     = updated .Where(g => !existingPaths.Contains(g.InstallPath)).ToList();
        var removedGames = snapshot.Where(g => !updatedPaths .Contains(g.InstallPath)).ToList();
        _logger.LogRescan(newGames, removedGames);

        if (_cts.IsCancellationRequested) return;

        lock (_stateLock)
        {
            _currentGames = updated;
            _monitor?.UpdateGames(updated, _filter);
        }

        LibraryChanged?.Invoke();
    }

    public IReadOnlyList<GameInfo> GetCurrentGames() => _currentGames;

    public IReadOnlySet<int> GetActiveGamePids() =>
        _monitor?.GetActiveGamePids() ?? (IReadOnlySet<int>)new HashSet<int>();

    private void OnGameStart(GameInfo game)
    {
        var displays = _hdr.GetDisplays();
        lock (_stateLock)
        {
            if (!_preGameHdrState.ContainsKey(game.InstallPath))
                _preGameHdrState[game.InstallPath] = displays;
        }
        _logger.LogGameStarted(game, displays);
        GameStarted?.Invoke(game);
        // TODO: auto-enable HDR here
    }

    private void OnGameExit(GameInfo game)
    {
        Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DisplayInfo>? preGameDisplays;
                lock (_stateLock)
                {
                    _preGameHdrState.TryGetValue(game.InstallPath, out preGameDisplays);
                    _preGameHdrState.Remove(game.InstallPath);
                }
                _logger.LogGameExited(game, preGameDisplays);
                GameExited?.Invoke(game);
                // TODO: auto-restore HDR here
            }
            catch (Exception ex) { _logger.LogScanError("GameExit", ex); }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _rescanTimer?.Dispose();
        _monitor?.Dispose();
    }
}
