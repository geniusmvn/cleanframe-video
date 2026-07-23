using System.Diagnostics;
using System.Text;
using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.App.Services;

public sealed class WorkerClient
{
    private readonly object _sync = new();
    private Process? _currentProcess;

    public bool IsWorkerAvailable => File.Exists(FindWorkerExecutable());
    public bool IsBusy
    {
        get
        {
            lock (_sync) return _currentProcess is { HasExited: false };
        }
    }

    public async Task<WorkerMessage> RunAsync(
        WorkerRequest request,
        Action<WorkerMessage>? onMessage = null,
        CancellationToken cancellationToken = default)
    {
        var worker = FindWorkerExecutable();
        if (!File.Exists(worker)) throw new FileNotFoundException("Artifact thiếu video worker riêng.", worker);
        AppPaths.EnsureCreated();
        var requestPath = Path.Combine(AppPaths.Root, $"request-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, WorkerJson.Serialize(request), Encoding.UTF8, cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = worker,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(worker) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        lock (_sync)
        {
            if (_currentProcess is { HasExited: false }) throw new InvalidOperationException("Worker đang bận.");
            _currentProcess = process;
        }

        WorkerMessage? completed = null;
        string? failure = null;
        var stderr = new StringBuilder();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Không khởi động được video worker.");
            using var registration = cancellationToken.Register(() => Kill(process));
            var outputTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    var message = WorkerJson.Deserialize<WorkerMessage>(line);
                    if (message is null) continue;
                    onMessage?.Invoke(message);
                    if (message.IsCompleted) completed = message;
                    if (message.IsFailed) failure = message.Error ?? message.Message;
                }
            }, cancellationToken);
            var errorTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    stderr.AppendLine(line);
                }
            }, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (process.ExitCode != 0 || failure is not null)
            {
                var detail = failure ?? stderr.ToString();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"Worker dừng với mã {process.ExitCode}."
                    : detail.Trim());
            }
            return completed ?? throw new InvalidOperationException("Worker kết thúc nhưng không trả về kết quả.");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentProcess, process)) _currentProcess = null;
            }
            try { File.Delete(requestPath); } catch (IOException) { }
        }
    }

    public void CancelCurrent()
    {
        lock (_sync)
        {
            if (_currentProcess is not null) Kill(_currentProcess);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private static string FindWorkerExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "worker", "Erasa.Video2.Worker.exe"),
            Path.Combine(baseDirectory, "Erasa.Video2.Worker.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Erasa.Video2.Worker", "Erasa.Video2.Worker.exe"))
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
