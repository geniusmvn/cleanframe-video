namespace Erasa.Video2.Core.Models;

public readonly record struct NormalizedPoint(double X, double Y)
{
    public NormalizedPoint Clamp() => new(Math.Clamp(X, 0, 1), Math.Clamp(Y, 0, 1));
}

public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public NormalizedRect Normalize()
    {
        var x1 = Math.Min(X, X + Width);
        var x2 = Math.Max(X, X + Width);
        var y1 = Math.Min(Y, Y + Height);
        var y2 = Math.Max(Y, Y + Height);
        x1 = Math.Clamp(x1, 0, 1);
        x2 = Math.Clamp(x2, 0, 1);
        y1 = Math.Clamp(y1, 0, 1);
        y2 = Math.Clamp(y2, 0, 1);
        return new NormalizedRect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }
}
