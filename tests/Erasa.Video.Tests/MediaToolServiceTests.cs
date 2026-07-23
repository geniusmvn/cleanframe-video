using Erasa.Video.Core.Media;
using System.Diagnostics;

namespace Erasa.Video.Tests;

public sealed class MediaToolServiceTests
{
    [Fact]
    public void ParseProbeJson_ReadsVideoAudioAndMetadata()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "width": 1280, "height": 720, "avg_frame_rate": "24000/1001", "duration": "3.500" },
            { "codec_type": "audio", "sample_rate": "48000" }
          ],
          "format": { "duration": "3.500" }
        }
        """;

        var result = MediaToolService.ParseProbeJson(json);

        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.InRange(result.FramesPerSecond, 23.97, 23.99);
        Assert.Equal(3.5, result.DurationSeconds, 3);
        Assert.True(result.HasAudio);
    }

    [Fact]
    public async Task BundledFfmpeg_CreatesPreviewWithoutPythonOrLama()
    {
        var appRoot = Environment.GetEnvironmentVariable("ERASA_TEST_APP_ROOT");
        if (string.IsNullOrWhiteSpace(appRoot) || !OperatingSystem.IsWindows()) return;

        var service = new MediaToolService(appRoot);
        Assert.True(service.IsAvailable, service.AvailabilityMessage);

        var temporary = Path.Combine(Path.GetTempPath(), "erasa-media-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var input = Path.Combine(temporary, "input.mp4");
            var preview = Path.Combine(temporary, "preview.png");
            await RunAsync(
                service.FfmpegPath,
                [
                    "-y", "-v", "error",
                    "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5:duration=2",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", input
                ]);

            var metadata = await service.ProbeAsync(input);
            await service.CreatePreviewFrameAsync(input, preview, .5, 640);

            Assert.Equal(160, metadata.Width);
            Assert.Equal(90, metadata.Height);
            Assert.InRange(metadata.FramesPerSecond, 4.99, 5.01);
            Assert.True(metadata.HasAudio);
            Assert.True(File.Exists(preview));
            Assert.True(new FileInfo(preview).Length > 0);
        }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); } catch { }
        }
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments)
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Không khởi động được FFmpeg test.");
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await standardError;
        Assert.True(process.ExitCode == 0, error);
    }
}
