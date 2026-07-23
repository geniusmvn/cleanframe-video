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
    private string ModelRoot => Path.Combine(AppRoot, "runtime", "models", "big-lama");

    public bool IsUtilityAvailable => File.Exists(Python) && File.Exists(Worker);

    public bool IsRuntimeAvailable => IsUtilityAvailable
        && Directory.Exists(Path.Combine(AppRoot, "runtime", "lama", "saicinpainting"))
        && File.Exists(Path.Combine(ModelRoot, "config.yaml"))
        && File.Exists(Path.Combine(ModelRoot, "models", "best.ckpt"));

    public string UtilityError => IsUtilityAvailable
        ? string.Empty
        : "Thiếu Python đóng gói hoặc worker ERASA trong thư mục ứng dụng.";

    public string RuntimeError => IsRuntimeAvailable
        ? string.Empty
        : "Thiếu mã nguồn advimman/lama hoặc checkpoint big-lama trong thư mục runtime.";

    public async Task<WorkerEvent> RunAsync(
        IEnumerable<string> arguments,
        Action<WorkerEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        var argumentList = arguments as IReadOnlyList<string> ?? arguments.ToArray();
        if (argumentList.Count == 0) throw new ArgumentException("Worker command is required.", nameof(arguments));

        if (!IsUtilityAvailable) throw new FileNotFoundException(UtilityError);
        if (RequiresInference(argumentList[0]) && !IsRuntimeAvailable)
            throw new FileNotFoundException(RuntimeError);

        var psi = new ProcessStartInfo
        {
            FileName = Python,
            WorkingDirectory = AppRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add("utf8");
        psi.ArgumentList.Add(Worker);
        foreach (var argument in argumentList) psi.ArgumentList.Add(argument);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["ERASA_APP_ROOT"] = AppRoot;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Không khởi động được worker ERASA: {ex.Message}", ex);
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Cancellation is surfaced after the worker exits.
            }
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
                last = evt;
                onEvent?.Invoke(evt);
            }
            catch (JsonException)
            {
                onEvent?.Invoke(new WorkerEvent(WorkerEventKind.Log, Message: line));
            }
        }

        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (process.ExitCode != 0)
        {
            var detail = last?.Message;
            if (string.IsNullOrWhiteSpace(detail)) detail = LastUsefulLines(stderr, 12);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"Worker ERASA thoát mã {process.ExitCode}."
                : detail);
        }
        return last ?? new WorkerEvent(WorkerEventKind.Completed);
    }

    private static bool RequiresInference(string command)
        => command is "image" or "video" or "selftest" or "diagnose";

    private static string LastUsefulLines(string text, int maximumLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(maximumLines));
    }

    public string DeviceArgument(ComputeDevice device) => device switch
    {
        ComputeDevice.Nvidia => "cuda",
        ComputeDevice.Cpu => "cpu",
        _ => "auto"
    };
}
