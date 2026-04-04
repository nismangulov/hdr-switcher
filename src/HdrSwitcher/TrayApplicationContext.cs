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
    }

    private void RefreshIcon()
    {
        var displays = _hdr.GetDisplays();
        HdrState state = displays.Count == 0 ? HdrState.AllOff
            : displays.All(d => d.HdrEnabled)  ? HdrState.AllOn
            : displays.All(d => !d.HdrEnabled) ? HdrState.AllOff
            : HdrState.Mixed;

        var oldIcon = _tray.Icon;
        _tray.Icon = IconRenderer.RenderMultiSize(state);
        oldIcon?.Dispose();

        _tray.Text = state switch
        {
            HdrState.AllOn  => "HDR Switcher — All On",
            HdrState.AllOff => "HDR Switcher — All Off",
            HdrState.Mixed  => "HDR Switcher — Mixed",
            _               => "HDR Switcher"
        };
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        try
        {
            var displays = _hdr.GetDisplays();
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            if (primary is null) return;
            _hdr.SetHdr(primary.Id, !primary.HdrEnabled);
            RefreshIcon();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => RefreshIcon();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            RefreshIcon();
    }

    private void RebuildMenu()
    {
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
                _hdr.SetHdr(capturedPrimary.Id, !capturedPrimary.HdrEnabled);
                RefreshIcon();
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
                    RefreshIcon();
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
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }


}
