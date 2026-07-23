using System.Runtime.InteropServices.WindowsRuntime;
using Erasa.Video2.Core.Masking;
using Erasa.Video2.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Erasa.Video2.App.Controls;

public sealed class PanDeltaEventArgs(double deltaX, double deltaY) : EventArgs
{
    public double DeltaX { get; } = deltaX;
    public double DeltaY { get; } = deltaY;
}

public sealed partial class MaskEditor : UserControl
{
    private readonly Stack<MaskDocument> _undo = new();
    private readonly Stack<MaskDocument> _redo = new();
    private MaskOperation? _activeOperation;
    private Point _startPoint;
    private Point _lastPoint;
    private Polyline? _livePolyline;
    private Shape? _liveShape;
    private byte[]? _baseAlpha;
    private int _pixelWidth = 1280;
    private int _pixelHeight = 720;

    public MaskEditor()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
    }

    public MaskDocument Document { get; private set; } = new();
    public MaskTool Tool { get; set; } = MaskTool.Brush;
    public double BrushRadius { get; set; } = 0.025;
    public double Softness { get; set; } = 0.2;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasBaseMask => _baseAlpha is not null && MaskRasterizer.HasVisiblePixels(_baseAlpha);

    public event EventHandler? DocumentChanged;
    public event EventHandler<PanDeltaEventArgs>? PanDelta;

    public void SetSurfaceSize(int width, int height)
    {
        _pixelWidth = Math.Max(1, width);
        _pixelHeight = Math.Max(1, height);
        Width = _pixelWidth;
        Height = _pixelHeight;
        RenderOverlay();
    }

    public void SetDocument(MaskDocument document)
    {
        Document = document.Clone();
        _undo.Clear();
        _redo.Clear();
        ClearLiveVisuals();
        RenderOverlay();
    }

    public void SetBaseMask(byte[]? alpha)
    {
        if (alpha is not null && alpha.Length != _pixelWidth * _pixelHeight)
            throw new ArgumentException("Base mask size mismatch.", nameof(alpha));
        _baseAlpha = alpha?.ToArray();
        RenderOverlay();
    }

    public byte[] RenderCombinedMask() => MaskRasterizer.Render(Document, _pixelWidth, _pixelHeight, _baseAlpha ?? []);

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Document.Clone());
        Document = _undo.Pop();
        RenderOverlay();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Document.Clone());
        Document = _redo.Pop();
        RenderOverlay();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetMask()
    {
        if (!Document.HasContent && _baseAlpha is null) return;
        PushUndo();
        Document.Clear();
        _baseAlpha = null;
        RenderOverlay();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _startPoint = point.Position;
        _lastPoint = point.Position;
        CapturePointer(e.Pointer);

        if (Tool == MaskTool.Pan) return;
        _activeOperation = new MaskOperation
        {
            Tool = Tool,
            Radius = BrushRadius,
            Softness = Softness
        };
        if (Tool is MaskTool.Brush or MaskTool.Eraser)
        {
            _activeOperation.Points.Add(Normalize(point.Position));
            _livePolyline = new Polyline
            {
                Stroke = new SolidColorBrush(Tool == MaskTool.Eraser ? Colors.White : ColorHelper.FromArgb(230, 255, 100, 0)),
                StrokeThickness = Math.Max(2, BrushRadius * Math.Min(ActualWidth, ActualHeight) * 2),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = Tool == MaskTool.Eraser ? 0.55 : 0.8
            };
            _livePolyline.Points.Add(point.Position);
            LiveCanvas.Children.Add(_livePolyline);
        }
        else
        {
            _liveShape = Tool == MaskTool.Ellipse ? new Ellipse() : new Rectangle();
            _liveShape.Fill = new SolidColorBrush(ColorHelper.FromArgb(70, 255, 100, 0));
            _liveShape.Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 100, 0));
            _liveShape.StrokeThickness = 2;
            LiveCanvas.Children.Add(_liveShape);
            UpdateLiveShape(point.Position);
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activeOperation is null && Tool != MaskTool.Pan) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (Tool == MaskTool.Pan)
        {
            PanDelta?.Invoke(this, new PanDeltaEventArgs(point.Position.X - _lastPoint.X, point.Position.Y - _lastPoint.Y));
            _lastPoint = point.Position;
            e.Handled = true;
            return;
        }
        if (_activeOperation is null) return;
        if (Tool is MaskTool.Brush or MaskTool.Eraser)
        {
            var normalized = Normalize(point.Position);
            var previous = _activeOperation.Points[^1];
            if (Math.Abs(previous.X - normalized.X) + Math.Abs(previous.Y - normalized.Y) > 0.001)
            {
                _activeOperation.Points.Add(normalized);
                _livePolyline?.Points.Add(point.Position);
            }
        }
        else
        {
            UpdateLiveShape(point.Position);
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        if (Tool == MaskTool.Pan)
        {
            _activeOperation = null;
            return;
        }
        if (_activeOperation is null) return;
        var end = e.GetCurrentPoint(this).Position;
        if (Tool is MaskTool.Rectangle or MaskTool.Ellipse)
        {
            var startNormalized = Normalize(_startPoint);
            var endNormalized = Normalize(end);
            _activeOperation.Rect = new NormalizedRect(
                startNormalized.X,
                startNormalized.Y,
                endNormalized.X - startNormalized.X,
                endNormalized.Y - startNormalized.Y).Normalize();
            if (_activeOperation.Rect.Width < 0.002 || _activeOperation.Rect.Height < 0.002)
            {
                ClearLiveVisuals();
                _activeOperation = null;
                return;
            }
        }
        PushUndo();
        Document.Add(_activeOperation);
        _activeOperation = null;
        ClearLiveVisuals();
        RenderOverlay();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _activeOperation = null;
        ClearLiveVisuals();
    }

    private void PushUndo()
    {
        _undo.Push(Document.Clone());
        _redo.Clear();
    }

    private NormalizedPoint Normalize(Point point)
    {
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        return new NormalizedPoint(point.X / width, point.Y / height).Clamp();
    }

    private void UpdateLiveShape(Point end)
    {
        if (_liveShape is null) return;
        var left = Math.Min(_startPoint.X, end.X);
        var top = Math.Min(_startPoint.Y, end.Y);
        var width = Math.Abs(end.X - _startPoint.X);
        var height = Math.Abs(end.Y - _startPoint.Y);
        Canvas.SetLeft(_liveShape, left);
        Canvas.SetTop(_liveShape, top);
        _liveShape.Width = width;
        _liveShape.Height = height;
    }

    private void ClearLiveVisuals()
    {
        LiveCanvas.Children.Clear();
        _livePolyline = null;
        _liveShape = null;
    }

    private void RenderOverlay()
    {
        if (_pixelWidth <= 0 || _pixelHeight <= 0) return;
        var mask = RenderCombinedMask();
        var bitmap = new WriteableBitmap(_pixelWidth, _pixelHeight);
        var pixels = new byte[mask.Length * 4];
        for (var index = 0; index < mask.Length; index++)
        {
            var offset = index * 4;
            pixels[offset] = 0;
            pixels[offset + 1] = 96;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = (byte)Math.Round(mask[index] * 0.42);
        }
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Position = 0;
        stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        MaskImage.Source = bitmap;
    }
}
