namespace Erasa.Video.App.Services;

public static class AppLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ERASA_VIDEO",
        "logs");

    public static string LatestLogPath => Path.Combine(LogDirectory, $"erasa-{DateTime.UtcNow:yyyyMMdd}.log");

    public static async Task WriteAsync(string area, Exception exception)
        => await WriteAsync(area, exception.ToString());

    public static async Task WriteAsync(string area, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            await Gate.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(
                    LatestLogPath,
                    $"[{DateTimeOffset.Now:O}] [{area}] {message}{Environment.NewLine}{Environment.NewLine}");
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // Logging must never terminate the UI.
        }
    }
}
