using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;

namespace PictureEditor.Views;

public class StripOverlay : Control
{
    // Strip rectangle in image pixel coordinates (full height, only X and Width vary)
    public static readonly StyledProperty<int> StripXProperty =
        AvaloniaProperty.Register<StripOverlay, int>(nameof(StripX));
    public static readonly StyledProperty<int> StripWidthProperty =
        AvaloniaProperty.Register<StripOverlay, int>(nameof(StripWidth));

    // Actual image pixel dimensions (for coordinate mapping)
    public static readonly StyledProperty<int> ImagePixelWidthProperty =
        AvaloniaProperty.Register<StripOverlay, int>(nameof(ImagePixelWidth));
    public static readonly StyledProperty<int> ImagePixelHeightProperty =
        AvaloniaProperty.Register<StripOverlay, int>(nameof(ImagePixelHeight));

    public int StripX { get => GetValue(StripXProperty); set => SetValue(StripXProperty, value); }
    public int StripWidth { get => GetValue(StripWidthProperty); set => SetValue(StripWidthProperty, value); }
    public int ImagePixelWidth { get => GetValue(ImagePixelWidthProperty); set => SetValue(ImagePixelWidthProperty, value); }
    public int ImagePixelHeight { get => GetValue(ImagePixelHeightProperty); set => SetValue(ImagePixelHeightProperty, value); }

