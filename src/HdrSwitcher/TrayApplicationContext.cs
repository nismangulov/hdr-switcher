using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdrSwitcher;

public class TrayApplicationContext : ApplicationContext
{
    private readonly IHdrManager _hdr;
    private readonly AutostartManager _autostart;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly AppLogger _gameLogger;
    private GameProcessMonitor? _gameMonitor;
    private volatile List<GameInfo> _currentGames = []; // written on UI + timer threads
    private readonly Dictionary<string, bool> _preGameHdrState = new(); // install path → HDR was on
    private readonly object _gameStateLock = new();
    private System.Threading.Timer? _rescanTimer;
    private readonly CancellationTokenSource _cts = new();

    // Icon render cache — skip GDI+ work when state and theme haven't changed
    private HdrState _lastIconState = (HdrState)(-1);
    private bool _lastIconDarkMode;

    // NIM_SETVERSION — tells the shell to send NOTIFYICON_VERSION_4 messages,
    // which fixes tray icon behaviour on multi-monitor setups
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint   dwState;
        public uint   dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint   uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string szInfoTitle;
        public uint   dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    private const uint NIM_SETVERSION = 4;
    private const uint NOTIFYICON_VERSION_4 = 4;

    public TrayApplicationContext(IHdrManager hdr, AutostartManager autostart)
    {
        _hdr = hdr;
        _autostart = autostart;
        _menu = new ContextMenuStrip();
        Win11MenuRenderer.Apply(_menu);
        _menu.Opening += (_, _) => RebuildMenu();

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "HDR Switcher"
        };
        _tray.MouseClick += OnTrayClick;

        RefreshIcon();
        ApplyNotifyIconVersion4();

        // Re-render icon when HDR state changes externally (e.g. via Windows Settings)
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Re-render icon when accent colour or dark/light mode changes
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Scan game libraries on a background thread to avoid blocking the UI thread.
        // GameProcessMonitor must be created on the UI thread (SetWinEventHook requirement),
        // so we post back via SynchronizationContext after the scan completes.
        _gameLogger = new AppLogger();
        _gameLogger.LogHdrStatus("startup", _hdr.GetDisplays());

