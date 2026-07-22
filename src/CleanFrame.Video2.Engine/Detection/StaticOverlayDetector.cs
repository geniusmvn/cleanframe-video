using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Processing;
using OpenCvSharp;

namespace CleanFrame.Video2.Engine.Detection;

public sealed record DetectionCandidate(MaskDocument Mask, double Score, Rect Bounds, int MaskPixels);

public sealed class StaticOverlayDetector
{
    public IReadOnlyList<DetectionCandidate> Detect(IReadOnlyList<Mat> frames, int maxCandidates = 5)
    {
        if (frames.Count < 4) throw new ArgumentException("At least four sampled frames are required.", nameof(frames));
        var size = frames[0].Size();
        if (frames.Any(x => x.Size() != size)) throw new ArgumentException("All frames must have the same size.");

        using var edgeFrequency = new Mat(size, MatType.CV_32FC1, Scalar.All(0));
        using var motionMean = new Mat(size, MatType.CV_32FC1, Scalar.All(0));
        Mat? previousGray = null;
        foreach (var frame in frames)
        {
            using var gray = new Mat();
            using var edges = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);
            Cv2.Canny(gray, edges, 45, 120);
            using var edgesFloat = new Mat();
            edges.ConvertTo(edgesFloat, MatType.CV_32FC1, 1.0 / 255.0);
            Cv2.Add(edgeFrequency, edgesFloat, edgeFrequency);

            if (previousGray is not null)
            {
                using var diff = new Mat();
                using var diffFloat = new Mat();
                Cv2.Absdiff(gray, previousGray, diff);
                diff.ConvertTo(diffFloat, MatType.CV_32FC1, 1.0 / 32.0);
                Cv2.Min(diffFloat, 1.0, diffFloat);
                Cv2.Add(motionMean, diffFloat, motionMean);
                previousGray.Dispose();
            }
            previousGray = gray.Clone();
        }
        previousGray?.Dispose();
        edgeFrequency.ConvertTo(edgeFrequency, MatType.CV_32FC1, 1.0 / frames.Count);
        motionMean.ConvertTo(motionMean, MatType.CV_32FC1, 1.0 / Math.Max(1, frames.Count - 1));

        using var inverseMotion = new Mat();
        Cv2.Subtract(Scalar.All(1), motionMean, inverseMotion);
        using var score = edgeFrequency.Mul(inverseMotion);
        using var persistent = new Mat();
        Cv2.Threshold(edgeFrequency, persistent, 0.58, 255, ThresholdTypes.Binary);
        persistent.ConvertTo(persistent, MatType.CV_8UC1);
        using var lowMotion = new Mat();
        Cv2.Threshold(motionMean, lowMotion, 0.22, 255, ThresholdTypes.BinaryInv);
        lowMotion.ConvertTo(lowMotion, MatType.CV_8UC1);
        using var binary = new Mat();
        Cv2.BitwiseAnd(persistent, lowMotion, binary);
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 5));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, closeKernel, iterations: 2);
        using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.Dilate(binary, binary, dilateKernel, iterations: 1);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids);
        var frameArea = size.Width * size.Height;
        var candidates = new List<DetectionCandidate>();
        for (var label = 1; label < count; label++)
        {
            var x = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
            var y = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
            var w = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            var h = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            var areaRatio = (double)area / frameArea;
            if (areaRatio is < 0.00002 or > 0.08 || w < 3 || h < 3) continue;

            using var component = new Mat();
            Cv2.InRange(labels, Scalar.All(label), Scalar.All(label), component);
            var meanScore = Cv2.Mean(score, component).Val0;
            var compactness = area / (double)(w * h);
            var candidateScore = meanScore * (0.65 + 0.35 * compactness) * Math.Min(1, frames.Count / 8.0);
            if (candidateScore < 0.12) continue;

            using var tight = new Mat();
            Cv2.MorphologyEx(component, tight, MorphTypes.Close, closeKernel);
            Cv2.Dilate(tight, tight, dilateKernel, iterations: 1);
            var mask = MaskFromMat(tight, size.Width, size.Height);
            if (mask.Operations.Count == 0) continue;
            candidates.Add(new DetectionCandidate(mask, candidateScore, new Rect(x, y, w, h), Cv2.CountNonZero(tight)));
        }
        return candidates.OrderByDescending(x => x.Score).Take(maxCandidates).ToArray();
    }

    private static MaskDocument MaskFromMat(Mat mask, int width, int height)
    {
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var document = new MaskDocument { SourceAspectRatio = width / (double)height };
        foreach (var contour in contours)
        {
            var points = contour.Select(p => new NormalizedPoint(p.X / (double)width, p.Y / (double)height)).ToArray();
            if (points.Length < 3) continue;
            document.Add(new PolygonMaskOperation(points, 0.65));
        }
        return document;
    }
}
