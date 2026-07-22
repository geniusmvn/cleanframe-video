using CleanFrame.Video2.Engine.Processing;
using OpenCvSharp;

namespace CleanFrame.Video2.Tests;

public sealed class TemporalPipelineMaskTests
{
    [Fact]
    public void Temporal_reconstruction_keeps_every_raw_pixel_outside_mask()
    {
        using var current = BuildFrame(96, 54, 0);
        using var previous = BuildFrame(96, 54, -2);
        using var next = BuildFrame(96, 54, 2);
        var alpha = new float[current.Rows * current.Cols];
        for (var y = 18; y < 36; y++)
        for (var x = 62; x < 88; x++)
            alpha[y * current.Cols + x] = 1;

        using var result = new TemporalReconstructor().Reconstruct(
            current, previous, next, alpha, ReconstructionOptions.Fast);

        for (var y = 0; y < current.Rows; y++)
        for (var x = 0; x < current.Cols; x++)
        {
            if (alpha[y * current.Cols + x] > 0) continue;
            Assert.Equal(current.At<Vec3b>(y, x), result.Frame.At<Vec3b>(y, x));
        }
    }

    private static Mat BuildFrame(int width, int height, int shift)
    {
        var frame = new Mat(new Size(width, height), MatType.CV_8UC3, new Scalar(35, 55, 75));
        Cv2.Rectangle(frame, new Rect(10 + shift, 12, 30, 20), new Scalar(130, 90, 45), -1);
        Cv2.Circle(frame, new Point(72 + shift, 28), 10, new Scalar(240, 240, 240), -1);
        return frame;
    }
}
