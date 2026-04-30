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
    /// Returns a concatenated key string (e.g. <c>"A D N "</c>) listing the categorized
    /// move-target folders — other than <paramref name="currentDirectory"/> — that already
    /// contain a file with the given name. <c>FolderEdit</c> is intentionally excluded
    /// (it's a write-target, not a library folder).
    /// </summary>
    public static string CheckDuplicates(string fileName, string currentDirectory, MoveTargets targets)
    {
        var result = new StringBuilder();
        Check("A", targets.FolderA);
        Check("D", targets.FolderD);
        Check("L", targets.FolderL);
        Check("N", targets.FolderN);
        Check("P", targets.FolderP);
        Check("S", targets.FolderS);
        Check("T", targets.FolderT);
        Check("U", targets.FolderU);
        Check("Y", targets.FolderY);
        Check("YC", targets.FolderYC);
        Check("YN", targets.FolderYN);
        Check("YS", targets.FolderYS);
        Check("YL", targets.FolderYL);
        Check("YD", targets.FolderYD);
        Check("YP", targets.FolderYP);
        Check("YU", targets.FolderYU);
        Check("LT", targets.FolderLT);
        Check("DT", targets.FolderDT);
        Check("NT", targets.FolderNT);
        Check("ST", targets.FolderST);
        Check("PT", targets.FolderPT);
        Check("UT", targets.FolderUT);
        return result.ToString();

        void Check(string label, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (folder.Equals(currentDirectory, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(Path.Combine(folder, fileName)))
                result.Append(label).Append(' ');
        }
    }

    private void PushUndo(string source, string destination)
    {
        _undoStack.Add((source, destination));
        if (_undoStack.Count > MaxUndoLevels)
            _undoStack.RemoveAt(0);
    }
}
