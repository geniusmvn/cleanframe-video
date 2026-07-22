using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanFrame.Video2.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ShapeMaskOperation), "shape")]
[JsonDerivedType(typeof(StrokeMaskOperation), "stroke")]
[JsonDerivedType(typeof(PolygonMaskOperation), "polygon")]
public abstract record MaskOperation(double Softness, bool Erase);

public sealed record ShapeMaskOperation(
    MaskTool Tool,
    NormalizedRect Rect,
    double Softness,
    bool Erase = false) : MaskOperation(Softness, Erase);

public sealed record PolygonMaskOperation(
    IReadOnlyList<NormalizedPoint> Points,
    double Softness,
    bool Erase = false) : MaskOperation(Softness, Erase);

public sealed record StrokeMaskOperation(
    IReadOnlyList<NormalizedPoint> Points,
    double Radius,
    double Softness,
    bool Erase) : MaskOperation(Softness, Erase);

public sealed class MaskDocument
{
    public int Version { get; set; } = 1;
    public double SourceAspectRatio { get; set; }
    public List<MaskOperation> Operations { get; set; } = [];

    [JsonIgnore]
    private readonly Stack<MaskOperation> _redo = new();

    public void Add(MaskOperation operation)
    {
        Operations.Add(operation);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (Operations.Count == 0) return false;
        var op = Operations[^1];
        Operations.RemoveAt(Operations.Count - 1);
        _redo.Push(op);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        Operations.Add(_redo.Pop());
        return true;
    }

    public void Reset()
    {
        Operations.Clear();
        _redo.Clear();
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
    }

    public static async Task<MaskDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MaskDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Mask document is empty.");
    }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
