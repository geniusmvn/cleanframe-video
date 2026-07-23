using Erasa.Video2.Core.Masking;
using Erasa.Video2.Core.Models;
using Erasa.Video2.Core.Processing;

namespace Erasa.Video2.Tests;

public sealed class MaskRasterizerTests
{
    [Fact]
    public void RectangleMaskUsesRelativeCoordinatesAcrossResolutions()
    {
        var document = new MaskDocument();
        document.Add(new MaskOperation
        {
            Tool = MaskTool.Rectangle,
            Rect = new NormalizedRect(0.7, 0.72, 0.2, 0.18),
            Softness = 0.1
        });
        var first = MaskRasterizer.Render(document, 1000, 500);
        var second = MaskRasterizer.Render(document, 2000, 1000);
        var firstBounds = MaskRasterizer.GetBounds(first, 1000, 500)!.Value;
        var secondBounds = MaskRasterizer.GetBounds(second, 2000, 1000)!.Value;
        Assert.InRange(Math.Abs(firstBounds.Left / 1000d - secondBounds.Left / 2000d), 0, 0.003);
        Assert.InRange(Math.Abs(firstBounds.Top / 500d - secondBounds.Top / 1000d), 0, 0.003);
    }

    [Fact]
    public void EraserRemovesExistingMaskPixels()
    {
        var document = new MaskDocument();
        document.Add(new MaskOperation
        {
            Tool = MaskTool.Rectangle,
            Rect = new NormalizedRect(0.2, 0.2, 0.6, 0.6),
            Softness = 0
        });
        document.Add(new MaskOperation
        {
            Tool = MaskTool.Eraser,
            Points = [new NormalizedPoint(0.5, 0.5)],
            Radius = 0.1,
            Softness = 0
        });
        var mask = MaskRasterizer.Render(document, 200, 200);
        Assert.Equal(0, mask[100 * 200 + 100]);
        Assert.True(mask[60 * 200 + 60] > 0);
    }

    [Fact]
    public void CompositeNeverChangesPixelsOutsideMask()
    {
        var original = Enumerable.Range(0, 300).Select(index => (byte)(index % 251)).ToArray();
        var replacement = Enumerable.Repeat((byte)255, 300).ToArray();
        var mask = new byte[100];
        mask[50] = 255;
        var result = PixelCompositor.CompositeBgr(original, replacement, mask);
        for (var pixel = 0; pixel < 100; pixel++)
        {
            if (pixel == 50) continue;
            Assert.Equal(original[pixel * 3], result[pixel * 3]);
            Assert.Equal(original[pixel * 3 + 1], result[pixel * 3 + 1]);
            Assert.Equal(original[pixel * 3 + 2], result[pixel * 3 + 2]);
        }
    }
    [Fact]
    public void ResizeAlphaKeepsRelativeMaskPosition()
    {
        var source = new byte[100 * 50];
        for (var y = 30; y < 40; y++)
        for (var x = 70; x < 90; x++)
            source[y * 100 + x] = 255;

        var target = MaskRasterizer.ResizeAlpha(source, 100, 50, 200, 100);
        var bounds = MaskRasterizer.GetBounds(target, 200, 100)!.Value;

        Assert.InRange(bounds.Left, 139, 141);
        Assert.InRange(bounds.Top, 59, 61);
        Assert.InRange(bounds.Right, 179, 181);
        Assert.InRange(bounds.Bottom, 79, 81);
    }

}
