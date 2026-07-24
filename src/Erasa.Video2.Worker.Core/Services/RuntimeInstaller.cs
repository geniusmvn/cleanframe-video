using System.Text;
using System.Text.Json;
using Erasa.Video2.Core.Models;

namespace Erasa.Video2.Worker.Core.Services;

public sealed class RuntimeInstaller
{
    public RuntimeStatus GetStatus(string runtimeDirectory)
    {
        var marker = Path.Combine(runtimeDirectory, "runtime.ready.json");
        var python = FindPython(runtimeDirectory);
        var sourceFile = Path.Combine(runtimeDirectory, "lama-source", "saicinpainting", "training", "modules", "ffc.py");
        var modelConfig = Path.Combine(runtimeDirectory, "model", "config.yaml");
        var generatorState = Path.Combine(runtimeDirectory, "model", "generator.safetensors");
        var exportMetadata = Path.Combine(runtimeDirectory, "model", "export-metadata.json");
        var status = new RuntimeStatus
        {
            RuntimeDirectory = runtimeDirectory,
            FfmpegReady = File.Exists(ToolPaths.FindFfmpeg("ffmpeg")),
            PythonReady = File.Exists(python),
            OriginalSourceReady = File.Exists(sourceFile),
            ModelReady = File.Exists(modelConfig) && File.Exists(generatorState) && File.Exists(exportMetadata)
        };

        if (File.Exists(marker) && status.PythonReady && status.OriginalSourceReady && status.ModelReady)
        {
            status.State = RuntimeState.Ready;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker, Encoding.UTF8));
                status.Profile = document.RootElement.TryGetProperty("profile", out var profile)
                    ? profile.GetString() ?? "unknown"
                    : "unknown";
                status.Message = "Bộ xử lý LaMa gốc đã được kiểm thử và đóng gói sẵn.";
            }
            catch (JsonException)
            {
                status.State = RuntimeState.Broken;
                status.Profile = "unknown";
                status.Message = "runtime.ready.json không hợp lệ.";
            }
        }
        else
        {
            status.State = Directory.Exists(runtimeDirectory) ? RuntimeState.Broken : RuntimeState.Missing;
            status.Message = status.State == RuntimeState.Missing
                ? "Artifact không có runtime LaMa."
                : "Runtime LaMa trong artifact chưa đầy đủ.";
        }
        return status;
    }

    public static string FindPython(string runtimeDirectory)
        => Path.Combine(runtimeDirectory, "python", "python.exe");
}
