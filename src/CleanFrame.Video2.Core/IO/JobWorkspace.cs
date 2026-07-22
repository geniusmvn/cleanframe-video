namespace CleanFrame.Video2.Core.IO;

public sealed class JobWorkspace : IAsyncDisposable
{
    private int _disposed;
    public string RootPath { get; }
    public string InputFramesPath { get; }
    public string OutputFramesPath { get; }

    public JobWorkspace(string parentPath, Guid jobId)
    {
        RootPath = Path.Combine(parentPath, jobId.ToString("N"));
        InputFramesPath = Path.Combine(RootPath, "input");
        OutputFramesPath = Path.Combine(RootPath, "output");
        Directory.CreateDirectory(InputFramesPath);
        Directory.CreateDirectory(OutputFramesPath);
        File.WriteAllText(Path.Combine(RootPath, "owner.pid"), Environment.ProcessId.ToString());
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        DeleteWithRetries(RootPath);
        return ValueTask.CompletedTask;
    }

    public static void DeleteWithRetries(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (IOException) when (attempt < 3) { Thread.Sleep(100 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 3) { Thread.Sleep(100 * (attempt + 1)); }
        }
        Directory.Delete(path, true);
    }
}
