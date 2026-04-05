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
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint EVENT_SYSTEM_FOREGROUND           = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT             = 0x0000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

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

    // pid → (game, processName) — processName guards against PID reuse
    private readonly Dictionary<int, (GameInfo Game, string ProcessName)> _activeGames = new();
    private readonly object _activeGamesLock = new();

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
        _stopWatcher.Start();
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

            var exePath = GetProcessPath((int)pid);
            if (exePath is null) return;

            // volatile read — no lock needed, just a reference load
            var game = Match(_games, exePath);
            if (game is null) return;

            var procName = Path.GetFileName(exePath);
            bool isNew;
            lock (_activeGamesLock)
            {
                isNew = !_activeGames.ContainsKey((int)pid);
                if (isNew) _activeGames[(int)pid] = (game, procName);
            }

            // Offload to thread pool so GetDisplays() and future HDR toggling
            // never block the UI message pump
            if (isNew) Task.Run(() => _onGameStart(game));
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

    /// <summary>Updates the game list after a periodic library rescan.</summary>
    public void UpdateGames(List<GameInfo> updatedGames) =>
        _games = updatedGames; // volatile write — safe reference swap

    private static string? GetProcessPath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            // Try with a standard buffer first, retry with max extended-path size
            // if QueryFullProcessImageName signals ERROR_INSUFFICIENT_BUFFER
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            if (QueryFullProcessImageName(handle, 0, sb, ref size)) return sb.ToString();

            size = 32767; // max extended-length path (\\?\ prefix)
            sb   = new StringBuilder((int)size);
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
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
        if (_hookHandle != IntPtr.Zero) UnhookWinEvent(_hookHandle);
        _stopWatcher.Stop();
        _stopWatcher.Dispose();
    }
}
