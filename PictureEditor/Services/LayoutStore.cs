using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PictureEditor.Services;

/// <summary>
/// A screen layout the user has named and can reopen from the startup chooser.
/// </summary>
public class NamedLayout
{
    public string Name { get; set; } = "";
    public List<WindowLayout> Windows { get; set; } = new();
}

/// <summary>
/// Every named layout, stored in layouts.json. Slot 0 in the chooser is always
/// "Default" (a fresh start) and is not held here — these are the user's own layouts.
/// </summary>
public class LayoutStore
{
    /// <summary>Chooser keys 1-9; 0 is reserved for Default.</summary>
    public const int MaxLayouts = 9;

    /// <summary>Longest name the save prompt will accept.</summary>
    public const int MaxNameLength = 40;

    public List<NamedLayout> Layouts { get; set; } = new();

    /// <summary>Name of the layout opened last, used to preselect the chooser.</summary>
    public string? LastUsed { get; set; }

    [JsonIgnore]
    public bool IsFull => Layouts.Count >= MaxLayouts;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PictureEditor");

    private static readonly string StorePath = Path.Combine(SettingsDir, "layouts.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the saved layouts, importing the pre-named-layouts session file on first run.
    /// Never returns null — a missing or corrupt file yields an empty store.
    /// </summary>
    public static LayoutStore Load()
    {
        LayoutStore? store = null;
        try
        {
            if (File.Exists(StorePath))
                store = JsonSerializer.Deserialize<LayoutStore>(File.ReadAllText(StorePath));
        }
        catch
        {
            // Corrupt store — fall back to an empty one rather than blocking startup
        }

        if (store != null)
        {
            // Drop anything unusable so the chooser never shows a dead entry.
            store.Layouts.RemoveAll(l => string.IsNullOrWhiteSpace(l.Name) || l.Windows.Count == 0);
            if (store.Layouts.Count > MaxLayouts)
                store.Layouts = store.Layouts.Take(MaxLayouts).ToList();
            return store;
        }

        store = new LayoutStore();
        store.TryImportLegacy();
        return store;
    }

    /// <summary>
    /// Carries the single anonymous layout saved by earlier versions into a named slot,
    /// then removes the old file. Only runs when there is no layouts.json yet.
    /// </summary>
    private void TryImportLegacy()
    {
        var legacy = SessionLayout.Load();
        if (legacy == null) return;

        Layouts.Add(new NamedLayout { Name = "Previous Session", Windows = legacy.Windows });
        Save();
        SessionLayout.Clear();
    }

    public NamedLayout? Find(string? name) =>
        name == null ? null : Layouts.FirstOrDefault(l => NameEquals(l.Name, name));

    public bool IsNameTaken(string name) => Find(name) != null;

    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, SerializerOptions);
            // Atomic write: write to temp file then rename, so concurrent instances don't corrupt
            var tempPath = StorePath + "." + Path.GetRandomFileName();
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, StorePath, overwrite: true);
        }
        catch
        {
            // Non-critical, silently ignore
        }
    }
}
