using Erasa.Video.Core.Models;
using System.Text.Json;

namespace Erasa.Video.App.Services;

public sealed class AppSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ERASA_Output");
    public ComputeDevice Device { get; set; } = ComputeDevice.Auto;
    public int VideoCrf { get; set; } = 18;

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ERASA_VIDEO", "settings.json");

    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true });
    }
}
