using System.Drawing;

namespace HdrSwitcher;

/// <summary>
/// Settings window with three tabs: Games, Blacklist, Manually Added.
/// Single instance — created in Program.cs, shown/hidden via TrayApplicationContext callback.
/// </summary>
public class SettingsForm : Form
{
    private readonly GameCoordinator  _coordinator;
    private readonly AutostartManager _autostart;

    // In-memory edits — applied on Save, discarded on Cancel / close
    private List<string>     _pendingBlacklist   = [];
    private List<ManualGame> _pendingManualGames = [];

    // Controls
    private TabControl  _tabs            = null!;
    private ListView    _gamesListView   = null!;
    private Button      _refreshButton   = null!;
    private Button      _blacklistButton = null!;

    private ListBox _blacklistBox   = null!;
    private TextBox _blacklistInput = null!;
    private Button  _blAddButton    = null!;
    private Button  _blRemoveButton = null!;

    private ListView _manualListView     = null!;
    private TextBox  _manualNameInput    = null!;
    private TextBox  _manualPathInput    = null!;
    private Button   _manualBrowseButton = null!;
    private Button   _manualAddButton    = null!;
    private Button   _manualRemoveButton = null!;

    private CheckBox _autostartCheckbox = null!;
    private Button   _saveButton        = null!;
    private Button   _cancelButton      = null!;

    public SettingsForm(GameCoordinator coordinator, AutostartManager autostart)
    {
        _coordinator = coordinator;
        _autostart   = autostart;
        BuildUI();
    }

    private void BuildUI()
    {
        Text             = "HDR Switcher — Settings";
        Size             = new Size(560, 480);
        FormBorderStyle  = FormBorderStyle.FixedSingle;
        MaximizeBox      = false;
        MinimizeBox      = false;
        ShowInTaskbar    = false;
        StartPosition    = FormStartPosition.CenterScreen;

        ApplyTheme();

        // ── Tab control ──────────────────────────────────────────────────────
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildGamesTab());
        _tabs.TabPages.Add(BuildBlacklistTab());
        _tabs.TabPages.Add(BuildManualTab());

        // ── Bottom panel (Autostart + Save/Cancel) ───────────────────────────
        var bottomPanel = new Panel
        {
            Dock   = DockStyle.Bottom,
            Height = 48,
        };
        ApplyPanelTheme(bottomPanel);

        _autostartCheckbox = new CheckBox
        {
            Text     = "Start with Windows",
            AutoSize = true,
            Location = new Point(12, 14),
        };
        ApplyControlTheme(_autostartCheckbox);
        _autostartCheckbox.CheckedChanged += (_, _) => _autostart.SetEnabled(_autostartCheckbox.Checked);

