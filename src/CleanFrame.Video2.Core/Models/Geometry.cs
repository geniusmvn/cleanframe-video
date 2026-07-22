namespace CleanFrame.Video2.Core.Models;

public readonly record struct NormalizedPoint(double X, double Y)
{
    public NormalizedPoint Clamp() => new(Math.Clamp(X, 0, 1), Math.Clamp(Y, 0, 1));
}

public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public NormalizedRect Normalize()
    {
        var x1 = Math.Clamp(Math.Min(X, X + Width), 0, 1);
        var y1 = Math.Clamp(Math.Min(Y, Y + Height), 0, 1);
        var x2 = Math.Clamp(Math.Max(X, X + Width), 0, 1);
        var y2 = Math.Clamp(Math.Max(Y, Y + Height), 0, 1);
        return new(x1, y1, x2 - x1, y2 - y1);
    }
}
