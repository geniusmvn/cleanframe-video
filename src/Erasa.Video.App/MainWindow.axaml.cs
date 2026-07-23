using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Erasa.Video.App.Services;
using Erasa.Video.Core.Media;
using Erasa.Video.Core.Models;
using Erasa.Video.Core.Processing;
using Erasa.Video.Core.Queue;
using Erasa.Video.Core.Worker;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

namespace Erasa.Video.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" };

    private readonly ObservableCollection<MediaItem> _items = [];
    private readonly WorkerClient _worker = new();
    private readonly MediaToolService _media = new();
    private readonly QueueStateStore _queueStore = new();
    private readonly string _appData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ERASA_VIDEO");

    private AppSettings _settings = new();
    private MediaItem? _selected;
    private MediaItem? _runningItem;
    private MediaKind _activeTab = MediaKind.Video;
    private CancellationTokenSource? _processingCts;
    private bool _pauseRequested;
    private bool _timelineInternal;
    private bool _loadingSelection;
    private bool _mediaReady;
    private bool _utilityReady;
    private bool _inferenceReady;
    private double _zoom = 1;

    private string WorkDirectory => Path.Combine(_appData, "work");
    private string JobsDirectory => Path.Combine(_appData, "jobs");

    public MainWindow()
    {
        InitializeComponent();
        RefreshQueueView();

        Editor.ZoomChanged += zoom =>
        {
            _zoom = zoom;
            ZoomText.Text = $"{zoom * 100:0}%";
        };

        Editor.MaskChanged += (_, _) =>
        {
            if (_selected is null || _loadingSelection) return;
            _selected.Mask = Editor.Document;
            _selected.MaskPath = null;
            _selected.MaskConfirmed = false;
            _selected.Error = null;
            _selected.Status = JobStatus.WaitingForMask;
            _selected.NotifyMaskStateChanged();
            InvalidateResumeState(_selected);
            HintText.Text = _selected.HasMaskContent
                ? "Mask đã thay đổi. Bấm Xác nhận mask trước khi preview hoặc xử lý."
                : "Hãy vẽ vùng cần xóa.";
            UpdateSelectedState();
            UpdateCounts();
            _ = PersistQueueAsync();
        };

        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WorkDirectory);
            Directory.CreateDirectory(JobsDirectory);
            _settings = await AppSettings.LoadAsync();

            foreach (var item in await _queueStore.LoadAsync())
            {
                _items.Add(item);
                if (item.HasError || item.Width <= 0 || item.Height <= 0
                    || string.IsNullOrWhiteSpace(item.PreviewPath) || !File.Exists(item.PreviewPath))
                    await PrepareItemAsync(item);
            }

            RefreshQueueView();
            UpdateCounts();
            if (QueueList.ItemCount > 0) QueueList.SelectedIndex = 0;

            await RefreshRuntimeStatusAsync();
            EmptyState.IsVisible = _items.Count == 0;
            StatusText.Text = _items.Count == 0
                ? "Sẵn sàng • Thêm ảnh hoặc video để bắt đầu"
                : "Sẵn sàng";
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("Startup", ex);
            StatusText.Text = $"Không khởi tạo được ứng dụng: {ex.Message}";
        }
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        _mediaReady = false;
        _utilityReady = false;
        _inferenceReady = false;
        var messages = new List<string>();

        try
        {
            await _media.ValidateAsync();
            _mediaReady = true;
            messages.Add("FFmpeg sẵn sàng");
        }
        catch (Exception ex)
        {
            messages.Add("FFmpeg lỗi");
            await AppLog.WriteAsync("RuntimeMedia", ex);
        }

        if (_worker.IsUtilityAvailable)
        {
            try
            {
                await _worker.RunAsync(["diagnose-utility"]);
                _utilityReady = true;
            }
            catch (Exception ex)
            {
                await AppLog.WriteAsync("RuntimeUtility", ex);
            }
        }
        messages.Add(_utilityReady ? "Công cụ đề xuất sẵn sàng" : "Công cụ đề xuất lỗi");

        if (_worker.IsRuntimeAvailable)
        {
            try
            {
                await _worker.RunAsync(["diagnose"]);
                _inferenceReady = true;
            }
            catch (Exception ex)
            {
                await AppLog.WriteAsync("RuntimeInference", ex);
            }
        }
        messages.Add(_inferenceReady ? "LaMa gốc sẵn sàng" : "LaMa gốc lỗi");

        RuntimeStatusText.Text = string.Join("  •  ", messages);
        RuntimeStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(_mediaReady && _inferenceReady ? "#2E7D32" : "#B42318"));
        UpdateActionButtons();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _pauseRequested = true;
            _processingCts?.Cancel();
            await _queueStore.SaveAsync(_items);
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("Shutdown", ex);
        }
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Chọn ảnh hoặc video",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Ảnh và video")
                    {
                        Patterns =
                        [
                            "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v",
                            "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.tif", "*.tiff"
                        ]
                    }
                ]
            });
            await AddPathsAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("AddFiles", ex);
            StatusText.Text = ex.Message;
        }
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Chọn thư mục ảnh hoặc video",
                AllowMultiple = false
            });
            var folder = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(folder)) return;
            var paths = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedPath);
            await AddPathsAsync(paths);
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("AddFolder", ex);
            StatusText.Text = $"Không đọc được thư mục: {ex.Message}";
        }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var added = new List<MediaItem>();
        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_items.Any(item => string.Equals(item.InputPath, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            var kind = KindFromPath(path);
            if (kind is null) continue;
            var item = new MediaItem { InputPath = path, Kind = kind.Value };
            _items.Add(item);
            added.Add(item);
            await PrepareItemAsync(item);
        }

        if (added.Count > 0 && !added.Any(item => item.Kind == _activeTab))
            _activeTab = added[0].Kind;

        ApplyTabStyle();
        RefreshQueueView();
        UpdateCounts();
        await PersistQueueAsync();
        if (_selected is null && QueueList.ItemCount > 0) QueueList.SelectedIndex = 0;
    }

    private async Task PrepareItemAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await _media.ProbeAsync(item.InputPath, cancellationToken);
            var preview = Path.Combine(WorkDirectory, $"{item.Id:N}_preview.png");
            await _media.CreatePreviewFrameAsync(item.InputPath, preview, 0, 1600, cancellationToken);
            item.PreviewPath = preview;
            item.Width = metadata.Width;
            item.Height = metadata.Height;
            item.DurationSeconds = item.Kind == MediaKind.Video ? metadata.DurationSeconds : 0;
            item.Error = null;
            item.Status = item.MaskConfirmed ? JobStatus.Ready : JobStatus.WaitingForMask;
            item.NotifyMaskStateChanged();
        }
        catch (Exception ex)
        {
            item.Status = JobStatus.Failed;
            item.Error = $"Không đọc được tệp: {ex.Message}";
            await AppLog.WriteAsync("PrepareItem", ex);
        }
    }

    private async void QueueList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is not MediaItem item) return;
        _selected = item;
        item.Mask ??= new MaskDocument();
        _loadingSelection = true;
        try
        {
            Editor.SetExternalOverlay(null);
            Editor.SetBaseMask(null);
            Editor.SetDocument(item.Mask);

            if (!string.IsNullOrWhiteSpace(item.SuggestedMaskRawPath) && File.Exists(item.SuggestedMaskRawPath)
                && item.Width > 0 && item.Height > 0)
            {
                var raw = await File.ReadAllBytesAsync(item.SuggestedMaskRawPath);
                if (raw.Length == item.Width * item.Height) Editor.SetBaseMask(raw, item.Width, item.Height);
            }
            else if (!string.IsNullOrWhiteSpace(item.SuggestedOverlayPath) && File.Exists(item.SuggestedOverlayPath))
            {
                await using var overlayStream = File.OpenRead(item.SuggestedOverlayPath);
                Editor.SetExternalOverlay(new Bitmap(overlayStream));
            }

            if (!string.IsNullOrWhiteSpace(item.PreviewPath) && File.Exists(item.PreviewPath))
            {
                await using var stream = File.OpenRead(item.PreviewPath);
                Editor.SetSource(new Bitmap(stream));
            }
            else
            {
                Editor.SetSource(null);
            }

            _timelineInternal = true;
            Timeline.Maximum = Math.Max(1, item.DurationSeconds);
            Timeline.Value = 0;
            DurationText.Text = item.Kind == MediaKind.Video ? FormatTime(item.DurationSeconds) : "Ảnh";
            CurrentTimeText.Text = "00:00";
            _timelineInternal = false;
            Timeline.IsEnabled = item.Kind == MediaKind.Video && item.DurationSeconds > 0;
            EmptyState.IsVisible = !Editor.HasSource;
            HintText.Text = item.Status switch
            {
                JobStatus.Failed => item.Error ?? "Không đọc được tệp. Bấm Thử đọc lại hoặc mở log.",
                JobStatus.Paused => "Tác vụ đã tạm dừng. Bấm Tiếp tục để dùng lại các đoạn đã hoàn thành.",
                JobStatus.Completed => $"Đã xuất: {item.OutputPath}",
                _ when item.MaskConfirmed => "Mask đã được xác nhận. Có thể tạo preview hoặc xử lý.",
                _ when item.HasMaskContent => "Hãy kiểm tra vùng màu cam rồi bấm Xác nhận mask.",
                _ => "Vẽ mask hoặc dùng tự động đề xuất cho video."
            };
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("SelectItem", ex);
            StatusText.Text = ex.Message;
        }
        finally
        {
            _loadingSelection = false;
            UpdateSelectedState();
            UpdateActionButtons();
        }
    }

    private async void Timeline_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_timelineInternal || _selected?.Kind != MediaKind.Video || !_media.IsAvailable || _processingCts is not null)
            return;
        CurrentTimeText.Text = FormatTime(e.NewValue);
        var selected = _selected;
        await Task.Delay(160);
        if (!ReferenceEquals(selected, _selected) || Math.Abs(Timeline.Value - e.NewValue) > .01) return;

        try
        {
            var preview = Path.Combine(WorkDirectory, $"{selected.Id:N}_{e.NewValue:0.00}.png");
            await _media.CreatePreviewFrameAsync(selected.InputPath, preview, e.NewValue, 1600);
            await using var stream = File.OpenRead(preview);
            Editor.SetSource(new Bitmap(stream));
            Editor.SetDocument(selected.Mask);
            EmptyState.IsVisible = false;
            selected.Error = null;
            UpdateSelectedState();
        }
        catch (Exception ex)
        {
            selected.Error = $"Không đọc được frame tại {FormatTime(e.NewValue)}: {ex.Message}";
            await AppLog.WriteAsync("TimelinePreview", ex);
            StatusText.Text = selected.Error;
            UpdateSelectedState();
        }
    }

    private async void Suggest_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Kind != MediaKind.Video)
        {
            HintText.Text = "Tự động đề xuất chỉ dành cho overlay tĩnh trong video.";
            return;
        }
        if (_processingCts is not null) return;
        if (!_utilityReady)
        {
            _selected.Error = "Công cụ tự động đề xuất chưa sẵn sàng. Bạn vẫn có thể chọn vùng thủ công.";
            UpdateSelectedState();
            return;
        }

        try
        {
            SetBusy(true, "Đang lấy mẫu nhiều frame để đề xuất vùng tĩnh…");
            InvalidateResumeState(_selected);
            var mask = Path.Combine(WorkDirectory, $"{_selected.Id:N}_suggested.png");
            var result = await _worker.RunAsync(
                ["suggest-video", "--input", _selected.InputPath, "--output", mask],
                evt => Dispatcher.UIThread.Post(() => StatusText.Text = evt.Message ?? StatusText.Text));
            _selected.Mask.Clear();
            _selected.MaskPath = null;
            _selected.SuggestedMaskPath = result.Mask ?? mask;
            _selected.SuggestedMaskRawPath = result.MaskRaw;
            _selected.Width = result.Width ?? _selected.Width;
            _selected.Height = result.Height ?? _selected.Height;
            _selected.DurationSeconds = result.Duration ?? _selected.DurationSeconds;
            _selected.Error = null;
            Editor.SetExternalOverlay(null);

            if (!string.IsNullOrWhiteSpace(result.MaskRaw) && File.Exists(result.MaskRaw)
                && (result.Width ?? _selected.Width) > 0 && (result.Height ?? _selected.Height) > 0)
            {
                var width = result.Width ?? _selected.Width;
                var height = result.Height ?? _selected.Height;
                var raw = await File.ReadAllBytesAsync(result.MaskRaw);
                if (raw.Length == width * height) Editor.SetBaseMask(raw, width, height);
            }
            else if (!string.IsNullOrWhiteSpace(result.Output) && File.Exists(result.Output))
            {
                await using var overlayStream = File.OpenRead(result.Output);
                Editor.SetExternalOverlay(new Bitmap(overlayStream));
            }
            _selected.SuggestedOverlayPath = result.Output;

            _selected.MaskConfirmed = false;
            _selected.Status = JobStatus.WaitingForMask;
            _selected.NotifyMaskStateChanged();
            HintText.Text = "Đã tạo đề xuất. Hãy kiểm tra vùng màu cam, chỉnh nếu cần rồi bấm Xác nhận mask.";
            UpdateSelectedState();
            UpdateCounts();
            await PersistQueueAsync();
        }
        catch (Exception ex)
        {
            _selected.Status = JobStatus.Failed;
            _selected.Error = ex.Message;
            StatusText.Text = ex.Message;
            await AppLog.WriteAsync("Suggest", ex);
            await PersistQueueAsync();
        }
        finally
        {
            SetBusy(false);
            UpdateSelectedState();
        }
    }

    private void Manual_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            HintText.Text = "Hãy chọn một tệp trước.";
            return;
        }
        Editor.Tool = MaskTool.Brush;
        SelectTool(BrushTool);
        CanvasModeText.Text = "CỌ";
        HintText.Text = _selected.HasMaskContent
            ? "Có thể dùng cọ, tẩy, khung hoặc elip để chỉnh mask hiện tại."
            : "Dùng cọ, tẩy, khung hoặc elip để vẽ vùng cần xóa.";
    }

    private async void ConfirmMask_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            if (!_selected.HasMaskContent)
                throw new InvalidOperationException("Chưa có vùng mask để xác nhận.");
            var maskPath = await EnsureMaskFileAsync(_selected);
            if (!File.Exists(maskPath) || new FileInfo(maskPath).Length == 0)
                throw new InvalidOperationException("Không tạo được tệp mask.");
            _selected.MaskConfirmed = true;
            _selected.Status = JobStatus.Ready;
            _selected.Error = null;
            _selected.NotifyMaskStateChanged();
            HintText.Text = "Mask đã được xác nhận. Có thể tạo Preview 3 giây hoặc bấm XỬ LÝ.";
            StatusText.Text = $"Đã xác nhận mask cho {_selected.DisplayName}";
            UpdateSelectedState();
            UpdateCounts();
            await PersistQueueAsync();
        }
        catch (Exception ex)
        {
            _selected.MaskConfirmed = false;
            _selected.Error = ex.Message;
            await AppLog.WriteAsync("ConfirmMask", ex);
            UpdateSelectedState();
        }
    }

    private async Task<string> EnsureMaskAsync(MediaItem item)
    {
        if (!item.MaskConfirmed)
            throw new InvalidOperationException($"{item.DisplayName}: mask chưa được người dùng xác nhận.");
        return await EnsureMaskFileAsync(item);
    }

    private async Task<string> EnsureMaskFileAsync(MediaItem item)
    {
        if (item.Mask.Operations.Count == 0 && !string.IsNullOrWhiteSpace(item.SuggestedMaskPath)
            && File.Exists(item.SuggestedMaskPath))
        {
            if (!string.IsNullOrWhiteSpace(item.SuggestedMaskRawPath) && File.Exists(item.SuggestedMaskRawPath))
            {
                var raw = await File.ReadAllBytesAsync(item.SuggestedMaskRawPath);
                if (!raw.Any(value => value > 8))
                    throw new InvalidOperationException("Mask đề xuất đang rỗng. Hãy chọn vùng thủ công.");
            }
            item.MaskPath = item.SuggestedMaskPath;
            return item.SuggestedMaskPath;
        }
        if (item.Mask.Operations.Count == 0 && string.IsNullOrWhiteSpace(item.SuggestedMaskPath))
            throw new InvalidOperationException($"{item.DisplayName}: chưa có vùng mask.");

        if (item.Width <= 0 || item.Height <= 0)
            throw new InvalidOperationException($"{item.DisplayName}: chưa đọc được độ phân giải nguồn.");
        var width = item.Width;
        var height = item.Height;
        byte[] baseAlpha = [];
        if (!string.IsNullOrWhiteSpace(item.SuggestedMaskRawPath) && File.Exists(item.SuggestedMaskRawPath))
        {
            var raw = await File.ReadAllBytesAsync(item.SuggestedMaskRawPath);
            if (raw.Length == width * height) baseAlpha = raw;
        }
        var alpha = MaskRasterizer.Render(item.Mask, width, height, baseAlpha);
        if (!alpha.Any(value => value > 8))
            throw new InvalidOperationException("Mask đang rỗng. Hãy vẽ lại vùng cần xóa.");
        var path = Path.Combine(WorkDirectory, $"{item.Id:N}_mask.png");
        await MaskPngWriter.WriteGrayscaleAsync(path, width, height, alpha);
        item.MaskPath = path;
        return path;
    }

    private async void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || _processingCts is not null) return;
        var item = _selected;
        if (!_inferenceReady)
        {
            item.Error = "LaMa gốc chưa sẵn sàng. Bấm Mở log để xem chẩn đoán runtime.";
            UpdateSelectedState();
            return;
        }
        if (!item.MaskConfirmed)
        {
            HintText.Text = "Hãy bấm Xác nhận mask trước khi tạo preview.";
            return;
        }
        item.Error = null;
        _processingCts = new CancellationTokenSource();
        _runningItem = item;
        _pauseRequested = false;
        var previewState = PreviewStateDirectory(item);

        try
        {
            var mask = await EnsureMaskAsync(item);
            Directory.CreateDirectory(_settings.OutputDirectory);
            var output = Path.Combine(
                _settings.OutputDirectory,
                Path.GetFileNameWithoutExtension(item.InputPath) + "_preview" +
                (item.Kind == MediaKind.Video ? ".mp4" : ".png"));
            SetBusy(true, "Đang tạo preview…");
            CancelButton.IsEnabled = true;
            await _worker.RunAsync(
                BuildProcessArgs(item, mask, output, preview: true, previewState),
                HandleWorkerEvent,
                _processingCts.Token);
            StatusText.Text = $"Đã tạo preview: {output}";
            OpenPath(output);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Đã hủy tạo preview.";
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            StatusText.Text = ex.Message;
            await AppLog.WriteAsync("Preview", ex);
            UpdateSelectedState();
        }
        finally
        {
            CleanupDirectory(previewState);
            DisposeProcessingState();
            SetBusy(false);
        }
    }

    private async void Process_Click(object? sender, RoutedEventArgs e)
    {
        if (!_inferenceReady)
        {
            StatusText.Text = "LaMa gốc chưa sẵn sàng. Bấm Mở log để xem lỗi runtime.";
            return;
        }
        var candidates = _items
            .Where(item => item.Kind == _activeTab
                && item.MaskConfirmed
                && item.HasMaskContent
                && item.Status is not (JobStatus.Completed or JobStatus.Running or JobStatus.Queued))
            .ToArray();
        if (candidates.Length == 0)
        {
            StatusText.Text = "Chưa có tệp nào có mask đã được xác nhận.";
            HintText.Text = "Chọn vùng cần xóa rồi bấm Xác nhận mask.";
            return;
        }
        await RunQueueAsync(candidates);
    }

    private async Task RunQueueAsync(IReadOnlyList<MediaItem> candidates)
    {
        if (candidates.Count == 0 || _processingCts is not null) return;
        _processingCts = new CancellationTokenSource();
        _pauseRequested = false;
        foreach (var item in candidates)
        {
            if (item.Status != JobStatus.Paused) item.Progress = 0;
            item.Status = JobStatus.Queued;
            item.Error = null;
        }
        await PersistQueueAsync();
        SetQueueBusy(true);
        var completedThisRun = 0;

        try
        {
            foreach (var item in candidates)
            {
                if (_processingCts.IsCancellationRequested) break;
                _runningItem = item;
                _selected = item;
                RefreshQueueView();
                QueueList.SelectedItem = item;

                try
                {
                    var mask = await EnsureMaskAsync(item);
                    Directory.CreateDirectory(_settings.OutputDirectory);
                    var extension = item.Kind == MediaKind.Video ? ".mp4" : ".png";
                    var output = Path.Combine(
                        _settings.OutputDirectory,
                        Path.GetFileNameWithoutExtension(item.InputPath) + "_erasa" + extension);

                    item.Status = JobStatus.Running;
                    item.Progress = Math.Clamp(item.Progress, 0, .99);
                    item.Error = null;
                    StatusText.Text = $"Đang xử lý {item.DisplayName}";
                    await PersistQueueAsync();

                    await _worker.RunAsync(
                        BuildProcessArgs(item, mask, output, preview: false, JobStateDirectory(item)),
                        evt => Dispatcher.UIThread.Post(() =>
                        {
                            if (evt.Progress is double progress) item.Progress = progress;
                            if (!string.IsNullOrWhiteSpace(evt.Message)) StatusText.Text = evt.Message;
                            if (evt.Kind == WorkerEventKind.Checkpoint) _ = PersistQueueAsync();
                            UpdateBatchProgress();
                        }),
                        _processingCts.Token);

                    item.OutputPath = output;
                    item.Status = JobStatus.Completed;
                    item.Progress = 1;
                    item.Error = null;
                    CleanupDirectory(JobStateDirectory(item));
                    completedThisRun++;
                    StatusText.Text = $"Đã xuất: {output}";
                    await PersistQueueAsync();
                }
                catch (OperationCanceledException)
                {
                    if (_pauseRequested)
                    {
                        item.Status = JobStatePolicy.StatusAfterInterruption(pauseRequested: true);
                        item.Error = null;
                        StatusText.Text = "Đã tạm dừng. Các đoạn hoàn thành đã được giữ để tiếp tục.";
                    }
                    else
                    {
                        item.Status = JobStatePolicy.StatusAfterInterruption(pauseRequested: false);
                        item.Progress = 0;
                        item.Error = null;
                        CleanupDirectory(JobStateDirectory(item));
                        StatusText.Text = "Đã hủy tác vụ và dọn dữ liệu tạm.";
                    }
                    await PersistQueueAsync();
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = JobStatus.Failed;
                    item.Error = ex.Message;
                    StatusText.Text = $"{item.DisplayName}: {ex.Message}";
                    await AppLog.WriteAsync("ProcessItem", ex);
                    await PersistQueueAsync();
                    // A failed item must not close the application or block the remaining queue.
                }
                finally
                {
                    _runningItem = null;
                    UpdateCounts();
                    UpdateBatchProgress();
                }
            }

            if (!_pauseRequested && !_processingCts.IsCancellationRequested && completedThisRun > 0)
                StatusText.Text = $"Đã hoàn thành {completedThisRun} tác vụ.";
        }
        finally
        {
            DisposeProcessingState();
            SetQueueBusy(false);
            await PersistQueueAsync();
        }
    }

    private string[] BuildProcessArgs(MediaItem item, string mask, string output, bool preview, string? stateDirectory)
    {
        var args = new List<string>
        {
            item.Kind == MediaKind.Video ? "video" : "image",
            "--input", item.InputPath,
            "--mask", mask,
            "--output", output,
            "--device", _worker.DeviceArgument(_settings.Device)
        };

        if (item.Kind == MediaKind.Video)
        {
            args.AddRange(["--crf", _settings.VideoCrf.ToString(CultureInfo.InvariantCulture)]);
            if (!string.IsNullOrWhiteSpace(stateDirectory))
                args.AddRange(["--state-dir", stateDirectory, "--segment-seconds", "2"]);
            if (preview)
                args.AddRange([
                    "--start", Timeline.Value.ToString(CultureInfo.InvariantCulture),
                    "--duration", "3"
                ]);
        }
        return [.. args];
    }

    private void HandleWorkerEvent(WorkerEvent evt) => Dispatcher.UIThread.Post(() =>
    {
        if (!string.IsNullOrWhiteSpace(evt.Message)) StatusText.Text = evt.Message;
    });

    private void Pause_Click(object? sender, RoutedEventArgs e)
    {
        if (_processingCts is null || _runningItem is null) return;
        _pauseRequested = true;
        _processingCts.Cancel();
    }

    private async void Resume_Click(object? sender, RoutedEventArgs e)
    {
        if (_processingCts is not null) return;
        var item = SelectedOrFirst(JobStatus.Paused);
        if (item is null || !JobStatePolicy.CanResume(item.Status)) return;
        item.Status = JobStatus.Queued;
        item.Error = null;
        await PersistQueueAsync();
        await RunQueueAsync([item]);
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e)
    {
        if (_processingCts is not null) return;
        var item = _selected;
        if (item is null || !item.MaskConfirmed || !JobStatePolicy.CanRetry(item.Status)) return;
        item.Status = JobStatus.Queued;
        item.Error = null;
        await PersistQueueAsync();
        await RunQueueAsync([item]);
    }

    private async void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_processingCts is not null)
        {
            _pauseRequested = false;
            _processingCts.Cancel();
            return;
        }

        var item = _selected;
        if (item is null || item.Status is not (JobStatus.Paused or JobStatus.Queued or JobStatus.Failed)) return;
        CleanupDirectory(JobStateDirectory(item));
        item.Status = JobStatus.Cancelled;
        item.Progress = 0;
        item.Error = null;
        await PersistQueueAsync();
        UpdateCounts();
        UpdateActionButtons();
        StatusText.Text = "Đã hủy tác vụ và dọn dữ liệu tạm.";
    }

    private void Tool_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag ||
            !Enum.TryParse<MaskTool>(tag, out var tool)) return;
        Editor.Tool = tool;
        SelectTool(button);
        CanvasModeText.Text = tool switch
        {
            MaskTool.Brush => "CỌ",
            MaskTool.Eraser => "TẨY",
            MaskTool.Rectangle => "KHUNG",
            MaskTool.Ellipse => "ELIP",
            MaskTool.Pan => "PAN",
            _ => tool.ToString().ToUpperInvariant()
        };
    }

    private void SelectTool(Button active)
    {
        foreach (var button in new[] { BrushTool, EraserTool, RectTool, EllipseTool, PanTool })
            button.Classes.Remove("selected");
        active.Classes.Add("selected");
    }

    private void BrushSlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => Editor.BrushRadius = e.NewValue;

    private void SoftnessSlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => Editor.Softness = e.NewValue;

    private void Undo_Click(object? sender, RoutedEventArgs e) => Editor.Undo();
    private void Redo_Click(object? sender, RoutedEventArgs e) => Editor.Redo();

    private void ClearMask_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is not null)
        {
            _selected.MaskPath = null;
            _selected.SuggestedOverlayPath = null;
            _selected.SuggestedMaskPath = null;
            _selected.SuggestedMaskRawPath = null;
            _selected.MaskConfirmed = false;
            _selected.Status = JobStatus.WaitingForMask;
            _selected.NotifyMaskStateChanged();
            InvalidateResumeState(_selected);
        }
        Editor.ClearMask();
        HintText.Text = "Mask đã được đặt lại. Hãy vẽ vùng cần xóa.";
        UpdateSelectedState();
        UpdateCounts();
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetZoom(Math.Min(3, _zoom + .25));
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetZoom(Math.Max(.5, _zoom - .25));

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, .5, 4);
        Editor.SetZoom(_zoom);
        ZoomText.Text = $"{_zoom * 100:0}%";
    }

    private void VideoTab_Click(object? sender, RoutedEventArgs e) => SwitchTab(MediaKind.Video);

    private void ImageTab_Click(object? sender, RoutedEventArgs e) => SwitchTab(MediaKind.Image);

    private void SwitchTab(MediaKind kind)
    {
        _activeTab = kind;
        ApplyTabStyle();
        var target = _selected?.Kind == kind
            ? _selected
            : _items.FirstOrDefault(item => item.Kind == kind);
        if (!ReferenceEquals(_selected, target))
        {
            _selected = target;
            Editor.SetSource(null);
            Editor.SetExternalOverlay(null);
            Editor.SetBaseMask(null);
        }
        RefreshQueueView();
        QueueList.SelectedItem = target;
        if (target is null)
        {
            HintText.Text = kind == MediaKind.Video ? "Thêm video để bắt đầu." : "Thêm ảnh để bắt đầu.";
            UpdateSelectedState();
        }
        UpdateCounts();
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings);
        if (await window.ShowDialog<bool>(this))
            await RefreshRuntimeStatusAsync();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is not null) OpenPath(Path.GetDirectoryName(_selected.InputPath)!);
    }

    private void OpenOutputFolder_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.OutputDirectory);
        OpenPath(_settings.OutputDirectory);
    }

    private async void RetryLoad_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || _processingCts is not null) return;
        var item = _selected;
        StatusText.Text = $"Đang đọc lại {item.DisplayName}…";
        await RefreshRuntimeStatusAsync();
        await PrepareItemAsync(item);
        if (!string.IsNullOrWhiteSpace(item.PreviewPath) && File.Exists(item.PreviewPath))
        {
            await using var stream = File.OpenRead(item.PreviewPath);
            Editor.SetSource(new Bitmap(stream));
            Editor.SetDocument(item.Mask);
            EmptyState.IsVisible = false;
        }
        UpdateSelectedState();
        UpdateCounts();
        await PersistQueueAsync();
    }

    private void OpenLog_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppLog.LogDirectory);
        if (File.Exists(AppLog.LatestLogPath)) OpenPath(AppLog.LatestLogPath);
        else OpenPath(AppLog.LogDirectory);
    }

    private async void RemoveItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaItem item } || ReferenceEquals(item, _runningItem)) return;
        CleanupItemFiles(item);
        _items.Remove(item);
        if (ReferenceEquals(_selected, item))
        {
            _selected = null;
            Editor.SetSource(null);
            Editor.SetExternalOverlay(null);
            Editor.SetBaseMask(null);
        }
        RefreshQueueView();
        UpdateCounts();
        UpdateSelectedState();
        await PersistQueueAsync();
    }

    private async void ClearQueue_Click(object? sender, RoutedEventArgs e)
    {
        if (_processingCts is not null) return;
        foreach (var item in _items) CleanupItemFiles(item);
        _items.Clear();
        _selected = null;
        Editor.SetSource(null);
        Editor.SetExternalOverlay(null);
        Editor.SetBaseMask(null);
        RefreshQueueView();
        UpdateCounts();
        UpdateSelectedState();
        await PersistQueueAsync();
    }

    private void RefreshQueueView()
    {
        var selected = _selected;
        var activeItems = _items.Where(item => item.Kind == _activeTab).ToArray();
        for (var index = 0; index < activeItems.Length; index++) activeItems[index].QueueNumber = index + 1;
        QueueList.ItemsSource = activeItems;
        if (selected?.Kind == _activeTab) QueueList.SelectedItem = selected;
        EmptyState.IsVisible = selected?.Kind != _activeTab || !Editor.HasSource;
    }

    private void ApplyTabStyle()
    {
        VideoTab.Classes.Remove("selected");
        ImageTab.Classes.Remove("selected");
        (_activeTab == MediaKind.Video ? VideoTab : ImageTab).Classes.Add("selected");
    }

    private void UpdateCounts()
    {
        var activeItems = _items.Where(item => item.Kind == _activeTab).ToArray();
        QueueCountText.Text = $"{activeItems.Length} tệp";
        SelectedCountText.Text = _items.Count == 0
            ? "Chưa chọn tệp"
            : $"Đã chọn {_items.Count} tệp";
        BatchProgressText.Text = $"{activeItems.Count(item => item.Status == JobStatus.Completed)} / {activeItems.Length} hoàn thành";
        UpdateBatchProgress();
        UpdateSelectedState();
        UpdateActionButtons();
    }

    private void UpdateSelectedState()
    {
        var item = _selected;
        var processing = _processingCts is not null;
        if (item is null)
        {
            SelectionStateText.Text = "Chưa chọn tệp";
            ErrorPanel.IsVisible = false;
            ErrorText.Text = string.Empty;
            ConfirmMaskButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            RescanButton.IsEnabled = false;
            ManualButton.IsEnabled = false;
            EmptyState.IsVisible = true;
            return;
        }

        PreviewButton.Content = item.Kind == MediaKind.Video ? "Preview 3 giây" : "Preview ảnh";
        SelectionStateText.Text = item.MaskConfirmed
            ? "Mask đã xác nhận"
            : item.HasMaskContent
                ? "Mask cần xác nhận"
                : item.HasError
                    ? "Không đọc được tệp"
                    : "Chưa chọn vùng";
        ErrorPanel.IsVisible = item.HasError;
        ErrorText.Text = item.Error ?? string.Empty;
        EmptyState.IsVisible = !Editor.HasSource;
        ConfirmMaskButton.IsEnabled = !processing && item.HasMaskContent && !item.MaskConfirmed && !item.HasError;
        PreviewButton.IsEnabled = !processing && item.MaskConfirmed && item.HasMaskContent && _inferenceReady;
        RescanButton.IsEnabled = !processing && item.Kind == MediaKind.Video && _utilityReady && !item.HasError;
        ManualButton.IsEnabled = !processing && Editor.HasSource;
    }

    private void UpdateBatchProgress()
    {
        var activeItems = _items.Where(item => item.Kind == _activeTab).ToArray();
        if (activeItems.Length == 0)
        {
            BatchProgress.Value = 0;
            return;
        }
        BatchProgress.Value = activeItems.Sum(item => item.Status == JobStatus.Completed ? 1 : item.Progress) / activeItems.Length;
    }

    private void UpdateActionButtons()
    {
        var processing = _processingCts is not null;
        PauseButton.IsEnabled = processing && _runningItem is not null;
        var selectedStatus = _selected?.Status;
        CancelButton.IsEnabled = processing || selectedStatus is JobStatus.Paused or JobStatus.Queued or JobStatus.Failed;
        ResumeButton.IsEnabled = !processing && (_selected?.Status == JobStatus.Paused || _items.Any(item => item.Status == JobStatus.Paused));
        RetryButton.IsEnabled = !processing && _selected is not null && _selected.MaskConfirmed && JobStatePolicy.CanRetry(_selected.Status);
        ProcessButton.IsEnabled = !processing && _inferenceReady
            && _items.Any(item => item.Kind == _activeTab && item.MaskConfirmed && item.HasMaskContent
                && item.Status is not (JobStatus.Completed or JobStatus.Running or JobStatus.Queued));
        AutoButton.IsEnabled = !processing && _selected?.Kind == MediaKind.Video && _utilityReady && !_selected.HasError;
        Editor.IsEnabled = !processing && Editor.HasSource;
        UpdateSelectedState();
    }

    private void SetBusy(bool busy, string? text = null)
    {
        Editor.IsEnabled = !busy && Editor.HasSource;
        if (text is not null) StatusText.Text = text;
        if (busy)
        {
            ProcessButton.IsEnabled = false;
            ManualButton.IsEnabled = false;
            AutoButton.IsEnabled = false;
            ConfirmMaskButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            RescanButton.IsEnabled = false;
        }
        else UpdateActionButtons();
    }

    private void SetQueueBusy(bool busy)
    {
        PauseButton.IsEnabled = busy;
        CancelButton.IsEnabled = busy;
        ResumeButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        Editor.IsEnabled = !busy && Editor.HasSource;
        if (busy)
        {
            ProcessButton.IsEnabled = false;
            ManualButton.IsEnabled = false;
            AutoButton.IsEnabled = false;
            ConfirmMaskButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            RescanButton.IsEnabled = false;
        }
        else UpdateActionButtons();
    }

    private void DisposeProcessingState()
    {
        _processingCts?.Dispose();
        _processingCts = null;
        _runningItem = null;
        _pauseRequested = false;
        UpdateActionButtons();
    }

    private MediaItem? SelectedOrFirst(JobStatus status)
        => _selected?.Status == status ? _selected : _items.FirstOrDefault(item => item.Status == status);

    private string JobStateDirectory(MediaItem item)
        => Path.Combine(JobsDirectory, item.Id.ToString("N"));

    private string PreviewStateDirectory(MediaItem item)
        => Path.Combine(WorkDirectory, "preview-state", item.Id.ToString("N"));

    private void InvalidateResumeState(MediaItem item)
    {
        if (!ReferenceEquals(item, _runningItem)) CleanupDirectory(JobStateDirectory(item));
        item.Progress = 0;
        if (item.Status is JobStatus.Paused or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Completed or JobStatus.Ready)
            item.Status = item.MaskConfirmed && item.HasMaskContent ? JobStatus.Ready : JobStatus.WaitingForMask;
    }

    private void CleanupItemFiles(MediaItem item)
    {
        CleanupDirectory(JobStateDirectory(item));
        foreach (var path in new[]
                 { item.PreviewPath, item.SuggestedOverlayPath, item.SuggestedMaskPath, item.SuggestedMaskRawPath, item.MaskPath })
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && path.StartsWith(_appData, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private async Task PersistQueueAsync()
    {
        try
        {
            await _queueStore.SaveAsync(_items);
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("QueueSave", ex);
        }
    }

    private static bool IsSupportedPath(string path) => KindFromPath(path) is not null;

    private static MediaKind? KindFromPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (VideoExtensions.Contains(extension)) return MediaKind.Video;
        if (ImageExtensions.Contains(extension)) return MediaKind.Image;
        return null;
    }

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var files = e.Data.GetFiles();
            if (files is null) return;
            var paths = new List<string>();
            foreach (var item in files)
            {
                var path = item.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (File.Exists(path)) paths.Add(path);
                else if (Directory.Exists(path))
                    paths.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Where(IsSupportedPath));
            }
            await AddPathsAsync(paths);
        }
        catch (Exception ex)
        {
            await AppLog.WriteAsync("Drop", ex);
            StatusText.Text = ex.Message;
        }
    }
}
