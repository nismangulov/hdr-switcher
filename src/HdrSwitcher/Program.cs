using HdrSwitcher;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Prevent multiple instances — two tray icons would be confusing
        using var mutex = new Mutex(true, "Global\\HdrSwitcher-3F8A1C2D-9E4B-4F7A-8C1D-2E5F6A7B8C9D", out bool createdNew);
        if (!createdNew) return;

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetColorMode(SystemColorMode.System);
        Application.SetCompatibleTextRenderingDefault(false);

        using var logger      = new AppLogger();
        var       autostart   = new AutostartManager();
        var       settings    = new SettingsManager();
        using var hdr         = new HdrController(new HdrManager(), logger);
        var       dispatcher  = new WinFormsDispatcher(
            SynchronizationContext.Current
            ?? throw new InvalidOperationException("Main must run on the UI thread."));
        using var coordinator = new GameCoordinator(settings, hdr, logger, dispatcher);
        var       form        = new SettingsForm(coordinator, autostart);
        var       tray        = new TrayApplicationContext(hdr, coordinator, logger.LogPath, () => form.Show());

        Application.Run(tray);
    }
}
