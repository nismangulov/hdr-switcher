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
        _coordinator  = coordinator;
        _autostart    = autostart;
        AutoScaleMode = AutoScaleMode.None;
        BuildUI();
    }

    private void BuildUI()
    {
        Text            = "HDR Switcher — Settings";
        Size            = new Size(800, 640);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.CenterScreen;

        ApplyTheme();

        // ── Bottom panel (Autostart + Save/Cancel) ───────────────────────────
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        ApplyPanelTheme(bottomPanel);

        _autostartCheckbox = new CheckBox
        {
            Text     = "Start with Windows",
            AutoSize = true,
            Anchor   = AnchorStyles.Left | AnchorStyles.Top,
            Location = new Point(14, 14),
        };
        ApplyControlTheme(_autostartCheckbox);
        _autostartCheckbox.CheckedChanged += OnAutostartChanged;

        _cancelButton = new Button
        {
            Text   = "Cancel",
            Size   = new Size(92, 32),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        _cancelButton.Location = new Point(bottomPanel.Width - _cancelButton.Width - 12, 12);
        _cancelButton.Click += (_, _) => { DiscardEdits(); Hide(); };

        _saveButton = new Button
        {
            Text   = "Save",
            Size   = new Size(92, 32),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        _saveButton.Location = new Point(_cancelButton.Left - _saveButton.Width - 8, 12);
        _saveButton.Click += OnSave;

        bottomPanel.Controls.AddRange([_autostartCheckbox, _saveButton, _cancelButton]);

        // ── Tab control ──────────────────────────────────────────────────────
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGamesTab());
        tabs.TabPages.Add(BuildBlacklistTab());
        tabs.TabPages.Add(BuildManualTab());

        Controls.Add(tabs);
        Controls.Add(bottomPanel); // added after tabs so it renders on top
    }

    // ── Games tab ────────────────────────────────────────────────────────────

    private TabPage BuildGamesTab()
    {
        var page = new TabPage("Games");
        ApplyPanelTheme(page);

        // Top bar: Blacklist + Refresh buttons
        var topBar = new Panel { Dock = DockStyle.Top, Height = 44 };
        ApplyPanelTheme(topBar);

        _blacklistButton = new Button
        {
            Text    = "Blacklist",
            Size    = new Size(96, 30),
            Anchor  = AnchorStyles.Top | AnchorStyles.Right,
            Enabled = false,
        };
        _blacklistButton.Click += OnBlacklistSelectedGame;

        _refreshButton = new Button
        {
            Text   = "↺ Refresh",
            Size   = new Size(96, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _refreshButton.Click += (_, _) =>
        {
            _coordinator.Rescan();
            LoadGamesTab();
        };

        // Position buttons right-aligned after topBar has its final width
        topBar.SizeChanged += (_, _) =>
        {
            _refreshButton.Location   = new Point(topBar.Width - _refreshButton.Width - 8, 7);
            _blacklistButton.Location = new Point(_refreshButton.Left - _blacklistButton.Width - 6, 7);
        };

        topBar.Controls.AddRange([_blacklistButton, _refreshButton]);

        _gamesListView = new ListView
        {
            Dock          = DockStyle.Fill,
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = false,
            MultiSelect   = false,
        };
        _gamesListView.Columns.Add("Game",    420);
        _gamesListView.Columns.Add("Store",    90);
        _gamesListView.Columns.Add("Running",  70);
        _gamesListView.SelectedIndexChanged += (_, _) =>
            _blacklistButton.Enabled = _gamesListView.SelectedItems.Count > 0;
        ApplyListViewTheme(_gamesListView);

        // Add Fill control first, then Top — Fill gets the remaining space
        page.Controls.Add(_gamesListView);
        page.Controls.Add(topBar);
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
            if (bl.Contains(g.InstallPath)) continue;
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
        LoadBlacklistTab();
    }

    // ── Blacklist tab ─────────────────────────────────────────────────────────

    private TabPage BuildBlacklistTab()
    {
        var page = new TabPage("Blacklist");
        ApplyPanelTheme(page);

        // Bottom bar: text input + Add/Remove buttons
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        ApplyPanelTheme(bottomBar);

        _blacklistInput = new TextBox
        {
            Anchor          = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Location        = new Point(8, 8),
            Height          = 26,
            PlaceholderText = "Executable name (launcher.exe) or full install path…",
        };
        ApplyTextBoxTheme(_blacklistInput);

        _blRemoveButton = new Button
        {
            Text    = "Remove",
            Size    = new Size(92, 30),
            Anchor  = AnchorStyles.Right | AnchorStyles.Top,
            Enabled = false,
        };
        _blRemoveButton.Click += OnBlacklistRemove;

        _blAddButton = new Button
        {
            Text   = "+ Add",
            Size   = new Size(80, 30),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        _blAddButton.Click += OnBlacklistAdd;

        bottomBar.SizeChanged += (_, _) =>
        {
            _blRemoveButton.Location  = new Point(bottomBar.Width - _blRemoveButton.Width - 8, 7);
            _blAddButton.Location     = new Point(_blRemoveButton.Left - _blAddButton.Width - 6, 7);
            _blacklistInput.Width     = _blAddButton.Left - 16;
        };

        bottomBar.Controls.AddRange([_blacklistInput, _blAddButton, _blRemoveButton]);

        _blacklistBox = new ListBox
        {
            Dock          = DockStyle.Fill,
            SelectionMode = SelectionMode.One,
        };
        _blacklistBox.SelectedIndexChanged += (_, _) =>
            _blRemoveButton.Enabled = _blacklistBox.SelectedIndex >= 0;
        ApplyListBoxTheme(_blacklistBox);

        page.Controls.Add(_blacklistBox);
        page.Controls.Add(bottomBar);
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
        LoadGamesTab();
    }

    // ── Manually Added tab ────────────────────────────────────────────────────

    private TabPage BuildManualTab()
    {
        var page = new TabPage("Manually Added");
        ApplyPanelTheme(page);

        // Bottom bar: two input rows stacked
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 80 };
        ApplyPanelTheme(bottomBar);

        var nameLabel = new Label { Text = "Name:", Location = new Point(8, 10), AutoSize = true };
        var pathLabel = new Label { Text = "Path:",  Location = new Point(8, 44), AutoSize = true };
        ApplyControlTheme(nameLabel);
        ApplyControlTheme(pathLabel);

        _manualNameInput = new TextBox
        {
            Location        = new Point(54, 7),
            Size            = new Size(220, 26),
            PlaceholderText = "Display name…",
        };
        ApplyTextBoxTheme(_manualNameInput);

        _manualAddButton = new Button
        {
            Text   = "+ Add",
            Size   = new Size(92, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _manualAddButton.Click += OnManualAdd;

        _manualPathInput = new TextBox
        {
            Location        = new Point(54, 41),
            Anchor          = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Height          = 26,
            PlaceholderText = "Full path to .exe…",
        };
        ApplyTextBoxTheme(_manualPathInput);

        _manualBrowseButton = new Button
        {
            Text   = "Browse…",
            Size   = new Size(92, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _manualBrowseButton.Click += OnManualBrowse;

        _manualRemoveButton = new Button
        {
            Text    = "Remove",
            Size    = new Size(92, 30),
            Anchor  = AnchorStyles.Top | AnchorStyles.Right,
            Enabled = false,
        };
        _manualRemoveButton.Click += OnManualRemove;

        bottomBar.SizeChanged += (_, _) =>
        {
            _manualRemoveButton.Location = new Point(bottomBar.Width - _manualRemoveButton.Width - 8, 7);
            _manualAddButton.Location    = new Point(_manualRemoveButton.Left - _manualAddButton.Width - 6, 7);
            _manualBrowseButton.Location = new Point(_manualRemoveButton.Left - _manualBrowseButton.Width - 6, 41);
            _manualPathInput.Width       = _manualBrowseButton.Left - 54 - 8;
        };

        bottomBar.Controls.AddRange([
            nameLabel, pathLabel,
            _manualNameInput, _manualPathInput,
            _manualBrowseButton, _manualAddButton, _manualRemoveButton]);

        _manualListView = new ListView
        {
            Dock          = DockStyle.Fill,
            View          = View.Details,
            FullRowSelect = true,
            MultiSelect   = false,
        };
        _manualListView.Columns.Add("Name", 240);
        _manualListView.Columns.Add("Path", 498);
        _manualListView.SelectedIndexChanged += (_, _) =>
            _manualRemoveButton.Enabled = _manualListView.SelectedItems.Count > 0;
        ApplyListViewTheme(_manualListView);

        page.Controls.Add(_manualListView);
        page.Controls.Add(bottomBar);
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
        if (dlg.ShowDialog(this) == DialogResult.OK)
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

    // OnVisibleChanged fires on every Show() — OnShown only fires the first time
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) return;

        DiscardEdits();
        ApplyTheme();
        LoadGamesTab();
        LoadBlacklistTab();
        LoadManualTab();

        // Suppress the handler to avoid a registry write on form open
        _autostartCheckbox.CheckedChanged -= OnAutostartChanged;
        _autostartCheckbox.Checked = _autostart.IsEnabled();
        _autostartCheckbox.CheckedChanged += OnAutostartChanged;
    }

    private void OnAutostartChanged(object? sender, EventArgs e) =>
        _autostart.SetEnabled(_autostartCheckbox.Checked);

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
