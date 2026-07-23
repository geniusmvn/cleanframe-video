using Erasa.Video2.Core.Protocol;
using Erasa.Video2.Worker.Core.Services;

namespace Erasa.Video2.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public async Task ThinHostEmitsExecutorResult()
    {
        var emitted = new List<WorkerMessage>();
        var executor = new StubExecutor(new WorkerMessage { Kind = "completed", Message = "ok" });
        var host = new WorkerProcessHost(executor, emitted.Add);

        await host.ExecuteAsync(new WorkerRequest { Command = "test" }, CancellationToken.None);

        var message = Assert.Single(emitted);
        Assert.True(message.IsCompleted);
        Assert.Equal("ok", message.Message);
    }

    [Fact]
    public async Task ThinHostDoesNotSwallowWorkerFailure()
    {
        var host = new WorkerProcessHost(new ThrowingExecutor(), _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.ExecuteAsync(new WorkerRequest { Command = "test" }, CancellationToken.None));
    }

    private sealed class StubExecutor(WorkerMessage result) : IWorkerCommandExecutor
    {
        public Task<WorkerMessage> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class ThrowingExecutor : IWorkerCommandExecutor
    {
        public Task<WorkerMessage> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
            => Task.FromException<WorkerMessage>(new InvalidOperationException("expected"));
    }
}
