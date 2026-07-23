namespace Erasa.Video2.Core.Queue;

public static class JobWorkspace
{
    public static void CleanupPartialFiles(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path).Contains(".partial", StringComparison.OrdinalIgnoreCase)
                                    || Path.GetFileName(path).Contains(".writing", StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(file);
        }
    }

    public static void ClearResumeArtifacts(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            TryDelete(file);
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try { Directory.Delete(child, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void CleanupOutputPartials(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var fileName = Path.GetFileName(outputPath);
        foreach (var candidate in Directory.EnumerateFiles(directory, fileName + ".partial*"))
            TryDelete(candidate);
    }

    public static int FindFirstMissingSegment(string directory, int segmentCount)
    {
        for (var index = 0; index < segmentCount; index++)
        {
            if (!File.Exists(Path.Combine(directory, $"segment_{index:D5}.mp4"))) return index;
        }
        return segmentCount;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
