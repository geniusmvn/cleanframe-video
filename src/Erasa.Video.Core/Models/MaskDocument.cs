using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erasa.Video.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaskTool { Brush, Eraser, Rectangle, Ellipse, Pan }

public sealed record MaskOperation
{
    public MaskTool Tool { get; init; }
    public List<NormalizedPoint> Points { get; init; } = [];
    public NormalizedRect Rect { get; init; }
    public double Radius { get; init; } = 0.025;
    public double Softness { get; init; } = 0.2;
    public bool Erase { get; init; }
}

public sealed class MaskDocument
{
    private readonly Stack<MaskOperation> _redo = new();
    public List<MaskOperation> Operations { get; init; } = [];
    public double SourceAspectRatio { get; set; } = 16d / 9d;

    [JsonIgnore] public bool CanUndo => Operations.Count > 0;
    [JsonIgnore] public bool CanRedo => _redo.Count > 0;

    public void Add(MaskOperation operation)
    {
        Operations.Add(operation);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (Operations.Count == 0) return false;
        var last = Operations[^1];
        Operations.RemoveAt(Operations.Count - 1);
        _redo.Push(last);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        Operations.Add(_redo.Pop());
        return true;
    }

    public void Clear()
    {
        Operations.Clear();
        _redo.Clear();
    }

    public MaskDocument Clone() => JsonSerializer.Deserialize<MaskDocument>(JsonSerializer.Serialize(this, JsonOptions)) ?? new();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, ct);
    }

    public static async Task<MaskDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MaskDocument>(stream, JsonOptions, ct) ?? new();
    }
}
