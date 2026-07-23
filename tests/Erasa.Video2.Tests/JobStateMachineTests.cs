using Erasa.Video2.Core.Models;
using Erasa.Video2.Core.Processing;

namespace Erasa.Video2.Tests;

public sealed class JobStateMachineTests
{
    [Fact]
    public void PreviewIsDisabledUntilManualMaskIsConfirmed()
    {
        var job = new MediaJob { State = JobState.NeedsMask, Width = 100, Height = 100 };
        job.Mask.Add(new MaskOperation
        {
            Tool = MaskTool.Rectangle,
            Rect = new NormalizedRect(0.1, 0.1, 0.2, 0.2)
        });
        JobStateMachine.MarkMaskChanged(job);
        Assert.False(JobStateMachine.CanPreview(job));
        job.MaskPath = "confirmed-mask.pgm";
        JobStateMachine.ConfirmMask(job);
        Assert.True(JobStateMachine.CanPreview(job));
        Assert.True(JobStateMachine.CanProcess(job));
    }

    [Fact]
    public void ExternalSuggestedMaskCanBeConfirmedWithoutManualOperations()
    {
        var job = new MediaJob
        {
            State = JobState.MaskDirty,
            BaseMaskPath = "suggested-mask.pgm",
            MaskPath = "confirmed-mask.pgm"
        };

        Assert.Empty(job.Mask.Operations);
        JobStateMachine.ConfirmMask(job);

        Assert.True(job.MaskConfirmed);
        Assert.True(JobStateMachine.CanPreview(job));
        Assert.True(JobStateMachine.CanProcess(job));
    }

    [Fact]
    public void EditingAfterConfirmationLocksPreviewAgain()
    {
        var job = new MediaJob
        {
            State = JobState.MaskDirty,
            BaseMaskPath = "suggested-mask.pgm",
            MaskPath = "confirmed-mask.pgm"
        };
        JobStateMachine.ConfirmMask(job);
        Assert.True(JobStateMachine.CanPreview(job));

        job.Mask.Add(new MaskOperation { Tool = MaskTool.Brush, Points = [new NormalizedPoint(0.5, 0.5)] });
        job.MaskPath = null;
        JobStateMachine.MarkMaskChanged(job);

        Assert.False(job.MaskConfirmed);
        Assert.False(JobStateMachine.CanPreview(job));
    }

    [Fact]
    public void WorkerFailureDoesNotDestroyJobOrConfirmedMask()
    {
        var job = new MediaJob { State = JobState.MaskDirty, MaskPath = "confirmed-mask.pgm" };
        job.Mask.Add(new MaskOperation { Tool = MaskTool.Brush, Points = [new NormalizedPoint(0.5, 0.5)] });
        JobStateMachine.ConfirmMask(job);
        JobStateMachine.MarkFailed(job, "worker crashed");
        Assert.Equal(JobState.Failed, job.State);
        Assert.True(job.MaskConfirmed);
        Assert.Equal("worker crashed", job.Error);
        Assert.True(JobStateMachine.CanRetry(job));
        Assert.True(JobStateMachine.CanPreview(job));
    }
}
