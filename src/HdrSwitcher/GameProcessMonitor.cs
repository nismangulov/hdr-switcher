using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace HdrSwitcher;

/// <summary>
/// Detects game launches via SetWinEventHook (EVENT_SYSTEM_FOREGROUND) — fires instantly
/// when a game window comes to the foreground. Detects exits via WMI process deletion
/// (WITHIN 1 second). Must be constructed on the UI thread.
/// </summary>
public class GameProcessMonitor : IDisposable
{
    #region Win32

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint EVENT_SYSTEM_FOREGROUND           = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT             = 0x0000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE                      = 259;

    #endregion

    // volatile: UpdateGames swaps the reference; OnForegroundChanged reads it on the UI
    // thread. A volatile reference swap is safe and avoids locking on every focus change.
    private volatile List<GameInfo> _games;

    private readonly Action<GameInfo> _onGameStart;
    private readonly Action<GameInfo> _onGameExit;
    private readonly AppLogger? _logger;

    // Kept as a field so the GC does not collect the delegate while the hook is live
    private readonly WinEventDelegate _winEventProc;
    private readonly IntPtr _hookHandle;

    private readonly ManagementEventWatcher _stopWatcher;
    private volatile bool _disposed;

    // pid → (game, processName) — processName guards against PID reuse
    private readonly Dictionary<int, (GameInfo Game, string ProcessName)> _activeGames = new();
    private readonly object _activeGamesLock = new();

    // Reused across foreground-change callbacks (UI thread only) to avoid per-event allocation
    private readonly System.Text.StringBuilder _pathBuffer = new(1024);

