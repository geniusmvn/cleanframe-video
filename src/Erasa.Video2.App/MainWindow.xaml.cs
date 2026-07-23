using System.Collections.ObjectModel;
using System.Diagnostics;
using Erasa.Video2.App.Services;
using Erasa.Video2.App.ViewModels;
using Erasa.Video2.Core.Masking;
using Erasa.Video2.Core.Models;
using Erasa.Video2.Core.Processing;
using Erasa.Video2.Core.Protocol;
using Erasa.Video2.Core.Queue;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using Microsoft.Windows.Storage.Pickers;

namespace Erasa.Video2.App;

public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" };

    private readonly WorkerClient _worker = new();
    private readonly QueueStateStore _queueStore = new(AppPaths.QueueFile);
    private JobViewModel? _selected;
    private AppSettings _settings = new();
    private RuntimeStatus _runtime = new();
    private CancellationTokenSource? _currentOperation;
    private CancellationTokenSource? _timelineOperation;
    private bool _loadingEditor;
    private bool _pauseRequested;
    private MediaKind _activeTab = MediaKind.Video;

    public MainWindow()
    {
        InitializeComponent();
        AppPaths.EnsureCreated();
        Editor.DocumentChanged += Editor_DocumentChanged;
        Editor.PanDelta += Editor_PanDelta;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        SetTool(MaskTool.Brush);
        UpdateButtons();
    }

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public void ShowFatalError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        OverallStatusText.Text = "Ứng dụng gặp lỗi nhưng vẫn đang mở.";
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        try
        {
            _settings = await AppSettings.LoadAsync();
            QualityCombo.SelectedIndex = _settings.Quality == QualityMode.Fast ? 1 : 0;
            foreach (var model in await _queueStore.LoadAsync())
            {
                var viewModel = new JobViewModel(model);
                Jobs.Add(viewModel);
                viewModel.Thumbnail = await BitmapLoader.LoadAsync(model.ThumbnailPath);
            }
            await RefreshRuntimeStatusAsync();
            ApplyTab();
            if (QueueList.Items.Count > 0)
            {
                QueueList.SelectedIndex = 0;
            }
            UpdateCounts();
            OverallStatusText.Text = _worker.IsWorkerAvailable
                ? "Sẵn sàng. Chọn tệp, tạo mask, xác nhận rồi preview."
                : "Artifact thiếu worker riêng; không thể xử lý.";
        }
        catch (Exception exception)
        {
            HandleError("Khởi động", exception);
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            _currentOperation?.Cancel();
            _worker.CancelCurrent();
            await SaveQueueAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("Close", exception);
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                ViewMode = PickerViewMode.Thumbnail
            };
            foreach (var extension in VideoExtensions.Concat(ImageExtensions).OrderBy(value => value))
                picker.FileTypeFilter.Add(extension);
            var files = await picker.PickMultipleFilesAsync();
            await AddPathsAsync(files.Select(file => file.Path));
        }
        catch (Exception exception)
        {
            HandleError("Chọn tệp", exception);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var paths = Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories).Where(IsSupported);
            await AddPathsAsync(paths);
        }
        catch (Exception exception)
        {
            HandleError("Thêm thư mục", exception);
        }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var added = new List<JobViewModel>();
        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSupported(path) || Jobs.Any(job => string.Equals(job.Model.InputPath, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            var model = new MediaJob
            {
                InputPath = path,
                Kind = VideoExtensions.Contains(Path.GetExtension(path)) ? MediaKind.Video : MediaKind.Image,
                State = JobState.LoadingPreview,
                ResumeDirectory = AppPaths.JobDirectory(Guid.NewGuid()),
                Quality = _settings.Quality
            };
            model.ResumeDirectory = AppPaths.JobDirectory(model.Id);
            var viewModel = new JobViewModel(model);
            Jobs.Add(viewModel);
            added.Add(viewModel);
            await PrepareJobAsync(viewModel);
        }
        if (added.Count > 0)
        {
            _activeTab = added[0].Model.Kind;
            ApplyTab();
            QueueList.SelectedItem = added[0];
        }
        UpdateCounts();
        await SaveQueueAsync();
    }

    private async Task PrepareJobAsync(JobViewModel viewModel)
    {
        var job = viewModel.Model;
        job.State = JobState.LoadingPreview;
        job.StatusMessage = "Đang đọc tệp…";
        viewModel.Refresh();
        try
        {
            var probe = await _worker.RunAsync(new WorkerRequest
            {
                Command = WorkerCommands.Probe,
                JobId = job.Id,
                InputPath = job.InputPath
            });
            ApplyProbe(job, probe);
            var thumbnail = Path.Combine(AppPaths.JobDirectory(job.Id), "thumbnail.png");
            var thumbnailResult = await _worker.RunAsync(new WorkerRequest
            {
                Command = WorkerCommands.Thumbnail,
                JobId = job.Id,
                InputPath = job.InputPath,
                OutputPath = thumbnail,
                StartSeconds = 0
            });
            job.ThumbnailPath = thumbnailResult.ThumbnailPath ?? thumbnail;
            viewModel.Thumbnail = await BitmapLoader.LoadAsync(job.ThumbnailPath);
            JobStateMachine.MarkPreviewLoaded(job);
            job.StatusMessage = "Tạo mask rồi bấm Xác nhận mask.";
            job.RefreshComputedProperties();
            viewModel.Refresh();
            if (ReferenceEquals(_selected, viewModel)) await LoadSelectedJobAsync(viewModel);
        }
        catch (Exception exception)
        {
            JobStateMachine.MarkFailed(job, exception.Message);
            job.StatusMessage = "Không đọc được tệp. Bấm Quét lại để thử lại.";
            viewModel.Refresh();
            HandleError(job.FileName, exception, keepPreview: true);
        }
    }

    private static void ApplyProbe(MediaJob job, WorkerMessage probe)
    {
        job.Width = probe.Width ?? job.Width;
        job.Height = probe.Height ?? job.Height;
        job.FramesPerSecond = probe.FramesPerSecond ?? job.FramesPerSecond;
        job.DurationSeconds = probe.DurationSeconds ?? job.DurationSeconds;
        job.HasAudio = probe.HasAudio ?? job.HasAudio;
        job.FileSizeBytes = probe.FileSizeBytes ?? job.FileSizeBytes;
        job.RefreshComputedProperties();
    }

    private async void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is not JobViewModel viewModel) return;
        await LoadSelectedJobAsync(viewModel);
    }

    private async Task LoadSelectedJobAsync(JobViewModel viewModel)
    {
        _selected = viewModel;
        var job = viewModel.Model;
        StopPreviewPlayback();
        _loadingEditor = true;
        try
        {
            var width = Math.Max(1, job.Width > 0 ? job.Width : 1280);
            var height = Math.Max(1, job.Height > 0 ? job.Height : 720);
            ImageSurface.Width = width;
            ImageSurface.Height = height;
            Editor.SetSurfaceSize(width, height);
            Editor.SetDocument(job.Mask);
            if (!string.IsNullOrWhiteSpace(job.BaseMaskPath) && File.Exists(job.BaseMaskPath))
            {
                var baseMask = await MaskFileReader.ReadPgmAsync(job.BaseMaskPath);
                if (baseMask.Width == width && baseMask.Height == height) Editor.SetBaseMask(baseMask.Pixels);
                else Editor.SetBaseMask(null);
            }
            else
            {
                Editor.SetBaseMask(null);
            }
            SourceImage.Source = viewModel.Thumbnail ?? await BitmapLoader.LoadAsync(job.ThumbnailPath);
            EmptyState.Visibility = SourceImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
            Timeline.IsEnabled = job.Kind == MediaKind.Video && job.DurationSeconds > 0;
            Timeline.Maximum = Math.Max(1, job.DurationSeconds);
            Timeline.Value = 0;
            CurrentTimeText.Text = "00:00";
            DurationText.Text = job.DurationText;
            ViewModeText.Text = "CHỈNH MASK";
            OverallStatusText.Text = job.Error ?? job.StatusMessage ?? job.StateText;
            ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(job.Error);
            ErrorBar.Message = job.Error ?? string.Empty;
            DispatcherQueue.TryEnqueue(FitImageToViewport);
        }
        catch (Exception exception)
        {
            HandleError("Mở tệp trong editor", exception, keepPreview: true);
        }
        finally
        {
            _loadingEditor = false;
            UpdateButtons();
            UpdateCounts();
        }
    }

    private void Editor_DocumentChanged(object? sender, EventArgs e)
    {
        if (_loadingEditor || _selected is null) return;
        var job = _selected.Model;
        job.Mask = Editor.Document.Clone();
        if (!Editor.HasBaseMask) job.BaseMaskPath = null;
        job.MaskPath = null;
        job.PreviewOutputPath = null;
        JobStateMachine.MarkMaskChanged(job);
        job.StatusMessage = "Mask đã thay đổi. Hãy bấm Xác nhận mask.";
        _selected.Refresh();
        ViewModeText.Text = "MASK CHƯA XÁC NHẬN";
        OverallStatusText.Text = job.StatusMessage;
        UpdateButtons();
        _ = SaveQueueAsync();
    }

    private void Editor_PanDelta(object? sender, Controls.PanDeltaEventArgs e)
    {
        EditorScroll.ChangeView(
            Math.Max(0, EditorScroll.HorizontalOffset - e.DeltaX),
            Math.Max(0, EditorScroll.VerticalOffset - e.DeltaY),
            null,
            disableAnimation: true);
    }

    private async void ConfirmMask_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var job = _selected.Model;
            var alpha = Editor.RenderCombinedMask();
            if (!MaskRasterizer.HasVisiblePixels(alpha))
            {
                await ShowMessageAsync("Mask trống", "Hãy dùng Cọ, Khung, Elip hoặc Tự động đề xuất để chọn vùng cần xóa.");
                return;
            }
            var path = Path.Combine(AppPaths.JobDirectory(job.Id), "confirmed-mask.pgm");
            await MaskRasterizer.WritePgmAsync(path, alpha, job.Width, job.Height);
            job.Mask = Editor.Document.Clone();
            job.MaskPath = path;
            JobStateMachine.ConfirmMask(job);
            job.ResumeDirectory = Path.Combine(AppPaths.JobDirectory(job.Id), $"resume-{job.Mask.ConfirmedRevision}");
            Directory.CreateDirectory(job.ResumeDirectory);
            var appliedCount = await ApplyConfirmedMaskToSameAspectJobsAsync(job, alpha);
            job.StatusMessage = appliedCount > 0
                ? $"Mask đã xác nhận và áp cho {appliedCount} video cùng tỉ lệ."
                : "Mask đã xác nhận. Có thể tạo Preview 3 giây hoặc Xử lý.";
            _selected.Refresh();
            ViewModeText.Text = "MASK ĐÃ XÁC NHẬN";
            OverallStatusText.Text = job.StatusMessage;
            ErrorBar.IsOpen = false;
            UpdateButtons();
            await SaveQueueAsync();
        }
        catch (Exception exception)
        {
            HandleError("Xác nhận mask", exception, keepPreview: true);
        }
    }

    private async void Suggest_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Model.Kind != MediaKind.Video)
        {
            await ShowMessageAsync("Chỉ dành cho video", "Tự động đề xuất chỉ tìm vùng cố định trong nhiều frame video. Ảnh dùng công cụ thủ công.");
            return;
        }
        if (!await EnsureRuntimeAsync()) return;
        var job = _selected.Model;
        try
        {
            SetBusy(true, "Đang lấy mẫu nhiều frame để đề xuất mask…");
            var path = Path.Combine(AppPaths.JobDirectory(job.Id), "suggested-mask.pgm");
            var result = await RunWorkerAsync(new WorkerRequest
            {
                Command = WorkerCommands.Suggest,
                JobId = job.Id,
                InputPath = job.InputPath,
                OutputPath = path,
                RuntimeDirectory = AppPaths.RuntimeDirectory,
                Profile = _settings.DeviceProfile
            });
            job.BaseMaskPath = result.MaskPath ?? path;
            job.Mask = new MaskDocument { Revision = 1, ConfirmedRevision = -1 };
            job.MaskPath = null;
            job.MaskConfirmed = false;
            job.State = JobState.MaskDirty;
            var mask = await MaskFileReader.ReadPgmAsync(job.BaseMaskPath);
            Editor.SetDocument(job.Mask);
            Editor.SetBaseMask(mask.Pixels);
            job.StatusMessage = "Đây chỉ là đề xuất. Hãy kiểm tra, chỉnh và bấm Xác nhận mask.";
            _selected.Refresh();
            ViewModeText.Text = "ĐỀ XUẤT • CHƯA XÁC NHẬN";
            OverallStatusText.Text = job.StatusMessage;
            UpdateButtons();
            await SaveQueueAsync();
        }
        catch (Exception exception)
        {
            JobStateMachine.MarkFailed(job, exception.Message);
            _selected.Refresh();
            HandleError("Tự động đề xuất", exception, keepPreview: true);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void Manual_Click(object sender, RoutedEventArgs e) => SetTool(MaskTool.Brush);

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !JobStateMachine.CanPreview(_selected.Model)) return;
        if (!await EnsureRuntimeAsync()) return;
        var job = _selected.Model;
        try
        {
            job.State = JobState.Previewing;
            job.Progress = 0;
            _selected.Refresh();
            SetBusy(true, "Đang tạo preview 3 giây…");
            var extension = job.Kind == MediaKind.Video ? ".mp4" : ".png";
            var output = Path.Combine(AppPaths.JobDirectory(job.Id), "preview" + extension);
            var result = await RunWorkerAsync(new WorkerRequest
            {
                Command = WorkerCommands.Preview,
                JobId = job.Id,
                InputPath = job.InputPath,
                MaskPath = job.MaskPath,
                OutputPath = output,
                RuntimeDirectory = AppPaths.RuntimeDirectory,
                StartSeconds = job.Kind == MediaKind.Video ? Timeline.Value : 0,
                DurationSeconds = 3,
                Quality = job.Quality,
                Profile = _settings.DeviceProfile
            }, message =>
            {
                job.Progress = message.Progress ?? job.Progress;
                _selected.Refresh();
            });
            job.PreviewOutputPath = result.OutputPath ?? output;
            job.State = JobState.Ready;
            job.Progress = 1;
            job.StatusMessage = "Preview đã tạo. Kiểm tra kết quả rồi mới Xử lý toàn bộ.";
            _selected.Refresh();
            await ShowPreviewResultAsync(job);
            OverallStatusText.Text = job.StatusMessage;
            await SaveQueueAsync();
        }
        catch (Exception exception)
        {
            JobStateMachine.MarkFailed(job, exception.Message);
            _selected.Refresh();
            HandleError("Preview 3 giây", exception, keepPreview: true);
        }
        finally
        {
            SetBusy(false, string.Empty);
            UpdateButtons();
        }
    }

    private async Task ShowPreviewResultAsync(MediaJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PreviewOutputPath) || !File.Exists(job.PreviewOutputPath)) return;
        if (job.Kind == MediaKind.Video)
        {
            var previewFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(job.PreviewOutputPath));
            PreviewPlayer.Source = MediaSource.CreateFromStorageFile(previewFile);
            PreviewPlayer.Visibility = Visibility.Visible;
            EditorScroll.Visibility = Visibility.Collapsed;
            PreviewPlayer.MediaPlayer?.Play();
        }
        else
        {
            SourceImage.Source = await BitmapLoader.LoadAsync(job.PreviewOutputPath);
        }
        ViewModeText.Text = "PREVIEW KẾT QUẢ";
    }

    private async void Process_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureRuntimeAsync()) return;
        await ProcessQueueAsync(resumeOnly: false);
    }

    private async Task ProcessQueueAsync(bool resumeOnly)
    {
        if (_currentOperation is not null) return;
        var candidates = Jobs
            .Where(viewModel => resumeOnly
                ? viewModel.Model.State == JobState.Paused
                : JobStateMachine.CanProcess(viewModel.Model))
            .ToList();
        if (candidates.Count == 0)
        {
            await ShowMessageAsync("Chưa có tác vụ sẵn sàng", "Mỗi tệp cần có mask đã xác nhận trước khi xử lý.");
            return;
        }

        _currentOperation = new CancellationTokenSource();
        _pauseRequested = false;
        UpdateButtons();
        try
        {
            Directory.CreateDirectory(_settings.OutputDirectory);
            for (var index = 0; index < candidates.Count; index++)
            {
                var viewModel = candidates[index];
                var job = viewModel.Model;
                _selected = viewModel;
                QueueList.SelectedItem = viewModel;
                job.State = JobState.Processing;
                job.Error = null;
                job.Progress = 0;
                viewModel.Refresh();
                var output = BuildOutputPath(job);
                job.OutputPath = output;
                OverallStatusText.Text = $"{job.FileName} • 0%";
                try
                {
                    var result = await RunWorkerAsync(new WorkerRequest
                    {
                        Command = WorkerCommands.Process,
                        JobId = job.Id,
                        InputPath = job.InputPath,
                        MaskPath = job.MaskPath,
                        OutputPath = output,
                        JobDirectory = job.ResumeDirectory ?? AppPaths.JobDirectory(job.Id),
                        RuntimeDirectory = AppPaths.RuntimeDirectory,
                        Quality = job.Quality,
                        Profile = _settings.DeviceProfile
                    }, message =>
                    {
                        job.Progress = message.Progress ?? job.Progress;
                        job.StatusMessage = message.Message;
                        viewModel.Refresh();
                        var overall = (index + job.Progress) / candidates.Count;
                        OverallProgress.Value = overall * 100;
                        OverallStatusText.Text = $"{job.FileName} • {job.Progress:P0} • {message.Message}";
                    }, _currentOperation.Token);
                    job.OutputPath = result.OutputPath ?? output;
                    job.Progress = 1;
                    job.State = JobState.Completed;
                    job.StatusMessage = "Hoàn thành.";
                    viewModel.Refresh();
                    await SaveQueueAsync();
                }
                catch (OperationCanceledException)
                {
                    if (!string.IsNullOrWhiteSpace(job.ResumeDirectory))
                    {
                        if (_pauseRequested) JobWorkspace.CleanupPartialFiles(job.ResumeDirectory);
                        else JobWorkspace.ClearResumeArtifacts(job.ResumeDirectory);
                    }
                    if (!string.IsNullOrWhiteSpace(job.OutputPath)) JobWorkspace.CleanupOutputPartials(job.OutputPath);
                    job.State = _pauseRequested ? JobState.Paused : JobState.Cancelled;
                    job.StatusMessage = _pauseRequested
                        ? "Đã tạm dừng. Các segment hoàn thành được giữ để tiếp tục."
                        : "Đã hủy và dọn file tạm.";
                    viewModel.Refresh();
                    await SaveQueueAsync();
                    break;
                }
                catch (Exception exception)
                {
                    if (!string.IsNullOrWhiteSpace(job.ResumeDirectory)) JobWorkspace.CleanupPartialFiles(job.ResumeDirectory);
                    if (!string.IsNullOrWhiteSpace(job.OutputPath)) JobWorkspace.CleanupOutputPartials(job.OutputPath);
                    JobStateMachine.MarkFailed(job, exception.Message);
                    viewModel.Refresh();
                    HandleError(job.FileName, exception, keepPreview: true);
                    await SaveQueueAsync();
                    break;
                }
            }
        }
        finally
        {
            _currentOperation.Dispose();
            _currentOperation = null;
            _pauseRequested = false;
            UpdateButtons();
            UpdateCounts();
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOperation is null) return;
        _pauseRequested = true;
        _currentOperation.Cancel();
        _worker.CancelCurrent();
        OverallStatusText.Text = "Đang tạm dừng sau khi worker dừng…";
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureRuntimeAsync()) return;
        await ProcessQueueAsync(resumeOnly: true);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !JobStateMachine.CanRetry(_selected.Model)) return;
        var job = _selected.Model;
        if (job.MaskConfirmed && File.Exists(job.MaskPath)) job.State = JobState.MaskConfirmed;
        else job.State = JobState.MaskDirty;
        job.Error = null;
        _selected.Refresh();
        UpdateButtons();
        if (job.State == JobState.MaskConfirmed)
        {
            if (!await EnsureRuntimeAsync()) return;
            await ProcessQueueAsync(resumeOnly: false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOperation is null) return;
        _pauseRequested = false;
        _currentOperation.Cancel();
        _worker.CancelCurrent();
        OverallStatusText.Text = "Đang hủy tác vụ…";
    }

    private async void ReloadFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        StopPreviewPlayback();
        try
        {
            SetBusy(true, "Đang kiểm tra lại tệp và tạo preview…");
            await PrepareJobAsync(_selected);
            if (_selected.Model.State != JobState.Failed)
            {
                await LoadSelectedJobAsync(_selected);
                OverallStatusText.Text = "Đã đọc lại tệp thành công.";
                ErrorBar.IsOpen = false;
            }
        }
        catch (Exception exception)
        {
            HandleError("Quét lại tệp", exception, keepPreview: true);
        }
        finally
        {
            SetBusy(false, string.Empty);
            UpdateButtons();
        }
    }

    private async void Timeline_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        CurrentTimeText.Text = FormatTime(e.NewValue);
        if (_selected?.Model.Kind != MediaKind.Video || _loadingEditor || _worker.IsBusy) return;
        _timelineOperation?.Cancel();
        _timelineOperation?.Dispose();
        _timelineOperation = new CancellationTokenSource();
        var token = _timelineOperation.Token;
        try
        {
            await Task.Delay(250, token);
            if (_selected is null) return;
            var job = _selected.Model;
            var path = Path.Combine(AppPaths.JobDirectory(job.Id), $"scrub-{e.NewValue:0.00}.png");
            var result = await _worker.RunAsync(new WorkerRequest
            {
                Command = WorkerCommands.Thumbnail,
                JobId = job.Id,
                InputPath = job.InputPath,
                OutputPath = path,
                StartSeconds = e.NewValue
            }, cancellationToken: token);
            SourceImage.Source = await BitmapLoader.LoadAsync(result.ThumbnailPath ?? path);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Write("Timeline", exception);
        }
    }

    private async Task<bool> EnsureRuntimeAsync()
    {
        await RefreshRuntimeStatusAsync();
        if (_runtime.IsReady) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Cài bộ xử lý LaMa gốc",
            Content = "Artifact đầy đủ đã kèm LaMa. Nếu thư mục runtime bị thiếu hoặc bị xóa, ứng dụng có thể tải lại Python nhúng, PyTorch, mã nguồn advimman/lama và checkpoint Big-LaMa. Bạn không cần cài hay chạy lệnh.",
            PrimaryButtonText = "Cài tự động",
            SecondaryButtonText = "Chỉ CPU",
            CloseButtonText = "Hủy",
            DefaultButton = ContentDialogButton.Primary
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return false;
        var profile = choice == ContentDialogResult.Secondary ? "cpu" : "auto";
        try
        {
            SetBusy(true, "Đang cài bộ xử lý LaMa gốc…");
            var result = await RunWorkerAsync(new WorkerRequest
            {
                Command = WorkerCommands.RuntimeInstall,
                RuntimeDirectory = AppPaths.LocalRuntimeDirectory,
                Profile = profile
            }, message =>
            {
                BusyText.Text = message.Message ?? BusyText.Text;
                OverallProgress.Value = (message.Progress ?? 0) * 100;
                OverallStatusText.Text = message.Message ?? OverallStatusText.Text;
            });
            _runtime = result.Runtime ?? _runtime;
            UpdateRuntimeBadge();
            return _runtime.IsReady;
        }
        catch (Exception exception)
        {
            HandleError("Cài runtime LaMa", exception, keepPreview: true);
            return false;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        if (!_worker.IsWorkerAvailable)
        {
            _runtime = new RuntimeStatus { State = RuntimeState.Broken, Message = "Thiếu worker" };
            UpdateRuntimeBadge();
            return;
        }
        try
        {
            var result = await _worker.RunAsync(new WorkerRequest
            {
                Command = WorkerCommands.RuntimeStatus,
                RuntimeDirectory = AppPaths.RuntimeDirectory
            });
            _runtime = result.Runtime ?? new RuntimeStatus();
        }
        catch (Exception exception)
        {
            _runtime = new RuntimeStatus { State = RuntimeState.Broken, Message = exception.Message };
            AppLog.Write("Runtime status", exception);
        }
        UpdateRuntimeBadge();
    }

    private void UpdateRuntimeBadge()
    {
        RuntimeText.Text = _runtime.IsReady
            ? $"LaMa sẵn sàng • {_runtime.Profile.ToUpperInvariant()}"
            : _runtime.Message ?? "LaMa chưa cài";
        RuntimeBadge.Background = new SolidColorBrush(_runtime.IsReady
            ? Windows.UI.Color.FromArgb(255, 231, 247, 236)
            : Windows.UI.Color.FromArgb(255, 245, 242, 238));
    }

    private async Task<WorkerMessage> RunWorkerAsync(
        WorkerRequest request,
        Action<WorkerMessage>? update = null,
        CancellationToken cancellationToken = default)
    {
        return await _worker.RunAsync(request, message =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrWhiteSpace(message.Message)) OverallStatusText.Text = message.Message;
                if (message.Progress is not null) OverallProgress.Value = message.Progress.Value * 100;
                update?.Invoke(message);
            });
        }, cancellationToken);
    }

    private void BrushTool_Click(object sender, RoutedEventArgs e) => SetTool(MaskTool.Brush);
    private void EraserTool_Click(object sender, RoutedEventArgs e) => SetTool(MaskTool.Eraser);
    private void RectangleTool_Click(object sender, RoutedEventArgs e) => SetTool(MaskTool.Rectangle);
    private void EllipseTool_Click(object sender, RoutedEventArgs e) => SetTool(MaskTool.Ellipse);
    private void PanTool_Click(object sender, RoutedEventArgs e) => SetTool(MaskTool.Pan);

    private void SetTool(MaskTool tool)
    {
        Editor.Tool = tool;
        BrushTool.IsChecked = tool == MaskTool.Brush;
        EraserTool.IsChecked = tool == MaskTool.Eraser;
        RectangleTool.IsChecked = tool == MaskTool.Rectangle;
        EllipseTool.IsChecked = tool == MaskTool.Ellipse;
        PanTool.IsChecked = tool == MaskTool.Pan;
    }

    private void BrushSize_Changed(object sender, RangeBaseValueChangedEventArgs e) => Editor.BrushRadius = e.NewValue;
    private void Softness_Changed(object sender, RangeBaseValueChangedEventArgs e) => Editor.Softness = e.NewValue;
    private void Undo_Click(object sender, RoutedEventArgs e) { Editor.Undo(); UpdateButtons(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Editor.Redo(); UpdateButtons(); }
    private void Reset_Click(object sender, RoutedEventArgs e) { Editor.ResetMask(); UpdateButtons(); }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => ChangeZoom(Math.Min(4, EditorScroll.ZoomFactor + 0.25f));

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => ChangeZoom(Math.Max(0.25f, EditorScroll.ZoomFactor - 0.25f));

    private void ChangeZoom(float zoom)
    {
        EditorScroll.ChangeView(null, null, zoom);
        ZoomText.Text = $"{zoom * 100:0}%";
    }

    private void FitImageToViewport()
    {
        if (_selected is null || ImageSurface.Width <= 0 || ImageSurface.Height <= 0) return;
        var width = Math.Max(1, EditorScroll.ActualWidth - 24);
        var height = Math.Max(1, EditorScroll.ActualHeight - 24);
        var zoom = (float)Math.Clamp(Math.Min(width / ImageSurface.Width, height / ImageSurface.Height), 0.25, 1.0);
        EditorScroll.ChangeView(0, 0, zoom, disableAnimation: true);
        ZoomText.Text = $"{zoom * 100:0}%";
    }

    private void VideoTab_Click(object sender, RoutedEventArgs e)
    {
        _activeTab = MediaKind.Video;
        ApplyTab();
    }

    private void ImageTab_Click(object sender, RoutedEventArgs e)
    {
        _activeTab = MediaKind.Image;
        ApplyTab();
    }

    private void ApplyTab()
    {
        VideoTabButton.IsChecked = _activeTab == MediaKind.Video;
        ImageTabButton.IsChecked = _activeTab == MediaKind.Image;
        QueueList.ItemsSource = Jobs.Where(job => job.Model.Kind == _activeTab).ToList();
        if (QueueList.Items.Count > 0) QueueList.SelectedIndex = 0;
        UpdateCounts();
    }

    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QualityCombo is null) return;
        _settings.Quality = QualityCombo.SelectedIndex == 1 ? QualityMode.Fast : QualityMode.Beautiful;
        foreach (var job in Jobs) job.Model.Quality = _settings.Quality;
        _ = _settings.SaveAsync();
    }

    private async void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JobViewModel viewModel }) return;
        if (_selected == viewModel) _selected = null;
        Jobs.Remove(viewModel);
        ApplyTab();
        await SaveQueueAsync();
        if (Jobs.Count == 0) ShowEmptyEditor();
    }

    private async void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOperation is not null) return;
        Jobs.Clear();
        _selected = null;
        QueueList.ItemsSource = Jobs;
        ShowEmptyEditor();
        await SaveQueueAsync();
        UpdateCounts();
    }

    private void ShowEmptyEditor()
    {
        StopPreviewPlayback();
        SourceImage.Source = null;
        Editor.SetDocument(new MaskDocument());
        Editor.SetBaseMask(null);
        EmptyState.Visibility = Visibility.Visible;
        OverallStatusText.Text = "Thêm tệp để bắt đầu.";
        UpdateButtons();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Thêm vào ERASA VIDEO";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
            {
                if (item is StorageFile file) paths.Add(file.Path);
                if (item is StorageFolder folder)
                    paths.AddRange(Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories).Where(IsSupported));
            }
            await AddPathsAsync(paths);
        }
        catch (Exception exception)
        {
            HandleError("Kéo thả", exception);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) OpenPath(Path.GetDirectoryName(_selected.Model.InputPath) ?? _selected.Model.InputPath);
    }

    private void OutputFolder_Click(object sender, RoutedEventArgs e) => OpenPath(_settings.OutputDirectory);

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var outputBox = new TextBox { Text = _settings.OutputDirectory, IsReadOnly = true, MinWidth = 430 };
        var deviceCombo = new ComboBox { MinWidth = 220 };
        deviceCombo.Items.Add(new ComboBoxItem { Content = "Tự động (ưu tiên NVIDIA)", Tag = "auto" });
        deviceCombo.Items.Add(new ComboBoxItem { Content = "NVIDIA GPU", Tag = "cuda" });
        deviceCombo.Items.Add(new ComboBoxItem { Content = "Chỉ CPU", Tag = "cpu" });
        deviceCombo.SelectedIndex = _settings.DeviceProfile switch { "cuda" => 1, "cpu" => 2, _ => 0 };
        var applyCheck = new CheckBox { Content = "Áp mask theo tọa độ tương đối cho video cùng tỉ lệ", IsChecked = _settings.ApplyMaskToSameAspectRatio };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Thư mục lưu" });
        panel.Children.Add(outputBox);
        panel.Children.Add(new TextBlock { Text = "Thiết bị xử lý LaMa" });
        panel.Children.Add(deviceCombo);
        panel.Children.Add(applyCheck);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Cài đặt",
            Content = panel,
            PrimaryButtonText = "Chọn thư mục",
            SecondaryButtonText = "Lưu",
            CloseButtonText = "Đóng"
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var picker = new FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) _settings.OutputDirectory = folder.Path;
        }
        _settings.DeviceProfile = (deviceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        _settings.ApplyMaskToSameAspectRatio = applyCheck.IsChecked == true;
        await _settings.SaveAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void StopPreviewPlayback()
    {
        try { PreviewPlayer.MediaPlayer?.Pause(); } catch (Exception) { }
        PreviewPlayer.Source = null;
        PreviewPlayer.Visibility = Visibility.Collapsed;
        EditorScroll.Visibility = Visibility.Visible;
    }

    private async Task<int> ApplyConfirmedMaskToSameAspectJobsAsync(MediaJob sourceJob, byte[] sourceAlpha)
    {
        if (!_settings.ApplyMaskToSameAspectRatio || sourceJob.Kind != MediaKind.Video || sourceJob.Width <= 0 || sourceJob.Height <= 0)
            return 0;
        var sourceRatio = sourceJob.Width / (double)sourceJob.Height;
        var applied = 0;
        foreach (var targetViewModel in Jobs)
        {
            var target = targetViewModel.Model;
            if (ReferenceEquals(target, sourceJob) || target.Kind != MediaKind.Video || target.Width <= 0 || target.Height <= 0) continue;
            if (target.HasMaskContent || target.State is JobState.Processing or JobState.Completed) continue;
            var targetRatio = target.Width / (double)target.Height;
            if (Math.Abs(targetRatio - sourceRatio) > 0.002) continue;

            var scaled = MaskRasterizer.ResizeAlpha(sourceAlpha, sourceJob.Width, sourceJob.Height, target.Width, target.Height);
            var targetMaskPath = Path.Combine(AppPaths.JobDirectory(target.Id), "confirmed-mask.pgm");
            await MaskRasterizer.WritePgmAsync(targetMaskPath, scaled, target.Width, target.Height);
            target.Mask = new MaskDocument();
            target.BaseMaskPath = targetMaskPath;
            target.MaskPath = targetMaskPath;
            JobStateMachine.ConfirmMask(target);
            target.ResumeDirectory = Path.Combine(AppPaths.JobDirectory(target.Id), $"resume-{target.Mask.ConfirmedRevision}");
            Directory.CreateDirectory(target.ResumeDirectory);
            target.StatusMessage = $"Đã nhận mask tương đối từ {sourceJob.FileName}.";
            targetViewModel.Refresh();
            applied++;
        }
        return applied;
    }

    private string BuildOutputPath(MediaJob job)
    {
        var name = Path.GetFileNameWithoutExtension(job.InputPath);
        var extension = job.Kind == MediaKind.Video ? ".mp4" : ".png";
        var candidate = Path.Combine(_settings.OutputDirectory, name + "_erasa" + extension);
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(_settings.OutputDirectory, $"{name}_erasa_{index}{extension}");
            index++;
        }
        return candidate;
    }

    private void SetBusy(bool busy, string message)
    {
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = message;
        AddFilesButton.IsEnabled = !busy && _currentOperation is null;
        AddFolderButton.IsEnabled = !busy && _currentOperation is null;
    }

    private void UpdateButtons()
    {
        var job = _selected?.Model;
        var operationRunning = _currentOperation is not null;
        ConfirmMaskButton.IsEnabled = !operationRunning && job is not null && JobStateMachine.CanConfirmMask(job);
        PreviewButton.IsEnabled = !operationRunning && job is not null && JobStateMachine.CanPreview(job);
        ProcessButton.IsEnabled = !operationRunning && Jobs.Any(item => JobStateMachine.CanProcess(item.Model));
        SuggestButton.IsEnabled = !operationRunning && job?.Kind == MediaKind.Video && job.State is not JobState.LoadingPreview;
        ReloadFrameButton.IsEnabled = !operationRunning && job is not null;
        UndoButton.IsEnabled = !operationRunning && Editor.CanUndo;
        RedoButton.IsEnabled = !operationRunning && Editor.CanRedo;
        ResetButton.IsEnabled = !operationRunning && job is not null;
        PauseButton.IsEnabled = operationRunning;
        CancelButton.IsEnabled = operationRunning;
        ResumeButton.IsEnabled = !operationRunning && Jobs.Any(item => item.Model.State == JobState.Paused);
        RetryButton.IsEnabled = !operationRunning && job is not null && JobStateMachine.CanRetry(job);
    }

    private void UpdateCounts()
    {
        var visible = Jobs.Count(job => job.Model.Kind == _activeTab);
        SelectedCountText.Text = Jobs.Count == 0 ? "Chưa chọn tệp" : $"Đã chọn {Jobs.Count} tệp • {visible} trong tab";
        UpdateButtons();
    }

    private async Task SaveQueueAsync()
    {
        try
        {
            await _queueStore.SaveAsync(Jobs.Select(viewModel => viewModel.Model));
        }
        catch (Exception exception)
        {
            AppLog.Write("Save queue", exception);
        }
    }

    private void HandleError(string context, Exception exception, bool keepPreview = false)
    {
        AppLog.Write(context, exception);
        ErrorBar.Message = exception.Message;
        ErrorBar.IsOpen = true;
        OverallStatusText.Text = $"{context}: {exception.Message}";
        if (!keepPreview && SourceImage.Source is null) EmptyState.Visibility = Visibility.Visible;
        UpdateButtons();
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "Đóng"
        };
        await dialog.ShowAsync();
    }

    private static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return VideoExtensions.Contains(extension) || ImageExtensions.Contains(extension);
    }

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.Write("Open path", exception);
        }
    }
    public async Task RunWorkerFailureSmokeAsync(string signalPath)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (_worker.IsBusy && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            await _worker.RunAsync(new WorkerRequest
            {
                Command = "intentional-worker-failure-smoke"
            });
            throw new InvalidOperationException("Worker smoke test did not fail as expected.");
        }
        catch (Exception exception)
        {
            AppLog.Write("Expected worker failure smoke", exception);
            OverallStatusText.Text = "UI vẫn hoạt động sau khi worker lỗi.";
            Directory.CreateDirectory(Path.GetDirectoryName(signalPath) ?? ".");
            await File.WriteAllTextAsync(signalPath, "UI_ALIVE_AFTER_WORKER_FAILURE");
        }
    }

}
