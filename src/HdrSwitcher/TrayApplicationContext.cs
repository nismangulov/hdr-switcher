using System.Drawing;

namespace HdrSwitcher;

public class TrayApplicationContext : ApplicationContext
{
    private readonly IHdrManager _hdr;
    private readonly AutostartManager _autostart;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;

    public TrayApplicationContext(IHdrManager hdr, AutostartManager autostart)
    {
        _hdr = hdr;
        _autostart = autostart;
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "HDR Switcher"
        };
        _tray.MouseClick += OnTrayClick;

        RefreshIcon();
    }

    private void RefreshIcon()
    {
        var displays = _hdr.GetDisplays();
        HdrState state = displays.Count == 0 ? HdrState.AllOff
            : displays.All(d => d.HdrEnabled) ? HdrState.AllOn
            : displays.All(d => !d.HdrEnabled) ? HdrState.AllOff
            : HdrState.Mixed;

        int sizePx = SystemInformation.SmallIconSize.Width;
        var oldIcon = _tray.Icon;
        _tray.Icon = IconRenderer.Render(state, sizePx);
        oldIcon?.Dispose();

        _tray.Text = state switch
        {
            HdrState.AllOn => "HDR Switcher — All On",
            HdrState.AllOff => "HDR Switcher — All Off",
            HdrState.Mixed => "HDR Switcher — Mixed",
            _ => "HDR Switcher"
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
            MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
                    MessageBox.Show($"SetHdr failed: {ex.Message}", "HDR Switcher Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _autostart.SetEnabled(!_autostart.IsEnabled());
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
