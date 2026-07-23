using System.Globalization;
using Erasa.Video2.Core.Models;
using Erasa.Video2.Core.Queue;
using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.Worker.Core.Services;

public sealed class VideoPipelineService(Action<WorkerMessage> emit, FfmpegService ffmpeg, PythonBridgeService bridge)
{
    private const double SegmentSeconds = 20.0;

    public async Task<WorkerMessage> SuggestAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        EnsureRuntime(request);
        var input = RequireFile(request.InputPath, "Thiếu video đầu vào.");
        var output = RequirePath(request.OutputPath, "Thiếu đường dẫn mask đề xuất.");
        return await bridge.RunAsync(
            request.RuntimeDirectory!,
            [
                "suggest-video", "--input", input, "--output", output,
                "--ffmpeg", ffmpeg.FfmpegPath, "--ffprobe", ffmpeg.FfprobePath
            ],
            cancellationToken);
    }

    public async Task<WorkerMessage> PreviewAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        EnsureRuntime(request);
        var input = RequireFile(request.InputPath, "Thiếu tệp đầu vào.");
        var mask = RequireFile(request.MaskPath, "Thiếu mask đã xác nhận.");
        var output = RequirePath(request.OutputPath, "Thiếu đường dẫn preview.");
        var probe = await ffmpeg.ProbeAsync(input, cancellationToken);
        if (probe.DurationSeconds is null or <= 0)
        {
            return await bridge.RunAsync(
                request.RuntimeDirectory!,
                [
                    "inpaint-image", "--input", input, "--mask", mask, "--output", output,
                    "--device", DeviceArgument(request.Profile), "--quality", QualityArgument(request.Quality),
                    "--runtime", request.RuntimeDirectory!
                ],
                cancellationToken);
        }

        var start = Math.Clamp(request.StartSeconds, 0, Math.Max(0, probe.DurationSeconds.Value - 0.1));
        var duration = Math.Min(request.DurationSeconds > 0 ? request.DurationSeconds : 3, probe.DurationSeconds.Value - start);
        var directory = Path.GetDirectoryName(output) ?? ".";
        Directory.CreateDirectory(directory);
        var silent = Path.Combine(directory, Path.GetFileNameWithoutExtension(output) + ".silent.mp4");
        var previewContext = ContextSeconds(probe.FramesPerSecond ?? 30, request.Quality);
        var decodeStart = Math.Max(0, start - previewContext);
        var trimStart = start - decodeStart;
        var decodeEnd = Math.Min(probe.DurationSeconds.Value, start + duration + previewContext);
        await bridge.RunAsync(
            request.RuntimeDirectory!,
            BuildVideoArguments(
                input, mask, silent, decodeStart, decodeEnd - decodeStart, trimStart, duration,
                request.Quality, request.RuntimeDirectory!, DeviceArgument(request.Profile)),
            cancellationToken,
            value => value * 0.9);
        await ffmpeg.MuxAudioAsync(silent, input, output, duration, cancellationToken, start);
        TryDelete(silent);
        return new WorkerMessage
        {
            Kind = "completed",
            Message = "Preview 3 giây đã sẵn sàng.",
            OutputPath = output,
            DurationSeconds = duration,
            Width = probe.Width,
            Height = probe.Height,
            FramesPerSecond = probe.FramesPerSecond,
            HasAudio = probe.HasAudio
        };
    }

    public async Task<WorkerMessage> ProcessAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        EnsureRuntime(request);
        var input = RequireFile(request.InputPath, "Thiếu tệp đầu vào.");
        var mask = RequireFile(request.MaskPath, "Thiếu mask đã xác nhận.");
        var output = RequirePath(request.OutputPath, "Thiếu đường dẫn xuất.");
        var probe = await ffmpeg.ProbeAsync(input, cancellationToken);
        if (probe.DurationSeconds is null or <= 0)
        {
            return await bridge.RunAsync(
                request.RuntimeDirectory!,
                [
                    "inpaint-image", "--input", input, "--mask", mask, "--output", output,
                    "--device", DeviceArgument(request.Profile), "--quality", QualityArgument(request.Quality),
                    "--runtime", request.RuntimeDirectory!
                ],
                cancellationToken);
        }

        var jobDirectory = RequirePath(request.JobDirectory, "Thiếu thư mục resume.");
        Directory.CreateDirectory(jobDirectory);
        JobWorkspace.CleanupPartialFiles(jobDirectory);
        var duration = probe.DurationSeconds.Value;
        var segmentCount = Math.Max(1, (int)Math.Ceiling(duration / SegmentSeconds));
        var firstMissing = JobWorkspace.FindFirstMissingSegment(jobDirectory, segmentCount);
        if (firstMissing > 0)
        {
            emit(new WorkerMessage
            {
                Kind = "log",
                Progress = firstMissing / (double)segmentCount,
                Message = $"Tiếp tục từ segment {firstMissing + 1}/{segmentCount}."
            });
        }

        for (var index = firstMissing; index < segmentCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = index * SegmentSeconds;
            var segmentDuration = Math.Min(SegmentSeconds, duration - start);
            var segment = Path.Combine(jobDirectory, $"segment_{index:D5}.mp4");
            var temporary = segment + ".partial.mp4";
            TryDelete(temporary);
            var segmentStart = index / (double)segmentCount;
            var segmentSpan = 1d / segmentCount;
            emit(new WorkerMessage
            {
                Kind = "progress",
                Progress = segmentStart,
                Message = $"Đang xử lý đoạn {index + 1}/{segmentCount}…"
            });
            var context = ContextSeconds(probe.FramesPerSecond ?? 30, request.Quality);
            var decodeStart = Math.Max(0, start - context);
            var trimStart = start - decodeStart;
            var decodeEnd = Math.Min(duration, start + segmentDuration + context);
            await bridge.RunAsync(
                request.RuntimeDirectory!,
                BuildVideoArguments(
                    input, mask, temporary, decodeStart, decodeEnd - decodeStart, trimStart, segmentDuration,
                    request.Quality, request.RuntimeDirectory!, DeviceArgument(request.Profile)),
                cancellationToken,
                value => segmentStart + value * segmentSpan * 0.96);
            if (!File.Exists(temporary)) throw new InvalidOperationException("Worker không tạo segment kết quả.");
            File.Move(temporary, segment, overwrite: true);
        }

        var segments = Enumerable.Range(0, segmentCount)
            .Select(index => Path.Combine(jobDirectory, $"segment_{index:D5}.mp4"))
            .ToList();
        var silent = Path.Combine(jobDirectory, "joined_silent.mp4");
        emit(new WorkerMessage { Kind = "progress", Progress = 0.97, Message = "Đang ghép các đoạn video…" });
        await ffmpeg.ConcatVideoSegmentsAsync(segments, silent, cancellationToken);
        emit(new WorkerMessage { Kind = "progress", Progress = 0.985, Message = "Đang giữ lại audio gốc…" });
        await ffmpeg.MuxAudioAsync(silent, input, output, duration, cancellationToken);
        TryDelete(silent);
        return new WorkerMessage
        {
            Kind = "completed",
            Progress = 1,
            Message = "Đã xử lý xong.",
            OutputPath = output,
            DurationSeconds = duration,
            Width = probe.Width,
            Height = probe.Height,
            FramesPerSecond = probe.FramesPerSecond,
            HasAudio = probe.HasAudio
        };
    }

    private IEnumerable<string> BuildVideoArguments(
        string input,
        string mask,
        string output,
        double start,
        double duration,
        double trimStart,
        double trimDuration,
        QualityMode quality,
        string runtimeDirectory,
        string device)
    {
        return
        [
            "process-video-segment",
            "--input", input,
            "--mask", mask,
            "--output", output,
            "--start", start.ToString("0.######", CultureInfo.InvariantCulture),
            "--duration", duration.ToString("0.######", CultureInfo.InvariantCulture),
            "--trim-start", trimStart.ToString("0.######", CultureInfo.InvariantCulture),
            "--trim-duration", trimDuration.ToString("0.######", CultureInfo.InvariantCulture),
            "--quality", QualityArgument(quality),
            "--device", device,
            "--runtime", runtimeDirectory,
            "--ffmpeg", ffmpeg.FfmpegPath,
            "--ffprobe", ffmpeg.FfprobePath
        ];
    }

    private static double ContextSeconds(double framesPerSecond, QualityMode quality)
    {
        var frames = quality == QualityMode.Beautiful ? 2 : 1;
        return frames / Math.Max(framesPerSecond, 1);
    }

    private static string QualityArgument(QualityMode quality) => quality == QualityMode.Fast ? "fast" : "beautiful";

    private static string DeviceArgument(string? profile)
        => profile?.ToLowerInvariant() switch
        {
            "cuda" => "cuda",
            "cpu" => "cpu",
            _ => "auto"
        };

    private static void EnsureRuntime(WorkerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuntimeDirectory)) throw new InvalidOperationException("Thiếu thư mục runtime.");
    }

    private static string RequireFile(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException(message, path);
        return path;
    }

    private static string RequirePath(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException(message);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
