using System.Diagnostics;
using CleanFrame.Video2.Core.IO;
using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Threading;
using CleanFrame.Video2.Core.Worker;
using CleanFrame.Video2.Engine.Media;
using CleanFrame.Video2.Engine.Processing;
using OpenCvSharp;

namespace CleanFrame.Video2.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(WorkerJson.Serialize(new
            {
                ok = true,
                process = "CleanFrame.Video2.Worker",
                opencv = Cv2.GetVersionString(),
                lamaUsed = false
            }));
            return 0;
        }

        var host = new WorkerHost();
        return await host.RunAsync();
    }
}

internal sealed class WorkerHost
{
    private readonly object _writeLock = new();
    private CancellationTokenSource? _activeCts;
    private PauseGate? _pauseGate;
    private Task? _activeTask;
    private Guid? _activeJobId;
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CleanFrameVideo2", "logs");
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "CleanFrameVideo2");

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(_logDirectory);
        Directory.CreateDirectory(_tempRoot);
        CleanupOrphanedWorkspaces();
        Write(new WorkerEvent(WorkerEventKind.Ready, Message: "Worker ready"));
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            WorkerCommand command;
            try { command = WorkerJson.Deserialize<WorkerCommand>(line); }
            catch (Exception ex) { Write(new WorkerEvent(WorkerEventKind.Failed, Message: $"Invalid command: {ex.Message}")); continue; }

            var commandType = command.Type?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(commandType))
            {
                Write(new WorkerEvent(WorkerEventKind.Failed, command.JobId, Message: "Command type is missing."));
                continue;
            }
            switch (commandType)
            {
                case "ping":
                    Write(new WorkerEvent(WorkerEventKind.Pong, Message: "pong"));
                    break;
                case "start":
                    Start(command);
                    break;
                case "pause":
                    if (Matches(command.JobId)) { _pauseGate?.Pause(); Write(new WorkerEvent(WorkerEventKind.Log, _activeJobId, Message: "Paused")); }
                    break;
                case "resume":
                    if (Matches(command.JobId)) { _pauseGate?.Resume(); Write(new WorkerEvent(WorkerEventKind.Log, _activeJobId, Message: "Resumed")); }
                    break;
                case "cancel":
                    if (Matches(command.JobId)) _activeCts?.Cancel();
                    break;
                case "shutdown":
                    _activeCts?.Cancel();
                    if (_activeTask is not null) await IgnoreFailure(_activeTask);
                    return 0;
                default:
                    Write(new WorkerEvent(WorkerEventKind.Failed, command.JobId, Message: $"Unknown command '{command.Type}'."));
                    break;
            }
        }
        _activeCts?.Cancel();
        if (_activeTask is not null) await IgnoreFailure(_activeTask);
        return 0;
    }

    private void Start(WorkerCommand command)
    {
        if (command.Job is null)
        {
            Write(new WorkerEvent(WorkerEventKind.Failed, Message: "Start command has no job."));
            return;
        }
        if (_activeTask is { IsCompleted: false })
        {
            Write(new WorkerEvent(WorkerEventKind.Failed, command.Job.Id, Message: "Worker is already processing another job."));
            return;
        }

        _activeCts?.Dispose();
        _pauseGate?.Dispose();
        _activeCts = new CancellationTokenSource();
        _pauseGate = new PauseGate();
        _activeJobId = command.Job.Id;
        _activeTask = ExecuteAsync(command, _activeCts.Token);
    }

    private async Task ExecuteAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        var job = command.Job!;
        try
        {
            var tools = command.FfmpegDirectory ?? Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "tools", "ffmpeg", "bin"));
            var pipeline = new VideoPipeline(new FfmpegRunner(tools), _tempRoot);
            var progress = new InlineProgress<double>(value => Write(new WorkerEvent(WorkerEventKind.Progress, job.Id, value)));
            if (job.Kind == JobKind.Detect)
            {
                var candidates = await pipeline.DetectAsync(job, command.DetectionSamples, progress, cancellationToken);
                Write(new WorkerEvent(WorkerEventKind.Detection, job.Id, 1, "Detection completed", Candidates: candidates));
            }
            else
            {
                await pipeline.ProcessAsync(job, _pauseGate!, progress, cancellationToken);
                Write(new WorkerEvent(WorkerEventKind.Completed, job.Id, 1, "Completed", job.OutputPath));
            }
        }
        catch (OperationCanceledException)
        {
            Write(new WorkerEvent(WorkerEventKind.Cancelled, job.Id, Message: "Cancelled; temporary files cleaned."));
        }
        catch (Exception ex)
        {
            await LogExceptionAsync(job.Id, ex);
            Write(new WorkerEvent(WorkerEventKind.Failed, job.Id, Message: ex.Message));
        }
        finally
        {
            _activeJobId = null;
        }
    }

    private void CleanupOrphanedWorkspaces()
    {
        foreach (var directory in Directory.EnumerateDirectories(_tempRoot))
        {
            try
            {
                var ownerPath = Path.Combine(directory, "owner.pid");
                if (File.Exists(ownerPath) && int.TryParse(File.ReadAllText(ownerPath), out var ownerPid) && IsProcessAlive(ownerPid))
                    continue;
                JobWorkspace.DeleteWithRetries(directory);
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(_logDirectory, $"worker-{DateTime.UtcNow:yyyyMMdd}.log"),
                        $"[{DateTimeOffset.Now:O}] Could not clean orphan workspace {directory}: {ex.Message}{Environment.NewLine}");
                }
                catch { }
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private bool Matches(Guid? id) => id is null || id == _activeJobId;

    private void Write(WorkerEvent value)
    {
        lock (_writeLock)
        {
            Console.Out.WriteLine(WorkerJson.Serialize(value));
            Console.Out.Flush();
        }
    }

    private async Task LogExceptionAsync(Guid jobId, Exception exception)
    {
        var path = Path.Combine(_logDirectory, $"worker-{DateTime.UtcNow:yyyyMMdd}.log");
        await File.AppendAllTextAsync(path, $"[{DateTimeOffset.Now:O}] Job {jobId}\n{exception}\n\n");
    }

    private static async Task IgnoreFailure(Task task)
    {
        try { await task; } catch { }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
