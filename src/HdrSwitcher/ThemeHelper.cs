using Microsoft.Win32;

namespace HdrSwitcher;

internal static class ThemeHelper
{
    /// <summary>True when the system is using a dark taskbar/shell.</summary>
    public static bool IsDarkMode
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return (int)(key?.GetValue("SystemUsesLightTheme") ?? 0) == 0;
            }
            catch { return true; }
        }
    }

}
