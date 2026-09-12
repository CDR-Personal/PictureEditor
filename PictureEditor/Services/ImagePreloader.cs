using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Threading.Tasks;

namespace PictureEditor.Services;

/// <summary>
/// Decodes one image ahead of time on the thread pool so that navigating to it becomes a
/// handoff instead of a decode. Holds at most one image: starting a preload for a different
/// path drops the previous one, which caps the extra cost at a single decoded frame.
/// </summary>
/// <remarks>
/// All members are intended to be called from the UI thread — the background task only
/// touches its own locals, so no locking is needed.
/// </remarks>
internal sealed class ImagePreloader : IDisposable
{
    private string? _path;
    private Task<Image<Rgba32>>? _decode;

    /// <summary>
    /// Begins decoding <paramref name="filePath"/> in the background. A preload already
    /// running (or finished) for the same path is kept, so repeated calls are cheap.
    /// </summary>
    public void Start(string filePath)
    {
        if (Matches(filePath)) return;

        Discard();
        _path = filePath;
        _decode = Task.Run(() => Image.Load<Rgba32>(filePath));
    }

    /// <summary>
    /// Completes once a preload of <paramref name="filePath"/> has finished decoding, and
    /// completes immediately when nothing is being preloaded for that path. Never faults —
    /// a failed decode surfaces as a miss from <see cref="TakeIfReady"/> instead.
    /// </summary>
    public Task WaitAsync(string filePath)
    {
        if (!Matches(filePath) || _decode!.IsCompleted) return Task.CompletedTask;
        return AwaitQuietly(_decode);

        static async Task AwaitQuietly(Task decode)
        {
            try { await decode; } catch { /* reported by the caller's own load attempt */ }
        }
    }

    /// <summary>
    /// Hands ownership of the preloaded image for <paramref name="filePath"/> to the caller.
    /// Returns null — and drops whatever was preloaded — when the path does not match, the
    /// decode is still running, or it failed; the caller then loads the file itself.
    /// </summary>
    public Image<Rgba32>? TakeIfReady(string filePath)
    {
        if (_decode == null) return null;

        if (!Matches(filePath) || !_decode.IsCompletedSuccessfully)
        {
            Discard();
            return null;
        }

        var image = _decode.Result;
        _path = null;
        _decode = null;
        return image;
    }

    /// <summary>Drops any preloaded image, disposing it once its decode finishes.</summary>
    public void Discard()
    {
        if (_decode != null) DisposeWhenDone(_decode);
        _path = null;
        _decode = null;
    }

    public void Dispose() => Discard();

    // Ordinal on purpose: paths come from the same directory listing, so a case difference
    // means a different file rather than a cache hit we can trust.
    private bool Matches(string filePath) =>
        _decode != null && string.Equals(_path, filePath, StringComparison.Ordinal);

    private static void DisposeWhenDone(Task<Image<Rgba32>> decode)
    {
        decode.ContinueWith(static t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
            else _ = t.Exception; // observe the failure so it stays out of the finalizer
        }, TaskScheduler.Default);
    }
}
