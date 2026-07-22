using System.Diagnostics;

namespace Erasa.Video.App.Services;

public sealed class GpuRuntimeInstaller
{
    private readonly string _appRoot = AppContext.BaseDirectory;
    private string Python => Path.Combine(_appRoot, "runtime", "python", "python.exe");

    public bool CanInstall => File.Exists(Python);

    public async Task InstallAsync(Action<string>? onMessage = null, CancellationToken cancellationToken = default)
    {
        if (!CanInstall)
            throw new FileNotFoundException("Không tìm thấy Python runtime đóng gói. Hãy dùng artifact Windows từ GitHub Actions.");

        var startInfo = new ProcessStartInfo
        {
            FileName = Python,
            WorkingDirectory = _appRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-X", "utf8", "-m", "pip", "install", "--no-warn-script-location", "--force-reinstall", "--no-deps",
            "torch==1.8.0+cu111", "torchvision==0.9.0+cu111", "-f", "https://download.pytorch.org/whl/torch_stable.html"
        }) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        var standardError = new List<string>();
        var stdout = ReadLinesAsync(process.StandardOutput, line => onMessage?.Invoke(line), cancellationToken);
        var stderr = ReadLinesAsync(process.StandardError, line => { standardError.Add(line); onMessage?.Invoke(line); }, cancellationToken);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken));
        if (process.ExitCode != 0)
            throw new InvalidOperationException(standardError.Count == 0 ? $"Cài gói NVIDIA thất bại, mã {process.ExitCode}." : string.Join(Environment.NewLine, standardError.TakeLast(12)));
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            if (!string.IsNullOrWhiteSpace(line)) onLine(line);
    }
}
