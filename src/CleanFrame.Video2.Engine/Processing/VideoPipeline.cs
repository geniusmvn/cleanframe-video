using CleanFrame.Video2.Core.IO;
using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Processing;
using CleanFrame.Video2.Core.Threading;
using CleanFrame.Video2.Core.Worker;
using CleanFrame.Video2.Engine.Detection;
using CleanFrame.Video2.Engine.Media;
using OpenCvSharp;

namespace CleanFrame.Video2.Engine.Processing;

public sealed class VideoPipeline
{
    private readonly FfmpegRunner _ffmpeg;
    private readonly string _tempRoot;

    public VideoPipeline(FfmpegRunner ffmpeg, string tempRoot)
    {
        _ffmpeg = ffmpeg;
        _tempRoot = tempRoot;
        Directory.CreateDirectory(_tempRoot);
    }

    public async Task<IReadOnlyList<DetectionCandidateDto>> DetectAsync(
        VideoJob job,
        int samples,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var metadata = await _ffmpeg.ProbeAsync(job.InputPath, cancellationToken);
        await using var workspace = new JobWorkspace(_tempRoot, job.Id);
        var pattern = Path.Combine(workspace.InputFramesPath, "sample_%05d.png");
        await _ffmpeg.ExtractSamplesAsync(job.InputPath, pattern, samples, metadata.DurationSeconds, cancellationToken);
        var paths = Directory.GetFiles(workspace.InputFramesPath, "sample_*.png").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (paths.Length < 4) throw new InvalidOperationException("Could not extract enough frames for overlay detection.");
        var frames = paths.Select(path => Cv2.ImRead(path, ImreadModes.Color)).ToArray();
        try
        {
            progress?.Report(0.55);
            var detector = new StaticOverlayDetector();
            var candidates = detector.Detect(frames);
            Directory.CreateDirectory(job.OutputPath);
            var result = new List<DetectionCandidateDto>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var path = Path.Combine(job.OutputPath, $"{job.Id:N}_candidate_{i + 1}.cfmask.json");
                await candidate.Mask.SaveAsync(path, cancellationToken);
                var bounds = new NormalizedRect(
                    candidate.Bounds.X / (double)metadata.Width,
                    candidate.Bounds.Y / (double)metadata.Height,
                    candidate.Bounds.Width / (double)metadata.Width,
                    candidate.Bounds.Height / (double)metadata.Height);
                result.Add(new DetectionCandidateDto(path, candidate.Score, bounds, candidate.MaskPixels));
            }
            progress?.Report(1);
            return result;
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    public async Task ProcessAsync(
        VideoJob job,
        PauseGate pauseGate,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.MaskPath)) throw new InvalidOperationException("A user-confirmed mask is required.");
        var metadata = await _ffmpeg.ProbeAsync(job.InputPath, cancellationToken);
        var maskDocument = await MaskDocument.LoadAsync(job.MaskPath, cancellationToken);
        if (maskDocument.Operations.Count == 0)
            throw new InvalidOperationException("The confirmed mask is empty.");
        if (maskDocument.SourceAspectRatio > 0 &&
            Math.Abs(maskDocument.SourceAspectRatio - metadata.Width / (double)metadata.Height) > 0.01)
            throw new InvalidOperationException("Mask aspect ratio does not match this video.");

        await using var workspace = new JobWorkspace(_tempRoot, job.Id);
        var inputPattern = Path.Combine(workspace.InputFramesPath, "frame_%08d.png");
        var outputPattern = Path.Combine(workspace.OutputFramesPath, "frame_%08d.png");
        double? start = null;
        double? duration = null;
        if (job.Kind == JobKind.Preview)
        {
            duration = Math.Min(metadata.DurationSeconds, Math.Min(3, Math.Max(0.1, job.PreviewDurationSeconds)));
            start = Math.Clamp(job.PreviewStartSeconds, 0, Math.Max(0, metadata.DurationSeconds - duration.Value));
        }
        await _ffmpeg.ExtractFramesAsync(job.InputPath, inputPattern, start, duration, cancellationToken);
        var frames = Directory.GetFiles(workspace.InputFramesPath, "frame_*.png").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (frames.Length == 0) throw new InvalidOperationException("FFmpeg extracted no frames.");

        var alpha = MaskRasterizer.Render(maskDocument, metadata.Width, metadata.Height);
        var reconstructionOptions = job.Mode == ProcessingMode.Fast
            ? ReconstructionOptions.Fast
            : ReconstructionOptions.Beautiful;
        var reconstructor = new TemporalReconstructor();
        Mat? previous = null;
        Mat? current = Cv2.ImRead(frames[0], ImreadModes.Color);
        Mat? next = frames.Length > 1 ? Cv2.ImRead(frames[1], ImreadModes.Color) : null;
        try
        {
            for (var i = 0; i < frames.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pauseGate.Wait(cancellationToken);
                using var result = reconstructor.Reconstruct(current!, previous, next, alpha, reconstructionOptions);
                var outputPath = Path.Combine(workspace.OutputFramesPath, $"frame_{i:00000000}.png");
                if (!Cv2.ImWrite(outputPath, result.Frame)) throw new IOException($"Could not write frame {i}.");
                progress?.Report(0.05 + 0.85 * (i + 1) / frames.Length);

                previous?.Dispose();
                previous = current;
                current = next;
                next = i + 2 < frames.Length ? Cv2.ImRead(frames[i + 2], ImreadModes.Color) : null;
            }
        }
        finally
        {
            previous?.Dispose();
            current?.Dispose();
            next?.Dispose();
        }

        var outputDirectory = Path.GetDirectoryName(job.OutputPath)
            ?? throw new InvalidOperationException("Output path must include a directory.");
        Directory.CreateDirectory(outputDirectory);
        var stagedOutput = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(job.OutputPath)}.{job.Id:N}.partial.mp4");
        try
        {
            await _ffmpeg.EncodeAsync(outputPattern, job.InputPath, stagedOutput, metadata.Fps, start, duration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedOutput, job.OutputPath, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            try { File.Delete(stagedOutput); } catch { }
        }
    }
}
