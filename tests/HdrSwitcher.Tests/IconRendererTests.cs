using System.Drawing;
using HdrSwitcher;

namespace HdrSwitcher.Tests;

public class IconRendererTests
{
    [Theory]
    [InlineData(HdrState.AllOn, 16)]
    [InlineData(HdrState.AllOff, 16)]
    [InlineData(HdrState.Mixed, 16)]
    [InlineData(HdrState.AllOn, 32)]
    [InlineData(HdrState.AllOff, 32)]
    [InlineData(HdrState.Mixed, 32)]
    public void Render_ReturnsNonNullIcon(HdrState state, int size)
    {
        using var icon = IconRenderer.Render(state, size);
        Assert.NotNull(icon);
    }

    [Theory]
    [InlineData(HdrState.AllOn, 16)]
    [InlineData(HdrState.AllOn, 32)]
    [InlineData(HdrState.AllOn, 48)]
    public void Render_ProducesCorrectSize(HdrState state, int size)
    {
        using var icon = IconRenderer.Render(state, size);
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    [Fact]
    public void AllOn_And_AllOff_ProduceDifferentIcons()
    {
        using var on = IconRenderer.Render(HdrState.AllOn, 32);
        using var off = IconRenderer.Render(HdrState.AllOff, 32);
        using var bmOn = on.ToBitmap();
        using var bmOff = off.ToBitmap();
        bool anyDifference = false;
        for (int x = 0; x < 32 && !anyDifference; x++)
            for (int y = 0; y < 32 && !anyDifference; y++)
                if (bmOn.GetPixel(x, y) != bmOff.GetPixel(x, y))
                    anyDifference = true;
        Assert.True(anyDifference);
    }
}
