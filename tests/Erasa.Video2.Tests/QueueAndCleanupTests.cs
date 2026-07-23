using Erasa.Video2.Core.Models;
using Erasa.Video2.Core.Queue;

namespace Erasa.Video2.Tests;

public sealed class QueueAndCleanupTests
{
    [Fact]
    public async Task RunningJobLoadsAsPausedForResume()
    {
        var root = Path.Combine(Path.GetTempPath(), "erasa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new QueueStateStore(Path.Combine(root, "queue.json"));
            await store.SaveAsync([new MediaJob { InputPath = "video.mp4", State = JobState.Processing }]);
            var loaded = await store.LoadAsync();
            Assert.Single(loaded);
            Assert.Equal(JobState.Paused, loaded[0].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PauseCleanupDeletesPartialAndWritingFilesButKeepsCompletedSegments()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "segment_00000.mp4"), "complete");
            File.WriteAllText(Path.Combine(root, "segment_00001.partial.mp4"), "partial");
            File.WriteAllText(Path.Combine(root, "segment_00001.partial.writing.mp4"), "partial");
            JobWorkspace.CleanupPartialFiles(root);
            Assert.True(File.Exists(Path.Combine(root, "segment_00000.mp4")));
            Assert.False(File.Exists(Path.Combine(root, "segment_00001.partial.mp4")));
            Assert.False(File.Exists(Path.Combine(root, "segment_00001.partial.writing.mp4")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CancelCleanupRemovesResumeArtifacts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "segment_00000.mp4"), "complete");
            File.WriteAllText(Path.Combine(root, "segment_00001.partial.mp4"), "partial");
            JobWorkspace.ClearResumeArtifacts(root);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResumeStartsAtFirstMissingSegment()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "segment_00000.mp4"), "complete");
            File.WriteAllText(Path.Combine(root, "segment_00001.mp4"), "complete");
            Assert.Equal(2, JobWorkspace.FindFirstMissingSegment(root, 5));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "erasa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
