using System.Text.Json.Serialization;

namespace Erasa.Video2.Core.Models;

public sealed class MaskOperation
{
    public MaskTool Tool { get; set; }
    public List<NormalizedPoint> Points { get; set; } = [];
    public NormalizedRect Rect { get; set; }
    public double Radius { get; set; } = 0.02;
    public double Softness { get; set; } = 0.2;

    [JsonIgnore]
    public bool IsEraser => Tool == MaskTool.Eraser;

    public MaskOperation Clone() => new()
    {
        Tool = Tool,
        Points = [.. Points],
        Rect = Rect,
        Radius = Radius,
        Softness = Softness
    };
}

public sealed class MaskDocument
{
    public List<MaskOperation> Operations { get; set; } = [];
    public int Revision { get; set; }
    public int ConfirmedRevision { get; set; } = -1;

    [JsonIgnore]
    public bool HasContent => Operations.Count > 0;

    [JsonIgnore]
    public bool IsConfirmed => HasContent && ConfirmedRevision == Revision;

    public MaskDocument Clone() => new()
    {
        Operations = Operations.Select(operation => operation.Clone()).ToList(),
        Revision = Revision,
        ConfirmedRevision = ConfirmedRevision
    };

    public void ReplaceWith(MaskDocument other)
    {
        Operations = other.Operations.Select(operation => operation.Clone()).ToList();
        Revision = other.Revision;
        ConfirmedRevision = other.ConfirmedRevision;
    }

    public void Add(MaskOperation operation)
    {
        Operations.Add(operation.Clone());
        Revision++;
        ConfirmedRevision = -1;
    }

    public void Clear()
    {
        Operations.Clear();
        Revision++;
        ConfirmedRevision = -1;
    }

    public void Confirm(bool hasExternalContent = false)
    {
        if (!HasContent && !hasExternalContent) throw new InvalidOperationException("Mask chưa có vùng cần xử lý.");
        ConfirmedRevision = Revision;
    }
}