    // Executables that live inside game install directories but are not the game itself.
    // Matching any of these prevents a launcher/anti-cheat/crash-reporter from triggering
    // a false game-start event. Extend as new false positives are observed in the log.
    private static readonly HashSet<string> KnownNonGameExes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Launchers
        "launcher.exe", "gamelauncher.exe", "gamelauncherhelper.exe",
        // Crash reporters / handlers
        "crashreporter.exe", "crashpad_handler.exe", "crashhandler.exe",
        "crashhandler64.exe", "crash_reporter.exe", "sentry.exe",
        // Anti-cheat / overlays
        "easyanticheat.exe", "easyanticheat_setup.exe",
        "battleye.exe", "beclauncher.exe",
        "gameoverlayrenderer.exe", "gameoverlayrenderer64.exe",
        // Unreal Engine helpers
        "unrealcefsubprocess.exe",
        // Installers / redistributables
        "vc_redist.x64.exe", "vc_redist.x86.exe",
        "dxsetup.exe", "dxwebsetup.exe",
        "ue4prereqsetup_x64.exe", "ue4prereqsetup_x86.exe",
        "setup.exe", "install.exe", "uninstall.exe", "unins000.exe",
    };

    public GameProcessMonitor(
        List<GameInfo> games,
        Action<GameInfo> onGameStart,
        Action<GameInfo> onGameExit,
        AppLogger? logger = null)
    {
        _games       = games;
        _onGameStart = onGameStart;
        _onGameExit  = onGameExit;
        _logger      = logger;

        // Hook foreground window changes — fires instantly when a game window appears.
        // WINEVENT_OUTOFCONTEXT delivers callbacks on the calling (UI) thread's message pump.
        _winEventProc = OnForegroundChanged; // must be a field — GC safety
        _hookHandle   = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        if (_hookHandle == IntPtr.Zero)
            _logger?.LogScanError("SetWinEventHook",
                new InvalidOperationException(
                    "SetWinEventHook returned null — game launch detection is disabled. " +
                    "Ensure the monitor is constructed on the UI thread."));

        // WMI exit detection — WITHIN 1 gives 1-second resolution without admin
        _stopWatcher = new ManagementEventWatcher(new WqlEventQuery(
            "SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'"));
        _stopWatcher.EventArrived += OnProcessDeleted;
        try
        {
            _stopWatcher.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogScanError("WmiWatcher",
                new InvalidOperationException(
                    "WMI process-exit watcher failed to start — game exits will not be detected. " +
                    "Check that the WMI service (winmgmt) is running.", ex));
        }
    }

    // Called on the UI thread via the WinForms message pump
    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;

            // Skip the expensive path query if we're already tracking this PID —
            // most foreground events are non-game processes, so this exits early cheaply.
            lock (_activeGamesLock)
            {
                if (_activeGames.ContainsKey((int)pid)) return;
            }

            var exePath = GetProcessPath((int)pid);
            if (exePath is null) return;

            // Reject known non-game executables (launchers, anti-cheat, crash reporters)
            // that live inside a game's install directory but are not the game itself.
            if (KnownNonGameExes.Contains(Path.GetFileName(exePath))) return;

            // volatile read — no lock needed, just a reference load
            var game = Match(_games, exePath);
            if (game is null) return;

            var procName = Path.GetFileName(exePath);
            lock (_activeGamesLock)
            {
                // Double-check: another event may have beaten us between the two lock sections
                if (_activeGames.ContainsKey((int)pid)) return;
                _activeGames[(int)pid] = (game, procName);
            }

            // Offload to thread pool so GetDisplays() and future HDR toggling
            // never block the UI message pump
            Task.Run(() =>
            {
                try { _onGameStart(game); }
                catch (Exception ex) { _logger?.LogScanError("GameStart", ex); }
            });
        }
        catch (Exception ex) { _logger?.LogScanError("ForegroundHook", ex); }
    }

    // Called on the WMI thread
    private void OnProcessDeleted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var proc     = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid      = Convert.ToInt32(proc["ProcessId"]);
            var procName = proc["Name"]?.ToString() ?? string.Empty;

            GameInfo? game;
            lock (_activeGamesLock)
            {
                if (!_activeGames.TryGetValue(pid, out var entry)) return;
                if (!string.Equals(entry.ProcessName, procName, StringComparison.OrdinalIgnoreCase)) return;
                _activeGames.Remove(pid);
                game = entry.Game;
            }

            _onGameExit(game);
        }
        catch (Exception ex) { _logger?.LogScanError("ProcessDeletion", ex); }
    }

    /// <summary>
    /// Scans all currently running processes and registers any that match the game list
    /// into <c>_activeGames</c> without firing <c>OnGameStart</c>. Call on the UI thread
    /// immediately after construction so that games already running when the app starts
    /// are tracked correctly — otherwise their exit event would fire with no saved state.
    /// </summary>
    public IReadOnlyList<(GameInfo Game, int Pid)> SeedRunningGames()
    {
        var found = new List<(GameInfo, int)>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (KnownNonGameExes.Contains(process.ProcessName + ".exe")) continue;

                var exePath = GetProcessPath(process.Id);
                if (exePath is null) continue;
                if (KnownNonGameExes.Contains(Path.GetFileName(exePath))) continue;

                var game = Match(_games, exePath);
                if (game is null) continue;

                var procName = Path.GetFileName(exePath);
                lock (_activeGamesLock)
                {
                    if (!_activeGames.ContainsKey(process.Id))
                    {
                        _activeGames[process.Id] = (game, procName);
                        found.Add((game, process.Id));
                    }
                }
            }
            catch { }
            finally { process.Dispose(); }
        }
        return found;
    }

    /// <summary>Updates the game list after a periodic library rescan.</summary>
    public void UpdateGames(List<GameInfo> updatedGames)
    {
        _games = updatedGames; // volatile write — safe reference swap
        PurgeDeadEntries();    // clean up stale entries from crashed/killed games
    }

    // Removes _activeGames entries whose processes no longer exist.
    // Called on the rescan timer thread (every 30 min) — infrequent enough that
    // the per-entry OpenProcess overhead is negligible.
    private void PurgeDeadEntries()
    {
        lock (_activeGamesLock)
        {
            var dead = _activeGames.Keys.Where(pid => !IsProcessAlive(pid)).ToList();
            foreach (var pid in dead)
                _activeGames.Remove(pid);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            // GetExitCodeProcess distinguishes "still running" (STILL_ACTIVE=259)
            // from "PID recycled to a different process that happens to be alive".
            // OpenProcess alone would return a valid handle for a recycled PID.
            return GetExitCodeProcess(handle, out uint exitCode) && exitCode == STILL_ACTIVE;
        }
        finally { CloseHandle(handle); }
    }

    // Instance method so it can reuse _pathBuffer (UI thread only — safe without locking).
    private string? GetProcessPath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            // Try with the reused 1 KB buffer first; on failure grow to the max
            // extended-path size and retry (rare — most paths fit in 1 KB).
            uint size = (uint)_pathBuffer.Capacity;
            _pathBuffer.Clear();
            if (QueryFullProcessImageName(handle, 0, _pathBuffer, ref size))
                return _pathBuffer.ToString();

            // Grow once to the max extended-path size and stay there for the
            // lifetime of the monitor — subsequent calls skip the first-stage
            // attempt but avoid re-allocating 32 KB on every foreground event.
            size = 32767; // max extended-length path (\\?\ prefix)
            _pathBuffer.EnsureCapacity((int)size);
            _pathBuffer.Clear();
            return QueryFullProcessImageName(handle, 0, _pathBuffer, ref size)
                ? _pathBuffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// Returns the first game whose install path is a directory ancestor of <paramref name="exePath"/>.
    /// Public for unit testing.
    /// </summary>
    public static GameInfo? Match(IReadOnlyList<GameInfo> games, string exePath) =>
        games.FirstOrDefault(g =>
            !string.IsNullOrEmpty(g.InstallPath) &&
            IsUnderDirectory(exePath, g.InstallPath));

    /// <summary>
    /// Returns true when <paramref name="exePath"/> is inside <paramref name="dirPath"/>,
    /// enforcing a directory-separator boundary to prevent false prefix matches
    /// (e.g. "Elden Ring" matching "Elden Ring GOTY Edition").
    /// Public for unit testing.
    /// </summary>
    public static bool IsUnderDirectory(string exePath, string dirPath)
    {
        if (!exePath.StartsWith(dirPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (exePath.Length == dirPath.Length) return true;
        return exePath[dirPath.Length] is '\\' or '/';
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookHandle != IntPtr.Zero) UnhookWinEvent(_hookHandle);
        _stopWatcher.Stop();
        _stopWatcher.Dispose();
    }
}