        var syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "TrayApplicationContext must be constructed on the UI thread.");
        Task.Run(() =>
        {
            var games = new GameLibraryScanner(_gameLogger).ScanAll();
            _gameLogger.LogLibrary(games);

            syncContext.Post(_ =>
            {
                _currentGames = games;
                _gameMonitor  = new GameProcessMonitor(games, OnGameStart, OnGameExit, _gameLogger);

                // Rescan libraries every 30 minutes to pick up newly installed games
                _rescanTimer = new System.Threading.Timer(
                    _ => RescanLibrary(), null,
                    TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
            }, null);
        });
    }

    private void OnGameStart(GameInfo game)
    {
        bool hdrOn = _hdr.GetDisplays().Any(d => d.HdrEnabled);
        lock (_gameStateLock)
        {
            if (!_preGameHdrState.ContainsKey(game.InstallPath))
                _preGameHdrState[game.InstallPath] = hdrOn;
        }
        _gameLogger.LogGameStarted(game, hdrOn);
        // TODO: auto-enable HDR here once logging phase is complete
    }

    private void OnGameExit(GameInfo game)
    {
        // Offload to thread pool for symmetry with OnGameStart and to keep
        // the WMI callback thread unblocked for future HDR-restore work
        Task.Run(() =>
        {
            try
            {
                bool hdrWasOn;
                lock (_gameStateLock)
                {
                    _preGameHdrState.TryGetValue(game.InstallPath, out hdrWasOn);
                    _preGameHdrState.Remove(game.InstallPath);
                }
                _gameLogger.LogGameExited(game, hdrWasOn);
                // TODO: auto-restore HDR here once logging phase is complete
            }
            catch (Exception ex) { _gameLogger.LogScanError("GameExit", ex); }
        });
    }

    // Called on a thread-pool thread by System.Threading.Timer — no Task.Run needed
    private void RescanLibrary()
    {
        if (_cts.IsCancellationRequested) return;

        var updated = new GameLibraryScanner(_gameLogger).ScanAll();

        // Volatile read — safe snapshot of the current list reference
        var snapshot     = _currentGames;
        var existingPaths = snapshot
            .Select(g => g.InstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatedPaths = updated
            .Select(g => g.InstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newGames     = updated .Where(g => !existingPaths.Contains(g.InstallPath)).ToList();
        var removedGames = snapshot.Where(g => !updatedPaths .Contains(g.InstallPath)).ToList();

        _gameLogger.LogRescan(newGames, removedGames);

        if (!_cts.IsCancellationRequested)
        {
            // Always update — the list may have shrunk if games were uninstalled.
            // Lock the paired write so two concurrent rescan callbacks (however unlikely
            // at a 30-minute interval) cannot interleave their list and monitor updates.
            lock (_gameStateLock)
            {
                _currentGames = updated; // volatile write
                _gameMonitor?.UpdateGames(updated);
            }
        }
    }

    private void RefreshIcon() => RefreshIcon(_hdr.GetDisplays());

    private void RefreshIcon(IReadOnlyList<DisplayInfo> displays)
    {
        HdrState state = displays.Count == 0 ? HdrState.AllOff
            : displays.All(d => d.HdrEnabled)  ? HdrState.AllOn
            : displays.All(d => !d.HdrEnabled) ? HdrState.AllOff
            : HdrState.Mixed;

        _tray.Text = state switch
        {
            HdrState.AllOn  => "HDR Switcher — All On",
            HdrState.AllOff => "HDR Switcher — All Off",
            HdrState.Mixed  => "HDR Switcher — Mixed",
            _               => "HDR Switcher"
        };

        // Skip the GDI+ render if neither state nor theme has changed
        bool dark = ThemeHelper.IsDarkMode;
        if (state == _lastIconState && dark == _lastIconDarkMode) return;
        _lastIconState   = state;
        _lastIconDarkMode = dark;

        var oldIcon = _tray.Icon;
        _tray.Icon = IconRenderer.RenderMultiSize(state);
        oldIcon?.Dispose();
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        try
        {
            var before  = _hdr.GetDisplays();
            var primary = before.FirstOrDefault(d => d.IsPrimary) ?? before.FirstOrDefault();
            if (primary is null) return;
            _hdr.SetHdr(primary.Id, !primary.HdrEnabled);
            var after = _hdr.GetDisplays();
            RefreshIcon(after);
            _gameLogger.LogHdrStatus("tray toggle", after);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            var displays = _hdr.GetDisplays();
            RefreshIcon(displays);
            _gameLogger.LogHdrStatus("external change", displays);
        }
        catch (Exception ex) { _gameLogger.LogScanError("DisplaySettingsChanged", ex); }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            try { RefreshIcon(); }
            catch (Exception ex) { _gameLogger.LogScanError("UserPreferenceChanged", ex); }
        }
    }

    private void RebuildMenu()
    {
        foreach (ToolStripItem item in _menu.Items) item.Dispose();
        _menu.Items.Clear();

        var displays = _hdr.GetDisplays();
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        var primaryItem = new ToolStripMenuItem("HDR: Primary")
        {
            Checked = primary?.HdrEnabled ?? false,
            CheckOnClick = false
        };
        if (primary is not null)
        {
            var capturedPrimary = primary;
            primaryItem.Click += (_, _) =>
            {
                try
                {
                    _hdr.SetHdr(capturedPrimary.Id, !capturedPrimary.HdrEnabled);
                    var d = _hdr.GetDisplays();
                    RefreshIcon(d);
                    _gameLogger.LogHdrStatus("tray toggle", d);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }
        _menu.Items.Add(primaryItem);
        _menu.Items.Add(new ToolStripSeparator());

        foreach (var display in displays)
        {
            var item = new ToolStripMenuItem(display.Name)
            {
                Checked = display.HdrEnabled,
                CheckOnClick = false
            };
            var captured = display;
            item.Click += (_, _) =>
            {
                try
                {
                    _hdr.SetHdr(captured.Id, !captured.HdrEnabled);
                    var d = _hdr.GetDisplays();
                    RefreshIcon(d);
                    _gameLogger.LogHdrStatus("tray toggle", d);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());

        var autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _autostart.IsEnabled(),
            CheckOnClick = false
        };
        autostartItem.Click += (_, _) =>
        {
            _autostart.SetEnabled(!autostartItem.Checked);
            RebuildMenu();
        };
        _menu.Items.Add(autostartItem);

        var logItem = new ToolStripMenuItem("Open log");
        logItem.Click += (_, _) =>
        {
            if (File.Exists(_gameLogger.LogPath))
                System.Diagnostics.Process.Start("notepad.exe", _gameLogger.LogPath);
        };
        _menu.Items.Add(logItem);
        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        };
        _menu.Items.Add(exitItem);
    }

    private void ApplyNotifyIconVersion4()
    {
        // Uses reflection to reach the internal NativeWindow — fail silently if
        // the field names change in a future .NET version.
        try
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var window = typeof(NotifyIcon).GetField("_window", flags)?.GetValue(_tray) as NativeWindow;
            var idObj  = (typeof(NotifyIcon).GetField("_id", flags)
                       ?? typeof(NotifyIcon).GetField("id",  flags))?.GetValue(_tray);
            uint id = idObj is int i ? (uint)i : 0u;

            if (window?.Handle is IntPtr hwnd && hwnd != IntPtr.Zero)
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize   = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd     = hwnd,
                    uID      = id,
                    uVersion = NOTIFYICON_VERSION_4
                };
                Shell_NotifyIcon(NIM_SETVERSION, ref nid);
            }
        }
        catch { /* Optional enhancement — degrade gracefully */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _cts.Cancel();
            _rescanTimer?.Dispose();
            _gameMonitor?.Dispose();
            _gameLogger.Dispose();
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
