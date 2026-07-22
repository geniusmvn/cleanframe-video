using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Worker;

namespace CleanFrame.Video2.Tests;

public sealed class WorkerIsolationTests
{
    [Fact]
    public async Task Worker_crash_does_not_kill_ui_host()
    {
        var fake = new FakeWorkerSession();
        await using var supervisor = new WorkerSupervisor(fake);
        await supervisor.StartAsync();
        fake.Crash(17);
        Assert.True(supervisor.IsUiHostAlive);
        Assert.False(supervisor.IsWorkerAlive);
        Assert.Contains("17", supervisor.LastError);
    }

    private sealed class FakeWorkerSession : IWorkerSession
    {
        public event EventHandler<WorkerEvent>? EventReceived { add { } remove { } }
        public event EventHandler<WorkerExitedEventArgs>? Exited;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Crash(int code) => Exited?.Invoke(this, new WorkerExitedEventArgs(code));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
