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
    private bool _maskConfirmed;
    private int _queueNumber;

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
    public int Width { get => _width; set { if (_width != value) { _width = value; Changed(); Changed(nameof(ResolutionText)); Changed(nameof(MetadataText)); } } }
    public int Height { get => _height; set { if (_height != value) { _height = value; Changed(); Changed(nameof(ResolutionText)); Changed(nameof(MetadataText)); } } }

    public bool MaskConfirmed
    {
        get => _maskConfirmed;
        set
        {
            if (_maskConfirmed == value) return;
            _maskConfirmed = value;
            Changed();
            Changed(nameof(StatusText));
            Changed(nameof(StatusShortText));
            Changed(nameof(CanProcess));
        }
    }

    public int QueueNumber
    {
        get => _queueNumber;
        set
        {
            if (_queueNumber == value) return;
            _queueNumber = value;
            Changed();
            Changed(nameof(QueueNumberText));
        }
    }

    public JobStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Changed();
            Changed(nameof(StatusText));
            Changed(nameof(StatusShortText));
            Changed(nameof(CanProcess));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            var normalized = Math.Clamp(value, 0, 1);
            if (Math.Abs(_progress - normalized) <= .0001) return;
            _progress = normalized;
            Changed();
            Changed(nameof(ProgressText));
            Changed(nameof(StatusText));
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (string.Equals(_error, value, StringComparison.Ordinal)) return;
            _error = value;
            Changed();
            Changed(nameof(StatusText));
            Changed(nameof(StatusShortText));
            Changed(nameof(ErrorSummary));
            Changed(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasMaskContent => Mask.Operations.Count > 0
        || !string.IsNullOrWhiteSpace(SuggestedMaskPath)
        || !string.IsNullOrWhiteSpace(MaskPath);
    public bool CanProcess => MaskConfirmed && HasMaskContent && Status is not (JobStatus.Running or JobStatus.Queued);
    public string QueueNumberText => QueueNumber <= 0 ? "--" : QueueNumber.ToString("00");
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "Đang đọc thông tin";
    public string MetadataText => Kind == MediaKind.Image
        ? ResolutionText
        : DurationSeconds > 0
            ? $"{ResolutionText}  •  {FormatDuration(DurationSeconds)}"
            : ResolutionText;
    public string ProgressText => $"{Progress * 100:0}%";
    public string ErrorSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Error)) return string.Empty;
            var firstLine = Error.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "Lỗi không xác định";
            return firstLine.Length <= 110 ? firstLine : firstLine[..107] + "…";
        }
    }

    public string StatusShortText => HasError ? "Lỗi" : Status switch
    {
        JobStatus.WaitingForMask when HasMaskContent && !MaskConfirmed => "Cần xác nhận",
        JobStatus.WaitingForMask => "Chọn vùng",
        JobStatus.Ready => "Sẵn sàng",
        JobStatus.Queued => "Đang chờ",
        JobStatus.Running => ProgressText,
        JobStatus.Paused => "Tạm dừng",
        JobStatus.Completed => "Xong",
        JobStatus.Failed => "Lỗi",
        JobStatus.Cancelled => "Đã hủy",
        _ => Status.ToString()
    };

    public string StatusText => HasError ? ErrorSummary : Status switch
    {
        JobStatus.WaitingForMask when HasMaskContent && !MaskConfirmed => "Mask chưa được xác nhận",
        JobStatus.WaitingForMask => "Chưa có vùng cần xóa",
        JobStatus.Ready => "Sẵn sàng xử lý",
        JobStatus.Queued => "Đang chờ trong hàng đợi",
        JobStatus.Running => $"Đang xử lý {ProgressText}",
        JobStatus.Paused => "Đã tạm dừng",
        JobStatus.Completed => "Đã hoàn thành",
        JobStatus.Failed => "Xử lý thất bại",
        JobStatus.Cancelled => "Đã hủy",
        _ => Status.ToString()
    };

    public void NotifyMaskStateChanged()
    {
        Changed(nameof(HasMaskContent));
        Changed(nameof(CanProcess));
        Changed(nameof(StatusText));
        Changed(nameof(StatusShortText));
    }

    private static string FormatDuration(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
