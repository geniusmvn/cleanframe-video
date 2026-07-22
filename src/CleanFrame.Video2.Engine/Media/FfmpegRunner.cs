using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CleanFrame.Video2.Engine.Media;

public sealed class FfmpegRunner
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    public FfmpegRunner(string toolsDirectory)
    {
        FfmpegPath = Resolve(toolsDirectory, "ffmpeg");
        FfprobePath = Resolve(toolsDirectory, "ffprobe");
    }

    public async Task<VideoMetadata> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        var json = await RunCaptureAsync(FfprobePath,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", inputPath], cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.First(x => x.GetProperty("codec_type").GetString() == "video");
        var audio = streams.FirstOrDefault(x => x.GetProperty("codec_type").GetString() == "audio");
        var fpsText = video.TryGetProperty("avg_frame_rate", out var avg) ? avg.GetString() : null;
        var fps = ParseRate(fpsText ?? "0/1");
        if (fps <= 0 && video.TryGetProperty("r_frame_rate", out var nominalRate))
            fps = ParseRate(nominalRate.GetString() ?? "0/1");
        if (fps <= 0) throw new InvalidDataException("FFprobe did not report a usable frame rate.");

        var duration = 0d;
        if (doc.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationElement))
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        if (duration <= 0 && video.TryGetProperty("duration", out var streamDuration))
            double.TryParse(streamDuration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        if (duration <= 0) throw new InvalidDataException("FFprobe did not report a usable duration.");
        return new VideoMetadata(
            video.GetProperty("width").GetInt32(),
            video.GetProperty("height").GetInt32(),
            fps,
            duration,
            audio.ValueKind != JsonValueKind.Undefined,
            video.GetProperty("codec_name").GetString() ?? "unknown",
            audio.ValueKind == JsonValueKind.Undefined ? null : audio.GetProperty("codec_name").GetString());
    }

    public Task ExtractFramesAsync(
        string inputPath,
        string outputPattern,
        double? startSeconds,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (startSeconds is not null) { args.Add("-ss"); args.Add(F(startSeconds.Value)); }
        args.AddRange(["-i", inputPath]);
        if (durationSeconds is not null) { args.Add("-t"); args.Add(F(durationSeconds.Value)); }
        args.AddRange(["-vsync", "0", "-start_number", "0", "-y", outputPattern]);
        return RunAsync(FfmpegPath, args, cancellationToken);
    }

    public Task ExtractSamplesAsync(string inputPath, string outputPattern, int sampleCount, double durationSeconds, CancellationToken cancellationToken)
    {
        var fps = Math.Max(0.05, sampleCount / Math.Max(durationSeconds, 0.1));
        return RunAsync(FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-i", inputPath, "-vf", $"fps={F(fps)}", "-frames:v", sampleCount.ToString(CultureInfo.InvariantCulture), "-y", outputPattern],
            cancellationToken);
    }

    public async Task EncodeAsync(
        string framePattern,
        string originalInput,
        string outputPath,
        double fps,
        double? startSeconds,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        var copyAudioArgs = BuildEncodeArguments(framePattern, originalInput, outputPath, fps, startSeconds, durationSeconds, copyAudio: true);
        try
        {
            await RunAsync(FfmpegPath, copyAudioArgs, cancellationToken);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            // Some source codecs cannot be muxed into MP4 unchanged. Retry with AAC
            // so audio is retained instead of failing the whole video.
            try { File.Delete(outputPath); } catch { }
            var aacArgs = BuildEncodeArguments(framePattern, originalInput, outputPath, fps, startSeconds, durationSeconds, copyAudio: false);
            await RunAsync(FfmpegPath, aacArgs, cancellationToken);
        }
    }

    private static IReadOnlyList<string> BuildEncodeArguments(
        string framePattern,
        string originalInput,
        string outputPath,
        double fps,
        double? startSeconds,
        double? durationSeconds,
        bool copyAudio)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-framerate", F(fps), "-start_number", "0", "-i", framePattern
        };
        if (startSeconds is not null) { args.Add("-ss"); args.Add(F(startSeconds.Value)); }
        args.AddRange(["-i", originalInput]);
        if (durationSeconds is not null) { args.Add("-t"); args.Add(F(durationSeconds.Value)); }
        args.AddRange([
            "-map", "0:v:0", "-map", "1:a?",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"
        ]);
        if (copyAudio)
            args.AddRange(["-c:a", "copy"]);
        else
            args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-map_metadata", "1", "-movflags", "+faststart", "-y", outputPath]);
        return args;
    }

    public async Task RunAsync(string executable, IReadOnlyList<string> args, CancellationToken cancellationToken)
        => _ = await RunInternalAsync(executable, args, captureOutput: false, cancellationToken);

    public async Task<string> RunCaptureAsync(string executable, IReadOnlyList<string> args, CancellationToken cancellationToken)
        => await RunInternalAsync(executable, args, captureOutput: true, cancellationToken);

    private static async Task<string> RunInternalAsync(string executable, IReadOnlyList<string> args, bool captureOutput, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        var stdoutTask = captureOutput ? process.StandardOutput.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {stderr.Trim()}");
        return stdout;
    }

    private static string Resolve(string directory, string baseName)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(directory, baseName + ".exe"), baseName + ".exe" }
            : new[] { Path.Combine(directory, baseName), baseName };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
    }

    private static double ParseRate(string rate)
    {
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0) return n / d;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string F(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
