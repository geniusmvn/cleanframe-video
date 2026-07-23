using System.Globalization;
using System.Text.Json;
using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.Worker.Services;

public sealed class FfmpegService
{
    private readonly string _ffmpeg = ToolPaths.FindFfmpeg("ffmpeg");
    private readonly string _ffprobe = ToolPaths.FindFfmpeg("ffprobe");

    public async Task<WorkerMessage> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        EnsureInput(path);
        var result = await ProcessRunner.RunAsync(
            _ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path],
            cancellationToken: cancellationToken);
        EnsureSuccess(result, "Không đọc được thông tin tệp.");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var streams = document.RootElement.GetProperty("streams");
        JsonElement? videoStream = null;
        var hasAudio = false;
        foreach (var stream in streams.EnumerateArray())
        {
            var codecType = stream.TryGetProperty("codec_type", out var typeElement) ? typeElement.GetString() : null;
            if (codecType == "video" && videoStream is null) videoStream = stream;
            if (codecType == "audio") hasAudio = true;
        }
        if (videoStream is null) throw new InvalidOperationException("Tệp không có luồng hình ảnh.");

        var video = videoStream.Value;
        var width = video.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
        var height = video.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
        var fps = ParseFraction(video.TryGetProperty("avg_frame_rate", out var fpsElement) ? fpsElement.GetString() : null);
        if (fps <= 0) fps = ParseFraction(video.TryGetProperty("r_frame_rate", out var rateElement) ? rateElement.GetString() : null);
        var duration = ReadDouble(video, "duration");
        if (duration <= 0 && document.RootElement.TryGetProperty("format", out var format)) duration = ReadDouble(format, "duration");
        var size = new FileInfo(path).Length;

        return new WorkerMessage
        {
            Kind = "completed",
            Message = "Đã đọc thông tin tệp.",
            Width = width,
            Height = height,
            FramesPerSecond = fps,
            DurationSeconds = duration,
            HasAudio = hasAudio,
            FileSizeBytes = size,
            OutputPath = path
        };
    }

    public async Task CreateThumbnailAsync(string input, string output, double timeSeconds, CancellationToken cancellationToken)
    {
        EnsureInput(input);
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        var temporary = output + ".partial.png";
        var arguments = new List<string> { "-y", "-v", "error" };
        if (timeSeconds > 0) arguments.AddRange(["-ss", timeSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", input, "-frames:v", "1", "-vf", "scale='min(1600,iw)':-2", temporary]);
        var result = await ProcessRunner.RunAsync(_ffmpeg, arguments, cancellationToken: cancellationToken);
        EnsureSuccess(result, "Không tạo được ảnh xem trước.");
        if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
            throw new InvalidOperationException("FFmpeg không tạo ảnh xem trước.");
        File.Move(temporary, output, overwrite: true);
    }

    public async Task MuxAudioAsync(
        string silentVideo,
        string originalInput,
        string output,
        double durationSeconds,
        CancellationToken cancellationToken,
        double audioStartSeconds = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        var temporary = output + ".partial.mp4";
        var arguments = new List<string> { "-y", "-v", "error", "-i", silentVideo };
        if (audioStartSeconds > 0)
            arguments.AddRange(["-ss", audioStartSeconds.ToString("0.######", CultureInfo.InvariantCulture)]);
        arguments.AddRange([
            "-i", originalInput,
            "-map", "0:v:0", "-map", "1:a?", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-shortest"
        ]);
        if (durationSeconds > 0)
            arguments.AddRange(["-t", durationSeconds.ToString("0.######", CultureInfo.InvariantCulture)]);
        arguments.Add(temporary);
        var result = await ProcessRunner.RunAsync(_ffmpeg, arguments, cancellationToken: cancellationToken);
        EnsureSuccess(result, "Không ghép được audio vào kết quả.");
        File.Move(temporary, output, overwrite: true);
    }

    public async Task ConcatVideoSegmentsAsync(IReadOnlyList<string> segments, string output, CancellationToken cancellationToken)
    {
        if (segments.Count == 0) throw new ArgumentException("Không có segment để ghép.", nameof(segments));
        var directory = Path.GetDirectoryName(output) ?? ".";
        Directory.CreateDirectory(directory);
        var listPath = Path.Combine(directory, "concat.txt");
        var concatLines = segments.Select(segment =>
        {
            var fullPath = Path.GetFullPath(segment).Replace('\\', '/');
            return $"file '{fullPath.Replace("'", "'\\''", StringComparison.Ordinal)}'";
        });
        await File.WriteAllLinesAsync(listPath, concatLines, cancellationToken);
        var temporary = output + ".partial.mp4";
        var result = await ProcessRunner.RunAsync(
            _ffmpeg,
            ["-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", temporary],
            cancellationToken: cancellationToken);
        EnsureSuccess(result, "Không ghép được các segment video.");
        File.Move(temporary, output, overwrite: true);
    }

    public async Task CreateSyntheticVideoAsync(string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        var result = await ProcessRunner.RunAsync(
            _ffmpeg,
            [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=3",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=3",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", output
            ],
            cancellationToken: cancellationToken);
        EnsureSuccess(result, "Không tạo được video test.");
    }

    public string FfmpegPath => _ffmpeg;
    public string FfprobePath => _ffprobe;

    private static double ReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double ParseFraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return 0;
        if (parts.Length == 1) return numerator;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) || Math.Abs(denominator) < 1e-9) return 0;
        return numerator / denominator;
    }

    private static void EnsureInput(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Không tìm thấy tệp đầu vào.", path);
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode == 0) return;
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        throw new InvalidOperationException($"{message} {detail}".Trim());
    }
}
