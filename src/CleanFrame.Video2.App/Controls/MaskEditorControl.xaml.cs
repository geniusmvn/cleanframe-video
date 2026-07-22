using System.Runtime.InteropServices.WindowsRuntime;
using CleanFrame.Video2.Core.Models;
using CleanFrame.Video2.Core.Processing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace CleanFrame.Video2.App.Controls;

public sealed partial class MaskEditorControl : UserControl
{
    private NormalizedPoint? _start;
    private readonly List<NormalizedPoint> _stroke = [];
    private uint? _pointerId;
    private Point? _panLast;
    private MaskOperation? _preview;

    public MaskDocument Document { get; private set; } = new();
    public MaskTool Tool { get; set; } = MaskTool.Rectangle;
    public double BrushRadius { get; set; } = 0.025;
    public double Softness { get; set; } = 0.45;
    public double VideoAspectRatio { get; set; } = 16d / 9;
    public event EventHandler? MaskChanged;
    public event EventHandler<PanDeltaEventArgs>? PanDelta;

    public MaskEditorControl()
    {
        InitializeComponent();
        SizeChanged += async (_, _) => await RenderAsync();
    }

    public async Task SetDocumentAsync(MaskDocument document)
    {
        Document = document;
        if (document.SourceAspectRatio > 0) VideoAspectRatio = document.SourceAspectRatio;
        await RenderAsync();
        MaskChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UndoAsync() { if (Document.Undo()) { await RenderAsync(); MaskChanged?.Invoke(this, EventArgs.Empty); } }
    public async Task RedoAsync() { if (Document.Redo()) { await RenderAsync(); MaskChanged?.Invoke(this, EventArgs.Empty); } }
    public async Task ResetAsync() { Document.Reset(); await RenderAsync(); MaskChanged?.Invoke(this, EventArgs.Empty); }

    private async void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        if (Tool == MaskTool.Pan)
        {
            _pointerId = e.Pointer.PointerId;
            _panLast = position;
            CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        var point = ToNormalized(position);
        if (point is null) return;
        _pointerId = e.Pointer.PointerId;
        CapturePointer(e.Pointer);
        _start = point;
        _stroke.Clear();
        _stroke.Add(point.Value);
        e.Handled = true;
        await UpdatePreviewAsync(point.Value);
    }

    private async void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        UpdateHover(position);
        if (Tool == MaskTool.Pan && _pointerId == e.Pointer.PointerId && _panLast is Point last)
        {
            PanDelta?.Invoke(this, new PanDeltaEventArgs(position.X - last.X, position.Y - last.Y));
            _panLast = position;
            e.Handled = true;
            return;
        }
        if (_pointerId != e.Pointer.PointerId || _start is null) return;
        var point = ToNormalized(position);
        if (point is null) return;
        if (Tool is MaskTool.Brush or MaskTool.Eraser) _stroke.Add(point.Value);
        await UpdatePreviewAsync(point.Value);
        e.Handled = true;
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        if (Tool == MaskTool.Pan)
        {
            ClearGesture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_start is null) return;
        var end = ToNormalized(e.GetCurrentPoint(this).Position) ?? _start.Value;
        var operation = BuildOperation(end);
        if (operation is not null) Document.Add(operation);
        ClearGesture(e.Pointer);
        await RenderAsync();
        MaskChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private async void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ClearGesture(e.Pointer);
        await RenderAsync();
    }

    private void ClearGesture(Pointer pointer)
    {
        ReleasePointerCapture(pointer);
        _pointerId = null;
        _panLast = null;
        _start = null;
        _stroke.Clear();
        _preview = null;
    }

    private MaskOperation? BuildOperation(NormalizedPoint end)
    {
        if (_start is null) return null;
        if (Tool is MaskTool.Brush or MaskTool.Eraser)
            return new StrokeMaskOperation(_stroke.ToArray(), BrushRadius, Softness, Tool == MaskTool.Eraser);
        if (Tool is MaskTool.Rectangle or MaskTool.Ellipse)
            return new ShapeMaskOperation(Tool,
                new NormalizedRect(_start.Value.X, _start.Value.Y, end.X - _start.Value.X, end.Y - _start.Value.Y),
                Softness);
        return null;
    }

    private async Task UpdatePreviewAsync(NormalizedPoint end)
    {
        _preview = BuildOperation(end);
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var availableWidth = Math.Clamp((int)Math.Round(ActualWidth), 64, 960);
        var availableHeight = Math.Clamp((int)Math.Round(ActualHeight), 64, 540);
        int width;
        int height;
        if (availableWidth / (double)availableHeight > VideoAspectRatio)
        {
            height = availableHeight;
            width = Math.Max(1, (int)Math.Round(height * VideoAspectRatio));
        }
        else
        {
            width = availableWidth;
            height = Math.Max(1, (int)Math.Round(width / VideoAspectRatio));
        }
        var previewDocument = new MaskDocument { SourceAspectRatio = VideoAspectRatio };
        previewDocument.Operations.AddRange(Document.Operations);
        if (_preview is not null) previewDocument.Operations.Add(_preview);
        var alpha = MaskRasterizer.Render(previewDocument, width, height);
        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < alpha.Length; i++)
            {
                var a = (byte)Math.Clamp(Math.Round(alpha[i] * 150), 0, 150);
                var offset = i * 4;
                pixels[offset] = 80;       // B
                pixels[offset + 1] = 45;   // G
                pixels[offset + 2] = 255;  // R
                pixels[offset + 3] = a;
            }
            await stream.WriteAsync(pixels);
        }
        bitmap.Invalidate();
        MaskImage.Source = bitmap;
    }

    private NormalizedPoint? ToNormalized(Point point)
    {
        var controlWidth = ActualWidth;
        var controlHeight = ActualHeight;
        if (controlWidth <= 0 || controlHeight <= 0) return null;
        var controlAspect = controlWidth / controlHeight;
        double videoWidth, videoHeight, left, top;
        if (controlAspect > VideoAspectRatio)
        {
            videoHeight = controlHeight;
            videoWidth = videoHeight * VideoAspectRatio;
            left = (controlWidth - videoWidth) / 2;
            top = 0;
        }
        else
        {
            videoWidth = controlWidth;
            videoHeight = videoWidth / VideoAspectRatio;
            left = 0;
            top = (controlHeight - videoHeight) / 2;
        }
        if (point.X < left || point.Y < top || point.X > left + videoWidth || point.Y > top + videoHeight) return null;
        return new NormalizedPoint((point.X - left) / videoWidth, (point.Y - top) / videoHeight).Clamp();
    }

    private void UpdateHover(Point point)
    {
        if (Tool is not (MaskTool.Brush or MaskTool.Eraser)) { HoverRing.Visibility = Visibility.Collapsed; return; }
        var diameter = Math.Max(8, BrushRadius * Math.Min(ActualWidth, ActualHeight) * 2);
        HoverRing.Width = diameter;
        HoverRing.Height = diameter;
        HoverRing.CornerRadius = new CornerRadius(diameter / 2);
        HoverRing.Visibility = Visibility.Visible;
        HoverRing.Margin = new Thickness(point.X - diameter / 2, point.Y - diameter / 2, 0, 0);
        HoverRing.HorizontalAlignment = HorizontalAlignment.Left;
        HoverRing.VerticalAlignment = VerticalAlignment.Top;
    }
}

public sealed class PanDeltaEventArgs(double deltaX, double deltaY) : EventArgs
{
    public double DeltaX { get; } = deltaX;
    public double DeltaY { get; } = deltaY;
}
