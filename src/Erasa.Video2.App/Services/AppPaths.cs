namespace Erasa.Video2.App.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ERASA_VIDEO_2");

    public static string QueueFile => Path.Combine(Root, "queue.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LocalRuntimeDirectory => Path.Combine(Root, "runtime");
    public static string BundledRuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");
    public static string RuntimeDirectory => IsRuntimeComplete(BundledRuntimeDirectory)
        ? BundledRuntimeDirectory
        : LocalRuntimeDirectory;
    public static string JobsDirectory => Path.Combine(Root, "jobs");
    public static string LogFile => Path.Combine(Root, "logs", "erasa.log");

    public static string JobDirectory(Guid id)
    {
        var directory = Path.Combine(JobsDirectory, id.ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile) ?? Root);
    }

    public static bool IsRuntimeComplete(string directory)
        => File.Exists(Path.Combine(directory, "runtime.ready.json"))
           && File.Exists(Path.Combine(directory, "python", "python.exe"))
           && File.Exists(Path.Combine(directory, "lama-source", "saicinpainting", "training", "modules", "ffc.py"))
           && File.Exists(Path.Combine(directory, "model", "config.yaml"))
           && File.Exists(Path.Combine(directory, "model", "models", "best.ckpt"));
}
