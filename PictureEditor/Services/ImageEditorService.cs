using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PictureEditor.Services;

public enum ImageSortOrder
{
    NameAsc,
    NameDesc,
    DateModifiedAsc,
    DateModifiedDesc,
    SizeAsc,
    SizeDesc,
}

public class ImageEditorService : IDisposable
{
    private Image<Rgba32>? _currentImage;
    private Image<Rgba32>? _previewBase;

    // Circular undo buffer — O(1) push and trim
    private const int MaxUndoSteps = 5;
    private readonly Image<Rgba32>?[] _undoRing = new Image<Rgba32>?[MaxUndoSteps];
    private int _undoHead; // next write position
    private int _undoCount;

    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    public int ImageWidth => _currentImage?.Width ?? 0;
    public int ImageHeight => _currentImage?.Height ?? 0;
    public bool HasImage => _currentImage != null;
    public bool CanUndo => _undoCount > 0;
    public bool HasPreviewBase => _previewBase != null;

    public void LoadImage(string filePath)
    {
        _currentImage?.Dispose();
        ClearUndoRing();
        _previewBase?.Dispose();
        _previewBase = null;
        _currentImage = Image.Load<Rgba32>(filePath);
    }

    /// <summary>
    /// Writes pixel data directly into a caller-provided BGRA buffer (e.g. a locked WriteableBitmap).
    /// Uses SIMD Vector to swap R/B channels when possible.
    /// </summary>
    public unsafe bool CopyPixelsToBgraDirect(IntPtr destination, int destStride)
    {
        if (_currentImage == null) return false;
        int w = _currentImage.Width;
        int h = _currentImage.Height;

        _currentImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                byte* destRow = (byte*)destination + (long)y * destStride;
                var destSpan = new Span<byte>(destRow, w * 4);

                // Reinterpret Rgba32 row as bytes
                var srcBytes = MemoryMarshal.AsBytes(row);

                // SIMD RGBA→BGRA swizzle
                SwizzleRgbaToBgra(srcBytes, destSpan);
            }
        });
        return true;
    }

    /// <summary>
    /// Swaps R and B channels from RGBA source to BGRA destination using SIMD where available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwizzleRgbaToBgra(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int pixelCount = src.Length / 4;
        int i = 0;

        // Process 4 pixels at a time using Vector128 if available
        if (Vector.IsHardwareAccelerated && pixelCount >= Vector<uint>.Count)
        {
            var srcUints = MemoryMarshal.Cast<byte, uint>(src);
            var dstUints = MemoryMarshal.Cast<byte, uint>(dst);

            // Masks for extracting and shifting channels
            // RGBA (little-endian memory): byte0=R, byte1=G, byte2=B, byte3=A
            // BGRA (little-endian memory): byte0=B, byte1=G, byte2=R, byte3=A
            var maskGA = new Vector<uint>(0xFF00FF00u); // green + alpha
            var maskR  = new Vector<uint>(0x000000FFu); // red channel
            var maskB  = new Vector<uint>(0x00FF0000u); // blue channel

            int vecCount = srcUints.Length - (srcUints.Length % Vector<uint>.Count);
            for (i = 0; i < vecCount; i += Vector<uint>.Count)
            {
                var v = new Vector<uint>(srcUints.Slice(i));
                var ga = v & maskGA;     // keep G and A
                var r = (v & maskR) << 16;  // move R to B position
                var b = (v & maskB) >> 16;  // move B to R position
                var result = ga | r | b;
                result.CopyTo(dstUints.Slice(i));
            }
            i *= 4; // convert back to byte index
        }

        // Scalar fallback for remaining pixels
        for (; i + 3 < src.Length; i += 4)
        {
            dst[i]     = src[i + 2]; // B
            dst[i + 1] = src[i + 1]; // G
            dst[i + 2] = src[i];     // R
            dst[i + 3] = src[i + 3]; // A
        }
    }

    // --- Circular undo buffer ---

    private void PushUndo(Image<Rgba32>? specificState = null)
    {
        // Dispose whatever is in the slot we're about to overwrite
        _undoRing[_undoHead]?.Dispose();
        _undoRing[_undoHead] = specificState ?? _currentImage?.Clone();
        _undoHead = (_undoHead + 1) % MaxUndoSteps;
        if (_undoCount < MaxUndoSteps)
            _undoCount++;
    }

    private void ClearUndoRing()
    {
        for (int i = 0; i < MaxUndoSteps; i++)
        {
            _undoRing[i]?.Dispose();
            _undoRing[i] = null;
        }
        _undoHead = 0;
        _undoCount = 0;
    }

    public bool Undo()
    {
        if (_undoCount == 0 || _currentImage == null) return false;
        _undoHead = (_undoHead - 1 + MaxUndoSteps) % MaxUndoSteps;
        _undoCount--;
        _currentImage.Dispose();
        _currentImage = _undoRing[_undoHead];
        _undoRing[_undoHead] = null;
        return true;
    }

    // --- Preview base for real-time adjustments/resize ---

    public void SavePreviewBase()
    {
        _previewBase?.Dispose();
        _previewBase = _currentImage?.Clone();
    }

    /// <summary>
    /// Fast restore: copies pixel data from preview base into current image
    /// without allocating a new Image. Falls back to clone if dimensions differ.
    /// </summary>
    public void RestoreFromPreviewBase()
    {
        if (_previewBase == null || _currentImage == null) return;

        if (_currentImage.Width == _previewBase.Width && _currentImage.Height == _previewBase.Height)
        {
            // Fast path: copy pixel rows directly
            _previewBase.ProcessPixelRows(_currentImage, (srcAcc, dstAcc) =>
            {
                for (int y = 0; y < srcAcc.Height; y++)
                {
                    var srcRow = srcAcc.GetRowSpan(y);
                    var dstRow = dstAcc.GetRowSpan(y);
                    srcRow.CopyTo(dstRow);
                }
            });
        }
        else
        {
            // Dimensions changed (e.g. resize preview) — must reallocate
            _currentImage.Dispose();
            _currentImage = _previewBase.Clone();
        }
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
        _currentImage = _previewBase;
        _previewBase = null;
    }

    // --- Single-pass adjustments (combines brightness, contrast, saturation, hue, gamma) ---

    public void ApplyAdjustmentsNoUndo(float brightness, float contrast, float saturation, float hue, float gamma)
    {
        if (_currentImage == null) return;

        bool hasBrightness = MathF.Abs(brightness - 1f) > 0.01f;
        bool hasContrast = MathF.Abs(contrast - 1f) > 0.01f;
        bool hasSaturation = MathF.Abs(saturation - 1f) > 0.01f;
        bool hasHue = MathF.Abs(hue) > 0.5f;
        bool hasGamma = MathF.Abs(gamma - 1f) > 0.01f;

        if (!hasBrightness && !hasContrast && !hasSaturation && !hasHue && !hasGamma)
            return;

        // Build a combined LUT for brightness + contrast + gamma
        // These are per-channel operations that can be composed into a single lookup
        bool hasLut = hasBrightness || hasContrast || hasGamma;
        byte[]? lut = null;

        if (hasLut)
        {
            lut = ArrayPool<byte>.Shared.Rent(256);
            float invGamma = hasGamma ? 1f / gamma : 1f;
            for (int i = 0; i < 256; i++)
            {
                float v = i / 255f;

                // Brightness: multiply
                if (hasBrightness) v *= brightness;

                // Contrast: scale around 0.5
                if (hasContrast) v = ((v - 0.5f) * contrast) + 0.5f;

                // Gamma: power curve
                if (hasGamma && v > 0f) v = MathF.Pow(v, invGamma);

                lut[i] = (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
            }
        }

        bool needsHsl = hasSaturation || hasHue;
        float hueRadians = hasHue ? hue * MathF.PI / 180f : 0f;

        _currentImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    byte r = p.R, g = p.G, b = p.B;

                    // Apply LUT (brightness + contrast + gamma combined)
                    if (lut != null)
                    {
                        r = lut[r];
                        g = lut[g];
                        b = lut[b];
                    }

                    // Apply saturation and hue in HSL space (single conversion)
                    if (needsHsl)
                    {
                        RgbToHsl(r, g, b, out float h, out float s, out float l);
                        if (hasHue) h = (h + hueRadians) % (2f * MathF.PI);
                        if (hasSaturation) s = Math.Clamp(s * saturation, 0f, 1f);
                        HslToRgb(h, s, l, out r, out g, out b);
                    }

                    p = new Rgba32(r, g, b, p.A);
                }
            }
        });

        if (lut != null)
            ArrayPool<byte>.Shared.Return(lut);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RgbToHsl(byte ri, byte gi, byte bi, out float h, out float s, out float l)
    {
        float r = ri / 255f, g = gi / 255f, b = bi / 255f;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;
        l = (max + min) * 0.5f;

        if (delta < 1e-6f)
        {
            h = 0f;
            s = 0f;
            return;
        }

        s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);

        if (max == r)
            h = ((g - b) / delta) + (g < b ? 6f : 0f);
        else if (max == g)
            h = ((b - r) / delta) + 2f;
        else
            h = ((r - g) / delta) + 4f;

        h *= MathF.PI / 3f; // convert to radians (sector * 60° in radians)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HslToRgb(float h, float s, float l, out byte ro, out byte go, out byte bo)
    {
        if (s < 1e-6f)
        {
            byte v = (byte)Math.Clamp((int)(l * 255f + 0.5f), 0, 255);
            ro = go = bo = v;
            return;
        }

        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float oneThird = 2f * MathF.PI / 3f;

        ro = HueToRgbByte(p, q, h + oneThird);
        go = HueToRgbByte(p, q, h);
        bo = HueToRgbByte(p, q, h - oneThird);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte HueToRgbByte(float p, float q, float t)
    {
        float twoPi = 2f * MathF.PI;
        if (t < 0f) t += twoPi;
        if (t > twoPi) t -= twoPi;

        float sixthPi = MathF.PI / 3f;
        float halfPi2 = MathF.PI; // 180 degrees = pi
        float twoThirdsPi2 = 2f * MathF.PI * 2f / 3f; // 240 degrees

        float v;
        if (t < sixthPi)
            v = p + (q - p) * (t / sixthPi);
        else if (t < halfPi2)
            v = q;
        else if (t < twoThirdsPi2)
            v = p + (q - p) * ((twoThirdsPi2 - t) / sixthPi);
        else
            v = p;

        return (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    }

    public void RotateNoUndo(float degrees)
    {
        if (_currentImage == null || degrees == 0f) return;
        _currentImage.Mutate(x => x.Rotate(degrees));
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

        int height = _currentImage.Height;
        var img = _currentImage;

        // Build per-channel histograms using parallel row processing with thread-local accumulators
        int[] histR = new int[256], histG = new int[256], histB = new int[256];
        Parallel.For(0, height,
            () => (r: new int[256], g: new int[256], b: new int[256]),
            (y, _, local) =>
            {
                var row = img.DangerousGetPixelRowMemory(y).Span;
                for (int x = 0; x < row.Length; x++)
                {
                    local.r[row[x].R]++;
                    local.g[row[x].G]++;
                    local.b[row[x].B]++;
                }
                return local;
            },
            local =>
            {
                lock (histR)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        histR[i] += local.r[i];
                        histG[i] += local.g[i];
                        histB[i] += local.b[i];
                    }
                }
            });

        int totalPixels = _currentImage.Width * height;
        float clipPercent = 0.005f;
        int clipCount = (int)(totalPixels * clipPercent);

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

        byte[] lutR = BuildLut(rMin, rMax);
        byte[] lutG = BuildLut(gMin, gMax);
        byte[] lutB = BuildLut(bMin, bMax);

        // Apply the LUTs in parallel
        Parallel.For(0, height, y =>
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
            {
                ref var p = ref row[x];
                p = new Rgba32(lutR[p.R], lutG[p.G], lutB[p.B], p.A);
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

    public void RemoveVerticalStrip(int x, int width)
    {
        if (_currentImage == null) return;
        x = Math.Max(0, Math.Min(x, _currentImage.Width - 1));
        width = Math.Min(width, _currentImage.Width - x);
        if (width <= 0 || width >= _currentImage.Width) return;

        PushUndo();

        int srcW = _currentImage.Width;
        int srcH = _currentImage.Height;
        int newW = srcW - width;

        var result = new Image<Rgba32>(newW, srcH);

        // Copy left portion (0 to x) and right portion (x+width to end)
        _currentImage.ProcessPixelRows(result, (srcAcc, dstAcc) =>
        {
            for (int y = 0; y < srcAcc.Height; y++)
            {
                var srcRow = srcAcc.GetRowSpan(y);
                var dstRow = dstAcc.GetRowSpan(y);

                // Left portion
                if (x > 0)
                    srcRow.Slice(0, x).CopyTo(dstRow);

                // Right portion
                int rightStart = x + width;
                int rightLen = srcW - rightStart;
                if (rightLen > 0)
                    srcRow.Slice(rightStart, rightLen).CopyTo(dstRow.Slice(x));
            }
        });

        _currentImage.Dispose();
        _currentImage = result;
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

    public static List<string> GetImagesInDirectory(string directoryPath,
        ImageSortOrder sort = ImageSortOrder.NameAsc)
    {
        if (!Directory.Exists(directoryPath)) return new List<string>();
        var files = Directory.EnumerateFiles(directoryPath)
            .Where(f => !Path.GetFileName(f).StartsWith("._") &&
                        SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        return sort switch
        {
            ImageSortOrder.NameAsc => files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
            ImageSortOrder.NameDesc => files.OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
            ImageSortOrder.DateModifiedAsc => files.OrderBy(f => File.GetLastWriteTimeUtc(f)).ToList(),
            ImageSortOrder.DateModifiedDesc => files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToList(),
            ImageSortOrder.SizeAsc => files.OrderBy(f => new FileInfo(f).Length).ToList(),
            ImageSortOrder.SizeDesc => files.OrderByDescending(f => new FileInfo(f).Length).ToList(),
            _ => files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    public static bool IsSupportedFile(string filePath)
    {
        return SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());
    }

    public void Dispose()
    {
        _currentImage?.Dispose();
        ClearUndoRing();
        _previewBase?.Dispose();
    }
}
