using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HdrSwitcher;

public enum HdrState { AllOn, AllOff, Mixed }

public static class IconRenderer
{
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Renders a single-size icon. Used by tests and as a fallback.</summary>
    public static Icon Render(HdrState state, int sizePx)
    {
        using var bmp = RenderBitmap(state, sizePx, ThemeHelper.IsDarkMode, ThemeHelper.AccentColor);
        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    /// <summary>
    /// Renders a multi-resolution icon (16/20/24/32 px) packed into one ICO stream.
    /// The shell picks the best size for the current DPI automatically.
    /// </summary>
    public static Icon RenderMultiSize(HdrState state)
    {
        bool dark = ThemeHelper.IsDarkMode;
        Color accent = ThemeHelper.AccentColor;

        int[] sizes = [16, 20, 24, 32];
        var pngs = sizes.Select(s =>
        {
            using var bmp = RenderBitmap(state, s, dark, accent);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();

        using var stream = new MemoryStream();
        using var bw = new BinaryWriter(stream);

        // ICONDIR
        bw.Write((ushort)0);             // reserved
        bw.Write((ushort)1);             // type: icon
        bw.Write((ushort)sizes.Length);  // image count

        // ICONDIRENTRY array
        int dataOffset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write((byte)sizes[i]);        // width  (0 = 256)
            bw.Write((byte)sizes[i]);        // height
            bw.Write((byte)0);               // color count (0 = true colour)
            bw.Write((byte)0);               // reserved
            bw.Write((ushort)1);             // planes
            bw.Write((ushort)32);            // bpp
            bw.Write((uint)pngs[i].Length);  // data size
            bw.Write((uint)dataOffset);      // data offset
            dataOffset += pngs[i].Length;
        }

        foreach (var png in pngs)
            bw.Write(png);

        stream.Position = 0;
        return new Icon(stream);
    }

    private static Bitmap RenderBitmap(HdrState state, int sizePx, bool darkMode, Color accentColor)
    {
        var bmp = new Bitmap(sizePx, sizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = sizePx / 2f, cy = sizePx / 2f;
        float r = sizePx * 0.27f;
        float rayLen = sizePx * 0.15f;
        float rayW = sizePx * 0.065f;
        const int numRays = 8;

        // Build sun path — shared by all states
        using var sunPath = new GraphicsPath();
        for (int i = 0; i < numRays; i++)
        {
            double angle = Math.PI * 2 * i / numRays;
            float rx = (float)Math.Cos(angle), ry = (float)Math.Sin(angle);
            float innerR = r + sizePx * 0.05f, outerR = innerR + rayLen;
            float perpX = -ry * rayW / 2, perpY = rx * rayW / 2;
            sunPath.AddPolygon(new[]
            {
                new PointF(cx + rx * innerR + perpX, cy + ry * innerR + perpY),
                new PointF(cx + rx * outerR,          cy + ry * outerR),
                new PointF(cx + rx * innerR - perpX,  cy + ry * innerR - perpY),
            });
        }
        sunPath.AddEllipse(cx - r, cy - r, r * 2, r * 2);

        // Stroke colour for the AllOff outlined sun
        Color strokeColor = darkMode ? Color.White : Color.FromArgb(255, 40, 40, 40);

        if (state == HdrState.AllOff)
        {
            // Outlined sun — same shape but hollow, reads as "inactive"
            using var pen = new Pen(strokeColor, sizePx * 0.075f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            for (int i = 0; i < numRays; i++)
            {
                double angle = Math.PI * 2 * i / numRays;
                float rx = (float)Math.Cos(angle), ry = (float)Math.Sin(angle);
                float innerR = r + sizePx * 0.06f, outerR = innerR + rayLen;
                g.DrawLine(pen, cx + rx * innerR, cy + ry * innerR, cx + rx * outerR, cy + ry * outerR);
            }
        }
        else
        {
            // AllOn: filled with the Windows accent colour — integrates with system theme
            // Mixed:  same but dimmed to signal partial state
            int alpha = state == HdrState.Mixed ? 160 : 255;
            using var brush = new SolidBrush(Color.FromArgb(alpha, accentColor));
            g.FillPath(brush, sunPath);
        }

        return bmp;
    }
}
