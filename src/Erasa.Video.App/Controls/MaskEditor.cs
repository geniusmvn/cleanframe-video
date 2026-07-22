using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Erasa.Video.Core.Models;
using Erasa.Video.Core.Processing;
using System.Runtime.InteropServices;

namespace Erasa.Video.App.Controls;

public sealed class MaskEditor : Control
{
    private Bitmap? _source;
    private WriteableBitmap? _overlay;
    private Bitmap? _externalOverlay;
    private byte[]? _baseMask;
    private int _baseMaskWidth;
    private int _baseMaskHeight;
    private NormalizedPoint? _start;
    private readonly List<NormalizedPoint> _stroke = [];
    private MaskOperation? _preview;
    private bool _dragging;
    private Point? _panLast;
    private double _zoom = 1;

    public MaskDocument Document { get; private set; } = new();
    public MaskTool Tool { get; set; } = MaskTool.Brush;
    public double BrushRadius { get; set; } = .025;
    public double Softness { get; set; } = .2;
    public event EventHandler? MaskChanged;
    public event EventHandler<PanDeltaEventArgs>? PanDelta;

    public MaskEditor()
    {
        ClipToBounds = true;
        Focusable = true;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
    }

    public void SetSource(Bitmap? bitmap)
    {
        _source?.Dispose();
        _source = bitmap;
        if (bitmap is not null)
        {
            Document.SourceAspectRatio = bitmap.PixelSize.Width / (double)bitmap.PixelSize.Height;
            ApplyZoomSize();
        }
        RenderOverlay();
        InvalidateVisual();
    }

    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, .5, 3);
        ApplyZoomSize();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ApplyZoomSize()
    {
        if (_source is null) return;
        Width = Math.Max(320, _source.PixelSize.Width * _zoom);
        Height = Math.Max(180, _source.PixelSize.Height * _zoom);
    }

    public void SetExternalOverlay(Bitmap? bitmap)
    {
        _externalOverlay?.Dispose();
        _externalOverlay = bitmap;
        InvalidateVisual();
    }

    public void SetBaseMask(byte[]? alpha, int width = 0, int height = 0)
    {
        if (alpha is not null && (width <= 0 || height <= 0 || alpha.Length != width * height))
            throw new ArgumentException("Base mask dimensions are invalid.", nameof(alpha));
        _baseMask = alpha is null ? null : [.. alpha];
        _baseMaskWidth = alpha is null ? 0 : width;
        _baseMaskHeight = alpha is null ? 0 : height;
        RenderOverlay();
        InvalidateVisual();
    }

    public void SetDocument(MaskDocument document)
    {
        Document = document;
        RenderOverlay();
        InvalidateVisual();
    }

    public void Undo() { if (Document.Undo()) Notify(); }
    public void Redo() { if (Document.Redo()) Notify(); }
    public void ClearMask() { Document.Clear(); _baseMask = null; _baseMaskWidth = 0; _baseMaskHeight = 0; _externalOverlay?.Dispose(); _externalOverlay = null; Notify(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Black, Bounds);
        if (_source is null) return;
        var rect = FitRect(_source.PixelSize.Width, _source.PixelSize.Height, Bounds);
        context.DrawImage(_source, rect);
        if (_externalOverlay is not null) context.DrawImage(_externalOverlay, rect);
        if (_overlay is not null) context.DrawImage(_overlay, rect);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_source is null) return;
        if (Tool == MaskTool.Pan)
        {
            _dragging = true;
            _panLast = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        var point = ToNormalized(e.GetPosition(this));
        if (point is null) return;
        _dragging = true; _start = point; _stroke.Clear(); _stroke.Add(point.Value);
        e.Pointer.Capture(this);
        UpdatePreview(point.Value);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        if (Tool == MaskTool.Pan && _panLast is Point last)
        {
            var current = e.GetPosition(this);
            PanDelta?.Invoke(this, new PanDeltaEventArgs(current.X - last.X, current.Y - last.Y));
            _panLast = current;
            e.Handled = true;
            return;
        }
        if (_start is null) return;
        var point = ToNormalized(e.GetPosition(this));
        if (point is null) return;
        if (Tool is MaskTool.Brush or MaskTool.Eraser) _stroke.Add(point.Value);
        UpdatePreview(point.Value);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        if (Tool == MaskTool.Pan)
        {
            _dragging = false;
            _panLast = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_start is null) return;
        var point = ToNormalized(e.GetPosition(this)) ?? _start.Value;
        var operation = BuildOperation(point);
        if (operation is not null) Document.Add(operation);
        _dragging = false; _start = null; _stroke.Clear(); _preview = null;
        e.Pointer.Capture(null);
        Notify();
        e.Handled = true;
    }

    private void UpdatePreview(NormalizedPoint end)
    {
        _preview = BuildOperation(end);
        RenderOverlay();
        InvalidateVisual();
    }

    private MaskOperation? BuildOperation(NormalizedPoint end)
    {
        if (_start is null) return null;
        return Tool switch
        {
            MaskTool.Brush or MaskTool.Eraser => new MaskOperation
            {
                Tool = Tool, Points = [.. _stroke], Radius = BrushRadius, Softness = Softness,
                Erase = Tool == MaskTool.Eraser
            },
            MaskTool.Rectangle or MaskTool.Ellipse => new MaskOperation
            {
                Tool = Tool,
                Rect = new NormalizedRect(_start.Value.X, _start.Value.Y, end.X - _start.Value.X, end.Y - _start.Value.Y),
                Softness = Softness
            },
            _ => null
        };
    }

    private void Notify()
    {
        RenderOverlay(); InvalidateVisual(); MaskChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderOverlay()
    {
        _overlay?.Dispose(); _overlay = null;
        if (_source is null) return;
        var document = Document.Clone();
        if (_preview is not null) document.Operations.Add(_preview);
        var width = _source.PixelSize.Width; var height = _source.PixelSize.Height;
        var baseAlpha = ResampleBaseMask(width, height);
        var alpha = MaskRasterizer.Render(document, width, height, baseAlpha);
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        unsafe
        {
            var target = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var a = (byte)(alpha[y * width + x] * 0.52);
                var offset = y * framebuffer.RowBytes + x * 4;
                target[offset] = 20; target[offset + 1] = 100; target[offset + 2] = 255; target[offset + 3] = a;
            }
        }
        _overlay = bitmap;
    }


    private byte[] ResampleBaseMask(int width, int height)
    {
        if (_baseMask is null || _baseMaskWidth <= 0 || _baseMaskHeight <= 0) return [];
        if (_baseMaskWidth == width && _baseMaskHeight == height) return [.. _baseMask];
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(_baseMaskHeight - 1, (int)((long)y * _baseMaskHeight / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(_baseMaskWidth - 1, (int)((long)x * _baseMaskWidth / width));
                result[y * width + x] = _baseMask[sourceY * _baseMaskWidth + sourceX];
            }
        }
        return result;
    }

    private NormalizedPoint? ToNormalized(Point point)
    {
        if (_source is null) return null;
        var rect = FitRect(_source.PixelSize.Width, _source.PixelSize.Height, Bounds);
        if (!rect.Contains(point)) return null;
        return new NormalizedPoint((point.X - rect.X) / rect.Width, (point.Y - rect.Y) / rect.Height).Clamp();
    }

    private static Rect FitRect(double width, double height, Rect bounds)
    {
        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        var w = width * scale; var h = height * scale;
        return new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
    }
}

public sealed class PanDeltaEventArgs(double deltaX, double deltaY) : EventArgs
{
    public double DeltaX { get; } = deltaX;
    public double DeltaY { get; } = deltaY;
}
