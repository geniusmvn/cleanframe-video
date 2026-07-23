using System.Text.Json;
using Erasa.Video2.Core.Models;

namespace Erasa.Video2.Core.Queue;

public sealed class QueueStateStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyList<MediaJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            var jobs = await JsonSerializer.DeserializeAsync<List<MediaJob>>(stream, Options, cancellationToken) ?? [];
            foreach (var job in jobs)
            {
                if (job.State is JobState.Processing or JobState.Previewing or JobState.LoadingPreview)
                    job.State = JobState.Paused;
                job.RefreshComputedProperties();
            }
            return jobs;
        }
        catch (JsonException)
        {
            var broken = path + $".broken-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(path, broken, overwrite: true);
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<MediaJob> jobs, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + ".partial";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, jobs, Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
