using System.Collections.ObjectModel;
using CleanFrame.Video2.App.Services;
using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Queue;
using CleanFrame.Video2.Core.Worker;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CleanFrame.Video2.App;

public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };

    private readonly ObservableCollection<VideoJobItem> _items = [];
    private readonly Queue<VideoJob> _pendingJobs = new();
    private WorkerSupervisor? _worker;
    private VideoJobItem? _selected;
    private Guid? _runningJobId;
    private JobQueue _persistentQueue = new();
    private readonly string _appData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanFrameVideo2");
    private string QueueStatePath => Path.Combine(_appData, "queue.json");
    private string MaskDirectory => Path.Combine(_appData, "masks");

    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = _items;
        MaskEditor.PanDelta += (_, delta) => EditorScroll.ChangeView(
            Math.Max(0, EditorScroll.HorizontalOffset - delta.DeltaX),
            Math.Max(0, EditorScroll.VerticalOffset - delta.DeltaY),
            null,
            disableAnimation: true);
        var mediaPlayer = new MediaPlayer { AutoPlay = false };
        Player.SetMediaPlayer(mediaPlayer);
        mediaPlayer.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            var session = mediaPlayer.PlaybackSession;
            if (session.NaturalVideoWidth > 0 && session.NaturalVideoHeight > 0)
            {
                var ratio = session.NaturalVideoWidth / (double)session.NaturalVideoHeight;
                if (_selected is not null) _selected.SourceAspectRatio = ratio;
                MaskEditor.VideoAspectRatio = ratio;
                await MaskEditor.SetDocumentAsync(MaskEditor.Document);
            }
        });
        RootGrid.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_appData);
        Directory.CreateDirectory(MaskDirectory);
        try
        {
            _persistentQueue = await JobQueue.LoadAsync(QueueStatePath);
        }
        catch (Exception ex)
        {
            _persistentQueue = new JobQueue();
            ShowWorkerError($"Không đọc được trạng thái hàng đợi cũ; đã mở hàng đợi mới: {ex.Message}");
        }
        RestoreQueue(_persistentQueue.Jobs);
        foreach (var item in _items)
            item.SourceAspectRatio = await TryReadAspectRatioAsync(item.InputPath);
        var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "CleanFrame.Video2.Worker.exe");
        _worker = new WorkerSupervisor(new ProcessWorkerSession(workerPath));
        _worker.EventReceived += Worker_EventReceived;
        await _worker.StartAsync();
        if (!_worker.IsWorkerAlive)
        {
            ShowWorkerError(_worker.LastError ?? "Không mở được worker.");
            return;
        }
        StatusText.Text = "Worker sẵn sàng";
        await StartNextAsync();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        try { await _persistentQueue.SaveAsync(QueueStatePath); } catch { }
        if (_worker is not null) await _worker.DisposeAsync();
        Player.MediaPlayer?.Dispose();
    }

    private void RestoreQueue(IReadOnlyList<VideoJob> jobs)
    {
        foreach (var job in jobs.Where(x => File.Exists(x.InputPath)))
        {
            var item = _items.FirstOrDefault(x => string.Equals(x.InputPath, job.InputPath, StringComparison.OrdinalIgnoreCase));
            if (item is null) { item = new VideoJobItem(job.InputPath); _items.Add(item); }
            item.ActiveJob = job;
            item.MaskPath = job.MaskPath;
            item.Status = job.Status;
            item.Progress = job.Progress;
            item.Error = job.Error;
            if (job.Status == JobStatus.Queued) _pendingJobs.Enqueue(job);
        }
        UpdateQueueCount();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in VideoExtensions) picker.FileTypeFilter.Add(extension);
        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        await AddPathsAsync(files.Select(x => x.Path));
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        try
        {
            await AddPathsAsync(Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path))));
        }
        catch (Exception ex) { ShowWorkerError($"Không đọc được thư mục: {ex.Message}"); }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(path))) continue;
            if (_items.Any(x => string.Equals(x.InputPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new VideoJobItem(path);
            _items.Add(item);
            item.SourceAspectRatio = await TryReadAspectRatioAsync(path);
        }
        UpdateQueueCount();
        if (_selected is null && _items.Count > 0) QueueList.SelectedIndex = 0;
    }

    private static async Task<double?> TryReadAspectRatioAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.GetVideoPropertiesAsync();
            return properties.Width > 0 && properties.Height > 0
                ? properties.Width / (double)properties.Height
                : null;
        }
        catch { return null; }
    }

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = QueueList.SelectedItem as VideoJobItem;
        if (_selected is null) return;
        SelectedFileText.Text = _selected.DisplayName;
        Player.Source = MediaSource.CreateFromUri(new Uri(_selected.InputPath));
        if (!string.IsNullOrWhiteSpace(_selected.MaskPath) && File.Exists(_selected.MaskPath))
            _ = LoadMaskAsync(_selected.MaskPath);
        else
            _ = MaskEditor.SetDocumentAsync(new MaskDocument());
    }

    private async Task LoadMaskAsync(string path)
    {
        try { await MaskEditor.SetDocumentAsync(await MaskDocument.LoadAsync(path)); }
        catch (Exception ex) { ShowWorkerError($"Không đọc được mask: {ex.Message}"); }
    }

    private async void Suggest_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSelected() || _worker is null) return;
        if (_runningJobId is not null) { StatusText.Text = "Hãy chờ tác vụ hiện tại xong."; return; }
        if (!_worker.IsWorkerAlive)
        {
            await _worker.StartAsync();
            if (!_worker.IsWorkerAlive) { ShowWorkerError(_worker.LastError ?? "Không thể khởi động worker."); return; }
        }
        var job = new VideoJob
        {
            Id = Guid.NewGuid(), InputPath = _selected!.InputPath,
            OutputPath = MaskDirectory, Kind = JobKind.Detect, Status = JobStatus.Detecting
        };
        _selected.ActiveJob = job;
        _selected.Status = JobStatus.Detecting;
        _selected.Progress = 0;
        _selected.Error = null;
        _runningJobId = job.Id;
        BusyRing.IsActive = true;
        try
        {
            await _worker.SendAsync(new WorkerCommand("start", job, FfmpegDirectory: ToolDirectory()));
        }
        catch (Exception ex)
        {
            _selected.Status = JobStatus.Failed;
            _selected.Error = ex.Message;
            _runningJobId = null;
            BusyRing.IsActive = false;
            ShowWorkerError(ex.Message);
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSelected() || !EnsureMask()) return;
        if (_selected!.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Paused or JobStatus.Detecting)
        {
            StatusText.Text = "Video này đã có tác vụ đang chờ hoặc đang chạy.";
            return;
        }
        var maskPath = await SaveCurrentMaskAsync(_selected);
        var outputDirectory = Path.Combine(Path.GetDirectoryName(_selected!.InputPath)!, "CleanFrame_Output");
        var output = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(_selected.InputPath) + "_preview_3s.mp4");
        var start = Player.MediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0;
        var job = new VideoJob
        {
            InputPath = _selected.InputPath, OutputPath = output,
            Kind = JobKind.Preview, Mode = SelectedMode(), Status = JobStatus.Queued,
            PreviewStartSeconds = Math.Max(0, start), PreviewDurationSeconds = 3
        };
        job.MaskPath = SnapshotMask(maskPath, job.Id);
        Enqueue(job, _selected);
        await StartNextAsync();
    }

    private async void ProcessAll_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSelected() || !EnsureMask()) return;
        var currentMask = await SaveCurrentMaskAsync(_selected!);
        var sourceRatio = MaskEditor.Document.SourceAspectRatio;
        var skipped = 0;
        foreach (var item in _items.Where(x => x.Status is not (JobStatus.Running or JobStatus.Queued or JobStatus.Paused or JobStatus.Detecting)))
        {
            string? maskPath;
            if (item == _selected)
            {
                maskPath = currentMask;
            }
            else if (ApplySameRatioCheck.IsChecked == true && item.SourceAspectRatio is double itemRatio &&
                     Math.Abs(itemRatio - sourceRatio) <= 0.01)
            {
                maskPath = currentMask;
            }
            else
            {
                maskPath = item.MaskPath;
            }

            if (string.IsNullOrWhiteSpace(maskPath) || !File.Exists(maskPath)) { skipped++; continue; }
            var outputDirectory = Path.Combine(Path.GetDirectoryName(item.InputPath)!, "CleanFrame_Output");
            var output = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(item.InputPath) + "_clean.mp4");
            var job = new VideoJob
            {
                InputPath = item.InputPath, OutputPath = output,
                Kind = JobKind.Full, Mode = SelectedMode(), Status = JobStatus.Queued
            };
            job.MaskPath = SnapshotMask(maskPath, job.Id);
            Enqueue(job, item);
        }
        if (skipped > 0) StatusText.Text = $"Đã bỏ qua {skipped} video chưa có mask hoặc khác tỉ lệ.";
        await StartNextAsync();
    }

    private void Enqueue(VideoJob job, VideoJobItem item)
    {
        item.ActiveJob = job;
        item.MaskPath = job.MaskPath;
        item.Status = JobStatus.Queued;
        item.Progress = 0;
        item.Error = null;
        _pendingJobs.Enqueue(job);
        _persistentQueue.Add(job);
        _ = _persistentQueue.SaveAsync(QueueStatePath);
    }

    private async Task StartNextAsync()
    {
        if (_worker is null || _runningJobId is not null || _pendingJobs.Count == 0) return;
        if (!_worker.IsWorkerAlive)
        {
            await _worker.StartAsync();
            if (!_worker.IsWorkerAlive)
            {
                ShowWorkerError(_worker.LastError ?? "Không thể khởi động lại worker.");
                return;
            }
        }
        var job = _pendingJobs.Dequeue();
        var item = FindItem(job);
        if (item is null) { await StartNextAsync(); return; }
        _runningJobId = job.Id;
        item.Status = JobStatus.Running;
        item.Progress = 0;
        BusyRing.IsActive = true;
        StatusText.Text = $"Đang xử lý {item.DisplayName}";
        _persistentQueue.Update(job.Id, JobStatus.Running);
        await _persistentQueue.SaveAsync(QueueStatePath);
        try { await _worker.SendAsync(new WorkerCommand("start", job, FfmpegDirectory: ToolDirectory())); }
        catch (Exception ex)
        {
            item.Status = JobStatus.Failed;
            item.Error = ex.Message;
            _persistentQueue.Update(job.Id, JobStatus.Failed, item.Progress, ex.Message);
            await _persistentQueue.SaveAsync(QueueStatePath);
            _runningJobId = null;
            BusyRing.IsActive = false;
            ShowWorkerError(ex.Message);
            await StartNextAsync();
        }
    }

    private void Worker_EventReceived(object? sender, WorkerEvent e)
        => DispatcherQueue.TryEnqueue(async () => await HandleWorkerEventAsync(e));

    private async Task HandleWorkerEventAsync(WorkerEvent e)
    {
        var item = e.JobId is null ? null : _items.FirstOrDefault(x => x.ActiveJob?.Id == e.JobId);
        switch (e.Kind)
        {
            case WorkerEventKind.Progress:
                if (item is not null)
                {
                    item.Progress = e.Progress;
                    if (item.ActiveJob?.Kind is not JobKind.Detect)
                        _persistentQueue.Update(e.JobId!.Value, item.Status, e.Progress);
                }
                break;
            case WorkerEventKind.Detection:
                _runningJobId = null;
                BusyRing.IsActive = false;
                if (item is not null)
                {
                    var first = e.Candidates?.FirstOrDefault();
                    if (first is null) { item.Status = JobStatus.Failed; item.Error = "Không tìm thấy vùng overlay cố định đủ tin cậy."; }
                    else
                    {
                        item.MaskPath = first.MaskPath;
                        item.Status = JobStatus.ReadyForMask;
                        await LoadMaskAsync(first.MaskPath);
                        StatusText.Text = "Đã tạo đề xuất. Hãy kiểm tra và chỉnh mask.";
                    }
                }
                await StartNextAsync();
                break;
            case WorkerEventKind.Completed:
                if (item is not null)
                {
                    item.Status = JobStatus.Completed; item.Progress = 1;
                    _persistentQueue.Update(e.JobId!.Value, JobStatus.Completed, 1);
                    StatusText.Text = $"Đã xuất: {e.OutputPath}";
                }
                _runningJobId = null; BusyRing.IsActive = false;
                await _persistentQueue.SaveAsync(QueueStatePath);
                await StartNextAsync();
                break;
            case WorkerEventKind.Cancelled:
                if (item is not null) { item.Status = JobStatus.Cancelled; _persistentQueue.Update(e.JobId!.Value, JobStatus.Cancelled); }
                _runningJobId = null; BusyRing.IsActive = false; StatusText.Text = "Đã huỷ và dọn file tạm.";
                await _persistentQueue.SaveAsync(QueueStatePath);
                await StartNextAsync();
                break;
            case WorkerEventKind.Log:
                if (!string.IsNullOrWhiteSpace(e.Message)) StatusText.Text = e.Message;
                break;
            case WorkerEventKind.Failed:
                if (item is null && _runningJobId is Guid runningId)
                    item = _items.FirstOrDefault(x => x.ActiveJob?.Id == runningId);
                if (item is not null && item.ActiveJob is not null)
                {
                    item.Status = JobStatus.Failed; item.Error = e.Message;
                    if (item.ActiveJob.Kind is not JobKind.Detect)
                        _persistentQueue.Update(item.ActiveJob.Id, JobStatus.Failed, item.Progress, e.Message);
                }
                ShowWorkerError(e.Message ?? "Worker dừng bất thường.");
                _runningJobId = null; BusyRing.IsActive = false;
                await _persistentQueue.SaveAsync(QueueStatePath);
                await StartNextAsync();
                break;
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedOrRunning(JobStatus.Running, JobStatus.Queued);
        if (item?.ActiveJob is null || _worker is null) return;
        if (_runningJobId == item.ActiveJob.Id)
            await _worker.SendAsync(new WorkerCommand("pause", JobId: item.ActiveJob.Id));
        else
            RemovePending(item.ActiveJob.Id);
        item.Status = JobStatus.Paused;
        _persistentQueue.Update(item.ActiveJob.Id, JobStatus.Paused, item.Progress);
        await _persistentQueue.SaveAsync(QueueStatePath);
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedOrRunning(JobStatus.Paused);
        if (item?.ActiveJob is null || _worker is null || item.Status != JobStatus.Paused) return;
        if (_runningJobId == item.ActiveJob.Id)
        {
            await _worker.SendAsync(new WorkerCommand("resume", JobId: item.ActiveJob.Id));
            item.Status = JobStatus.Running;
            _persistentQueue.Update(item.ActiveJob.Id, JobStatus.Running, item.Progress);
        }
        else
        {
            _pendingJobs.Enqueue(item.ActiveJob);
            item.Status = JobStatus.Queued;
            _persistentQueue.Update(item.ActiveJob.Id, JobStatus.Queued, item.Progress);
        }
        await _persistentQueue.SaveAsync(QueueStatePath);
        await StartNextAsync();
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedOrRunning(JobStatus.Running, JobStatus.Queued, JobStatus.Paused, JobStatus.Failed);
        if (item?.ActiveJob is null || _worker is null) return;
        if (_runningJobId == item.ActiveJob.Id)
        {
            await _worker.SendAsync(new WorkerCommand("cancel", JobId: item.ActiveJob.Id));
            return;
        }
        RemovePending(item.ActiveJob.Id);
        item.Status = JobStatus.Cancelled;
        _persistentQueue.Cancel(item.ActiveJob.Id);
        await _persistentQueue.SaveAsync(QueueStatePath);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.ActiveJob is null || _selected.Status is not (JobStatus.Failed or JobStatus.Cancelled)) return;
        if (_selected.ActiveJob.Kind == JobKind.Detect)
        {
            Suggest_Click(sender, e);
            return;
        }
        if (!_persistentQueue.Retry(_selected.ActiveJob.Id)) return;
        _selected.ActiveJob.Attempts++;
        _selected.ActiveJob.Status = JobStatus.Queued;
        _selected.Status = JobStatus.Queued;
        _selected.Error = null;
        _selected.Progress = 0;
        _pendingJobs.Enqueue(_selected.ActiveJob);
        await _persistentQueue.SaveAsync(QueueStatePath);
        await StartNextAsync();
    }

    private async Task<string> SaveCurrentMaskAsync(VideoJobItem item)
    {
        MaskEditor.Document.SourceAspectRatio = MaskEditor.VideoAspectRatio;
        var path = Path.Combine(MaskDirectory, $"{item.Id:N}.cfmask.json");
        await MaskEditor.Document.SaveAsync(path);
        item.MaskPath = path;
        return path;
    }

    private string SnapshotMask(string sourcePath, Guid jobId)
    {
        Directory.CreateDirectory(MaskDirectory);
        var snapshot = Path.Combine(MaskDirectory, $"{jobId:N}.cfmask.json");
        File.Copy(sourcePath, snapshot, overwrite: true);
        return snapshot;
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        foreach (var button in new[] { RectangleTool, EllipseTool, BrushTool, EraserTool, PanTool }) button.IsChecked = ReferenceEquals(button, sender);
        MaskEditor.Tool = Enum.Parse<MaskTool>((string)((FrameworkElement)sender).Tag);
    }
    private void BrushSize_Changed(object sender, RangeBaseValueChangedEventArgs e) => MaskEditor.BrushRadius = e.NewValue;
    private void Softness_Changed(object sender, RangeBaseValueChangedEventArgs e) => MaskEditor.Softness = e.NewValue;
    private async void Undo_Click(object sender, RoutedEventArgs e) => await MaskEditor.UndoAsync();
    private async void Redo_Click(object sender, RoutedEventArgs e) => await MaskEditor.RedoAsync();
    private async void ResetMask_Click(object sender, RoutedEventArgs e) => await MaskEditor.ResetAsync();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Thêm video vào hàng đợi";
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = new List<string>();
        foreach (var storageItem in items)
        {
            if (storageItem is StorageFile file) paths.Add(file.Path);
            else if (storageItem is StorageFolder folder)
                paths.AddRange(Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories));
        }
        await AddPathsAsync(paths);
    }

    private void RemovePending(Guid jobId)
    {
        if (_pendingJobs.Count == 0) return;
        var keep = _pendingJobs.Where(x => x.Id != jobId).ToArray();
        _pendingJobs.Clear();
        foreach (var job in keep) _pendingJobs.Enqueue(job);
    }

    private bool EnsureSelected()
    {
        if (_selected is not null) return true;
        StatusText.Text = "Hãy chọn một video.";
        return false;
    }

    private bool EnsureMask()
    {
        if (MaskEditor.Document.Operations.Count > 0) return true;
        StatusText.Text = "Mask đang trống. Hãy tự đề xuất hoặc vẽ mask trước.";
        return false;
    }

    private VideoJobItem? SelectedOrRunning(params JobStatus[] allowed)
    {
        if (_selected?.ActiveJob is not null && allowed.Contains(_selected.Status)) return _selected;
        return _items.FirstOrDefault(x => x.ActiveJob?.Id == _runningJobId && allowed.Contains(x.Status));
    }
    private VideoJobItem? FindItem(VideoJob job) => _items.FirstOrDefault(x => x.ActiveJob?.Id == job.Id);
    private ProcessingMode SelectedMode() => (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Fast" ? ProcessingMode.Fast : ProcessingMode.Beautiful;
    private string ToolDirectory() => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
    private void UpdateQueueCount() => QueueCountText.Text = $"{_items.Count} video";
    private void ShowWorkerError(string message) { ErrorBar.Message = message; ErrorBar.IsOpen = true; StatusText.Text = message; }
    internal void ShowUnhandledError(string message) => ShowWorkerError($"Ứng dụng gặp lỗi nhưng vẫn đang mở: {message}");

    private void InitializePicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}

