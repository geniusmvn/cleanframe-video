using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Erasa.Video.Core.Models;

public enum MediaKind { Video, Image }
public enum JobStatus { WaitingForMask, Ready, Queued, Running, Paused, Completed, Failed, Cancelled }
public enum ComputeDevice { Auto, Nvidia, Cpu }

public sealed class MediaItem : INotifyPropertyChanged
{
    private JobStatus _status = JobStatus.WaitingForMask;
    private double _progress;
    private string? _error;
    private int _width;
    private int _height;

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string InputPath { get; init; }
    public required MediaKind Kind { get; init; }
    public string DisplayName => Path.GetFileName(InputPath);
    public string? PreviewPath { get; set; }
    public string? SuggestedOverlayPath { get; set; }
    public string? SuggestedMaskPath { get; set; }
    public string? SuggestedMaskRawPath { get; set; }
    public string? OutputPath { get; set; }
    public string? MaskPath { get; set; }
    public MaskDocument Mask { get; set; } = new();
    public double DurationSeconds { get; set; }
    public int Width { get => _width; set { if (_width != value) { _width = value; Changed(); Changed(nameof(ResolutionText)); } } }
    public int Height { get => _height; set { if (_height != value) { _height = value; Changed(); Changed(nameof(ResolutionText)); } } }

    public JobStatus Status { get => _status; set { if (_status != value) { _status = value; Changed(); Changed(nameof(StatusText)); } } }
    public double Progress { get => _progress; set { if (Math.Abs(_progress - value) > .0001) { _progress = value; Changed(); Changed(nameof(ProgressText)); } } }
    public string? Error { get => _error; set { _error = value; Changed(); Changed(nameof(StatusText)); } }
    public string ResolutionText => Width > 0 ? $"{Width}×{Height}" : string.Empty;
    public string ProgressText => $"{Progress * 100:0}%";
    public string StatusText => Error is not null ? $"Lỗi: {Error}" : Status switch
    {
        JobStatus.WaitingForMask => "Chờ chọn vùng",
        JobStatus.Ready => "Sẵn sàng",
        JobStatus.Queued => "Chờ xử lý",
        JobStatus.Running => $"Đang xử lý {ProgressText}",
        JobStatus.Paused => "Đã tạm dừng",
        JobStatus.Completed => "Hoàn thành",
        JobStatus.Failed => "Thất bại",
        JobStatus.Cancelled => "Đã hủy",
        _ => Status.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
