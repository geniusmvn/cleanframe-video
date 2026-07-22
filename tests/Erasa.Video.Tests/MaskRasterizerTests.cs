using Erasa.Video.Core.Models;
using Erasa.Video.Core.Processing;

namespace Erasa.Video.Tests;

public sealed class MaskRasterizerTests
{
    [Fact]
    public void RectangleOnlyChangesInsideRequestedArea()
    {
        var doc = new MaskDocument();
        doc.Add(new MaskOperation { Tool = MaskTool.Rectangle, Rect = new NormalizedRect(.25, .25, .5, .5), Softness = 0 });
        var mask = MaskRasterizer.Render(doc, 100, 80);
        Assert.Equal(0, mask[5 * 100 + 5]);
        Assert.Equal(255, mask[40 * 100 + 50]);
    }

    [Fact]
    public void EraserRemovesExistingBrushPixels()
    {
        var doc = new MaskDocument();
        doc.Add(new MaskOperation { Tool = MaskTool.Brush, Radius = .15, Points = [new(.5, .5)] });
        doc.Add(new MaskOperation { Tool = MaskTool.Eraser, Radius = .08, Erase = true, Points = [new(.5, .5)] });
        var mask = MaskRasterizer.Render(doc, 100, 100);
        Assert.Equal(0, mask[50 * 100 + 50]);
        Assert.True(mask[50 * 100 + 60] > 0);
    }

    [Fact]
    public void EraserCanEditSuggestedBaseMask()
    {
        var baseMask = Enumerable.Repeat((byte)255, 100 * 100).ToArray();
        var doc = new MaskDocument();
        doc.Add(new MaskOperation
        {
            Tool = MaskTool.Eraser, Radius = .10, Erase = true, Points = [new(.5, .5)]
        });
        var mask = MaskRasterizer.Render(doc, 100, 100, baseMask);
        Assert.Equal(0, mask[50 * 100 + 50]);
        Assert.Equal(255, mask[5 * 100 + 5]);
    }

    [Fact]
    public async Task PngWriterProducesPngSignature()
    {
        var path = Path.Combine(Path.GetTempPath(), $"erasa-{Guid.NewGuid():N}.png");
        try
        {
            await MaskPngWriter.WriteGrayscaleAsync(path, 8, 8, Enumerable.Repeat((byte)255, 64).ToArray());
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new byte[] {137,80,78,71,13,10,26,10}, bytes[..8]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
