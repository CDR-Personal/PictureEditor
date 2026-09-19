using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;

namespace PictureEditor.Views;

/// <summary>
/// Pinch/scroll/keyboard zoom and pan for the main image area.
///
/// The transform is deliberately applied to the <em>content panel</em> that holds the
/// Image together with CropOverlay and StripOverlay, never to the Image alone: both
/// overlays independently re-derive the uniform-fit mapping from their own Bounds
/// (see CropOverlay.GetImageTransform), so transforming their shared parent keeps them
/// in lockstep with the picture and leaves their coordinate math untouched. A
/// RenderTransform runs after layout, so child Bounds never change and Avalonia's
/// hit-testing inverse-transforms pointer positions for free.
/// </summary>
public sealed class ImageZoomController
{
    private readonly Control _content;    // Panel holding Image + overlays

    // Zoom is a multiplier over fit-to-window: 1.0 means "fit", which is the floor.
    private double _zoom = 1.0;
    private Vector _offset;

    private int _imageW;
    private int _imageH;

    private const double MinZoom = 1.0;
    private const double WheelPanStep = 50.0;   // wheel delta is in "lines", not pixels
    private const double KeyZoomStep = 1.25;

    /// <summary>Raised whenever zoom changes, with the on-screen percentage of actual pixels.</summary>
    public event Action<double>? ZoomChanged;

    public ImageZoomController(Control content)
    {
        _content = content;
        _content.RenderTransformOrigin = RelativePoint.TopLeft;
    }

    public bool IsZoomed => _zoom > MinZoom + 1e-6;

    /// <summary>
    /// Rebase a viewport-space point into the content panel's frame. RenderTransform
    /// composes as <c>parent = O + M·p</c>, and ZoomContent's layout origin O is offset
    /// from the viewport by its Margin, so anchors must be shifted or the zoom drifts
    /// instead of staying pinned under the pointer.
    /// </summary>
    public Point ToContentFrame(Point viewportPoint) =>
        viewportPoint - _content.Bounds.Position;

    /// <summary>Image pixel dimensions, needed to locate the letterboxed image inside the panel.</summary>
    public void SetImageSize(int width, int height)
    {
        _imageW = width;
        _imageH = height;
        Clamp();
        Apply();
    }

    /// <summary>
    /// Scale that <c>Stretch="Uniform"</c> applies at zoom 1.0 — the same
    /// Math.Min(..) the overlays compute. Effective on-screen scale is FitScale * _zoom,
    /// so _zoom == 1/FitScale is exactly 100% actual pixels.
    /// </summary>
    private double FitScale()
    {
        if (_imageW <= 0 || _imageH <= 0) return 1.0;
        double w = _content.Bounds.Width, h = _content.Bounds.Height;
        if (w <= 0 || h <= 0) return 1.0;
        return Math.Min(w / _imageW, h / _imageH);
    }

    /// <summary>Zoom at which one screen pixel equals one image pixel.</summary>
    public double ActualPixelZoom()
    {
        double fit = FitScale();
        return fit <= 0 ? 1.0 : 1.0 / fit;
    }

    // Allow reaching 100% on a large photo, and still a useful 4x on a small one
    // (where Uniform already upscales and 1/FitScale would be below 1).
    private double MaxZoom() => Math.Clamp(ActualPixelZoom(), 4.0, 20.0);

    /// <summary>Zoom about a fixed point so the pixel under the fingers stays put.</summary>
    public void ZoomAt(double newZoom, Point anchor)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom());
        if (Math.Abs(newZoom - _zoom) < 1e-9) return;

        // Keep `anchor` mapping to the same content point: o' = a - z'(a - o)/z
        _offset = new Vector(
            anchor.X - newZoom * (anchor.X - _offset.X) / _zoom,
            anchor.Y - newZoom * (anchor.Y - _offset.Y) / _zoom);
        _zoom = newZoom;

        Clamp();
        Apply();
    }

    public void ZoomBy(double factor, Point anchor) => ZoomAt(_zoom * factor, anchor);

    public void ZoomByStep(bool zoomIn) =>
        ZoomBy(zoomIn ? KeyZoomStep : 1.0 / KeyZoomStep, ContentCenter());

    /// <summary>Double-click behaviour: snap between fit and 100% actual pixels.</summary>
    public void ToggleActualSize(Point anchor)
    {
        double actual = ActualPixelZoom();
        // Nothing to toggle when the image already fits at or below 100%.
        if (actual <= MinZoom + 1e-6) { Reset(); return; }
        ZoomAt(IsZoomed ? MinZoom : actual, anchor);
    }

    public void Pan(Vector delta)
    {
        if (!IsZoomed) return;
        _offset += delta;
        Clamp();
        Apply();
    }

    public void PanByWheel(Vector wheelDelta) =>
        Pan(new Vector(wheelDelta.X * WheelPanStep, wheelDelta.Y * WheelPanStep));

    public void Reset()
    {
        _zoom = MinZoom;
        _offset = default;
        Apply();
    }

    /// <summary>Re-clamp after the viewport resizes (window resize, chrome hiding).</summary>
    public void Refresh()
    {
        Clamp();
        Apply();
    }

    private Point ContentCenter() =>
        new(_content.Bounds.Width / 2, _content.Bounds.Height / 2);

    /// <summary>
    /// Keep the scaled <em>image</em> (not the whole panel) covering the viewport, so it
    /// can't be flung into empty space. On an axis the image no longer fills, re-center.
    /// </summary>
    private void Clamp()
    {
        _zoom = Math.Clamp(_zoom, MinZoom, MaxZoom());

        double panelW = _content.Bounds.Width, panelH = _content.Bounds.Height;
        if (panelW <= 0 || panelH <= 0 || _imageW <= 0 || _imageH <= 0)
        {
            _offset = default;
            return;
        }

        // The letterboxed image rect inside the untransformed panel.
        double fit = FitScale();
        double imgW = _imageW * fit, imgH = _imageH * fit;
        double imgX = (panelW - imgW) / 2, imgY = (panelH - imgH) / 2;

        // Where it lands once the panel transform is applied.
        double scaledW = imgW * _zoom, scaledH = imgH * _zoom;
        double left = imgX * _zoom + _offset.X;
        double top = imgY * _zoom + _offset.Y;

        _offset = new Vector(
            ClampAxis(left, scaledW, panelW, imgX * _zoom, _offset.X),
            ClampAxis(top, scaledH, panelH, imgY * _zoom, _offset.Y));
    }

    private static double ClampAxis(double edge, double scaledSize, double viewportSize,
                                    double baseOffset, double current)
    {
        if (scaledSize <= viewportSize)
            return (viewportSize - scaledSize) / 2 - baseOffset;   // center it

        // Don't let either edge pull inside the viewport.
        if (edge > 0) return current - edge;
        double farEdge = edge + scaledSize;
        if (farEdge < viewportSize) return current + (viewportSize - farEdge);
        return current;
    }

    private void Apply()
    {
        _content.RenderTransform = IsZoomed
            ? new MatrixTransform(
                Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y))
            : null;

        ZoomChanged?.Invoke(FitScale() * _zoom * 100.0);
    }
}
