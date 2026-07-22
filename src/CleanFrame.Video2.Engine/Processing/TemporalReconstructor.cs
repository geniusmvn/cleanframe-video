using OpenCvSharp;

namespace CleanFrame.Video2.Engine.Processing;

public sealed record ReconstructionOptions(
    bool HalfResolutionFlow,
    double ConsistencySigma,
    double SpatialBaseWeight,
    bool StabilizeColor)
{
    public static ReconstructionOptions Fast { get; } = new(true, 2.8, 0.35, true);
    public static ReconstructionOptions Beautiful { get; } = new(false, 1.8, 0.18, true);
}

public sealed record ReconstructionResult(Mat Frame, Mat Confidence) : IDisposable
{
    public void Dispose() { Frame.Dispose(); Confidence.Dispose(); }
}

public sealed class TemporalReconstructor
{
    public ReconstructionResult Reconstruct(
        Mat current,
        Mat? previous,
        Mat? next,
        float[] alpha,
        ReconstructionOptions options)
    {
        if (current.Type() != MatType.CV_8UC3) throw new ArgumentException("Expected BGR 8-bit frame.", nameof(current));
        if (alpha.Length != current.Rows * current.Cols) throw new ArgumentException("Mask dimensions do not match frame.", nameof(alpha));

        using var maskFloat = CreateMask(alpha, current.Cols, current.Rows);
        using var maskBinary = new Mat();
        Cv2.Threshold(maskFloat, maskBinary, 0.001, 255, ThresholdTypes.Binary);
        maskBinary.ConvertTo(maskBinary, MatType.CV_8UC1);
        using var spatial = SpatialFallback(current, maskBinary);
        using var prevWarp = previous is null ? null : BuildWarp(current, previous, maskBinary, options);
        using var nextWarp = next is null ? null : BuildWarp(current, next, maskBinary, options);

        var candidate = Fuse(spatial, prevWarp, nextWarp, maskFloat, options.SpatialBaseWeight, out var confidence);
        if (options.StabilizeColor) StabilizeBoundaryColor(current, candidate, maskBinary);
        var output = Composite(current, candidate, maskFloat);
        candidate.Dispose();
        return new ReconstructionResult(output, confidence);
    }

    private static Mat SpatialFallback(Mat frame, Mat mask)
    {
        using var telea = new Mat();
        using var navierStokes = new Mat();
        Cv2.Inpaint(frame, mask, telea, 3, InpaintMethod.Telea);
        Cv2.Inpaint(frame, mask, navierStokes, 3, InpaintMethod.NS);
        var blended = new Mat();
        Cv2.AddWeighted(telea, 0.58, navierStokes, 0.42, 0, blended);
        return blended;
    }

    private static WarpCandidate BuildWarp(Mat current, Mat reference, Mat mask, ReconstructionOptions options)
    {
        using var cleanCurrent = SpatialFallback(current, mask);
        using var cleanReference = SpatialFallback(reference, mask);
        using var currentGrayFull = ToGray(cleanCurrent);
        using var referenceGrayFull = ToGray(cleanReference);
        var flowSize = options.HalfResolutionFlow
            ? new Size(Math.Max(32, current.Cols / 2), Math.Max(32, current.Rows / 2))
            : current.Size();

        using var currentGray = ResizeIfNeeded(currentGrayFull, flowSize);
        using var referenceGray = ResizeIfNeeded(referenceGrayFull, flowSize);
        using var currentToReferenceSmall = new Mat();
        using var referenceToCurrentSmall = new Mat();
        Cv2.CalcOpticalFlowFarneback(currentGray, referenceGray, currentToReferenceSmall,
            0.5, 3, 15, 3, 5, 1.2, OpticalFlowFlags.FarnebackGaussian);
        Cv2.CalcOpticalFlowFarneback(referenceGray, currentGray, referenceToCurrentSmall,
            0.5, 3, 15, 3, 5, 1.2, OpticalFlowFlags.FarnebackGaussian);

        using var currentToReference = UpscaleFlow(currentToReferenceSmall, current.Size());
        using var referenceToCurrent = UpscaleFlow(referenceToCurrentSmall, current.Size());
        using var mapX = new Mat(current.Size(), MatType.CV_32FC1);
        using var mapY = new Mat(current.Size(), MatType.CV_32FC1);
        var confidence = new Mat(current.Size(), MatType.CV_32FC1, Scalar.All(0));
        var sigma2 = 2 * options.ConsistencySigma * options.ConsistencySigma;

        for (var y = 0; y < current.Rows; y++)
        for (var x = 0; x < current.Cols; x++)
        {
            var f = currentToReference.At<Vec2f>(y, x);
            var qx = x + f.Item0;
            var qy = y + f.Item1;
            mapX.Set(y, x, qx);
            mapY.Set(y, x, qy);
            if (qx < 1 || qy < 1 || qx >= current.Cols - 1 || qy >= current.Rows - 1) continue;
            var rx = Math.Clamp((int)Math.Round(qx), 0, current.Cols - 1);
            var ry = Math.Clamp((int)Math.Round(qy), 0, current.Rows - 1);
            var reverse = referenceToCurrent.At<Vec2f>(ry, rx);
            var ex = f.Item0 + reverse.Item0;
            var ey = f.Item1 + reverse.Item1;
            var error2 = ex * ex + ey * ey;
            confidence.Set(y, x, (float)Math.Exp(-error2 / sigma2));
        }

        var warped = new Mat();
        Cv2.Remap(cleanReference, warped, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Reflect101);
        return new WarpCandidate(warped, confidence);
    }

