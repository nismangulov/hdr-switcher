using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HdrSwitcher;

internal class Win11MenuRenderer : ToolStripProfessionalRenderer
{
    // DWM rounded corners (Win11)
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // Single font instance for the app lifetime. WinForms does NOT dispose fonts set via
    // Control.Font externally — that is the caller's responsibility — so static readonly is
    // the correct ownership model here: one allocation, freed by the finalizer at process exit.
    private static readonly Font MenuFont = new("Segoe UI Variable Text", 10f);

    // Colours are read dynamically so theme changes take effect without recreating the renderer
    private static Color BgColor     => ThemeHelper.IsDarkMode ? Color.FromArgb(255, 31, 31, 31)    : Color.FromArgb(255, 243, 243, 243);
    private static Color HoverColor  => ThemeHelper.IsDarkMode ? Color.FromArgb(255, 55, 55, 55)    : Color.FromArgb(255, 210, 210, 210);
    private static Color SepColor    => ThemeHelper.IsDarkMode ? Color.FromArgb(255, 60, 60, 60)    : Color.FromArgb(255, 200, 200, 200);
    private static Color TextEnabled => ThemeHelper.IsDarkMode ? Color.White                          : Color.Black;
    private static Color TextDim     => ThemeHelper.IsDarkMode ? Color.FromArgb(255, 155, 155, 155) : Color.FromArgb(255, 130, 130, 130);
    private static Color BorderColor => ThemeHelper.IsDarkMode ? Color.FromArgb(255, 70, 70, 70)    : Color.FromArgb(255, 200, 200, 200);

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new Win11MenuRenderer();
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.Padding = new Padding(4, 4, 4, 4);
        menu.Font = MenuFont;
        menu.HandleCreated += (s, _) =>
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(((Control)s!).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        };
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(BgColor);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1));
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var brush = new SolidBrush(HoverColor);
        using var path = RoundedRect(rect, 4);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextEnabled : TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(SepColor);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BgColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item || !item.Checked) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = e.ImageRectangle;
        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        float s = 4f;
        using var pen = new Pen(TextEnabled, 1.8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(cx - s,        cy),
            new PointF(cx - s * 0.1f, cy + s * 0.9f),
            new PointF(cx + s,        cy - s * 0.9f),
        });
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X,         r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }
}
