using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PictureEditor.Services;

public enum MoveResultKind
{
    Moved,
    Conflict,
    InvalidDestination,
    Error
}

public record MoveResult(MoveResultKind Kind, string? DestinationFile = null, string? Message = null);

public class FileMoveService
{
    public const int MaxUndoLevels = 25;

    private readonly List<(string Source, string Destination)> _undoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public int UndoCount => _undoStack.Count;

    /// <summary>
    /// Moves <paramref name="sourceFile"/> into <paramref name="destinationFolder"/>.
    /// Returns <see cref="MoveResultKind.Conflict"/> (without touching either file) when a
    /// file with the same name already exists in the destination — the caller resolves
    /// the UI flow and may then call <see cref="MoveOverwrite"/>.
    /// </summary>
    public MoveResult Move(string sourceFile, string destinationFolder)
    {
        if (string.IsNullOrEmpty(destinationFolder) || !Directory.Exists(destinationFolder))
            return new MoveResult(MoveResultKind.InvalidDestination,
                Message: "Invalid destination: " + destinationFolder);

        var destinationFile = Path.Combine(destinationFolder, Path.GetFileName(sourceFile));

        if (File.Exists(destinationFile))
            return new MoveResult(MoveResultKind.Conflict, destinationFile);

        try
        {
            File.Move(sourceFile, destinationFile);
            if (File.Exists(sourceFile)) File.Delete(sourceFile);
            PushUndo(sourceFile, destinationFile);
            return new MoveResult(MoveResultKind.Moved, destinationFile);
        }
        catch (Exception ex)
        {
            return new MoveResult(MoveResultKind.Error, Message: ex.Message);
        }
    }

    /// <summary>
    /// Moves <paramref name="sourceFile"/> over an existing destination file.
    /// The caller is responsible for confirming the replacement first.
    /// </summary>
    public MoveResult MoveOverwrite(string sourceFile, string destinationFolder)
    {
        if (string.IsNullOrEmpty(destinationFolder) || !Directory.Exists(destinationFolder))
            return new MoveResult(MoveResultKind.InvalidDestination,
                Message: "Invalid destination: " + destinationFolder);

        var destinationFile = Path.Combine(destinationFolder, Path.GetFileName(sourceFile));

        try
        {
            if (File.Exists(destinationFile)) File.Delete(destinationFile);
            File.Move(sourceFile, destinationFile);
            if (File.Exists(sourceFile)) File.Delete(sourceFile);
            PushUndo(sourceFile, destinationFile);
            return new MoveResult(MoveResultKind.Moved, destinationFile);
        }
        catch (Exception ex)
        {
            return new MoveResult(MoveResultKind.Error, Message: ex.Message);
        }
    }

    /// <summary>
    /// Reverses the most recent move. On success returns the restored source path
    /// in <see cref="MoveResult.DestinationFile"/>.
    /// </summary>
    public MoveResult Undo()
    {
        if (_undoStack.Count == 0)
            return new MoveResult(MoveResultKind.Error, Message: "Nothing to undo");

        var (source, destination) = _undoStack[^1];

        var sourceDir = Path.GetDirectoryName(source);
        if (sourceDir == null || !Directory.Exists(sourceDir))
            return new MoveResult(MoveResultKind.InvalidDestination,
                Message: "Invalid destination: " + sourceDir);

        if (File.Exists(source))
            return new MoveResult(MoveResultKind.Error,
                Message: "File already exists: " + Path.GetFileName(source));

        try
        {
            File.Move(destination, source);
            if (File.Exists(destination)) File.Delete(destination);
            _undoStack.RemoveAt(_undoStack.Count - 1);
            return new MoveResult(MoveResultKind.Moved, source);
        }
        catch (Exception ex)
        {
            return new MoveResult(MoveResultKind.Error, Message: ex.Message);
        }
    }

    /// <summary>
    /// The configured move targets paired with their single-key labels, in duplicate-check
    /// display order. <c>"E"</c> (the edit folder) sorts last and is skipped by
    /// <see cref="CheckDuplicates"/> — it's a write-target, not a library folder.
    /// </summary>
    public static (string Label, string Folder)[] LabeledTargets(MoveTargets t) => new[]
    {
        ("A", t.FolderA),
        ("D", t.FolderD),
        ("L", t.FolderL),
        ("N", t.FolderN),
        ("P", t.FolderP),
        ("S", t.FolderS),
        ("T", t.FolderT),
        ("U", t.FolderU),
        ("Y", t.FolderY),
        ("YC", t.FolderYC),
        ("YN", t.FolderYN),
        ("YS", t.FolderYS),
        ("YL", t.FolderYL),
        ("YD", t.FolderYD),
        ("YP", t.FolderYP),
        ("YU", t.FolderYU),
        ("LT", t.FolderLT),
        ("DT", t.FolderDT),
        ("NT", t.FolderNT),
        ("ST", t.FolderST),
        ("PT", t.FolderPT),
        ("UT", t.FolderUT),
        ("E", t.FolderEdit),
    };

    /// <summary>
    /// Returns the configured folder for a move-target label, or <c>null</c> when the label is
    /// unknown or that target has not been configured.
    /// </summary>
    public static string? FolderForLabel(string label, MoveTargets targets)
    {
        foreach (var (candidate, folder) in LabeledTargets(targets))
            if (candidate == label)
                return string.IsNullOrEmpty(folder) ? null : folder;
        return null;
    }

    /// <summary>
    /// Reverse of <see cref="FolderForLabel"/>: returns the move-target label for
    /// <paramref name="directory"/> (e.g. <c>"NT"</c>), or <c>null</c> when the directory is not
    /// one of the configured targets. When several labels share a folder, the first in
    /// <see cref="LabeledTargets"/> order wins.
    /// </summary>
    public static string? LabelForDirectory(string? directory, MoveTargets targets)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        var normalized = Normalize(directory);
        foreach (var (label, folder) in LabeledTargets(targets))
            if (!string.IsNullOrEmpty(folder) &&
                Normalize(folder).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return label;
        return null;
    }

    /// <summary>
    /// Returns a concatenated key string (e.g. <c>"A D N "</c>) listing the categorized
    /// move-target folders — other than <paramref name="currentDirectory"/> — that already
    /// contain a file with the given name. <c>FolderEdit</c> is intentionally excluded
    /// (it's a write-target, not a library folder).
    /// </summary>
    public static string CheckDuplicates(string fileName, string currentDirectory, MoveTargets targets)
    {
        var result = new StringBuilder();
        var current = Normalize(currentDirectory);

        foreach (var (label, folder) in LabeledTargets(targets))
        {
            if (label == "E" || string.IsNullOrEmpty(folder)) continue;
            if (Normalize(folder).Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(folder, fileName)))
                result.Append(label).Append(' ');
        }

        return result.ToString();
    }

    /// <summary>
    /// Canonicalizes a directory path so configured targets and the current directory compare
    /// equal despite trailing separators or relative segments.
    /// </summary>
    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { path = Path.GetFullPath(path); }
        catch { /* malformed path — fall back to the raw string */ }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void PushUndo(string source, string destination)
    {
        _undoStack.Add((source, destination));
        if (_undoStack.Count > MaxUndoLevels)
            _undoStack.RemoveAt(0);
    }
}
