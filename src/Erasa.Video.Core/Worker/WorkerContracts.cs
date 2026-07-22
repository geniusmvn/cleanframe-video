using System.Text.Json.Serialization;

namespace Erasa.Video.Core.Worker;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkerEventKind { Log, Metadata, Suggestion, Progress, Checkpoint, Completed, Failed, Cancelled }

public sealed record WorkerEvent(
    WorkerEventKind Kind,
    double? Progress = null,
    string? Message = null,
    string? Output = null,
    string? Mask = null,
    string? MaskRaw = null,
    int? Width = null,
    int? Height = null,
    double? Duration = null,
    double? Fps = null);
