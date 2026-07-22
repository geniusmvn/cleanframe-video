using System.Diagnostics;
using CleanFrame.Video2.Core.Worker;

namespace CleanFrame.Video2.App.Services;

public sealed class ProcessWorkerSession : IWorkerSession
{
    private readonly string _workerPath;
    private Process? _process;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public event EventHandler<WorkerEvent>? EventReceived;
    public event EventHandler<WorkerExitedEventArgs>? Exited;

    public ProcessWorkerSession(string workerPath) => _workerPath = workerPath;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_workerPath)) throw new FileNotFoundException("Không tìm thấy video worker trong artifact.", _workerPath);
        if (_process is { HasExited: false }) return Task.CompletedTask;
        _process?.Dispose();
        var startInfo = new ProcessStartInfo(_workerPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!
        };
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => Exited?.Invoke(this, new WorkerExitedEventArgs(SafeExitCode(process)));
        _process = process;
        if (!process.Start()) throw new InvalidOperationException("Không thể mở video worker.");
        _ = ReadStdoutAsync(process, cancellationToken);
        _ = ReadStderrAsync(process, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("Worker chưa khởi động.");
        if (process.HasExited) throw new InvalidOperationException("Worker đã dừng.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(WorkerJson.Serialize(command).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    private async Task ReadStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                try { EventReceived?.Invoke(this, WorkerJson.Deserialize<WorkerEvent>(line)); }
                catch (Exception ex) { EventReceived?.Invoke(this, new WorkerEvent(CleanFrame.Video2.Core.Models.WorkerEventKind.Log, Message: $"Worker output không hợp lệ: {ex.Message}")); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EventReceived?.Invoke(this, new WorkerEvent(CleanFrame.Video2.Core.Models.WorkerEventKind.Log,
                Message: $"Không đọc được output worker: {ex.Message}"));
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    EventReceived?.Invoke(this, new WorkerEvent(CleanFrame.Video2.Core.Models.WorkerEventKind.Log, Message: line));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EventReceived?.Invoke(this, new WorkerEvent(CleanFrame.Video2.Core.Models.WorkerEventKind.Log,
                Message: $"Không đọc được stderr worker: {ex.Message}"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_process is { HasExited: false } process)
        {
            try { await SendAsync(new WorkerCommand("shutdown")); } catch { }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        _process?.Dispose();
        _writeGate.Dispose();
    }
}
