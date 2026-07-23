using System.Text.Json;
using Erasa.Video2.Core.Models;
using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.App.Services;

public sealed class AppSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ERASA VIDEO");
    public QualityMode Quality { get; set; } = QualityMode.Beautiful;
    public string DeviceProfile { get; set; } = "auto";
    public bool ApplyMaskToSameAspectRatio { get; set; } = true;

    public static async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(AppPaths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, WorkerJson.Options) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            AppLog.Write("Load settings", exception);
            return new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.SettingsFile + ".partial";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, this, WorkerJson.Options);
            await stream.FlushAsync();
        }
        File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
    }
}
