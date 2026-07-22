using Erasa.Video.Core.Models;
using Erasa.Video.Core.Worker;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erasa.Video.App.Services;

public sealed class WorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string AppRoot { get; } = AppContext.BaseDirectory;
    private string Python => Path.Combine(AppRoot, "runtime", "python", "python.exe");
    private string Worker => Path.Combine(AppRoot, "worker", "erasa_worker.py");

    public bool IsRuntimeAvailable => File.Exists(Python) && File.Exists(Worker)
        && Directory.Exists(Path.Combine(AppRoot, "runtime", "lama"))
        && Directory.Exists(Path.Combine(AppRoot, "runtime", "models", "big-lama"));

    public async Task<WorkerEvent> RunAsync(IEnumerable<string> arguments, Action<WorkerEvent>? onEvent = null, CancellationToken ct = default)
    {
        if (!IsRuntimeAvailable) throw new FileNotFoundException("Thiếu runtime LaMa đóng gói. Hãy dùng artifact Windows từ GitHub Actions.");
        var psi = new ProcessStartInfo
        {
            FileName = Python,
            WorkingDirectory = AppRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-X"); psi.ArgumentList.Add("utf8");
        psi.ArgumentList.Add(Worker);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["ERASA_APP_ROOT"] = AppRoot;
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
        WorkerEvent? last = null;
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = JsonSerializer.Deserialize<WorkerEvent>(line, JsonOptions);
                if (evt is null) continue;
                last = evt; onEvent?.Invoke(evt);
            }
            catch { onEvent?.Invoke(new WorkerEvent(WorkerEventKind.Log, Message: line)); }
        }
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(last?.Message ?? (string.IsNullOrWhiteSpace(stderr) ? $"Worker thoát mã {process.ExitCode}." : stderr.Trim()));
        return last ?? new WorkerEvent(WorkerEventKind.Completed);
    }

    public string DeviceArgument(ComputeDevice device) => device switch
    {
        ComputeDevice.Nvidia => "cuda",
        ComputeDevice.Cpu => "cpu",
        _ => "auto"
    };
}
