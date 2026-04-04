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
        using var bmp = RenderBitmap(state, sizePx, ThemeHelper.IsDarkMode);
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

        int[] sizes = [16, 20, 24, 32];
        var pngs = sizes.Select(s =>
        {
            using var bmp = RenderBitmap(state, s, dark);
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

    private static Bitmap RenderBitmap(HdrState state, int sizePx, bool darkMode)
    {
        var bmp = new Bitmap(sizePx, sizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = sizePx / 2f, cy = sizePx / 2f;
        float r        = sizePx * 0.27f;
        float innerR   = r + sizePx * 0.05f;
        float outerR   = innerR + sizePx * 0.15f;
        float rayHalfW = sizePx * 0.08f;   // constant-width rectangular rays
        const int numRays = 8;

        // One path, used for both FillPath (AllOn/Mixed) and DrawPath (AllOff)
        using var sunPath = new GraphicsPath();
        for (int i = 0; i < numRays; i++)
        {
            double angle = Math.PI * 2 * i / numRays;
            float rx = (float)Math.Cos(angle), ry = (float)Math.Sin(angle);
            float px = -ry * rayHalfW, py = rx * rayHalfW;   // perpendicular offset

            // 4-corner rectangle — same width at base and tip
            sunPath.AddPolygon(new[]
            {
                new PointF(cx + rx * innerR + px, cy + ry * innerR + py),  // inner-left
                new PointF(cx + rx * outerR + px, cy + ry * outerR + py),  // outer-left
                new PointF(cx + rx * outerR - px, cy + ry * outerR - py),  // outer-right
                new PointF(cx + rx * innerR - px, cy + ry * innerR - py),  // inner-right
            });
        }
        sunPath.AddEllipse(cx - r, cy - r, r * 2, r * 2);

        Color iconColor = darkMode ? Color.White : Color.FromArgb(255, 40, 40, 40);

        if (state == HdrState.AllOff)
        {
            // Outlined — same path, hollow centre reads as "inactive"
            float penW = Math.Max(1f, sizePx * 0.065f);
            using var pen = new Pen(iconColor, penW);
            g.DrawPath(pen, sunPath);
        }
        else
        {
            // AllOn: solid; Mixed: dimmed to signal partial state
            int alpha = state == HdrState.Mixed ? 160 : 255;
            using var brush = new SolidBrush(Color.FromArgb(alpha, iconColor));
            g.FillPath(brush, sunPath);
        }

        return bmp;
    }
}
