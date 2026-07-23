namespace Erasa.Video2.Worker.Core.Services;

public static class ToolPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string FindFfmpeg(string executable)
    {
        var candidates = new[]
        {
            Path.Combine(BaseDirectory, "tools", "ffmpeg", "bin", executable + ".exe"),
            Path.Combine(BaseDirectory, "..", "tools", "ffmpeg", "bin", executable + ".exe"),
            executable + ".exe"
        };
        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return candidates[^1];
    }

    public static string BridgePath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(BaseDirectory, "Python", "lama_bridge.py"),
                Path.Combine(BaseDirectory, "worker", "Python", "lama_bridge.py")
            };
            return candidates.FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException("Không tìm thấy lama_bridge.py.");
        }
    }

    public static string ManifestPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(BaseDirectory, "Runtime", "runtime-manifest.json"),
                Path.Combine(BaseDirectory, "worker", "Runtime", "runtime-manifest.json")
            };
            return candidates.FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException("Không tìm thấy runtime-manifest.json.");
        }
    }
}
