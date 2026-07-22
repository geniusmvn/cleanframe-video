using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Erasa.Video.App.Services;
using Erasa.Video.Core.Models;

namespace Erasa.Video.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly GpuRuntimeInstaller _gpuInstaller = new();
    private CancellationTokenSource? _gpuInstallCts;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        OutputBox.Text = settings.OutputDirectory;
        DeviceBox.SelectedIndex = settings.Device switch { ComputeDevice.Nvidia => 1, ComputeDevice.Cpu => 2, _ => 0 };
        InstallGpuButton.IsEnabled = _gpuInstaller.CanInstall;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _gpuInstallCts?.Cancel();
        Close(false);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings.OutputDirectory = string.IsNullOrWhiteSpace(OutputBox.Text) ? _settings.OutputDirectory : OutputBox.Text!;
        _settings.Device = DeviceBox.SelectedIndex switch { 1 => ComputeDevice.Nvidia, 2 => ComputeDevice.Cpu, _ => ComputeDevice.Auto };
        await _settings.SaveAsync();
        Close(true);
    }

    private async void InstallGpu_Click(object? sender, RoutedEventArgs e)
    {
        if (_gpuInstallCts is not null) return;
        _gpuInstallCts = new CancellationTokenSource();
        InstallGpuButton.IsEnabled = false;
        GpuInstallProgress.IsVisible = true;
        GpuInstallStatus.Text = "Đang tải và cài gói NVIDIA…";
        try
        {
            await _gpuInstaller.InstallAsync(message => Dispatcher.UIThread.Post(() => GpuInstallStatus.Text = Shorten(message)), _gpuInstallCts.Token);
            GpuInstallStatus.Text = "Đã cài gói NVIDIA. Chọn NVIDIA GPU hoặc Tự động rồi bấm Lưu.";
            DeviceBox.SelectedIndex = 1;
        }
        catch (OperationCanceledException)
        {
            GpuInstallStatus.Text = "Đã hủy cài gói NVIDIA.";
        }
        catch (Exception ex)
        {
            GpuInstallStatus.Text = $"Cài gói NVIDIA thất bại: {ex.Message}";
        }
        finally
        {
            _gpuInstallCts.Dispose();
            _gpuInstallCts = null;
            GpuInstallProgress.IsVisible = false;
            InstallGpuButton.IsEnabled = _gpuInstaller.CanInstall;
        }
    }

    private static string Shorten(string message) => message.Length <= 150 ? message : message[^150..];
}
