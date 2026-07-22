using CleanFrame.Video2.Core.Models;

namespace CleanFrame.Video2.Core.Worker;

public interface IWorkerSession : IAsyncDisposable
{
    event EventHandler<WorkerEvent>? EventReceived;
    event EventHandler<WorkerExitedEventArgs>? Exited;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default);
}

public sealed class WorkerExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}

public sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly IWorkerSession _session;
    private bool _disposing;
    public bool IsUiHostAlive { get; private set; } = true;
    public bool IsWorkerAlive { get; private set; }
    public string? LastError { get; private set; }
    public event EventHandler<WorkerEvent>? EventReceived;

    public WorkerSupervisor(IWorkerSession session)
    {
        _session = session;
        _session.EventReceived += (_, e) => EventReceived?.Invoke(this, e);
        _session.Exited += (_, e) =>
        {
            IsWorkerAlive = false;
            if (_disposing) return;
            LastError = $"Worker exited with code {e.ExitCode}.";
            EventReceived?.Invoke(this, new WorkerEvent(WorkerEventKind.Failed, Message: LastError));
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsWorkerAlive) return;
        try
        {
            await _session.StartAsync(cancellationToken);
            IsWorkerAlive = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            IsWorkerAlive = false;
            LastError = ex.Message;
        }
    }

    public Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
        => _session.SendAsync(command, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        IsUiHostAlive = false;
        IsWorkerAlive = false;
        await _session.DisposeAsync();
    }
}
