using CleanFrame.Video2.Core.Models;

namespace CleanFrame.Video2.Core.Processing;

public static class MaskRasterizer
{
    public static float[] Render(MaskDocument document, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var alpha = new float[width * height];
        foreach (var operation in document.Operations)
        {
            switch (operation)
            {
                case ShapeMaskOperation shape:
                    ApplyShape(alpha, width, height, shape);
                    break;
                case StrokeMaskOperation stroke:
                    ApplyStroke(alpha, width, height, stroke);
                    break;
                case PolygonMaskOperation polygon:
                    ApplyPolygon(alpha, width, height, polygon);
                    break;
            }
        }
        return alpha;
    }

    private static void ApplyShape(float[] alpha, int width, int height, ShapeMaskOperation op)
    {
        var r = op.Rect.Normalize();
        var x0 = r.X * width;
        var y0 = r.Y * height;
        var x1 = (r.X + r.Width) * width;
        var y1 = (r.Y + r.Height) * height;
        var feather = Math.Max(0.75, op.Softness * Math.Min(width, height) * 0.02);
        var minX = Math.Max(0, (int)Math.Floor(x0 - feather));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(x1 + feather));
        var minY = Math.Max(0, (int)Math.Floor(y0 - feather));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(y1 + feather));

        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var px = x + 0.5;
            var py = y + 0.5;
            double signedDistance;
            if (op.Tool == MaskTool.Ellipse)
            {
                var cx = (x0 + x1) / 2;
                var cy = (y0 + y1) / 2;
                var rx = Math.Max(0.5, (x1 - x0) / 2);
                var ry = Math.Max(0.5, (y1 - y0) / 2);
                var norm = Math.Sqrt(Math.Pow((px - cx) / rx, 2) + Math.Pow((py - cy) / ry, 2));
                signedDistance = (1 - norm) * Math.Min(rx, ry);
            }
            else
            {
                var dx = Math.Min(px - x0, x1 - px);
                var dy = Math.Min(py - y0, y1 - py);
                signedDistance = Math.Min(dx, dy);
            }
            var value = SmoothAlpha(signedDistance, feather);
            Blend(alpha, y * width + x, (float)value, op.Erase);
        }
    }


    private static void ApplyPolygon(float[] alpha, int width, int height, PolygonMaskOperation op)
    {
        if (op.Points.Count < 3) return;
        var points = op.Points.Select(p => new NormalizedPoint(p.X * width, p.Y * height)).ToArray();
        var minX = Math.Max(0, (int)Math.Floor(points.Min(p => p.X)));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(points.Max(p => p.X)));
        var minY = Math.Max(0, (int)Math.Floor(points.Min(p => p.Y)));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(points.Max(p => p.Y)));
        var feather = Math.Max(0.75, op.Softness * Math.Min(width, height) * 0.012);
        minX = Math.Max(0, (int)Math.Floor(minX - feather));
        maxX = Math.Min(width - 1, (int)Math.Ceiling(maxX + feather));
        minY = Math.Max(0, (int)Math.Floor(minY - feather));
        maxY = Math.Min(height - 1, (int)Math.Ceiling(maxY + feather));
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var px = x + 0.5;
            var py = y + 0.5;
            var inside = PointInPolygon(px, py, points);
            var distance = double.MaxValue;
            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                distance = Math.Min(distance, DistanceToSegment(px, py, a.X, a.Y, b.X, b.Y));
            }
            var signed = inside ? distance : -distance;
            Blend(alpha, y * width + x, (float)SmoothAlpha(signed, feather), op.Erase);
        }
    }

    private static bool PointInPolygon(double x, double y, IReadOnlyList<NormalizedPoint> points)
    {
        var inside = false;
        for (var i = 0; i < points.Count; i++)
        {
            var j = i == 0 ? points.Count - 1 : i - 1;
            var pi = points[i];
            var pj = points[j];
            var intersects = ((pi.Y > y) != (pj.Y > y)) &&
                x < (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X;
            if (intersects) inside = !inside;
        }
        return inside;
    }

    private static void ApplyStroke(float[] alpha, int width, int height, StrokeMaskOperation op)
    {
        if (op.Points.Count == 0) return;
        var radius = Math.Max(1, op.Radius * Math.Min(width, height));
        var feather = Math.Max(0.75, radius * Math.Clamp(op.Softness, 0, 1));
        for (var i = 0; i < op.Points.Count; i++)
        {
            var a = op.Points[Math.Max(0, i - 1)].Clamp();
            var b = op.Points[i].Clamp();
            var ax = a.X * width;
            var ay = a.Y * height;
            var bx = b.X * width;
            var by = b.Y * height;
            var minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - radius - feather));
            var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + radius + feather));
            var minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - radius - feather));
            var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(ay, by) + radius + feather));
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var distance = DistanceToSegment(x + 0.5, y + 0.5, ax, ay, bx, by);
                var value = SmoothAlpha(radius - distance, feather);
                Blend(alpha, y * width + x, (float)value, op.Erase);
            }
        }
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var length2 = dx * dx + dy * dy;
        if (length2 < 1e-9) return Math.Sqrt(Math.Pow(px - ax, 2) + Math.Pow(py - ay, 2));
        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / length2, 0, 1);
        var qx = ax + t * dx;
        var qy = ay + t * dy;
        return Math.Sqrt(Math.Pow(px - qx, 2) + Math.Pow(py - qy, 2));
    }

    private static double SmoothAlpha(double signedDistance, double feather)
        => Math.Clamp(0.5 + signedDistance / Math.Max(feather, 1e-6), 0, 1);

    private static void Blend(float[] alpha, int index, float value, bool erase)
    {
        alpha[index] = erase
            ? alpha[index] * (1 - value)
            : Math.Max(alpha[index], value);
    }
}
