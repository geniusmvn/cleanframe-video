using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LaMaStudio.App.Controls;
using LaMaStudio.Core;

namespace LaMaStudio.App;

public partial class MainWindow : Window
{
    private readonly WorkerRunner _runner = new();
    private CancellationTokenSource? _cts;
    private string? _inputPath;
    private string? _previewFramePath;
    private string? _resultPath;
    private MediaKind _kind;
    private double _baseWidth = 960;
    private double _baseHeight = 540;

    public MainWindow() => InitializeComponent();

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn ảnh hoặc video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Ảnh và video") { Patterns = ["*.png","*.jpg","*.jpeg","*.bmp","*.webp","*.tif","*.tiff","*.mp4","*.mov","*.mkv","*.avi","*.webm","*.m4v"] }
            ]
        });
        var file = files.FirstOrDefault();
        if (file?.TryGetLocalPath() is not { } path) return;
        await LoadMediaAsync(path);
    }

    private async Task LoadMediaAsync(string path)
    {
        if (!MediaKinds.TryFromPath(path, out _kind)) { SetStatus("Định dạng chưa được hỗ trợ."); return; }
        _inputPath = path; _resultPath = null; ShowResultButton.IsEnabled = false;
        FileNameText.Text = Path.GetFileName(path);
        try
        {
            _previewFramePath = Path.Combine(Path.GetTempPath(), "LaMaStudio", $"{Guid.NewGuid():N}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(_previewFramePath)!);
            await RunWorkerAsync(["preview-frame", "--input", path, "--output", _previewFramePath]);
            Editor.LoadSource(_previewFramePath);
            UpdateEditorSize();
            SetStatus("Vẽ vùng cần xoá.");
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private void Brush_Click(object? sender, RoutedEventArgs e) => SelectTool(MaskTool.Brush, BrushButton);
    private void Eraser_Click(object? sender, RoutedEventArgs e) => SelectTool(MaskTool.Eraser, EraserButton);
    private void Rect_Click(object? sender, RoutedEventArgs e) => SelectTool(MaskTool.Rectangle, RectButton);
    private void SelectTool(MaskTool tool, Button selected)
    {
        Editor.Tool = tool;
        foreach (var button in new[] { BrushButton, EraserButton, RectButton }) button.Background = Avalonia.Media.Brush.Parse("#171A22");
        selected.Background = Avalonia.Media.Brush.Parse("#6D5CE7");
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => Editor.Undo();
    private void Redo_Click(object? sender, RoutedEventArgs e) => Editor.Redo();
    private void Reset_Click(object? sender, RoutedEventArgs e) => Editor.ResetMask();
    private void BrushSlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    { Editor.BrushSize = e.NewValue; BrushValueText.Text = $"{e.NewValue:0} px"; }
    private void ZoomSlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    { ZoomValueText.Text = $"{e.NewValue:0}%"; UpdateEditorSize(); }
    private void UpdateEditorSize()
    {
        if (Editor.ImageWidth > 0)
        {
            var ratio = Editor.ImageWidth / (double)Editor.ImageHeight;
            _baseWidth = ratio >= 1 ? 960 : 620 * ratio;
            _baseHeight = _baseWidth / ratio;
            if (_baseHeight > 680) { _baseHeight = 680; _baseWidth = _baseHeight * ratio; }
        }
        var scale = ZoomSlider.Value / 100d;
        Editor.Width = _baseWidth * scale; Editor.Height = _baseHeight * scale;
    }

    private async void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanProcess()) return;
        var temp = Path.Combine(Path.GetTempPath(), "LaMaStudio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var mask = Path.Combine(temp, "mask.pgm"); Editor.SaveMask(mask);
        var ext = _kind == MediaKind.Image ? ".png" : ".mp4";
        var output = Path.Combine(temp, "preview" + ext);
        var args = new List<string> { _kind == MediaKind.Image ? "image" : "video", "--input", _inputPath!, "--mask", mask, "--output", output, "--device", SelectedDevice() };
        if (_kind == MediaKind.Video) args.AddRange(["--duration", "3"]);
        try
        {
            await RunWorkerAsync(args);
            _resultPath = output; ShowResultButton.IsEnabled = true;
            if (_kind == MediaKind.Image) Editor.ShowSource(output);
            else OpenWithDefaultApp(output);
            SetStatus("Preview đã tạo.");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanProcess()) return;
        var suggested = Path.GetFileNameWithoutExtension(_inputPath!) + "_lama" + (_kind == MediaKind.Image ? ".png" : ".mp4");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Lưu kết quả",
            SuggestedFileName = suggested,
            DefaultExtension = _kind == MediaKind.Image ? "png" : "mp4"
        });
        if (file?.TryGetLocalPath() is not { } output) return;
        var temp = Path.Combine(Path.GetTempPath(), "LaMaStudio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var mask = Path.Combine(temp, "mask.pgm"); Editor.SaveMask(mask);
        try
        {
            await RunWorkerAsync([_kind == MediaKind.Image ? "image" : "video", "--input", _inputPath!, "--mask", mask, "--output", output, "--device", SelectedDevice()]);
            _resultPath = output; ShowResultButton.IsEnabled = true;
            SetStatus($"Đã xuất: {output}");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private bool CanProcess()
    {
        if (_inputPath is null) { SetStatus("Hãy mở ảnh hoặc video trước."); return false; }
        if (!Editor.HasMask) { SetStatus("Mask đang trống."); return false; }
        if (_runner.IsRunning) { SetStatus("Đang có tác vụ chạy."); return false; }
        return true;
    }

    private string SelectedDevice() => (DeviceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

    private async Task RunWorkerAsync(IEnumerable<string> args)
    {
        _cts?.Dispose(); _cts = new CancellationTokenSource();
        ProgressBar.Value = 0;
        var progress = new Progress<WorkerProgress>(evt =>
        {
            if (evt.Progress is double p) ProgressBar.Value = Math.Clamp(p * 100, 0, 100);
            if (!string.IsNullOrWhiteSpace(evt.Message)) StatusText.Text = evt.Message;
        });
        try { await _runner.RunAsync(args, progress, _cts.Token); ProgressBar.Value = 100; }
        catch (OperationCanceledException) { SetStatus("Đã huỷ."); throw; }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) { _cts?.Cancel(); _runner.Cancel(); }
    private void ShowOriginal_Click(object? sender, RoutedEventArgs e) { if (_previewFramePath is not null) Editor.ShowSource(_previewFramePath); }
    private void ShowResult_Click(object? sender, RoutedEventArgs e) { if (_resultPath is null) return; if (_kind == MediaKind.Image) Editor.ShowSource(_resultPath); else OpenWithDefaultApp(_resultPath); }
    private void OpenOutputFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = _resultPath is not null ? Path.GetDirectoryName(_resultPath) : _inputPath is not null ? Path.GetDirectoryName(_inputPath) : null;
        if (folder is not null) OpenWithDefaultApp(folder);
    }
    private static void OpenWithDefaultApp(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void SetStatus(string message) => StatusText.Text = message;
}