        _saveButton = new Button
        {
            Text     = "Save",
            Size     = new Size(80, 28),
            Location = new Point(460 - 80 - 88, 10),
            Anchor   = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _saveButton.Click += OnSave;

        _cancelButton = new Button
        {
            Text     = "Cancel",
            Size     = new Size(80, 28),
            Location = new Point(460 - 80, 10),
            Anchor   = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _cancelButton.Click += (_, _) => { DiscardEdits(); Hide(); };

        bottomPanel.Controls.AddRange([_autostartCheckbox, _saveButton, _cancelButton]);

        Controls.Add(_tabs);
        Controls.Add(bottomPanel); // added after tabs so it renders on top
    }

    // ── Games tab ────────────────────────────────────────────────────────────

    private TabPage BuildGamesTab()
    {
        var page = new TabPage("Games");
        ApplyPanelTheme(page);

        _gamesListView = new ListView
        {
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = false,
            MultiSelect   = false,
            Dock          = DockStyle.None,
            Location      = new Point(8, 38),
            Size          = new Size(520, 318),
        };
        _gamesListView.Columns.Add("Game",    280);
        _gamesListView.Columns.Add("Store",    70);
        _gamesListView.Columns.Add("Running",  60);
        _gamesListView.SelectedIndexChanged += (_, _) =>
            _blacklistButton.Enabled = _gamesListView.SelectedItems.Count > 0;
        ApplyListViewTheme(_gamesListView);

        _refreshButton = new Button
        {
            Text     = "↺ Refresh",
            Size     = new Size(88, 26),
            Location = new Point(440, 8),
        };
        _refreshButton.Click += (_, _) =>
        {
            _coordinator.Rescan();
            LoadGamesTab();
        };

        _blacklistButton = new Button
        {
            Text     = "Blacklist",
            Size     = new Size(80, 26),
            Location = new Point(348, 8),
            Enabled  = false,
        };
        _blacklistButton.Click += OnBlacklistSelectedGame;

        page.Controls.AddRange([_gamesListView, _refreshButton, _blacklistButton]);
        return page;
    }

    private void LoadGamesTab()
    {
        var activePaths = _coordinator.GetActiveGameInstallPaths();
        var games       = _coordinator.GetCurrentGames();
        var bl          = _pendingBlacklist.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _gamesListView.BeginUpdate();
        _gamesListView.Items.Clear();
        foreach (var g in games.OrderBy(g => g.Name))
        {
            if (bl.Contains(g.InstallPath)) continue; // hide already-blacklisted entries
            var item = new ListViewItem(g.Name);
            item.SubItems.Add(g.Source);
            item.SubItems.Add(activePaths.Contains(g.InstallPath) ? "●" : "");
            item.Tag = g;
            _gamesListView.Items.Add(item);
        }
        _gamesListView.EndUpdate();
        _blacklistButton.Enabled = false;
    }

    private void OnBlacklistSelectedGame(object? sender, EventArgs e)
    {
        if (_gamesListView.SelectedItems.Count == 0) return;
        var game = (GameInfo)_gamesListView.SelectedItems[0].Tag!;
        if (!_pendingBlacklist.Contains(game.InstallPath, StringComparer.OrdinalIgnoreCase))
            _pendingBlacklist.Add(game.InstallPath);
        _gamesListView.Items.Remove(_gamesListView.SelectedItems[0]);
        _blacklistButton.Enabled = false;
        // Refresh blacklist tab list if it is currently shown
        LoadBlacklistTab();
    }

    // ── Blacklist tab ─────────────────────────────────────────────────────────

    private TabPage BuildBlacklistTab()
    {
        var page = new TabPage("Blacklist");
        ApplyPanelTheme(page);

        _blacklistBox = new ListBox
        {
            Location      = new Point(8, 8),
            Size          = new Size(520, 310),
            SelectionMode = SelectionMode.One,
        };
        _blacklistBox.SelectedIndexChanged += (_, _) =>
            _blRemoveButton.Enabled = _blacklistBox.SelectedIndex >= 0;
        ApplyListBoxTheme(_blacklistBox);

        _blacklistInput = new TextBox
        {
            Location        = new Point(8, 326),
            Size            = new Size(380, 23),
            PlaceholderText = "Executable name (launcher.exe) or full install path…",
        };
        ApplyTextBoxTheme(_blacklistInput);

        _blAddButton = new Button
        {
            Text     = "+ Add",
            Size     = new Size(60, 26),
            Location = new Point(396, 324),
        };
        _blAddButton.Click += OnBlacklistAdd;

        _blRemoveButton = new Button
        {
            Text     = "Remove",
            Size     = new Size(66, 26),
            Location = new Point(462, 324),
            Enabled  = false,
        };
        _blRemoveButton.Click += OnBlacklistRemove;

        page.Controls.AddRange([_blacklistBox, _blacklistInput, _blAddButton, _blRemoveButton]);
        return page;
    }

    private void LoadBlacklistTab()
    {
        _blacklistBox.BeginUpdate();
        _blacklistBox.Items.Clear();
        foreach (var entry in _pendingBlacklist)
            _blacklistBox.Items.Add(entry);
        _blacklistBox.EndUpdate();
        _blRemoveButton.Enabled = false;
    }

    private void OnBlacklistAdd(object? sender, EventArgs e)
    {
        var entry = _blacklistInput.Text.Trim();
        if (string.IsNullOrEmpty(entry)) return;
        if (_pendingBlacklist.Contains(entry, StringComparer.OrdinalIgnoreCase)) return;
        _pendingBlacklist.Add(entry);
        _blacklistInput.Clear();
        LoadBlacklistTab();
    }

    private void OnBlacklistRemove(object? sender, EventArgs e)
    {
        if (_blacklistBox.SelectedIndex < 0) return;
        _pendingBlacklist.RemoveAt(_blacklistBox.SelectedIndex);
        LoadBlacklistTab();
    }

    // ── Manually Added tab ────────────────────────────────────────────────────

    private TabPage BuildManualTab()
    {
        var page = new TabPage("Manually Added");
        ApplyPanelTheme(page);

        _manualListView = new ListView
        {
            View          = View.Details,
            FullRowSelect = true,
            MultiSelect   = false,
            Location      = new Point(8, 8),
            Size          = new Size(520, 280),
        };
        _manualListView.Columns.Add("Name", 180);
        _manualListView.Columns.Add("Path", 330);
        _manualListView.SelectedIndexChanged += (_, _) =>
            _manualRemoveButton.Enabled = _manualListView.SelectedItems.Count > 0;
        ApplyListViewTheme(_manualListView);

        var nameLabel = new Label { Text = "Name:", Location = new Point(8,  298), AutoSize = true };
        var pathLabel = new Label { Text = "Path:", Location = new Point(8,  326), AutoSize = true };
        ApplyControlTheme(nameLabel);
        ApplyControlTheme(pathLabel);

        _manualNameInput = new TextBox
        {
            Location        = new Point(50, 295),
            Size            = new Size(160, 23),
            PlaceholderText = "Display name…",
        };
        ApplyTextBoxTheme(_manualNameInput);

        _manualPathInput = new TextBox
        {
            Location        = new Point(50, 323),
            Size            = new Size(320, 23),
            PlaceholderText = "Full path to .exe…",
        };
        ApplyTextBoxTheme(_manualPathInput);

        _manualBrowseButton = new Button
        {
            Text     = "Browse…",
            Size     = new Size(68, 26),
            Location = new Point(376, 322),
        };
        _manualBrowseButton.Click += OnManualBrowse;

        _manualAddButton = new Button
        {
            Text     = "+ Add",
            Size     = new Size(56, 26),
            Location = new Point(450, 295),
        };
        _manualAddButton.Click += OnManualAdd;

        _manualRemoveButton = new Button
        {
            Text     = "Remove",
            Size     = new Size(66, 26),
            Location = new Point(450, 322),
            Enabled  = false,
        };
        _manualRemoveButton.Click += OnManualRemove;

        page.Controls.AddRange([
            _manualListView,
            nameLabel, pathLabel,
            _manualNameInput, _manualPathInput,
            _manualBrowseButton, _manualAddButton, _manualRemoveButton]);
        return page;
    }

    private void LoadManualTab()
    {
        _manualListView.BeginUpdate();
        _manualListView.Items.Clear();
        foreach (var g in _pendingManualGames)
        {
            var item = new ListViewItem(g.Name);
            item.SubItems.Add(g.ExePath);
            item.Tag = g;
            _manualListView.Items.Add(item);
        }
        _manualListView.EndUpdate();
        _manualRemoveButton.Enabled = false;
    }

    private void OnManualBrowse(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Select game executable",
            Filter = "Executables (*.exe)|*.exe",
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            _manualPathInput.Text = dlg.FileName;
    }

    private void OnManualAdd(object? sender, EventArgs e)
    {
        var name = _manualNameInput.Text.Trim();
        var path = _manualPathInput.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path)) { MessageBox.Show("File not found.", "HDR Switcher"); return; }
        if (_pendingManualGames.Any(g => string.Equals(g.ExePath, path, StringComparison.OrdinalIgnoreCase))) return;
        _pendingManualGames.Add(new ManualGame(name, path));
        _manualNameInput.Clear();
        _manualPathInput.Clear();
        LoadManualTab();
    }

    private void OnManualRemove(object? sender, EventArgs e)
    {
        if (_manualListView.SelectedItems.Count == 0) return;
        var game = (ManualGame)_manualListView.SelectedItems[0].Tag!;
        _pendingManualGames.Remove(game);
        LoadManualTab();
    }

    // ── Save / Cancel / Close ─────────────────────────────────────────────────

    private void OnSave(object? sender, EventArgs e)
    {
        _coordinator.Settings.Save(_pendingBlacklist, _pendingManualGames);
        _coordinator.Rescan();
        Hide();
    }

    private void DiscardEdits()
    {
        _pendingBlacklist   = [.._coordinator.Settings.Blacklist];
        _pendingManualGames = [.._coordinator.Settings.ManualGames];
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            DiscardEdits();
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        DiscardEdits(); // reset to current saved state each time the form is shown
        ApplyTheme();   // re-apply in case theme changed since last open
        LoadGamesTab();
        LoadBlacklistTab();
        LoadManualTab();
        _autostartCheckbox.Checked = _autostart.IsEnabled();
    }

    // ── Theme helpers ─────────────────────────────────────────────────────────

    private static readonly Color DarkBg  = ColorTranslator.FromHtml("#1F1F1F");
    private static readonly Color DarkFg  = Color.White;
    private static readonly Color LightBg = ColorTranslator.FromHtml("#F3F3F3");
    private static readonly Color LightFg = Color.Black;

    private void ApplyTheme()
    {
        bool dark = ThemeHelper.IsDarkMode;
        BackColor = dark ? DarkBg : LightBg;
        ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyPanelTheme(Control c)
    {
        bool dark   = ThemeHelper.IsDarkMode;
        c.BackColor = dark ? DarkBg : LightBg;
        c.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyControlTheme(Control c)
    {
        bool dark   = ThemeHelper.IsDarkMode;
        c.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyListViewTheme(ListView lv)
    {
        bool dark    = ThemeHelper.IsDarkMode;
        lv.BackColor = dark ? Color.FromArgb(40, 40, 40) : Color.White;
        lv.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyListBoxTheme(ListBox lb)
    {
        bool dark    = ThemeHelper.IsDarkMode;
        lb.BackColor = dark ? Color.FromArgb(40, 40, 40) : Color.White;
        lb.ForeColor = dark ? DarkFg : LightFg;
    }

    private void ApplyTextBoxTheme(TextBox tb)
    {
        bool dark    = ThemeHelper.IsDarkMode;
        tb.BackColor = dark ? Color.FromArgb(40, 40, 40) : Color.White;
        tb.ForeColor = dark ? DarkFg : LightFg;
    }
}
