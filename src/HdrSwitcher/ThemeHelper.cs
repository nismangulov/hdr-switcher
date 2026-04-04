using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdrSwitcher;

internal static class ThemeHelper
{
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetColorizationColor(out uint pcrColorization,
        [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);

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

    /// <summary>The user's Windows accent color from DWM.</summary>
    public static Color AccentColor
    {
        get
        {
            try
            {
                if (DwmGetColorizationColor(out uint color, out _) == 0)
                    return Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
            }
            catch { }
            return Color.White;
        }
    }
}
