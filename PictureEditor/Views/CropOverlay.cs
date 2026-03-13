using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;

namespace PictureEditor.Views;

public class CropOverlay : Control
{
    // The crop rectangle in image pixel coordinates
    public static readonly StyledProperty<int> CropXProperty =
        AvaloniaProperty.Register<CropOverlay, int>(nameof(CropX));
    public static readonly StyledProperty<int> CropYProperty =
        AvaloniaProperty.Register<CropOverlay, int>(nameof(CropY));
    public static readonly StyledProperty<int> CropWidthProperty =
        AvaloniaProperty.Register<CropOverlay, int>(nameof(CropWidth));
    public static readonly StyledProperty<int> CropHeightProperty =
        AvaloniaProperty.Register<CropOverlay, int>(nameof(CropHeight));

    // Actual image pixel dimensions (for coordinate mapping)
    public static readonly StyledProperty<int> ImagePixelWidthProperty =
        AvaloniaProperty.Register<CropOverlay, int>(nameof(ImagePixelWidth));
    public static readonly StyledProperty<int> ImagePixelHeightProperty =
        AvaloniaProperty.Register<CropOverlay, int>(nameof(ImagePixelHeight));

    public int CropX { get => GetValue(CropXProperty); set => SetValue(CropXProperty, value); }
    public int CropY { get => GetValue(CropYProperty); set => SetValue(CropYProperty, value); }
    public int CropWidth { get => GetValue(CropWidthProperty); set => SetValue(CropWidthProperty, value); }
    public int CropHeight { get => GetValue(CropHeightProperty); set => SetValue(CropHeightProperty, value); }
    public int ImagePixelWidth { get => GetValue(ImagePixelWidthProperty); set => SetValue(ImagePixelWidthProperty, value); }
    public int ImagePixelHeight { get => GetValue(ImagePixelHeightProperty); set => SetValue(ImagePixelHeightProperty, value); }

