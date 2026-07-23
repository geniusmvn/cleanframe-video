using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Erasa.Video.Core.Models;
using Erasa.Video.Core.Processing;

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
    private Vector _pan;

    public MaskDocument Document { get; private set; } = new();
    public MaskTool Tool { get; set; } = MaskTool.Brush;
    public double BrushRadius { get; set; } = .025;
    public double Softness { get; set; } = .2;
    public bool HasSource => _source is not null;

    public event EventHandler? MaskChanged;
    public event Action<double>? ZoomChanged;

    public MaskEditor()
    {
        ClipToBounds = true;
        Focusable = true;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerWheelChanged += OnWheelChanged;
    }

    public void SetSource(Bitmap? bitmap)
    {
        _source?.Dispose();
        _source = bitmap;
        _zoom = 1;
        _pan = default;
        ZoomChanged?.Invoke(_zoom);
        if (bitmap is not null)
            Document.SourceAspectRatio = bitmap.PixelSize.Width / (double)bitmap.PixelSize.Height;
        RenderOverlay();
        InvalidateVisual();
    }

    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, .5, 4);
        ClampPan();
        InvalidateVisual();
        ZoomChanged?.Invoke(_zoom);
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

    public void Undo()
    {
        if (Document.Undo()) Notify();
    }

    public void Redo()
    {
        if (Document.Redo()) Notify();
    }

    public void ClearMask()
    {
        Document.Clear();
        _baseMask = null;
        _baseMaskWidth = 0;
        _baseMaskHeight = 0;
        _externalOverlay?.Dispose();
        _externalOverlay = null;
        Notify();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#090B0E")), Bounds);
        if (_source is null) return;
        var rect = GetImageRect();
        context.DrawImage(_source, rect);
        if (_externalOverlay is not null) context.DrawImage(_externalOverlay, rect);
        if (_overlay is not null) context.DrawImage(_overlay, rect);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_source is null) return;
        Focus();
        if (Tool == MaskTool.Pan || e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _dragging = true;
            _panLast = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var point = ToNormalized(e.GetPosition(this));
        if (point is null) return;
        _dragging = true;
        _start = point;
        _stroke.Clear();
        _stroke.Add(point.Value);
        e.Pointer.Capture(this);
        UpdatePreview(point.Value);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        if (_panLast is Point last)
        {
            var current = e.GetPosition(this);
            _pan += current - last;
            _panLast = current;
            ClampPan();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_start is null) return;
        var point = ToNormalized(e.GetPosition(this));
        if (point is null) return;
        if (Tool is MaskTool.Brush or MaskTool.Eraser) _stroke.Add(point.Value);
        UpdatePreview(point.Value);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        if (_panLast is not null)
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
        _dragging = false;
        _start = null;
        _stroke.Clear();
        _preview = null;
        e.Pointer.Capture(null);
        Notify();
        e.Handled = true;
    }

    private void OnWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_source is null || Math.Abs(e.Delta.Y) < double.Epsilon) return;
        SetZoom(_zoom + (e.Delta.Y > 0 ? .15 : -.15));
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
                Tool = Tool,
                Points = [.. _stroke],
                Radius = BrushRadius,
                Softness = Softness,
                Erase = Tool == MaskTool.Eraser
            },
            MaskTool.Rectangle or MaskTool.Ellipse => new MaskOperation
            {
                Tool = Tool,
                Rect = new NormalizedRect(
                    _start.Value.X,
                    _start.Value.Y,
                    end.X - _start.Value.X,
                    end.Y - _start.Value.Y),
                Softness = Softness
            },
            _ => null
        };
    }

    private void Notify()
    {
        RenderOverlay();
        InvalidateVisual();
        MaskChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderOverlay()
    {
        _overlay?.Dispose();
        _overlay = null;
        if (_source is null) return;

        var document = Document.Clone();
        if (_preview is not null) document.Operations.Add(_preview);
        var width = _source.PixelSize.Width;
        var height = _source.PixelSize.Height;
        var baseAlpha = ResampleBaseMask(width, height);
        var alpha = MaskRasterizer.Render(document, width, height, baseAlpha);
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var framebuffer = bitmap.Lock();
        unsafe
        {
            var target = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var a = (byte)(alpha[y * width + x] * 0.40);
                var offset = y * framebuffer.RowBytes + x * 4;
                target[offset] = 0;
                target[offset + 1] = 108;
                target[offset + 2] = 255;
                target[offset + 3] = a;
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
        var rect = GetImageRect();
        if (!rect.Contains(point)) return null;
        return new NormalizedPoint(
            (point.X - rect.X) / rect.Width,
            (point.Y - rect.Y) / rect.Height).Clamp();
    }

    private Rect GetImageRect()
    {
        if (_source is null) return Bounds;
        var fitted = FitRect(_source.PixelSize.Width, _source.PixelSize.Height, Bounds);
        var width = fitted.Width * _zoom;
        var height = fitted.Height * _zoom;
        return new Rect(
            Bounds.Center.X - width / 2 + _pan.X,
            Bounds.Center.Y - height / 2 + _pan.Y,
            width,
            height);
    }

    private void ClampPan()
    {
        if (_source is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _pan = default;
            return;
        }
        var fitted = FitRect(_source.PixelSize.Width, _source.PixelSize.Height, Bounds);
        var width = fitted.Width * _zoom;
        var height = fitted.Height * _zoom;
        var maximumX = Math.Max(0, (width - Bounds.Width) / 2);
        var maximumY = Math.Max(0, (height - Bounds.Height) / 2);
        _pan = new Vector(
            Math.Clamp(_pan.X, -maximumX, maximumX),
            Math.Clamp(_pan.Y, -maximumY, maximumY));
    }

    private static Rect FitRect(double width, double height, Rect bounds)
    {
        if (width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return bounds;
        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        var renderedWidth = width * scale;
        var renderedHeight = height * scale;
        return new Rect(
            (bounds.Width - renderedWidth) / 2,
            (bounds.Height - renderedHeight) / 2,
            renderedWidth,
            renderedHeight);
    }
}
