using System.Text.Json;
using System.Text.Json.Serialization;
using Erasa.Video2.Core.Models;

namespace Erasa.Video2.Core.Protocol;

public static class WorkerCommands
{
    public const string Probe = "probe";
    public const string Thumbnail = "thumbnail";
    public const string RuntimeStatus = "runtime-status";
    public const string RuntimeInstall = "runtime-install";
    public const string Suggest = "suggest";
    public const string Preview = "preview";
    public const string Process = "process";
    public const string SelfTestUtilities = "self-test-utilities";
    public const string SelfTestRuntime = "self-test-runtime";
}

public sealed class WorkerRequest
{
    public string Command { get; set; } = string.Empty;
    public Guid JobId { get; set; }
    public string? InputPath { get; set; }
    public string? OutputPath { get; set; }
    public string? MaskPath { get; set; }
    public string? JobDirectory { get; set; }
    public string? RuntimeDirectory { get; set; }
    public string? Profile { get; set; }
    public QualityMode Quality { get; set; } = QualityMode.Beautiful;
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkerMessage
{
    public string Kind { get; set; } = "log";
    public double? Progress { get; set; }
    public string? Message { get; set; }
    public string? OutputPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? MaskPath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FramesPerSecond { get; set; }
    public double? DurationSeconds { get; set; }
    public bool? HasAudio { get; set; }
    public long? FileSizeBytes { get; set; }
    public RuntimeStatus? Runtime { get; set; }
    public string? Error { get; set; }

    [JsonIgnore]
    public bool IsCompleted => string.Equals(Kind, "completed", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFailed => string.Equals(Kind, "failed", StringComparison.OrdinalIgnoreCase);
}

public static class WorkerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