    private static readonly IBrush OverlayBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
    private static readonly IPen BorderPen = new Pen(Brushes.White, 2, new DashStyle(new double[] { 6, 3 }, 0));
    private static readonly IPen BorderPenShadow = new Pen(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), 2);
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1);
    private static readonly IBrush LabelBackgroundBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
    private static readonly Typeface LabelTypeface = new("Inter", FontStyle.Normal, FontWeight.Normal);
    private const double HandleSize = 10;
    private const double EdgeHitSize = 14;

    // Cached cursors to avoid allocations on every mouse move
    private static readonly Cursor CursorCross = new(StandardCursorType.Cross);
    private static readonly Cursor CursorSizeAll = new(StandardCursorType.SizeAll);
    private static readonly Cursor CursorTopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CursorTopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CursorNS = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor CursorWE = new(StandardCursorType.SizeWestEast);

    private enum DragMode { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, NewSelection }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStart;
    private Rect _cropAtDragStart;

    // Cached label text to avoid allocation on every render
    private FormattedText? _cachedLabelText;
    private int _cachedLabelW, _cachedLabelH;

    static CropOverlay()
    {
        AffectsRender<CropOverlay>(CropXProperty, CropYProperty, CropWidthProperty, CropHeightProperty,
            ImagePixelWidthProperty, ImagePixelHeightProperty);
    }

    public CropOverlay()
    {
        ClipToBounds = true;
        Cursor = CursorCross;
    }

    // Convert image pixel rect to screen rect
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

    // Convert screen point to image pixel point
    private Point ScreenToImage(Point screenPt)
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0) return default;
        var (offsetX, offsetY, scale) = GetImageTransform();
        return new Point(
            (screenPt.X - offsetX) / scale,
            (screenPt.Y - offsetY) / scale);
    }

    // Compute how the image is laid out within this control (Uniform stretch)
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

        var fullImageRect = new Rect(0, 0, ImagePixelWidth, ImagePixelHeight);
        var fullScreen = ImageToScreen(fullImageRect);
        var cropImageRect = new Rect(CropX, CropY, CropWidth, CropHeight);
        var cropScreen = ImageToScreen(cropImageRect);

        // Draw dark overlay outside crop area (4 rectangles)
        // Top
        if (cropScreen.Top > fullScreen.Top)
            context.FillRectangle(OverlayBrush, new Rect(fullScreen.Left, fullScreen.Top, fullScreen.Width, cropScreen.Top - fullScreen.Top));
        // Bottom
        if (cropScreen.Bottom < fullScreen.Bottom)
            context.FillRectangle(OverlayBrush, new Rect(fullScreen.Left, cropScreen.Bottom, fullScreen.Width, fullScreen.Bottom - cropScreen.Bottom));
        // Left
        if (cropScreen.Left > fullScreen.Left)
            context.FillRectangle(OverlayBrush, new Rect(fullScreen.Left, cropScreen.Top, cropScreen.Left - fullScreen.Left, cropScreen.Height));
        // Right
        if (cropScreen.Right < fullScreen.Right)
            context.FillRectangle(OverlayBrush, new Rect(cropScreen.Right, cropScreen.Top, fullScreen.Right - cropScreen.Right, cropScreen.Height));

        // Draw crop border (shadow first, then dashed white)
        context.DrawRectangle(BorderPenShadow, cropScreen);
        context.DrawRectangle(BorderPen, cropScreen);

        // Draw 8 resize handles
        DrawHandle(context, cropScreen.TopLeft);
        DrawHandle(context, cropScreen.TopRight);
        DrawHandle(context, cropScreen.BottomLeft);
        DrawHandle(context, cropScreen.BottomRight);
        DrawHandle(context, new Point(cropScreen.Center.X, cropScreen.Top));
        DrawHandle(context, new Point(cropScreen.Center.X, cropScreen.Bottom));
        DrawHandle(context, new Point(cropScreen.Left, cropScreen.Center.Y));
        DrawHandle(context, new Point(cropScreen.Right, cropScreen.Center.Y));

        // Draw dimensions label (cache FormattedText when dimensions unchanged)
        if (_cachedLabelText == null || _cachedLabelW != CropWidth || _cachedLabelH != CropHeight)
        {
            _cachedLabelW = CropWidth;
            _cachedLabelH = CropHeight;
            _cachedLabelText = new FormattedText(
                $"{CropWidth} x {CropHeight}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                12, Brushes.White);
        }
        double labelX = cropScreen.Center.X - _cachedLabelText.Width / 2;
        double labelY = cropScreen.Bottom + 6;
        if (labelY + _cachedLabelText.Height > Bounds.Height)
            labelY = cropScreen.Top - _cachedLabelText.Height - 6;
        context.FillRectangle(LabelBackgroundBrush,
            new Rect(labelX - 4, labelY - 2, _cachedLabelText.Width + 8, _cachedLabelText.Height + 4), 3);
        context.DrawText(_cachedLabelText, new Point(labelX, labelY));
    }

    private static void DrawHandle(DrawingContext context, Point center)
    {
        var rect = new Rect(center.X - HandleSize / 2, center.Y - HandleSize / 2, HandleSize, HandleSize);
        context.FillRectangle(HandleFill, rect, 2);
        context.DrawRectangle(HandlePen, rect, 2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        var cropScreen = ImageToScreen(new Rect(CropX, CropY, CropWidth, CropHeight));

        _dragStart = pos;
        _cropAtDragStart = new Rect(CropX, CropY, CropWidth, CropHeight);
        _dragMode = HitTest(pos, cropScreen);

        if (_dragMode == DragMode.None)
        {
            // Start a new selection
            var imgPt = ScreenToImage(pos);
            imgPt = ClampToImage(imgPt);
            CropX = (int)imgPt.X;
            CropY = (int)imgPt.Y;
            CropWidth = 1;
            CropHeight = 1;
            _cropAtDragStart = new Rect(CropX, CropY, 1, 1);
            _dragMode = DragMode.BottomRight;
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
            // Update cursor based on hover position
            var cropScreen = ImageToScreen(new Rect(CropX, CropY, CropWidth, CropHeight));
            var hit = HitTest(pos, cropScreen);
            Cursor = hit switch
            {
                DragMode.Move => CursorSizeAll,
                DragMode.TopLeft or DragMode.BottomRight => CursorTopLeft,
                DragMode.TopRight or DragMode.BottomLeft => CursorTopRight,
                DragMode.Top or DragMode.Bottom => CursorNS,
                DragMode.Left or DragMode.Right => CursorWE,
                _ => CursorCross
            };
            return;
        }

        // Compute delta in image pixels
        var (_, _, scale) = GetImageTransform();
        if (scale <= 0) return;
        double dx = (pos.X - _dragStart.X) / scale;
        double dy = (pos.Y - _dragStart.Y) / scale;

        var orig = _cropAtDragStart;
        double newX = orig.X, newY = orig.Y, newW = orig.Width, newH = orig.Height;

        switch (_dragMode)
        {
            case DragMode.Move:
                newX = orig.X + dx;
                newY = orig.Y + dy;
                break;
            case DragMode.TopLeft:
                newX = orig.X + dx;
                newY = orig.Y + dy;
                newW = orig.Width - dx;
                newH = orig.Height - dy;
                break;
            case DragMode.Top:
                newY = orig.Y + dy;
                newH = orig.Height - dy;
                break;
            case DragMode.TopRight:
                newY = orig.Y + dy;
                newW = orig.Width + dx;
                newH = orig.Height - dy;
                break;
            case DragMode.Right:
                newW = orig.Width + dx;
                break;
            case DragMode.BottomRight:
                newW = orig.Width + dx;
                newH = orig.Height + dy;
                break;
            case DragMode.Bottom:
                newH = orig.Height + dy;
                break;
            case DragMode.BottomLeft:
                newX = orig.X + dx;
                newW = orig.Width - dx;
                newH = orig.Height + dy;
                break;
            case DragMode.Left:
                newX = orig.X + dx;
                newW = orig.Width - dx;
                break;
        }

        // Handle negative width/height (drag past opposite edge)
        if (newW < 1) { newX += newW - 1; newW = 1; }
        if (newH < 1) { newY += newH - 1; newH = 1; }

        // Clamp to image bounds
        if (newX < 0) { if (_dragMode == DragMode.Move) newW = orig.Width; else newW += newX; newX = 0; }
        if (newY < 0) { if (_dragMode == DragMode.Move) newH = orig.Height; else newH += newY; newY = 0; }
        if (newX + newW > ImagePixelWidth) { if (_dragMode == DragMode.Move) newX = ImagePixelWidth - newW; else newW = ImagePixelWidth - newX; }
        if (newY + newH > ImagePixelHeight) { if (_dragMode == DragMode.Move) newY = ImagePixelHeight - newH; else newH = ImagePixelHeight - newY; }

        CropX = Math.Max(0, (int)newX);
        CropY = Math.Max(0, (int)newY);
        CropWidth = Math.Max(1, (int)newW);
        CropHeight = Math.Max(1, (int)newH);

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragMode = DragMode.None;
        e.Pointer.Capture(null);
    }

    private DragMode HitTest(Point pos, Rect cropScreen)
    {
        double h = HandleSize + 4;

        // Corner handles
        if (IsNear(pos, cropScreen.TopLeft, h)) return DragMode.TopLeft;
        if (IsNear(pos, cropScreen.TopRight, h)) return DragMode.TopRight;
        if (IsNear(pos, cropScreen.BottomLeft, h)) return DragMode.BottomLeft;
        if (IsNear(pos, cropScreen.BottomRight, h)) return DragMode.BottomRight;

        // Edge handles
        if (IsNear(pos, new Point(cropScreen.Center.X, cropScreen.Top), h)) return DragMode.Top;
        if (IsNear(pos, new Point(cropScreen.Center.X, cropScreen.Bottom), h)) return DragMode.Bottom;
        if (IsNear(pos, new Point(cropScreen.Left, cropScreen.Center.Y), h)) return DragMode.Left;
        if (IsNear(pos, new Point(cropScreen.Right, cropScreen.Center.Y), h)) return DragMode.Right;

        // Edge proximity
        if (cropScreen.Width > 20 && cropScreen.Height > 20)
        {
            if (Math.Abs(pos.Y - cropScreen.Top) < EdgeHitSize && pos.X > cropScreen.Left && pos.X < cropScreen.Right) return DragMode.Top;
            if (Math.Abs(pos.Y - cropScreen.Bottom) < EdgeHitSize && pos.X > cropScreen.Left && pos.X < cropScreen.Right) return DragMode.Bottom;
            if (Math.Abs(pos.X - cropScreen.Left) < EdgeHitSize && pos.Y > cropScreen.Top && pos.Y < cropScreen.Bottom) return DragMode.Left;
            if (Math.Abs(pos.X - cropScreen.Right) < EdgeHitSize && pos.Y > cropScreen.Top && pos.Y < cropScreen.Bottom) return DragMode.Right;
        }

        // Inside crop = move
        if (cropScreen.Contains(pos)) return DragMode.Move;

        return DragMode.None;
    }

    private static bool IsNear(Point a, Point b, double threshold)
    {
        return Math.Abs(a.X - b.X) < threshold && Math.Abs(a.Y - b.Y) < threshold;
    }

    private Point ClampToImage(Point pt)
    {
        return new Point(
            Math.Clamp(pt.X, 0, ImagePixelWidth),
            Math.Clamp(pt.Y, 0, ImagePixelHeight));
    }
}
