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
        PackIco(stream, sizes, pngs);
        stream.Position = 0;
        return new Icon(stream);
    }

    /// <summary>
    /// Generates the application icon (.ico) bytes — golden sun at 16/32/48/256 px.
    /// Written once to icon.ico at build time; embedded via &lt;ApplicationIcon&gt;.
    /// </summary>
    public static byte[] RenderAppIconBytes()
    {
        // Golden amber — visible on any background (Explorer, desktop, taskbar)
        Color golden = Color.FromArgb(255, 255, 196, 0);
        int[] sizes = [16, 32, 48, 256];
        var pngs = sizes.Select(s =>
        {
            using var bmp = RenderAppIconBitmap(s, golden);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();
        using var stream = new MemoryStream();
        PackIco(stream, sizes, pngs);
        return stream.ToArray();
    }

    private static void PackIco(Stream stream, int[] sizes, byte[][] pngs)
    {
        using var bw = new BinaryWriter(stream, System.Text.Encoding.Default, leaveOpen: true);
        bw.Write((ushort)0);            // reserved
        bw.Write((ushort)1);            // type: icon
        bw.Write((ushort)sizes.Length); // image count

        int dataOffset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            int w = sizes[i] >= 256 ? 0 : sizes[i];
            bw.Write((byte)w);               // width  (0 = 256)
            bw.Write((byte)w);               // height
            bw.Write((byte)0);               // color count
            bw.Write((byte)0);               // reserved
            bw.Write((ushort)1);             // planes
            bw.Write((ushort)32);            // bpp
            bw.Write((uint)pngs[i].Length);
            bw.Write((uint)dataOffset);
            dataOffset += pngs[i].Length;
        }
        foreach (var png in pngs)
            bw.Write(png);
    }

    /// <summary>Renders the golden app icon bitmap with longer rays suitable for large sizes.</summary>
    private static Bitmap RenderAppIconBitmap(int sizePx, Color color)
    {
        var bmp = new Bitmap(sizePx, sizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = sizePx / 2f, cy = sizePx / 2f;
        float r      = sizePx * 0.27f;
        float innerR = r + sizePx * 0.06f;
        float outerR = innerR + sizePx * 0.17f;  // longer rays for larger icon
        const int numRays = 8;
        float penW = Math.Max(1.5f, sizePx * 0.07f);

        using var pen = new Pen(color, penW) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < numRays; i++)
        {
            double angle = Math.PI * 2 * i / numRays;
            float rx = (float)Math.Cos(angle), ry = (float)Math.Sin(angle);
            g.DrawLine(pen, cx + rx * innerR, cy + ry * innerR, cx + rx * outerR, cy + ry * outerR);
        }

        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);

        return bmp;
    }

    private static Bitmap RenderBitmap(HdrState state, int sizePx, bool darkMode)
    {
        var bmp = new Bitmap(sizePx, sizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = sizePx / 2f, cy = sizePx / 2f;
        float r      = sizePx * 0.27f;
        float innerR = r + sizePx * 0.06f;
        float outerR = innerR + sizePx * 0.10f;
        const int numRays = 8;

        Color iconColor = darkMode ? Color.White : Color.FromArgb(255, 40, 40, 40);
        int alpha = state == HdrState.Mixed ? 160 : 255;
        Color color = Color.FromArgb(alpha, iconColor);
        float penW = Math.Max(1f, sizePx * 0.07f);

        // Rays — always outlined lines, identical for every state
        using var rayPen = new Pen(color, penW) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < numRays; i++)
        {
            double angle = Math.PI * 2 * i / numRays;
            float rx = (float)Math.Cos(angle), ry = (float)Math.Sin(angle);
            g.DrawLine(rayPen, cx + rx * innerR, cy + ry * innerR, cx + rx * outerR, cy + ry * outerR);
        }

        // Circle — filled for AllOn/Mixed, outlined for AllOff
        if (state == HdrState.AllOff)
        {
            using var circlePen = new Pen(color, penW);
            g.DrawEllipse(circlePen, cx - r, cy - r, r * 2, r * 2);
        }
        else
        {
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
        }

        return bmp;
    }
}
