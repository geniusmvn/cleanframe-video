namespace CleanFrame.Video2.Core.Threading;

public sealed class PauseGate : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(true);
    public bool IsPaused => !_gate.IsSet;
    public void Pause() => _gate.Reset();
    public void Resume() => _gate.Set();
    public void Wait(CancellationToken cancellationToken) => _gate.Wait(cancellationToken);
    public void Dispose() => _gate.Dispose();
}
