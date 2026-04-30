using System;
using System.IO;
using System.Text.Json;

namespace PictureEditor.Services;

public class AppSettings
{
    public int SlideshowIntervalSeconds { get; set; } = 5;
    public MoveTargets MoveTargets { get; set; } = new();

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PictureEditor");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "appsettings.json");

    private static readonly string? BundledSettingsPath = Path.GetDirectoryName(
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location)
        is string dir ? Path.Combine(dir, "appsettings.json") : null;

    public static AppSettings Load()
    {
        // Check user-specific settings first, then bundled appsettings.json next to the executable
        string?[] paths = { SettingsPath, BundledSettingsPath };
        foreach (var path in paths)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch
            {
                // Ignore corrupt settings, try next
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = SettingsPath + "." + Path.GetRandomFileName();
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Non-critical, silently ignore
        }
    }
}

public class MoveTargets
{
    public string FolderEdit { get; set; } = "";
    public string FolderA { get; set; } = "";
    public string FolderT { get; set; } = "";
    public string FolderY { get; set; } = "";
    public string FolderN { get; set; } = "";
    public string FolderL { get; set; } = "";
    public string FolderS { get; set; } = "";
    public string FolderD { get; set; } = "";
    public string FolderP { get; set; } = "";
    public string FolderU { get; set; } = "";
    public string FolderYC { get; set; } = "";
    public string FolderYN { get; set; } = "";
    public string FolderYS { get; set; } = "";
    public string FolderYL { get; set; } = "";
    public string FolderYP { get; set; } = "";
    public string FolderYU { get; set; } = "";
    public string FolderYD { get; set; } = "";
    public string FolderLT { get; set; } = "";
    public string FolderDT { get; set; } = "";
    public string FolderNT { get; set; } = "";
    public string FolderST { get; set; } = "";
    public string FolderPT { get; set; } = "";
    public string FolderUT { get; set; } = "";
}
