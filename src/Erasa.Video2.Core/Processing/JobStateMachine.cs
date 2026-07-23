using Erasa.Video2.Core.Models;

namespace Erasa.Video2.Core.Processing;

public static class JobStateMachine
{
    public static bool CanConfirmMask(MediaJob job)
        => job.HasMaskContent
           && job.State is JobState.NeedsMask or JobState.MaskDirty or JobState.MaskConfirmed or JobState.Ready or JobState.Failed;

    public static bool CanPreview(MediaJob job)
        => job.MaskConfirmed
           && !string.IsNullOrWhiteSpace(job.MaskPath)
           && job.State is JobState.MaskConfirmed or JobState.Ready or JobState.Paused or JobState.Failed;

    public static bool CanProcess(MediaJob job)
        => job.MaskConfirmed
           && !string.IsNullOrWhiteSpace(job.MaskPath)
           && job.State is JobState.MaskConfirmed or JobState.Ready or JobState.Paused or JobState.Failed;

    public static bool CanPause(MediaJob job) => job.State == JobState.Processing;
    public static bool CanResume(MediaJob job) => job.State == JobState.Paused && job.MaskConfirmed;
    public static bool CanRetry(MediaJob job) => job.State is JobState.Failed or JobState.Cancelled;

    public static void MarkPreviewLoaded(MediaJob job)
    {
        job.Error = null;
        job.Progress = 0;
        job.State = job.HasMaskContent
            ? job.MaskConfirmed ? JobState.MaskConfirmed : JobState.MaskDirty
            : JobState.NeedsMask;
    }

    public static void MarkMaskChanged(MediaJob job)
    {
        job.Error = null;
        job.MaskConfirmed = false;
        job.Mask.ConfirmedRevision = -1;
        job.State = JobState.MaskDirty;
    }

    public static void ConfirmMask(MediaJob job)
    {
        job.Mask.Confirm(job.HasMaskContent);
        job.MaskConfirmed = true;
        job.Error = null;
        job.State = JobState.MaskConfirmed;
    }

    public static void MarkFailed(MediaJob job, string error)
    {
        job.Error = string.IsNullOrWhiteSpace(error) ? "Worker dừng mà không trả về thông báo." : error.Trim();
        job.State = JobState.Failed;
    }
}
