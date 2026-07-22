namespace CleanFrame.Video2.Core.Models;

public sealed class VideoJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string InputPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string? MaskPath { get; set; }
    public JobKind Kind { get; set; } = JobKind.Full;
    public ProcessingMode Mode { get; set; } = ProcessingMode.Beautiful;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public double PreviewStartSeconds { get; set; }
    public double PreviewDurationSeconds { get; set; } = 3;
    public double Progress { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
