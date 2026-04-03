using Microsoft.Win32;

namespace HdrSwitcher;

public class AutostartManager
{
    private readonly string _registryKeyPath;
    private const string ValueName = "HdrSwitcher";

    public AutostartManager()
        : this(@"Software\Microsoft\Windows\CurrentVersion\Run") { }

    public AutostartManager(string registryKeyPath)
    {
        _registryKeyPath = registryKeyPath;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registryKeyPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine exe path");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
