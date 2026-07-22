using System.Diagnostics;
using System.Text.Json;

namespace LaMaStudio.App;

public sealed record WorkerProgress(string? Kind, double? Progress, string? Message, string? Output);

public sealed class WorkerRunner
{
    private Process? _process;
    public bool IsRunning => _process is { HasExited: false };

    public async Task<string?> RunAsync(IEnumerable<string> arguments, IProgress<WorkerProgress>? progress, CancellationToken cancellationToken)
    {
        var python = Path.Combine(AppContext.BaseDirectory, "tools", "python", "python.exe");
        var worker = Path.Combine(AppContext.BaseDirectory, "worker", "main.py");
        if (!File.Exists(python)) throw new FileNotFoundException("Thiếu runtime Python đóng gói.", python);
        if (!File.Exists(worker)) throw new FileNotFoundException("Thiếu video/image worker.", worker);

        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(worker);
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONUTF8"] = "1";
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Không mở được worker.");
        using var registration = cancellationToken.Register(Cancel);
        string? lastOutput = null;
        var stderrTask = _process.StandardError.ReadToEndAsync(cancellationToken);
        while (await _process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            try
            {
                var evt = JsonSerializer.Deserialize<WorkerProgress>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null) { progress?.Report(evt); if (!string.IsNullOrWhiteSpace(evt.Output)) lastOutput = evt.Output; }
            }
            catch { progress?.Report(new WorkerProgress("log", null, line, null)); }
        }
        await _process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        var code = _process.ExitCode;
        _process.Dispose(); _process = null;
        if (code != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Worker dừng với mã {code}." : stderr.Trim());
        return lastOutput;
    }

    public void Cancel()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
    }
}
