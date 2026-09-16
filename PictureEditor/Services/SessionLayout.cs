using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PictureEditor.Services;

/// <summary>
/// One window's saved place in a session layout: where it sat, which monitor it sat on,
/// and what it was showing.
/// </summary>
public class WindowLayout
{
    // Geometry, in the same units MainWindow already uses: X/Y are physical pixels
    // (Window.Position), Width/Height are logical DIPs.
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 800;
    public bool IsMaximized { get; set; }

    // Monitor identity. DisplayName is matched first; Bounds are the fallback for
    // platforms that report no name, or report the same name for several displays.
    public string? ScreenName { get; set; }
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }

    // Content
    public string? Directory { get; set; }
    public bool IncludeSubdirectories { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ImageSortOrder SortOrder { get; set; } = ImageSortOrder.NameAsc;

    /// <summary>Full path of the image on screen. Preferred on restore.</summary>
    public string? CurrentFile { get; set; }

    /// <summary>Position in the directory listing, used when <see cref="CurrentFile"/> is gone.</summary>
    public int CurrentIndex { get; set; }
}

/// <summary>
/// The single anonymous layout written by versions before named layouts existed.
/// Read-only now: <see cref="LayoutStore"/> imports it once into a named slot and
/// then deletes layout.json. Nothing writes this file any more.
/// </summary>
public class SessionLayout
{
    public List<WindowLayout> Windows { get; set; } = new();

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PictureEditor");

    private static readonly string LayoutPath = Path.Combine(SettingsDir, "layout.json");

    /// <summary>Returns null when there is no usable saved layout.</summary>
    public static SessionLayout? Load()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return null;
            var json = File.ReadAllText(LayoutPath);
            var layout = JsonSerializer.Deserialize<SessionLayout>(json);
            if (layout == null || layout.Windows.Count == 0) return null;
            return layout;
        }
        catch
        {
            // Corrupt layout — fall back to a normal startup
            return null;
        }
    }

    /// <summary>Forgets the saved layout, so the next launch starts fresh.</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(LayoutPath))
                File.Delete(LayoutPath);
        }
        catch
        {
            // Non-critical, silently ignore
        }
    }
}
