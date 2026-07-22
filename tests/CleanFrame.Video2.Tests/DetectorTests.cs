using CleanFrame.Video2.Core.Processing;
using CleanFrame.Video2.Engine.Detection;
using OpenCvSharp;

namespace CleanFrame.Video2.Tests;

public sealed class DetectorTests
{
    [Fact]
    public void Detector_prefers_static_overlay_not_random_region()
    {
        var frames = BuildFrames(16);
        try
        {
            var candidates = new StaticOverlayDetector().Detect(frames);
            Assert.NotEmpty(candidates);
            var top = candidates[0];
            var expected = new Rect(244, 142, 62, 24);
            Assert.True(IntersectionOverUnion(top.Bounds, expected) > 0.20,
                $"Top candidate {top.Bounds} did not overlap expected overlay {expected}.");
            Assert.All(top.Mask.Operations, operation => Assert.IsType<CleanFrame.Video2.Core.Models.PolygonMaskOperation>(operation));
            var alpha = MaskRasterizer.Render(top.Mask, 320, 180);
            Assert.True(alpha.Count(x => x > 0.05f) < expected.Width * expected.Height * 3,
                "Detector returned an excessively broad region instead of a tight alpha mask.");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    [Fact]
    public void Detector_mask_is_stable_across_frame_windows()
    {
        var frames = BuildFrames(20);
        try
        {
            var detector = new StaticOverlayDetector();
            var first = detector.Detect(frames.Take(12).ToArray()).First();
            var second = detector.Detect(frames.Skip(8).Take(12).ToArray()).First();
            Assert.True(IntersectionOverUnion(first.Bounds, second.Bounds) > 0.65,
                $"Bounds moved between windows: {first.Bounds} vs {second.Bounds}.");
            var firstAlpha = MaskRasterizer.Render(first.Mask, 320, 180);
            var secondAlpha = MaskRasterizer.Render(second.Mask, 320, 180);
            Assert.True(BinaryMaskIoU(firstAlpha, secondAlpha, 0.05f) > 0.65,
                "Alpha mask was not stable across sampled frame windows.");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }


    [Fact]
    public void Detector_returns_no_candidate_for_random_motion_only()
    {
        var frames = BuildFramesWithoutOverlay(16);
        try
        {
            Assert.Empty(new StaticOverlayDetector().Detect(frames));
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static Mat[] BuildFrames(int count)
    {
        var frames = new Mat[count];
        for (var i = 0; i < count; i++)
        {
            var frame = new Mat(new Size(320, 180), MatType.CV_8UC3, new Scalar(25 + (i * 13) % 90, 35 + (i * 7) % 100, 45 + (i * 5) % 80));
            Cv2.Circle(frame, new Point((i * 23) % 360 - 20, 85), 34, new Scalar(160, 90, 35), -1);
            Cv2.Rectangle(frame, new Rect(244, 142, 62, 24), new Scalar(245, 245, 245), 2);
            Cv2.Line(frame, new Point(250, 150), new Point(298, 150), new Scalar(245, 245, 245), 2);
            Cv2.Line(frame, new Point(250, 157), new Point(286, 157), new Scalar(245, 245, 245), 2);
            frames[i] = frame;
        }
        return frames;
    }


    private static Mat[] BuildFramesWithoutOverlay(int count)
    {
        var frames = new Mat[count];
        var random = new Random(12345);
        for (var i = 0; i < count; i++)
        {
            var frame = new Mat(new Size(320, 180), MatType.CV_8UC3, new Scalar(20 + i * 4, 35 + i * 3, 55 + i * 2));
            for (var j = 0; j < 8; j++)
            {
                var x = random.Next(0, 300);
                var y = random.Next(0, 160);
                Cv2.Circle(frame, new Point(x, y), random.Next(4, 18),
                    new Scalar(random.Next(30, 230), random.Next(30, 230), random.Next(30, 230)), -1);
            }
            frames[i] = frame;
        }
        return frames;
    }

    private static double BinaryMaskIoU(float[] first, float[] second, float threshold)
    {
        var intersection = 0;
        var union = 0;
        for (var i = 0; i < first.Length; i++)
        {
            var a = first[i] > threshold;
            var b = second[i] > threshold;
            if (a && b) intersection++;
            if (a || b) union++;
        }
        return union == 0 ? 1 : intersection / (double)union;
    }

    private static double IntersectionOverUnion(Rect a, Rect b)
    {
        var intersection = a & b;
        var intersectionArea = Math.Max(0, intersection.Width) * Math.Max(0, intersection.Height);
        var union = a.Width * a.Height + b.Width * b.Height - intersectionArea;
        return union <= 0 ? 0 : intersectionArea / (double)union;
    }
}
