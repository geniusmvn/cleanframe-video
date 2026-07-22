using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Threading;
using CleanFrame.Video2.Engine.Media;
using CleanFrame.Video2.Engine.Processing;

namespace CleanFrame.Video2.Tests;

public sealed class FfmpegIntegrationTests
{
    [Fact]
    public async Task Preview_is_three_seconds_and_keeps_fps_and_audio()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CLEANFRAME_FFMPEG_DIR must point to bundled FFmpeg during CI.");
            return;
        }
        var root = NewRoot();
        try
        {
            var runner = new FfmpegRunner(tools);
            var source = await CreateFixtureAsync(runner, root);
            var original = await runner.ProbeAsync(source, CancellationToken.None);
            var mask = await CreateMaskAsync(root);
            var output = Path.Combine(root, "preview.mp4");
            var pipeline = new VideoPipeline(runner, Path.Combine(root, "temp"));
            await pipeline.ProcessAsync(new VideoJob
            {
                InputPath = source, OutputPath = output, MaskPath = mask,
                Kind = JobKind.Preview, PreviewStartSeconds = 3.8, PreviewDurationSeconds = 3,
                Mode = ProcessingMode.Fast
            }, new PauseGate(), null, CancellationToken.None);
            var metadata = await runner.ProbeAsync(output, CancellationToken.None);
            Assert.InRange(metadata.DurationSeconds, 2.90, 3.10);
            Assert.InRange(metadata.Fps, 23.99, 24.01);
            Assert.Equal(original.Width, metadata.Width);
            Assert.Equal(original.Height, metadata.Height);
            Assert.True(metadata.HasAudio);
            Assert.True(File.Exists(output));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Full_export_keeps_duration_fps_resolution_and_audio()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CLEANFRAME_FFMPEG_DIR must point to bundled FFmpeg during CI.");
            return;
        }
        var root = NewRoot();
        try
        {
            var runner = new FfmpegRunner(tools);
            var source = await CreateFixtureAsync(runner, root);
            var original = await runner.ProbeAsync(source, CancellationToken.None);
            var mask = await CreateMaskAsync(root);
            var output = Path.Combine(root, "full.mp4");
            var pipeline = new VideoPipeline(runner, Path.Combine(root, "temp"));
            await pipeline.ProcessAsync(new VideoJob
            {
                InputPath = source, OutputPath = output, MaskPath = mask,
                Kind = JobKind.Full, Mode = ProcessingMode.Fast
            }, new PauseGate(), null, CancellationToken.None);
            var result = await runner.ProbeAsync(output, CancellationToken.None);
            Assert.InRange(result.DurationSeconds, original.DurationSeconds - 0.08, original.DurationSeconds + 0.08);
            Assert.InRange(result.Fps, original.Fps - 0.01, original.Fps + 0.01);
            Assert.Equal(original.Width, result.Width);
            Assert.Equal(original.Height, result.Height);
            Assert.True(result.HasAudio);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }


    [Fact]
    public async Task Cancelled_job_removes_workspace_and_partial_output()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CLEANFRAME_FFMPEG_DIR must point to bundled FFmpeg during CI.");
            return;
        }
        var root = NewRoot();
        try
        {
            var runner = new FfmpegRunner(tools);
            var source = await CreateFixtureAsync(runner, root);
            var mask = await CreateMaskAsync(root);
            var output = Path.Combine(root, "cancelled.mp4");
            var tempRoot = Path.Combine(root, "temp");
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<double>(value =>
            {
                if (value >= 0.90) cancellation.Cancel();
            });
            var pipeline = new VideoPipeline(runner, tempRoot);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ProcessAsync(new VideoJob
            {
                InputPath = source, OutputPath = output, MaskPath = mask,
                Kind = JobKind.Full, Mode = ProcessingMode.Fast
            }, new PauseGate(), progress, cancellation.Token));

            Assert.False(File.Exists(output));
            Assert.Empty(Directory.Exists(tempRoot) ? Directory.EnumerateDirectories(tempRoot) : Array.Empty<string>());
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial.mp4", SearchOption.TopDirectoryOnly));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string? ResolveTools()
    {
        var dir = Environment.GetEnvironmentVariable("CLEANFRAME_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
        return File.Exists(ffmpeg) ? dir : null;
    }

    private static async Task<string> CreateFixtureAsync(FfmpegRunner runner, string root)
    {
        var path = Path.Combine(root, "synthetic.mp4");
        await runner.RunAsync(runner.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=4",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4", "-shortest",
             "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", path], CancellationToken.None);
        return path;
    }

    private static async Task<string> CreateMaskAsync(string root)
    {
        var path = Path.Combine(root, "mask.cfmask.json");
        var document = new MaskDocument { SourceAspectRatio = 160d / 90 };
        document.Add(new ShapeMaskOperation(MaskTool.Ellipse, new NormalizedRect(0.78, 0.72, 0.12, 0.16), 0.5));
        await document.SaveAsync(path);
        return path;
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "CleanFrameVideo2Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
