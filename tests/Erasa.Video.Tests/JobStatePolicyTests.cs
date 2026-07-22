using Erasa.Video.Core.Models;
using Erasa.Video.Core.Queue;

namespace Erasa.Video.Tests;

public sealed class JobStatePolicyTests
{
    [Fact]
    public void FailedOrCancelledJobsCanRetry()
    {
        Assert.True(JobStatePolicy.CanRetry(JobStatus.Failed));
        Assert.True(JobStatePolicy.CanRetry(JobStatus.Cancelled));
        Assert.False(JobStatePolicy.CanRetry(JobStatus.Completed));
        Assert.False(JobStatePolicy.CanRetry(JobStatus.Running));
    }

    [Fact]
    public void OnlyPausedJobsCanResume()
    {
        Assert.True(JobStatePolicy.CanResume(JobStatus.Paused));
        Assert.False(JobStatePolicy.CanResume(JobStatus.Failed));
        Assert.False(JobStatePolicy.CanResume(JobStatus.Ready));
    }

    [Theory]
    [InlineData(true, JobStatus.Paused)]
    [InlineData(false, JobStatus.Cancelled)]
    public void InterruptionChoosesExpectedState(bool pauseRequested, JobStatus expected)
        => Assert.Equal(expected, JobStatePolicy.StatusAfterInterruption(pauseRequested));
}
