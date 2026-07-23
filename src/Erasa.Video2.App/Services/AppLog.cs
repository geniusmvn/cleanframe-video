using System.Text;

namespace Erasa.Video2.App.Services;

public static class AppLog
{
    private static readonly object Sync = new();

    public static void Write(string context, Exception exception) => Write(context, exception.ToString());

    public static void Write(string context, string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            lock (Sync)
            {
                File.AppendAllText(
                    AppPaths.LogFile,
                    $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
