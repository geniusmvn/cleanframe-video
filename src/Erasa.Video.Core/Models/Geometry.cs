namespace Erasa.Video.Core.Models;

public readonly record struct NormalizedPoint(double X, double Y)
{
    public NormalizedPoint Clamp() => new(Math.Clamp(X, 0, 1), Math.Clamp(Y, 0, 1));
}

public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public NormalizedRect Normalize()
    {
        var left = Width >= 0 ? X : X + Width;
        var top = Height >= 0 ? Y : Y + Height;
        return new NormalizedRect(
            Math.Clamp(left, 0, 1),
            Math.Clamp(top, 0, 1),
            Math.Clamp(Math.Abs(Width), 0, 1),
            Math.Clamp(Math.Abs(Height), 0, 1));
    }
}
