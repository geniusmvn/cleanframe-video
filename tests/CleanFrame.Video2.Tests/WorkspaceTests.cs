using CleanFrame.Video2.Core.IO;

namespace CleanFrame.Video2.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task Failure_cleanup_removes_temporary_workspace()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CleanFrameVideo2Tests", Guid.NewGuid().ToString("N"));
        string? root = null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var workspace = new JobWorkspace(parent, Guid.NewGuid());
            root = workspace.RootPath;
            await File.WriteAllTextAsync(Path.Combine(workspace.InputFramesPath, "partial.tmp"), "partial");
            throw new InvalidOperationException("Synthetic worker failure");
        });

        Assert.NotNull(root);
        Assert.False(Directory.Exists(root));
        if (Directory.Exists(parent)) Directory.Delete(parent, true);
    }
}
