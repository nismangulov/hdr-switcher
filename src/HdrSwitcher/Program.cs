using HdrSwitcher;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var hdr = new HdrManager();
var autostart = new AutostartManager();
var context = new TrayApplicationContext(hdr, autostart);

Application.Run(context);
