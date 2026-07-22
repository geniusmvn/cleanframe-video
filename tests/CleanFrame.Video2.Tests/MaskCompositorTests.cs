using CleanFrame.Video2.Core.Processing;

namespace CleanFrame.Video2.Tests;

public sealed class MaskCompositorTests
{
    [Fact]
    public void Raw_pipeline_does_not_change_pixels_outside_mask()
    {
        const int pixels = 64;
        var source = Enumerable.Range(0, pixels * 3).Select(x => (byte)(x % 251)).ToArray();
        var fill = Enumerable.Repeat((byte)255, pixels * 3).ToArray();
        var alpha = new float[pixels];
        for (var i = 20; i < 28; i++) alpha[i] = 1;
        var output = RawMaskCompositor.CompositeBgr(source, fill, alpha);
        for (var i = 0; i < pixels; i++)
        {
            if (alpha[i] > 0) continue;
            Assert.Equal(source.AsSpan(i * 3, 3).ToArray(), output.AsSpan(i * 3, 3).ToArray());
        }
    }
}
