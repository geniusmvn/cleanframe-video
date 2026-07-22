using System.Text.Json;
using CleanFrame.Video2.Core.Models;

namespace CleanFrame.Video2.Core.Queue;

public sealed class JobQueue
{
    private readonly List<VideoJob> _jobs = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public IReadOnlyList<VideoJob> Jobs { get { lock (_sync) return _jobs.Select(Clone).ToArray(); } }

    public void Add(VideoJob job) { lock (_sync) _jobs.Add(Clone(job)); }

    public bool Pause(Guid id) => Transition(id, [JobStatus.Running, JobStatus.Queued], JobStatus.Paused);
    public bool Cancel(Guid id) => Transition(id, [JobStatus.Pending, JobStatus.Queued, JobStatus.Running, JobStatus.Paused, JobStatus.Failed], JobStatus.Cancelled);
    public bool Resume(Guid id) => Transition(id, [JobStatus.Paused], JobStatus.Queued);

    public bool Retry(Guid id)
    {
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(x => x.Id == id);
            if (job is null || job.Status is not (JobStatus.Failed or JobStatus.Cancelled)) return false;
            job.Status = JobStatus.Queued;
            job.Error = null;
            job.Progress = 0;
            job.Attempts++;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void Update(Guid id, JobStatus status, double progress = 0, string? error = null)
    {
        lock (_sync)
        {
            var job = _jobs.First(x => x.Id == id);
            job.Status = status;
            job.Progress = Math.Clamp(progress, 0, 1);
            job.Error = error;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Queue state path must include a directory.");
        Directory.CreateDirectory(directory);
        await _saveGate.WaitAsync(cancellationToken);
        var temporaryPath = path + ".tmp";
        try
        {
            VideoJob[] snapshot;
            lock (_sync) snapshot = _jobs.Select(Clone).ToArray();
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, MaskDocument.JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
            _saveGate.Release();
        }
    }

    public static async Task<JobQueue> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var queue = new JobQueue();
        if (!File.Exists(path)) return queue;
        await using var stream = File.OpenRead(path);
        var jobs = await JsonSerializer.DeserializeAsync<VideoJob[]>(stream, MaskDocument.JsonOptions, cancellationToken) ?? [];
        foreach (var job in jobs)
        {
            if (job.Status == JobStatus.Running) job.Status = JobStatus.Queued;
            queue._jobs.Add(Clone(job));
        }
        return queue;
    }

    private bool Transition(Guid id, JobStatus[] allowed, JobStatus next)
    {
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(x => x.Id == id);
            if (job is null || !allowed.Contains(job.Status)) return false;
            job.Status = next;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    private static VideoJob Clone(VideoJob x) => new()
    {
        Id = x.Id, InputPath = x.InputPath, OutputPath = x.OutputPath, MaskPath = x.MaskPath,
        Kind = x.Kind, Mode = x.Mode, Status = x.Status, PreviewStartSeconds = x.PreviewStartSeconds,
        PreviewDurationSeconds = x.PreviewDurationSeconds, Progress = x.Progress, Attempts = x.Attempts,
        Error = x.Error, UpdatedAt = x.UpdatedAt
    };
}
