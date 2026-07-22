using Erasa.Video.Core.Models;

namespace Erasa.Video.Tests;

public sealed class MaskDocumentTests
{
    [Fact]
    public void UndoRedoPreservesOperation()
    {
        var doc = new MaskDocument();
        doc.Add(new MaskOperation { Tool = MaskTool.Ellipse, Rect = new(.1, .2, .3, .4) });
        Assert.True(doc.Undo()); Assert.Empty(doc.Operations);
        Assert.True(doc.Redo()); Assert.Single(doc.Operations);
    }

    [Fact]
    public async Task SaveLoadRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"erasa-{Guid.NewGuid():N}.json");
        try
        {
            var doc = new MaskDocument(); doc.Add(new MaskOperation { Tool = MaskTool.Brush, Points = [new(.2, .4)], Radius = .03 });
            await doc.SaveAsync(path);
            var loaded = await MaskDocument.LoadAsync(path);
            Assert.Single(loaded.Operations); Assert.Equal(MaskTool.Brush, loaded.Operations[0].Tool);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
