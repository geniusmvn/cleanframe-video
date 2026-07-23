using Erasa.Video.Core.Models;
using System.Text.Json;

namespace Erasa.Video.App.Services;

public sealed record PersistedMediaItem
{
    public Guid Id { get; init; }
    public required string InputPath { get; init; }
    public MediaKind Kind { get; init; }
    public string? PreviewPath { get; init; }
    public string? SuggestedOverlayPath { get; init; }
    public string? SuggestedMaskPath { get; init; }
    public string? SuggestedMaskRawPath { get; init; }
    public string? OutputPath { get; init; }
    public string? MaskPath { get; init; }
    public MaskDocument Mask { get; init; } = new();
    public double DurationSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public JobStatus Status { get; init; }
    public double Progress { get; init; }
    public string? Error { get; init; }
    public bool MaskConfirmed { get; init; }
}

public sealed class QueueStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ERASA_VIDEO", "queue.json");

    public async Task SaveAsync(IEnumerable<MediaItem> items, CancellationToken cancellationToken = default)
    {
        var snapshots = items.Select(item => new PersistedMediaItem
        {
            Id = item.Id,
            InputPath = item.InputPath,
            Kind = item.Kind,
            PreviewPath = item.PreviewPath,
            SuggestedOverlayPath = item.SuggestedOverlayPath,
            SuggestedMaskPath = item.SuggestedMaskPath,
            SuggestedMaskRawPath = item.SuggestedMaskRawPath,
            OutputPath = item.OutputPath,
            MaskPath = item.MaskPath,
            Mask = item.Mask.Clone(),
            DurationSeconds = item.DurationSeconds,
            Width = item.Width,
            Height = item.Height,
            Status = item.Status == JobStatus.Running ? JobStatus.Paused : item.Status,
            Progress = item.Progress,
            Error = item.Error,
            MaskConfirmed = item.MaskConfirmed
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshots, Options), cancellationToken);
        File.Move(temporary, StatePath, overwrite: true);
    }

    public async Task<IReadOnlyList<MediaItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath)) return [];
            await using var stream = File.OpenRead(StatePath);
            var snapshots = await JsonSerializer.DeserializeAsync<PersistedMediaItem[]>(stream, Options, cancellationToken) ?? [];
            return snapshots.Where(x => File.Exists(x.InputPath)).Select(x => new MediaItem
            {
                Id = x.Id,
                InputPath = x.InputPath,
                Kind = x.Kind,
                PreviewPath = x.PreviewPath,
                SuggestedOverlayPath = x.SuggestedOverlayPath,
                SuggestedMaskPath = x.SuggestedMaskPath,
                SuggestedMaskRawPath = x.SuggestedMaskRawPath,
                OutputPath = x.OutputPath,
                MaskPath = x.MaskPath,
                Mask = x.Mask,
                DurationSeconds = x.DurationSeconds,
                Width = x.Width,
                Height = x.Height,
                Status = x.Status == JobStatus.Running ? JobStatus.Paused : x.Status,
                Progress = x.Progress,
                Error = x.Error,
                MaskConfirmed = x.MaskConfirmed
            }).ToArray();
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("QueueLoad", ex);
            return [];
        }
    }
}
