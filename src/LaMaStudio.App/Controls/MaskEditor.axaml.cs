using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LaMaStudio.Core;

namespace LaMaStudio.App.Controls;

public enum MaskTool { Brush, Eraser, Rectangle }

public partial class MaskEditor : UserControl
{
    private byte[] _alpha = [];
    private int _imageWidth;
    private int _imageHeight;
    private bool _drawing;
    private PixelPoint _last;
    private PixelPoint _rectangleStart;
    private readonly Stack<byte[]> _undo = new();
    private readonly Stack<byte[]> _redo = new();
    private WriteableBitmap? _overlay;

    public MaskTool Tool { get; set; } = MaskTool.Brush;
    public double BrushSize { get; set; } = 32;
    public bool HasSource => _imageWidth > 0 && _imageHeight > 0;
    public bool HasMask => _alpha.Any(x => x != 0);
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;

    public MaskEditor() => InitializeComponent();

    public void LoadSource(string path)
    {
        var bitmap = new Bitmap(path);
        SourceImage.Source = bitmap;
        _imageWidth = bitmap.PixelSize.Width;
        _imageHeight = bitmap.PixelSize.Height;
        _alpha = new byte[checked(_imageWidth * _imageHeight)];
        _undo.Clear(); _redo.Clear();
        RebuildOverlay();
    }

    public void ShowSource(string path)
    {
        SourceImage.Source = new Bitmap(path);
    }

    public void ResetMask()
    {
        if (!HasSource) return;
        PushUndo();
        Array.Clear(_alpha);
        RebuildOverlay();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push((byte[])_alpha.Clone());
        _alpha = _undo.Pop();
        RebuildOverlay();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push((byte[])_alpha.Clone());
        _alpha = _redo.Pop();
        RebuildOverlay();
    }

    public void SaveMask(string path)
    {
        if (!HasSource) throw new InvalidOperationException("Chưa mở ảnh hoặc video.");
        PgmMask.Save(path, _imageWidth, _imageHeight, _alpha);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!HasSource || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = ToPixel(e.GetPosition(this));
        if (p is null) return;
        PushUndo();
        _redo.Clear();
        _drawing = true;
        _last = p.Value;
        _rectangleStart = p.Value;
        e.Pointer.Capture(this);
        if (Tool != MaskTool.Rectangle) DrawLine(_last, _last, Tool == MaskTool.Eraser ? (byte)0 : (byte)255);
        RebuildOverlay();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_drawing || Tool == MaskTool.Rectangle) return;
        var p = ToPixel(e.GetPosition(this));
        if (p is null) return;
        DrawLine(_last, p.Value, Tool == MaskTool.Eraser ? (byte)0 : (byte)255);
        _last = p.Value;
        RebuildOverlay();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_drawing) return;
        var p = ToPixel(e.GetPosition(this)) ?? _last;
        if (Tool == MaskTool.Rectangle) FillRectangle(_rectangleStart, p, 255);
        _drawing = false;
        e.Pointer.Capture(null);
        RebuildOverlay();
    }

    private PixelPoint? ToPixel(Point p)
    {
        if (Bounds.Width <= 1 || Bounds.Height <= 1) return null;
        if (p.X < 0 || p.Y < 0 || p.X >= Bounds.Width || p.Y >= Bounds.Height) return null;
        return new PixelPoint(
            Math.Clamp((int)(p.X / Bounds.Width * _imageWidth), 0, _imageWidth - 1),
            Math.Clamp((int)(p.Y / Bounds.Height * _imageHeight), 0, _imageHeight - 1));
    }

    private void PushUndo()
    {
        _undo.Push((byte[])_alpha.Clone());
        while (_undo.Count > 20)
        {
            var keep = _undo.Reverse().Take(20).Reverse().ToArray();
            _undo.Clear(); foreach (var item in keep) _undo.Push(item);
        }
    }

    private void DrawLine(PixelPoint a, PixelPoint b, byte value)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps == 0) { DrawCircle(a.X, a.Y, value); return; }
        for (var i = 0; i <= steps; i++)
            DrawCircle(a.X + dx * i / steps, a.Y + dy * i / steps, value);
    }

    private void DrawCircle(int cx, int cy, byte value)
    {
        var scale = _imageWidth / Math.Max(Bounds.Width, 1);
        var radius = Math.Max(1, (int)Math.Round(BrushSize * scale / 2));
        var r2 = radius * radius;
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(_imageHeight - 1, cy + radius); y++)
        for (var x = Math.Max(0, cx - radius); x <= Math.Min(_imageWidth - 1, cx + radius); x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2) _alpha[y * _imageWidth + x] = value;
    }

    private void FillRectangle(PixelPoint a, PixelPoint b, byte value)
    {
        var left = Math.Min(a.X, b.X); var right = Math.Max(a.X, b.X);
        var top = Math.Min(a.Y, b.Y); var bottom = Math.Max(a.Y, b.Y);
        for (var y = top; y <= bottom; y++)
            _alpha.AsSpan(y * _imageWidth + left, right - left + 1).Fill(value);
    }

    private void RebuildOverlay()
    {
        if (!HasSource) return;
        _overlay?.Dispose();
        _overlay = new WriteableBitmap(new PixelSize(_imageWidth, _imageHeight), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = new byte[_imageWidth * _imageHeight * 4];
        for (var i = 0; i < _alpha.Length; i++)
        {
            if (_alpha[i] == 0) continue;
            const byte opacity = 92;
            var offset = i * 4;
            pixels[offset] = (byte)(230 * opacity / 255);      // B
            pixels[offset + 1] = (byte)(178 * opacity / 255); // G
            pixels[offset + 2] = (byte)(42 * opacity / 255);  // R
            pixels[offset + 3] = opacity;
        }
        using (var framebuffer = _overlay.Lock())
        {
            var rowSize = _imageWidth * 4;
            for (var y = 0; y < _imageHeight; y++)
                Marshal.Copy(pixels, y * rowSize, framebuffer.Address + y * framebuffer.RowBytes, rowSize);
        }
        OverlayImage.Source = _overlay;
    }
}
