using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdrSwitcher;

/// <summary>
/// Owns the system tray icon and context menu only.
/// All HDR logic is in HdrController; all game logic is in GameCoordinator.
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly HdrController    _hdr;
    private readonly GameCoordinator  _coordinator;
    private readonly string           _logPath;
    private readonly Action           _openSettings;
    private readonly NotifyIcon       _tray;
    private readonly ContextMenuStrip _menu;
    private readonly SynchronizationContext _syncContext;

    // Icon render cache — skip GDI+ work when neither state nor theme has changed
    private HdrState _lastIconState = (HdrState)(-1);
    private bool     _lastIconDarkMode;

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
    private const uint NIM_SETVERSION       = 4;
    private const uint NOTIFYICON_VERSION_4 = 4;

    public TrayApplicationContext(
        HdrController   hdr,
        GameCoordinator coordinator,
        string          logPath,
        Action          openSettings)
    {
        _hdr          = hdr;
        _coordinator  = coordinator;
        _logPath      = logPath;
        _openSettings = openSettings;
        _syncContext  = SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "TrayApplicationContext must be constructed on the UI thread.");

        _menu = new ContextMenuStrip();
        Win11MenuRenderer.Apply(_menu);
        _menu.Opening += (_, _) => RebuildMenu();

        _tray = new NotifyIcon
        {
            Visible          = true,
            ContextMenuStrip = _menu,
            Text             = "HDR Switcher"
        };
        _tray.MouseClick += OnTrayClick;

        // Subscribe to state-change events
        // GameStarted/GameExited fire off the UI thread — marshal back before touching WinForms
        _hdr.StateChanged           += OnHdrStateChanged;
        _coordinator.GameStarted    += _ => _syncContext.Post(_ => RefreshIcon(), null);
        _coordinator.GameExited     += _ => _syncContext.Post(_ => RefreshIcon(), null);
        _coordinator.LibraryChanged += RebuildMenu;

        // Re-render on theme change (dark/light mode switch)
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Initial icon render using current HDR state
        RefreshIcon(_hdr.GetDisplays());
        ApplyNotifyIconVersion4();
    }

    private void OnHdrStateChanged(IReadOnlyList<DisplayInfo> displays) => RefreshIcon(displays);

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        try
        {
            var before  = _hdr.GetDisplays();
            var primary = before.FirstOrDefault(d => d.IsPrimary) ?? before.FirstOrDefault();
            if (primary is null) return;
            _hdr.Toggle(primary.Id, !primary.HdrEnabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            try { RefreshIcon(); }
            catch (Exception ex) { _ = ex; /* best-effort */ }
        }
    }

    private void RefreshIcon() => RefreshIcon(_hdr.GetDisplays());

    private void RefreshIcon(IReadOnlyList<DisplayInfo> displays)
    {
        var state = HdrController.ComputeHdrState(displays);

        _tray.Text = state switch
        {
            HdrState.AllOn  => "HDR Switcher — All On",
            HdrState.AllOff => "HDR Switcher — All Off",
            HdrState.Mixed  => "HDR Switcher — Mixed",
            _               => "HDR Switcher"
        };

        bool dark = ThemeHelper.IsDarkMode;
        if (state == _lastIconState && dark == _lastIconDarkMode) return;
        _lastIconState    = state;
        _lastIconDarkMode = dark;

        var oldIcon = _tray.Icon;
        _tray.Icon = IconRenderer.RenderMultiSize(state);
        oldIcon?.Dispose();
    }

    private void RebuildMenu()
    {
        foreach (var item in _menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _menu.Items.Clear();

        var displays = _hdr.GetDisplays();
        var primary  = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        var primaryItem = new ToolStripMenuItem("HDR: Primary")
        {
            Checked      = primary?.HdrEnabled ?? false,
            CheckOnClick = false
        };
        if (primary is not null)
        {
            var cap = primary;
            primaryItem.Click += (_, _) =>
            {
                try { _hdr.Toggle(cap.Id, !cap.HdrEnabled); }
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
                Checked      = display.HdrEnabled,
                CheckOnClick = false
            };
            var cap = display;
            item.Click += (_, _) =>
            {
                try { _hdr.Toggle(cap.Id, !cap.HdrEnabled); }
                catch (Exception ex)
                {
                    MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => _openSettings();
        _menu.Items.Add(settingsItem);

        var logItem = new ToolStripMenuItem("Open log");
        logItem.Click += (_, _) =>
        {
            if (File.Exists(_logPath))
                System.Diagnostics.Process.Start("notepad.exe", _logPath);
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
        try
        {
            var flags  = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var window = typeof(NotifyIcon).GetField("_window", flags)?.GetValue(_tray) as NativeWindow;
            var idObj  = (typeof(NotifyIcon).GetField("_id", flags)
                       ?? typeof(NotifyIcon).GetField("id",  flags))?.GetValue(_tray);
            uint id    = idObj is int i ? (uint)i : 0u;

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
        catch { /* Optional — degrade gracefully */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _hdr.StateChanged                  -= OnHdrStateChanged;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
