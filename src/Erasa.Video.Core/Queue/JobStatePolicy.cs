using Erasa.Video.Core.Models;

namespace Erasa.Video.Core.Queue;

public static class JobStatePolicy
{
    public static bool CanRetry(JobStatus status)
        => status is JobStatus.Failed or JobStatus.Cancelled;

    public static bool CanResume(JobStatus status)
        => status == JobStatus.Paused;

    public static JobStatus StatusAfterInterruption(bool pauseRequested)
        => pauseRequested ? JobStatus.Paused : JobStatus.Cancelled;
}
