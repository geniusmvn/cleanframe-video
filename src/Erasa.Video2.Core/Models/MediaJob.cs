using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Erasa.Video2.Core.Models;

public sealed class MediaJob : INotifyPropertyChanged
{
    private JobState _state = JobState.Added;
    private double _progress;
    private string? _error;
    private string? _statusMessage;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string InputPath { get; set; } = string.Empty;
    public string? OutputPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? PreviewOutputPath { get; set; }
    public string? MaskPath { get; set; }
    public string? BaseMaskPath { get; set; }
    public string? ResumeDirectory { get; set; }
    public bool MaskConfirmed { get; set; }
    public MediaKind Kind { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FramesPerSecond { get; set; }
    public double DurationSeconds { get; set; }
    public bool HasAudio { get; set; }
    public long FileSizeBytes { get; set; }
    public QualityMode Quality { get; set; } = QualityMode.Beautiful;
    public MaskDocument Mask { get; set; } = new();

    public JobState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, Math.Clamp(value, 0, 1));
    }

    public string? Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    [JsonIgnore]
    public bool HasMaskContent => Mask.HasContent || !string.IsNullOrWhiteSpace(BaseMaskPath);

    [JsonIgnore]
    public string FileName => Path.GetFileName(InputPath);

    [JsonIgnore]
    public string DimensionsText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "Đang đọc";

    [JsonIgnore]
    public string DurationText => Kind == MediaKind.Image
        ? "Ảnh"
        : TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    [JsonIgnore]
    public string StateText => State switch
    {
        JobState.Added => "Đã thêm",
        JobState.LoadingPreview => "Đang đọc",
        JobState.NeedsMask => "Cần tạo mask",
        JobState.MaskDirty => "Mask chưa xác nhận",
        JobState.MaskConfirmed => "Mask đã xác nhận",
        JobState.Previewing => "Đang tạo preview",
        JobState.Ready => "Sẵn sàng",
        JobState.Processing => $"Đang xử lý {Progress:P0}",
        JobState.Paused => "Đã tạm dừng",
        JobState.Completed => "Hoàn thành",
        JobState.Failed => "Có lỗi",
        JobState.Cancelled => "Đã hủy",
        _ => State.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(StateText));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(State) or nameof(Progress)) OnPropertyChanged(nameof(StateText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
