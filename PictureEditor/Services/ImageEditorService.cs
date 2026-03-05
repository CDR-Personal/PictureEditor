using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PictureEditor.Services;

public class ImageEditorService : IDisposable
{
    private Image<Rgba32>? _currentImage;
    private readonly Stack<byte[]> _undoStack = new();
    private const int MaxUndoSteps = 5;
    private byte[]? _previewBase;

    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    public int ImageWidth => _currentImage?.Width ?? 0;
    public int ImageHeight => _currentImage?.Height ?? 0;
    public bool HasImage => _currentImage != null;
    public bool CanUndo => _undoStack.Count > 0;
    public bool HasPreviewBase => _previewBase != null;

    public void LoadImage(string filePath)
    {
        _currentImage?.Dispose();
        _undoStack.Clear();
        DiscardPreviewBase();
        _currentImage = Image.Load<Rgba32>(filePath);
    }

    public byte[] GetCurrentImageBytes()
    {
        if (_currentImage == null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        _currentImage.SaveAsPng(ms);
        return ms.ToArray();
    }

    private void PushUndo(byte[]? specificState = null)
    {
        if (specificState != null)
        {
            _undoStack.Push(specificState);
        }
        else
        {
            if (_currentImage == null) return;
            using var ms = new MemoryStream();
            _currentImage.SaveAsPng(ms);
            _undoStack.Push(ms.ToArray());
        }
        TrimUndoStack();
    }

    private void TrimUndoStack()
    {
        while (_undoStack.Count > MaxUndoSteps)
        {
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = 0; i < items.Length - 1; i++)
                _undoStack.Push(items[items.Length - 2 - i]);
        }
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0 || _currentImage == null) return false;
        var previousState = _undoStack.Pop();
        _currentImage.Dispose();
        _currentImage = Image.Load<Rgba32>(previousState);
        return true;
    }

    // --- Preview base for real-time adjustments/resize ---

    public void SavePreviewBase()
    {
        _previewBase = GetCurrentImageBytes();
    }

    public void RestoreFromPreviewBase()
    {
        if (_previewBase == null || _currentImage == null) return;
        _currentImage.Dispose();
        _currentImage = Image.Load<Rgba32>(_previewBase);
    }

    public void CommitPreview()
    {
        if (_previewBase == null) return;
        PushUndo(_previewBase);
        _previewBase = null;
    }

    public void DiscardPreviewBase()
    {
        if (_previewBase == null) return;
        _currentImage?.Dispose();
        _currentImage = Image.Load<Rgba32>(_previewBase);
        _previewBase = null;
    }

    // --- Operations without undo (used during real-time preview) ---

    public void ApplyAdjustmentsNoUndo(float brightness, float contrast, float saturation, float hue, float gamma)
    {
        if (_currentImage == null) return;
        _currentImage.Mutate(ctx =>
        {
            if (Math.Abs(brightness - 1f) > 0.01f) ctx.Brightness(brightness);
            if (Math.Abs(contrast - 1f) > 0.01f) ctx.Contrast(contrast);
            if (Math.Abs(saturation - 1f) > 0.01f) ctx.Saturate(saturation);
            if (Math.Abs(hue) > 0.5f) ctx.Hue(hue);
        });
        if (Math.Abs(gamma - 1f) > 0.01f)
        {
            _currentImage.Mutate(x => x.ProcessPixelRowsAsVector4(row =>
            {
                for (int i = 0; i < row.Length; i++)
                {
                    var pixel = row[i];
                    pixel.X = MathF.Pow(pixel.X, 1f / gamma);
                    pixel.Y = MathF.Pow(pixel.Y, 1f / gamma);
                    pixel.Z = MathF.Pow(pixel.Z, 1f / gamma);
                    row[i] = pixel;
                }
            }));
        }
    }

    public void ResizeNoUndo(double percentage)
    {
        if (_currentImage == null || percentage <= 0) return;
        int newWidth = (int)(_currentImage.Width * percentage / 100.0);
        int newHeight = (int)(_currentImage.Height * percentage / 100.0);
        if (newWidth <= 0 || newHeight <= 0) return;
        _currentImage.Mutate(x => x.Resize(newWidth, newHeight));
    }

    // --- Standard operations with undo ---

    public void RotateClockwise()
    {
        if (_currentImage == null) return;
        PushUndo();
        _currentImage.Mutate(x => x.Rotate(90));
    }

    public void RotateCounterClockwise()
    {
        if (_currentImage == null) return;
        PushUndo();
        _currentImage.Mutate(x => x.Rotate(270));
    }

    public void AutoColor()
    {
        if (_currentImage == null) return;
        PushUndo();

        // Build per-channel histograms
        int[] histR = new int[256], histG = new int[256], histB = new int[256];
        _currentImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    histR[row[x].R]++;
                    histG[row[x].G]++;
                    histB[row[x].B]++;
                }
            }
        });

        int totalPixels = _currentImage.Width * _currentImage.Height;
        float clipPercent = 0.005f; // clip 0.5% from each end
        int clipCount = (int)(totalPixels * clipPercent);

        // Find clipped min/max for each channel
        (byte min, byte max) FindRange(int[] hist)
        {
            int cumLow = 0, cumHigh = 0;
            byte lo = 0, hi = 255;
            for (int i = 0; i < 256; i++)
            {
                cumLow += hist[i];
                if (cumLow > clipCount) { lo = (byte)i; break; }
            }
            for (int i = 255; i >= 0; i--)
            {
                cumHigh += hist[i];
                if (cumHigh > clipCount) { hi = (byte)i; break; }
            }
            if (hi <= lo) hi = (byte)Math.Min(lo + 1, 255);
            return (lo, hi);
        }

        var (rMin, rMax) = FindRange(histR);
        var (gMin, gMax) = FindRange(histG);
        var (bMin, bMax) = FindRange(histB);

        // Build lookup tables for each channel
        byte[] lutR = BuildLut(rMin, rMax);
        byte[] lutG = BuildLut(gMin, gMax);
        byte[] lutB = BuildLut(bMin, bMax);

        // Apply the LUTs
        _currentImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(lutR[row[x].R], lutG[row[x].G], lutB[row[x].B], row[x].A);
                }
            }
        });
    }

    private static byte[] BuildLut(byte min, byte max)
    {
        byte[] lut = new byte[256];
        float range = max - min;
        for (int i = 0; i < 256; i++)
        {
            if (i <= min) lut[i] = 0;
            else if (i >= max) lut[i] = 255;
            else lut[i] = (byte)((i - min) / range * 255f + 0.5f);
        }
        return lut;
    }

    public void Crop(int x, int y, int width, int height)
    {
        if (_currentImage == null) return;
        x = Math.Max(0, Math.Min(x, _currentImage.Width - 1));
        y = Math.Max(0, Math.Min(y, _currentImage.Height - 1));
        width = Math.Min(width, _currentImage.Width - x);
        height = Math.Min(height, _currentImage.Height - y);
        if (width <= 0 || height <= 0) return;

        PushUndo();
        _currentImage.Mutate(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
    }

    public void SaveImage(string filePath)
    {
        if (_currentImage == null) return;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        switch (ext)
        {
            case ".jpg":
            case ".jpeg":
                _currentImage.Save(filePath, new JpegEncoder { Quality = 95 });
                break;
            case ".png":
                _currentImage.Save(filePath, new PngEncoder());
                break;
            case ".webp":
                _currentImage.Save(filePath, new WebpEncoder { Quality = 95 });
                break;
            default:
                _currentImage.Save(filePath, new PngEncoder());
                break;
        }
    }

    public static List<string> GetImagesInDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return new List<string>();
        return Directory.GetFiles(directoryPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsSupportedFile(string filePath)
    {
        return SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());
    }

    public void Dispose()
    {
        _currentImage?.Dispose();
    }
}
