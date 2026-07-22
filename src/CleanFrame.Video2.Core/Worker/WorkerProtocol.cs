using System.Text.Json;
using System.Text.Json.Serialization;
using CleanFrame.Video2.Core.Models;

namespace CleanFrame.Video2.Core.Worker;

public sealed record WorkerCommand(
    string Type,
    VideoJob? Job = null,
    Guid? JobId = null,
    string? FfmpegDirectory = null,
    int DetectionSamples = 16);

public sealed record DetectionCandidateDto(
    string MaskPath,
    double Score,
    NormalizedRect Bounds,
    int MaskPixels);

public sealed class WorkerEvent : EventArgs
{
    public WorkerEventKind Kind { get; }
    public Guid? JobId { get; }
    public double Progress { get; }
    public string? Message { get; }
    public string? OutputPath { get; }
    public IReadOnlyList<DetectionCandidateDto>? Candidates { get; }

    public WorkerEvent(
        WorkerEventKind Kind,
        Guid? JobId = null,
        double Progress = 0,
        string? Message = null,
        string? OutputPath = null,
        IReadOnlyList<DetectionCandidateDto>? Candidates = null)
    {
        this.Kind = Kind;
        this.JobId = JobId;
        this.Progress = Progress;
        this.Message = Message;
        this.OutputPath = OutputPath;
        this.Candidates = Candidates;
    }
}

public static class WorkerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("Invalid worker JSON.");
}
