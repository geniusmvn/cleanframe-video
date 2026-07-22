using System.ComponentModel;
using System.Runtime.CompilerServices;
using CleanFrame.Video2.Core.Models;

namespace CleanFrame.Video2.App;

public sealed class VideoJobItem : INotifyPropertyChanged
{
    private JobStatus _status = JobStatus.Pending;
    private double _progress;
    private string? _error;

    public Guid Id { get; } = Guid.NewGuid();
    public string InputPath { get; }
    public string DisplayName => Path.GetFileName(InputPath);
    public string? MaskPath { get; set; }
    public double? SourceAspectRatio { get; set; }
    public VideoJob? ActiveJob { get; set; }
    public JobStatus Status { get => _status; set { if (_status != value) { _status = value; OnChanged(); OnChanged(nameof(StatusText)); OnChanged(nameof(StatusGlyph)); } } }
    public double Progress { get => _progress; set { if (Math.Abs(_progress - value) > 0.0001) { _progress = value; OnChanged(); OnChanged(nameof(ProgressPercent)); } } }
    public double ProgressPercent => Progress * 100;
    public string? Error { get => _error; set { _error = value; OnChanged(); OnChanged(nameof(StatusText)); } }
    public string StatusText => Error is null ? Status switch
    {
        JobStatus.Pending => "Chờ mask",
        JobStatus.Detecting => "Đang tự đề xuất",
        JobStatus.ReadyForMask => "Sẵn sàng chỉnh mask",
        JobStatus.Queued => "Trong hàng đợi",
        JobStatus.Running => $"Đang xử lý {ProgressPercent:0}%",
        JobStatus.Paused => "Đã tạm dừng",
        JobStatus.Completed => "Hoàn thành",
        JobStatus.Failed => $"Lỗi: {Error}",
        JobStatus.Cancelled => "Đã huỷ",
        _ => Status.ToString()
    } : $"Lỗi: {Error}";
    public string StatusGlyph => Status switch
    {
        JobStatus.Completed => "\uE73E",
        JobStatus.Failed => "\uEA39",
        JobStatus.Cancelled => "\uE711",
        JobStatus.Running or JobStatus.Detecting => "\uE895",
        JobStatus.Paused => "\uE769",
        _ => "\uE823"
    };

    public VideoJobItem(string inputPath) => InputPath = inputPath;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
