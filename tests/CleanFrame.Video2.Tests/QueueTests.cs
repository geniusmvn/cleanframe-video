using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Queue;

namespace CleanFrame.Video2.Tests;

public sealed class QueueTests
{
    [Fact]
    public async Task Queue_can_retry_and_resume_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"queue-{Guid.NewGuid():N}.json");
        var id = Guid.NewGuid();
        var queue = new JobQueue();
        queue.Add(new VideoJob { Id = id, InputPath = "input.mp4", OutputPath = "output.mp4", Status = JobStatus.Failed, Attempts = 1 });
        Assert.True(queue.Retry(id));
        queue.Update(id, JobStatus.Running, 0.42);
        await queue.SaveAsync(path);

        var restored = await JobQueue.LoadAsync(path);
        var job = Assert.Single(restored.Jobs);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0.42, job.Progress, 3);
        Assert.Equal(2, job.Attempts);
        File.Delete(path);
    }
    [Fact]
    public async Task Paused_job_stays_paused_after_restart_until_resume()
    {
        var path = Path.Combine(Path.GetTempPath(), $"queue-{Guid.NewGuid():N}.json");
        var id = Guid.NewGuid();
        var queue = new JobQueue();
        queue.Add(new VideoJob { Id = id, InputPath = "input.mp4", OutputPath = "output.mp4", Status = JobStatus.Paused });
        await queue.SaveAsync(path);

        var restored = await JobQueue.LoadAsync(path);
        Assert.Equal(JobStatus.Paused, Assert.Single(restored.Jobs).Status);
        Assert.True(restored.Resume(id));
        Assert.Equal(JobStatus.Queued, Assert.Single(restored.Jobs).Status);
        File.Delete(path);
    }

}
