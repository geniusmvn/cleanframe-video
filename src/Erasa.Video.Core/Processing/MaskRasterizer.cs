using Erasa.Video.Core.Models;

namespace Erasa.Video.Core.Processing;

public static class MaskRasterizer
{
    public static byte[] Render(MaskDocument document, int width, int height)
        => Render(document, width, height, ReadOnlySpan<byte>.Empty);

    public static byte[] Render(MaskDocument document, int width, int height, ReadOnlySpan<byte> baseAlpha)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!baseAlpha.IsEmpty && baseAlpha.Length != width * height)
            throw new ArgumentException("Base mask size mismatch.", nameof(baseAlpha));
        var alpha = new byte[width * height];
        if (!baseAlpha.IsEmpty) baseAlpha.CopyTo(alpha);
        foreach (var operation in document.Operations)
        {
            switch (operation.Tool)
            {
                case MaskTool.Brush:
                case MaskTool.Eraser:
                    DrawStroke(alpha, width, height, operation);
                    break;
                case MaskTool.Rectangle:
                    DrawShape(alpha, width, height, operation, ellipse: false);
                    break;
                case MaskTool.Ellipse:
                    DrawShape(alpha, width, height, operation, ellipse: true);
                    break;
            }
        }
        return alpha;
    }

    private static void DrawStroke(byte[] alpha, int width, int height, MaskOperation operation)
    {
        if (operation.Points.Count == 0) return;
        var radius = Math.Max(1.0, operation.Radius * Math.Min(width, height));
        var spacing = Math.Max(1.0, radius * 0.28);
        for (var i = 0; i < operation.Points.Count; i++)
        {
            var a = operation.Points[Math.Max(0, i - 1)];
            var b = operation.Points[i];
            var ax = a.X * (width - 1); var ay = a.Y * (height - 1);
            var bx = b.X * (width - 1); var by = b.Y * (height - 1);
            var length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            var steps = Math.Max(1, (int)Math.Ceiling(length / spacing));
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (double)steps;
                DrawDisc(alpha, width, height, ax + (bx - ax) * t, ay + (by - ay) * t,
                    radius, operation.Softness, operation.Erase || operation.Tool == MaskTool.Eraser);
            }
        }
    }

    private static void DrawDisc(byte[] alpha, int width, int height, double cx, double cy, double radius, double softness, bool erase)
    {
        var left = Math.Max(0, (int)Math.Floor(cx - radius));
        var right = Math.Min(width - 1, (int)Math.Ceiling(cx + radius));
        var top = Math.Max(0, (int)Math.Floor(cy - radius));
        var bottom = Math.Min(height - 1, (int)Math.Ceiling(cy + radius));
        var feather = Math.Clamp(softness, 0, 1);
        var inner = radius * (1 - feather * 0.85);
        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++)
        {
            var distance = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            if (distance > radius) continue;
            var value = distance <= inner || radius <= inner
                ? 255
                : (byte)Math.Clamp(Math.Round(255 * (radius - distance) / Math.Max(radius - inner, 1e-6)), 0, 255);
            Apply(alpha, y * width + x, value, erase);
        }
    }

    private static void DrawShape(byte[] alpha, int width, int height, MaskOperation operation, bool ellipse)
    {
        var r = operation.Rect.Normalize();
        var left = Math.Clamp((int)Math.Floor(r.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(r.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((r.X + r.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((r.Y + r.Height) * height), top + 1, height);
        var featherPixels = Math.Max(1, operation.Softness * Math.Min(right - left, bottom - top) * 0.2);
        var cx = (left + right) / 2d; var cy = (top + bottom) / 2d;
        var rx = Math.Max(1, (right - left) / 2d); var ry = Math.Max(1, (bottom - top) / 2d);
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            double edgeDistance;
            if (ellipse)
            {
                var norm = Math.Sqrt(Math.Pow((x + .5 - cx) / rx, 2) + Math.Pow((y + .5 - cy) / ry, 2));
                if (norm > 1) continue;
                edgeDistance = (1 - norm) * Math.Min(rx, ry);
            }
            else
            {
                edgeDistance = Math.Min(Math.Min(x - left + 1, right - x), Math.Min(y - top + 1, bottom - y));
            }
            var value = (byte)Math.Clamp(Math.Round(255 * Math.Min(1, edgeDistance / featherPixels)), 0, 255);
            Apply(alpha, y * width + x, value, operation.Erase);
        }
    }

    private static void Apply(byte[] alpha, int index, byte value, bool erase)
        => alpha[index] = erase ? (byte)Math.Max(0, alpha[index] - value) : (byte)Math.Max((int)alpha[index], value);
}
