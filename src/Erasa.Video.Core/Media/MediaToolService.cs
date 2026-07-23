using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Erasa.Video.Core.Media;

public sealed record MediaProbeResult(
    int Width,
    int Height,
    double FramesPerSecond,
    double DurationSeconds,
    bool HasAudio);

/// <summary>
/// Lightweight FFmpeg/FFprobe process wrapper used for metadata and thumbnails.
/// It deliberately does not depend on Python or the LaMa model, so the editor can
/// still load media and let the user draw a mask when the inference runtime fails.
/// </summary>
public sealed class MediaToolService
{
    private readonly string _appRoot;

    public MediaToolService(string? appRoot = null)
    {
        _appRoot = Path.GetFullPath(appRoot ?? AppContext.BaseDirectory);
    }

    public string FfmpegPath => Path.Combine(_appRoot, "tools", "ffmpeg", "bin", "ffmpeg.exe");
    public string FfprobePath => Path.Combine(_appRoot, "tools", "ffmpeg", "bin", "ffprobe.exe");
    public bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    public string AvailabilityMessage => IsAvailable
        ? "FFmpeg sẵn sàng"
        : $"Thiếu FFmpeg đóng gói tại {Path.Combine(_appRoot, "tools", "ffmpeg", "bin")}";

    public async Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Không tìm thấy tệp nguồn.", inputPath);
        EnsureAvailable();

        var json = await RunCaptureAsync(
            FfprobePath,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", inputPath],
            cancellationToken);
        return ParseProbeJson(json);
    }

    public async Task CreatePreviewFrameAsync(
        string inputPath,
        string outputPath,
        double timeSeconds = 0,
        int maxWidth = 1600,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Không tìm thấy tệp nguồn.", inputPath);
        EnsureAvailable();

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        try
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
        catch (IOException)
        {
            // FFmpeg will report a clearer message if the file is really locked.
        }

        var filter = $"scale='min({Math.Max(320, maxWidth)},iw)':-2";
        await RunCaptureAsync(
            FfmpegPath,
            [
                "-y", "-v", "error", "-ss", Math.Max(0, timeSeconds).ToString("0.###", CultureInfo.InvariantCulture),
                "-i", inputPath, "-frames:v", "1", "-vf", filter, outputPath
            ],
            cancellationToken);

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("FFmpeg không tạo được ảnh xem trước.");
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var output = await RunCaptureAsync(FfmpegPath, ["-version"], cancellationToken);
        if (!output.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FFmpeg đóng gói không phản hồi đúng.");
    }

    public static MediaProbeResult ParseProbeJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("FFprobe không trả về danh sách stream.");

        JsonElement? video = null;
        var hasAudio = false;
        foreach (var stream in streams.EnumerateArray())
        {
            var type = GetString(stream, "codec_type");
            if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase) && video is null) video = stream;
            if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase)) hasAudio = true;
        }

        if (video is null) throw new InvalidOperationException("Tệp không có luồng hình ảnh.");
        var videoElement = video.Value;
        var width = GetInt(videoElement, "width");
        var height = GetInt(videoElement, "height");
        var fps = ParseRate(GetString(videoElement, "avg_frame_rate") ?? GetString(videoElement, "r_frame_rate"));
        var duration = GetDouble(videoElement, "duration");
        if (duration <= 0 && root.TryGetProperty("format", out var format)) duration = GetDouble(format, "duration");

        if (width <= 0 || height <= 0) throw new InvalidOperationException("Độ phân giải video không hợp lệ.");
        return new MediaProbeResult(width, height, fps, Math.Max(0, duration), hasAudio);
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable) throw new FileNotFoundException(AvailabilityMessage);
    }

    private static async Task<string> RunCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Không khởi động được {Path.GetFileName(executable)}: {ex.Message}", ex);
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Cancellation is reported by the caller after the process exits.
            }
        });

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var detail = LastUsefulLines(error, 10);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"{Path.GetFileName(executable)} thoát mã {process.ExitCode}."
                : detail);
        }
        return output;
    }

    private static string LastUsefulLines(string text, int maximumLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(maximumLines));
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static double GetDouble(JsonElement element, string name)
    {
        var raw = GetString(element, name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var parts = rate.Split('/', 2);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && Math.Abs(denominator) > double.Epsilon)
            return numerator / denominator;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct) ? direct : 0;
    }
}
