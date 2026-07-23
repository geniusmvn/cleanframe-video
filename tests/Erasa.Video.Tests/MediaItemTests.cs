using Erasa.Video.Core.Models;

namespace Erasa.Video.Tests;

public sealed class MediaItemTests
{
    [Fact]
    public void Item_CannotProcessUntilMaskIsExplicitlyConfirmed()
    {
        var item = new MediaItem { InputPath = "input.mp4", Kind = MediaKind.Video };
        item.Mask.Add(new MaskOperation
        {
            Tool = MaskTool.Rectangle,
            Rect = new NormalizedRect(.1, .1, .2, .2)
        });
        item.NotifyMaskStateChanged();

        Assert.True(item.HasMaskContent);
        Assert.False(item.CanProcess);

        item.MaskConfirmed = true;
        item.Status = JobStatus.Ready;

        Assert.True(item.CanProcess);
    }

    [Fact]
    public void ChangingMaskConfirmationUpdatesStatusText()
    {
        var item = new MediaItem { InputPath = "image.png", Kind = MediaKind.Image };
        item.Mask.Add(new MaskOperation { Tool = MaskTool.Brush, Points = [new NormalizedPoint(.5, .5)] });
        item.NotifyMaskStateChanged();

        Assert.Equal("Mask chưa được xác nhận", item.StatusText);
        item.MaskConfirmed = true;
        item.Status = JobStatus.Ready;
        Assert.Equal("Sẵn sàng xử lý", item.StatusText);
    }
}