    private static Mat Fuse(
        Mat spatial,
        WarpCandidate? previous,
        WarpCandidate? next,
        Mat mask,
        double spatialBaseWeight,
        out Mat confidence)
    {
        var candidate = spatial.Clone();
        confidence = new Mat(spatial.Size(), MatType.CV_32FC1, Scalar.All(0));
        for (var y = 0; y < spatial.Rows; y++)
        for (var x = 0; x < spatial.Cols; x++)
        {
            if (mask.At<float>(y, x) <= 0) continue;
            var wp = previous?.Confidence.At<float>(y, x) ?? 0;
            var wn = next?.Confidence.At<float>(y, x) ?? 0;
            var temporalWeight = wp + wn;
            var maxConfidence = Math.Max(wp, wn);
            // Temporal data is primary. Spatial inpainting contributes only where
            // bidirectional flow confidence is insufficient.
            var missingTemporal = Math.Clamp((0.72 - maxConfidence) / 0.72, 0, 1);
            var ws = spatialBaseWeight * missingTemporal * missingTemporal;
            var total = temporalWeight + ws;
            var s = spatial.At<Vec3b>(y, x);
            var p = previous?.Frame.At<Vec3b>(y, x) ?? default;
            var n = next?.Frame.At<Vec3b>(y, x) ?? default;
            var value = new Vec3b();
            for (var c = 0; c < 3; c++)
            {
                var sum = s[c] * ws;
                if (previous is not null) sum += p[c] * wp;
                if (next is not null) sum += n[c] * wn;
                value[c] = (byte)Math.Clamp(Math.Round(sum / Math.Max(total, 1e-6)), 0, 255);
            }
            candidate.Set(y, x, value);
            confidence.Set(y, x, (float)Math.Clamp(maxConfidence, 0, 1));
        }
        return candidate;
    }

    private static void StabilizeBoundaryColor(Mat original, Mat candidate, Mat mask)
    {
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        using var dilated = new Mat();
        using var eroded = new Mat();
        using var outerRing = new Mat();
        using var innerRing = new Mat();
        Cv2.Dilate(mask, dilated, kernel, iterations: 2);
        Cv2.Erode(mask, eroded, kernel, iterations: 1);
        Cv2.Subtract(dilated, mask, outerRing);
        Cv2.Subtract(mask, eroded, innerRing);
        if (Cv2.CountNonZero(outerRing) < 12 || Cv2.CountNonZero(innerRing) < 12) return;
        var sourceMean = Cv2.Mean(original, outerRing);
        var candidateMean = Cv2.Mean(candidate, innerRing);
        var bias = new[]
        {
            Math.Clamp(sourceMean.Val0 - candidateMean.Val0, -18, 18),
            Math.Clamp(sourceMean.Val1 - candidateMean.Val1, -18, 18),
            Math.Clamp(sourceMean.Val2 - candidateMean.Val2, -18, 18)
        };
        for (var y = 0; y < candidate.Rows; y++)
        for (var x = 0; x < candidate.Cols; x++)
        {
            if (mask.At<byte>(y, x) == 0) continue;
            var pixel = candidate.At<Vec3b>(y, x);
            for (var c = 0; c < 3; c++)
                pixel[c] = (byte)Math.Clamp(Math.Round(pixel[c] + bias[c]), 0, 255);
            candidate.Set(y, x, pixel);
        }
    }

    private static Mat Composite(Mat original, Mat candidate, Mat alpha)
    {
        var output = original.Clone();
        for (var y = 0; y < original.Rows; y++)
        for (var x = 0; x < original.Cols; x++)
        {
            var a = Math.Clamp(alpha.At<float>(y, x), 0, 1);
            if (a <= 0) continue;
            var source = original.At<Vec3b>(y, x);
            var fill = candidate.At<Vec3b>(y, x);
            var pixel = new Vec3b();
            for (var c = 0; c < 3; c++)
                pixel[c] = (byte)Math.Clamp(Math.Round(source[c] * (1 - a) + fill[c] * a), 0, 255);
            output.Set(y, x, pixel);
        }
        return output;
    }

    private static Mat CreateMask(float[] alpha, int width, int height)
    {
        var mask = new Mat(height, width, MatType.CV_32FC1);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            mask.Set(y, x, Math.Clamp(alpha[y * width + x], 0, 1));
        return mask;
    }

    private static Mat ToGray(Mat source)
    {
        var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Mat ResizeIfNeeded(Mat source, Size target)
    {
        if (source.Size() == target) return source.Clone();
        var resized = new Mat();
        Cv2.Resize(source, resized, target, 0, 0, InterpolationFlags.Area);
        return resized;
    }

    private static Mat UpscaleFlow(Mat flow, Size target)
    {
        if (flow.Size() == target) return flow.Clone();
        var scaleX = target.Width / (double)flow.Cols;
        var scaleY = target.Height / (double)flow.Rows;
        var result = new Mat();
        Cv2.Resize(flow, result, target, 0, 0, InterpolationFlags.Linear);
        for (var y = 0; y < result.Rows; y++)
        for (var x = 0; x < result.Cols; x++)
        {
            var f = result.At<Vec2f>(y, x);
            result.Set(y, x, new Vec2f((float)(f.Item0 * scaleX), (float)(f.Item1 * scaleY)));
        }
        return result;
    }

    private sealed class WarpCandidate : IDisposable
    {
        public Mat Frame { get; }
        public Mat Confidence { get; }
        public WarpCandidate(Mat frame, Mat confidence) { Frame = frame; Confidence = confidence; }
        public void Dispose() { Frame.Dispose(); Confidence.Dispose(); }
    }
}