    // The strip being removed is highlighted in red
    private static readonly IBrush OverlayBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly IBrush StripBrush = new SolidColorBrush(Color.FromArgb(100, 255, 60, 60));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 80, 80)), 2, new DashStyle(new double[] { 6, 3 }, 0));
    private static readonly IPen BorderPenShadow = new Pen(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), 2);
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.FromArgb(230, 255, 100, 100));
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1);
    private static readonly IBrush LabelBackgroundBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
    private static readonly Typeface LabelTypeface = new("Inter", FontStyle.Normal, FontWeight.Normal);
    private const double HandleSize = 10;
    private const double HandleHeight = 30;
    private const double EdgeHitSize = 14;

    private static readonly Cursor CursorCross = new(StandardCursorType.Cross);
    private static readonly Cursor CursorSizeAll = new(StandardCursorType.SizeAll);
    private static readonly Cursor CursorWE = new(StandardCursorType.SizeWestEast);

    private enum DragMode { None, Move, Left, Right, NewSelection }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStart;
    private (int X, int Width) _stripAtDragStart;

    private FormattedText? _cachedLabelText;
    private int _cachedLabelW;

    static StripOverlay()
    {
        AffectsRender<StripOverlay>(StripXProperty, StripWidthProperty,
            ImagePixelWidthProperty, ImagePixelHeightProperty);
    }

    public StripOverlay()
    {
        ClipToBounds = true;
        Cursor = CursorCross;
    }

    private Rect ImageToScreen(Rect imageRect)
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0) return default;
        var (offsetX, offsetY, scale) = GetImageTransform();
        return new Rect(
            imageRect.X * scale + offsetX,
            imageRect.Y * scale + offsetY,
            imageRect.Width * scale,
            imageRect.Height * scale);
    }

    private double ScreenToImageX(double screenX)
    {
        if (ImagePixelWidth <= 0) return 0;
        var (offsetX, _, scale) = GetImageTransform();
        return (screenX - offsetX) / scale;
    }

    private (double offsetX, double offsetY, double scale) GetImageTransform()
    {
        double controlW = Bounds.Width;
        double controlH = Bounds.Height;
        double scaleX = controlW / ImagePixelWidth;
        double scaleY = controlH / ImagePixelHeight;
        double scale = Math.Min(scaleX, scaleY);
        double renderW = ImagePixelWidth * scale;
        double renderH = ImagePixelHeight * scale;
        double offsetX = (controlW - renderW) / 2;
        double offsetY = (controlH - renderH) / 2;
        return (offsetX, offsetY, scale);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0) return;

        var fullScreen = ImageToScreen(new Rect(0, 0, ImagePixelWidth, ImagePixelHeight));
        var stripScreen = ImageToScreen(new Rect(StripX, 0, StripWidth, ImagePixelHeight));

        // Dim the left and right kept portions slightly
        if (stripScreen.Left > fullScreen.Left)
            context.FillRectangle(OverlayBrush, new Rect(fullScreen.Left, fullScreen.Top, stripScreen.Left - fullScreen.Left, fullScreen.Height));
        if (stripScreen.Right < fullScreen.Right)
            context.FillRectangle(OverlayBrush, new Rect(stripScreen.Right, fullScreen.Top, fullScreen.Right - stripScreen.Right, fullScreen.Height));

        // Fill the strip being removed with red tint
        context.FillRectangle(StripBrush, stripScreen);

        // Draw strip borders
        context.DrawRectangle(BorderPenShadow, stripScreen);
        context.DrawRectangle(BorderPen, stripScreen);

        // Draw left and right edge handles (tall vertical handles)
        DrawEdgeHandle(context, stripScreen.Left, stripScreen.Center.Y);
        DrawEdgeHandle(context, stripScreen.Right, stripScreen.Center.Y);

        // Draw width label
        if (_cachedLabelText == null || _cachedLabelW != StripWidth)
        {
            _cachedLabelW = StripWidth;
            _cachedLabelText = new FormattedText(
                $"Remove {StripWidth}px",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                12, Brushes.White);
        }
        double labelX = stripScreen.Center.X - _cachedLabelText.Width / 2;
        double labelY = stripScreen.Bottom + 6;
        if (labelY + _cachedLabelText.Height > Bounds.Height)
            labelY = stripScreen.Top - _cachedLabelText.Height - 6;
        context.FillRectangle(LabelBackgroundBrush,
            new Rect(labelX - 4, labelY - 2, _cachedLabelText.Width + 8, _cachedLabelText.Height + 4), 3);
        context.DrawText(_cachedLabelText, new Point(labelX, labelY));
    }

    private static void DrawEdgeHandle(DrawingContext context, double x, double centerY)
    {
        var rect = new Rect(x - HandleSize / 2, centerY - HandleHeight / 2, HandleSize, HandleHeight);
        context.FillRectangle(HandleFill, rect, 3);
        context.DrawRectangle(HandlePen, rect, 3);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        var stripScreen = ImageToScreen(new Rect(StripX, 0, StripWidth, ImagePixelHeight));

        _dragStart = pos;
        _stripAtDragStart = (StripX, StripWidth);
        _dragMode = HitTest(pos, stripScreen);

        if (_dragMode == DragMode.None)
        {
            // Start a new selection at click position
            double imgX = Math.Clamp(ScreenToImageX(pos.X), 0, ImagePixelWidth);
            StripX = (int)imgX;
            StripWidth = 1;
            _stripAtDragStart = (StripX, 1);
            _dragMode = DragMode.Right;
        }

        e.Handled = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var pos = e.GetPosition(this);

        if (_dragMode == DragMode.None)
        {
            var stripScreen = ImageToScreen(new Rect(StripX, 0, StripWidth, ImagePixelHeight));
            var hit = HitTest(pos, stripScreen);
            Cursor = hit switch
            {
                DragMode.Move => CursorSizeAll,
                DragMode.Left or DragMode.Right => CursorWE,
                _ => CursorCross
            };
            return;
        }

        var (_, _, scale) = GetImageTransform();
        if (scale <= 0) return;
        double dx = (pos.X - _dragStart.X) / scale;

        var orig = _stripAtDragStart;
        double newX = orig.X, newW = orig.Width;

        switch (_dragMode)
        {
            case DragMode.Move:
                newX = orig.X + dx;
                break;
            case DragMode.Left:
                newX = orig.X + dx;
                newW = orig.Width - dx;
                break;
            case DragMode.Right:
                newW = orig.Width + dx;
                break;
        }

        // Handle negative width (drag past opposite edge)
        if (newW < 1) { newX += newW - 1; newW = 1; }

        // Clamp to image bounds
        if (newX < 0) { if (_dragMode == DragMode.Move) { /* keep width */ } else newW += newX; newX = 0; }
        if (newX + newW > ImagePixelWidth) { if (_dragMode == DragMode.Move) newX = ImagePixelWidth - newW; else newW = ImagePixelWidth - newX; }

        StripX = Math.Max(0, (int)newX);
        StripWidth = Math.Max(1, (int)newW);

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragMode = DragMode.None;
        e.Pointer.Capture(null);
    }

    private DragMode HitTest(Point pos, Rect stripScreen)
    {
        // Left edge
        if (Math.Abs(pos.X - stripScreen.Left) < EdgeHitSize && pos.Y > stripScreen.Top && pos.Y < stripScreen.Bottom)
            return DragMode.Left;
        // Right edge
        if (Math.Abs(pos.X - stripScreen.Right) < EdgeHitSize && pos.Y > stripScreen.Top && pos.Y < stripScreen.Bottom)
            return DragMode.Right;
        // Inside strip = move
        if (stripScreen.Contains(pos))
            return DragMode.Move;

        return DragMode.None;
    }
}
