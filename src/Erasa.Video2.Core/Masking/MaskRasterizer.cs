using Erasa.Video2.Core.Models;

namespace Erasa.Video2.Core.Masking;

public static class MaskRasterizer
{
    public static byte[] Render(MaskDocument document, int width, int height)
        => Render(document, width, height, ReadOnlySpan<byte>.Empty);

    public static byte[] Render(MaskDocument document, int width, int height, ReadOnlySpan<byte> baseAlpha)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var alpha = new byte[checked(width * height)];
        if (!baseAlpha.IsEmpty)
        {
            if (baseAlpha.Length != alpha.Length) throw new ArgumentException("Base mask size mismatch.", nameof(baseAlpha));
            baseAlpha.CopyTo(alpha);
        }
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

    public static bool HasVisiblePixels(ReadOnlySpan<byte> alpha)
    {
        foreach (var value in alpha)
        {
            if (value > 0) return true;
        }
        return false;
    }

    public static (int Left, int Top, int Right, int Bottom)? GetBounds(ReadOnlySpan<byte> alpha, int width, int height)
    {
        if (alpha.Length != width * height) throw new ArgumentException("Mask size mismatch.", nameof(alpha));
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (alpha[y * width + x] == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < 0 ? null : (left, top, right, bottom);
    }

    public static async Task WritePgmAsync(string path, ReadOnlyMemory<byte> alpha, int width, int height, CancellationToken cancellationToken = default)
    {
        if (alpha.Length != width * height) throw new ArgumentException("Mask size mismatch.", nameof(alpha));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + ".partial";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(alpha, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void DrawStroke(byte[] alpha, int width, int height, MaskOperation operation)
    {
        if (operation.Points.Count == 0) return;
        var radius = Math.Max(1.0, operation.Radius * Math.Min(width, height));
        var spacing = Math.Max(1.0, radius * 0.28);
        for (var index = 0; index < operation.Points.Count; index++)
        {
            var start = operation.Points[Math.Max(0, index - 1)].Clamp();
            var end = operation.Points[index].Clamp();
            var ax = start.X * (width - 1);
            var ay = start.Y * (height - 1);
            var bx = end.X * (width - 1);
            var by = end.Y * (height - 1);
            var distance = Math.Sqrt(Math.Pow(bx - ax, 2) + Math.Pow(by - ay, 2));
            var steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (double)steps;
                DrawDisc(alpha, width, height, ax + (bx - ax) * t, ay + (by - ay) * t,
                    radius, operation.Softness, operation.IsEraser);
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
        var inner = radius * (1 - feather * 0.9);

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2));
                if (distance > radius) continue;
                byte value = distance <= inner || radius <= inner
                    ? byte.MaxValue
                    : (byte)Math.Clamp(Math.Round(255 * (radius - distance) / Math.Max(radius - inner, 1e-6)), 0, 255);
                Apply(alpha, y * width + x, value, erase);
            }
        }
    }

    private static void DrawShape(byte[] alpha, int width, int height, MaskOperation operation, bool ellipse)
    {
        var rect = operation.Rect.Normalize();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var left = Math.Clamp((int)Math.Floor(rect.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(rect.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((rect.X + rect.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((rect.Y + rect.Height) * height), top + 1, height);
        var featherPixels = Math.Max(1, operation.Softness * Math.Min(right - left, bottom - top) * 0.2);
        var cx = (left + right) / 2d;
        var cy = (top + bottom) / 2d;
        var rx = Math.Max(1, (right - left) / 2d);
        var ry = Math.Max(1, (bottom - top) / 2d);

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                double edgeDistance;
                if (ellipse)
                {
                    var norm = Math.Sqrt(Math.Pow((x + 0.5 - cx) / rx, 2) + Math.Pow((y + 0.5 - cy) / ry, 2));
                    if (norm > 1) continue;
                    edgeDistance = (1 - norm) * Math.Min(rx, ry);
                }
                else
                {
                    edgeDistance = Math.Min(Math.Min(x - left + 1, right - x), Math.Min(y - top + 1, bottom - y));
                }
                var value = (byte)Math.Clamp(Math.Round(255 * Math.Min(1, edgeDistance / featherPixels)), 0, 255);
                Apply(alpha, y * width + x, value, operation.IsEraser);
            }
        }
    }

    private static void Apply(byte[] alpha, int index, byte value, bool erase)
    {
        alpha[index] = erase
            ? (byte)Math.Max(0, alpha[index] - value)
            : (byte)Math.Max(alpha[index], value);
    }
    public static byte[] ResizeAlpha(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (source.Length != sourceWidth * sourceHeight)
            throw new ArgumentException("Source alpha size mismatch.", nameof(source));
        var target = new byte[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Clamp((int)Math.Round((y + 0.5) * sourceHeight / targetHeight - 0.5), 0, sourceHeight - 1);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Clamp((int)Math.Round((x + 0.5) * sourceWidth / targetWidth - 0.5), 0, sourceWidth - 1);
                target[y * targetWidth + x] = source[sourceY * sourceWidth + sourceX];
            }
        }
        return target;
    }

}
