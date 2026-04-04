using HdrSwitcher;

// Prevent multiple instances — two tray icons would be confusing
using var mutex = new Mutex(true, "Global\\HdrSwitcher-3F8A1C2D-9E4B-4F7A-8C1D-2E5F6A7B8C9D", out bool createdNew);
if (!createdNew) return;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var hdr = new HdrManager();
var autostart = new AutostartManager();
var context = new TrayApplicationContext(hdr, autostart);

Application.Run(context);
