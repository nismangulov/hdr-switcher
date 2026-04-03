using System.Drawing;
using System.Drawing.Drawing2D;

namespace HdrSwitcher;

public enum HdrState { AllOn, AllOff, Mixed }

public static class IconRenderer
{
    public static Icon Render(HdrState state, int sizePx)
    {
        using var bmp = new Bitmap(sizePx, sizePx);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = sizePx / 2f;
        float cy = sizePx / 2f;
        float r = sizePx * 0.30f;
        float rayLen = sizePx * 0.18f;
        float rayW = sizePx * 0.07f;
        int numRays = 8;

        Color bodyColor = state switch
        {
            HdrState.AllOn => Color.FromArgb(255, 255, 220, 0),
            HdrState.AllOff => Color.FromArgb(180, 130, 130, 130),
            HdrState.Mixed => Color.FromArgb(220, 200, 180, 60),
            _ => Color.Gray
        };

        using var bodyBrush = new SolidBrush(bodyColor);
        using var slashPen = new Pen(Color.FromArgb(220, 200, 50, 50), sizePx * 0.10f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        for (int i = 0; i < numRays; i++)
        {
            double angle = Math.PI * 2 * i / numRays;
            float rx = (float)Math.Cos(angle);
            float ry = (float)Math.Sin(angle);
            float innerR = r + sizePx * 0.04f;
            float outerR = r + sizePx * 0.04f + rayLen;

            using var path = new GraphicsPath();
            float perpX = -ry * rayW / 2;
            float perpY = rx * rayW / 2;
            path.AddPolygon(new[]
            {
                new PointF(cx + rx * innerR + perpX, cy + ry * innerR + perpY),
                new PointF(cx + rx * outerR,         cy + ry * outerR),
                new PointF(cx + rx * innerR - perpX, cy + ry * innerR - perpY),
            });
            g.FillPath(bodyBrush, path);
        }

        g.FillEllipse(bodyBrush, cx - r, cy - r, r * 2, r * 2);

        if (state == HdrState.Mixed)
        {
            using var maskBrush = new SolidBrush(Color.FromArgb(140, 50, 50, 50));
            g.FillPie(maskBrush, cx - r, cy - r, r * 2, r * 2, 90, 180);
        }

        if (state == HdrState.AllOff)
        {
            float slashPad = sizePx * 0.10f;
            g.DrawLine(slashPen,
                cx + r * 0.6f + slashPad * 0.3f, cy - r * 0.6f - slashPad * 0.3f,
                cx - r * 0.6f - slashPad * 0.3f, cy + r * 0.6f + slashPad * 0.3f);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
