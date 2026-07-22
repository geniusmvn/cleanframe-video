using LaMaStudio.Core;

namespace LaMaStudio.Tests;

public sealed class PgmMaskTests
{
    [Fact]
    public void RoundTripPreservesMask()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pgm");
        try
        {
            byte[] expected = [0, 255, 10, 20, 30, 40];
            PgmMask.Save(path, 3, 2, expected);
            var actual = PgmMask.Load(path);
            Assert.Equal(3, actual.Width); Assert.Equal(2, actual.Height); Assert.Equal(expected, actual.Alpha);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("a.png", MediaKind.Image)]
    [InlineData("a.MP4", MediaKind.Video)]
    public void MediaKindIsDetected(string path, MediaKind expected)
    { Assert.True(MediaKinds.TryFromPath(path, out var actual)); Assert.Equal(expected, actual); }
}
