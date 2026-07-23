using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.Worker.Core.Services;

public sealed class WorkerProcessHost
{
    private readonly IWorkerCommandExecutor _executor;
    private readonly Action<WorkerMessage> _emit;

    public WorkerProcessHost(Action<WorkerMessage> emit)
        : this(new WorkerCommandExecutor(emit), emit)
    {
    }

    public WorkerProcessHost(IWorkerCommandExecutor executor, Action<WorkerMessage> emit)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
    }

    public async Task ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _executor.ExecuteAsync(request, cancellationToken);
        _emit(result ?? throw new InvalidOperationException("Worker executor không trả về kết quả."));
    }
}
