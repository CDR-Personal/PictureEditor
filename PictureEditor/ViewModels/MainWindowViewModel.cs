using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PictureEditor.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PictureEditor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ImageEditorService _editor = new();
    private readonly AppSettings _appSettings = AppSettings.Load();
    private string? _currentFilePath;
    private string? _currentDirectory;
    private List<string> _directoryImages = new();
    private int _currentImageIndex = -1;
    private bool _hasUnsavedChanges;
    private ImageSortOrder _sortOrder = ImageSortOrder.NameAsc;
    private bool _includeSubdirectories;
    private string? _titleStatus;
    private bool _suppressPreviewUpdate;
    private CancellationTokenSource? _previewDebounceCts;
    private CancellationTokenSource? _slideshowCts;
    private bool _isShuffleMode;
    private List<int>? _shuffledOrder;
    private int _shufflePosition;
    private int _adaptiveDebounceMs = 30;
    private readonly Stopwatch _renderStopwatch = new();

    // Double-buffered WriteableBitmaps — we alternate between two so Avalonia's
    // binding system always sees a new reference and triggers a visual update.
    private WriteableBitmap?[] _bitmapPool = new WriteableBitmap?[2];
    private int _bitmapPoolIndex;
    private int _bitmapPoolWidth;
    private int _bitmapPoolHeight;

    [ObservableProperty] private Bitmap? _displayImage;
    [ObservableProperty] private string _title = "Cedar Image Editor";
    [ObservableProperty] private string _statusText = "Open a file or directory to begin";
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _isCropMode;
    [ObservableProperty] private bool _isResizeMode;
    [ObservableProperty] private bool _isAdjustMode;
    [ObservableProperty] private bool _isRotateMode;
    [ObservableProperty] private int _resizePercentage = 100;
    [ObservableProperty] private double _rotateDegrees;
    [ObservableProperty] private string _imageDimensions = "";
    [ObservableProperty] private int _imagePixelWidthValue;
    [ObservableProperty] private int _imagePixelHeightValue;

    // Tracks whether a preview render is in progress (for adaptive interpolation)
    [ObservableProperty] private bool _isPreviewActive;

    // Continuous/slideshow mode
    [ObservableProperty] private bool _isContinuousMode;

    // Adjustment slider values
    [ObservableProperty] private double _brightnessValue = 1.0;
    [ObservableProperty] private double _contrastValue = 1.0;
    [ObservableProperty] private double _saturationValue = 1.0;
    [ObservableProperty] private double _hueValue = 0;
    [ObservableProperty] private double _gammaValue = 1.0;

    // Crop rectangle
    [ObservableProperty] private int _cropX;
    [ObservableProperty] private int _cropY;
    [ObservableProperty] private int _cropWidth;
    [ObservableProperty] private int _cropHeight;

    // Strip removal
    [ObservableProperty] private bool _isStripMode;
    [ObservableProperty] private int _stripX;
    [ObservableProperty] private int _stripWidth = 1;

    public string? StartupFilePath { get; set; }

    public Func<Task<string?>>? OpenFileDialog { get; set; }
    public Func<Task<string?>>? OpenFolderDialog { get; set; }
    public Func<string, string?, Task<string?>>? SaveFileDialog { get; set; }
    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }
    public Func<string, string, string, Task<string?>>? TextInputDialog { get; set; }
    public Func<ImageSortOrder, Task<ImageSortOrder?>>? SortDialog { get; set; }

    // --- Real-time preview change handlers ---

    partial void OnBrightnessValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnContrastValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnSaturationValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnHueValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnGammaValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnResizePercentageChanged(int value) => ScheduleResizePreview();
    partial void OnRotateDegreesChanged(double value) => ScheduleRotatePreview();

    private void ScheduleAdjustmentPreview()
    {
        if (_suppressPreviewUpdate || !IsAdjustMode || !_editor.HasPreviewBase) return;
        ScheduleDebouncedPreview(UpdateAdjustmentPreviewCore);
    }

    private void ScheduleResizePreview()
    {
        if (_suppressPreviewUpdate || !IsResizeMode || !_editor.HasPreviewBase) return;
        if (ResizePercentage <= 0 || ResizePercentage == 100) return;
        ScheduleDebouncedPreview(UpdateResizePreviewCore);
    }

    private async void ScheduleDebouncedPreview(Action action)
    {
        _previewDebounceCts?.Cancel();
        _previewDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewDebounceCts = cts;
        try
        {
            await Task.Delay(_adaptiveDebounceMs, cts.Token);
            if (!cts.Token.IsCancellationRequested)
            {
                IsPreviewActive = true;
                _renderStopwatch.Restart();
                action();
                _renderStopwatch.Stop();

                // Adaptive debounce: if the last render took long, increase debounce
                long elapsed = _renderStopwatch.ElapsedMilliseconds;
                _adaptiveDebounceMs = elapsed switch
                {
                    > 200 => 150,
                    > 100 => 80,
                    > 50 => 50,
                    _ => 30
                };

                IsPreviewActive = false;
            }
        }
        catch (TaskCanceledException)
        {
            // Expected when a newer update supersedes this one
        }
    }

    private void UpdateAdjustmentPreviewCore()
    {
        if (_suppressPreviewUpdate || !IsAdjustMode || !_editor.HasPreviewBase) return;
        _editor.RestoreFromPreviewBase();
        _editor.ApplyAdjustmentsNoUndo(
            (float)BrightnessValue, (float)ContrastValue, (float)SaturationValue,
            (float)HueValue, (float)GammaValue);
        RefreshDisplay();
    }

    private void UpdateResizePreviewCore()
    {
        if (_suppressPreviewUpdate || !IsResizeMode || !_editor.HasPreviewBase) return;
        if (ResizePercentage <= 0 || ResizePercentage == 100) return;
        _editor.RestoreFromPreviewBase();
        _editor.ResizeNoUndo(ResizePercentage);
        RefreshDisplay();
    }

    private void ScheduleRotatePreview()
    {
        if (_suppressPreviewUpdate || !IsRotateMode || !_editor.HasPreviewBase) return;
        ScheduleDebouncedPreview(UpdateRotatePreviewCore);
    }

    private void UpdateRotatePreviewCore()
    {
        if (_suppressPreviewUpdate || !IsRotateMode || !_editor.HasPreviewBase) return;
        _editor.RestoreFromPreviewBase();
        if (Math.Abs(RotateDegrees) > 0.01)
            _editor.RotateNoUndo((float)RotateDegrees);
        InvalidateBitmapPool();
        RefreshDisplay();
    }

    private bool HasAdjustmentChanges()
    {
        return Math.Abs(BrightnessValue - 1.0) > 0.01
            || Math.Abs(ContrastValue - 1.0) > 0.01
            || Math.Abs(SaturationValue - 1.0) > 0.01
            || Math.Abs(HueValue) > 0.5
            || Math.Abs(GammaValue - 1.0) > 0.01;
    }

    private void CommitPendingPreview()
    {
        if (!_editor.HasPreviewBase) return;

        if (IsAdjustMode && HasAdjustmentChanges())
        {
            _editor.CommitPreview();
            MarkChanged();
        }
        else if (IsResizeMode && ResizePercentage != 100)
        {
            _editor.CommitPreview();
            MarkChanged();
        }
        else if (IsRotateMode && Math.Abs(RotateDegrees) > 0.01)
        {
            _editor.CommitPreview();
            MarkChanged();
            InvalidateBitmapPool();
        }
        else
        {
            _editor.DiscardPreviewBase();
        }
    }

    // --- File operations ---

    [RelayCommand]
    private async Task OpenFile()
    {
        var filePath = OpenFileDialog != null ? await OpenFileDialog() : null;
        if (filePath != null)
            await LoadFile(filePath);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        var folderPath = OpenFolderDialog != null ? await OpenFolderDialog() : null;
        if (folderPath == null) return;

        bool includeSubdirs = false;
        try
        {
            if (Directory.GetDirectories(folderPath).Any(d => !Path.GetFileName(d).StartsWith('.')) && ConfirmDialog != null)
                includeSubdirs = await ConfirmDialog("Include Subdirectories",
                    "This folder contains subdirectories. Include images from all subdirectories?");
        }
        catch { /* ignore permission errors */ }

        _includeSubdirectories = includeSubdirs;
        _currentDirectory = folderPath;
        _directoryImages = ImageEditorService.GetImagesInDirectory(folderPath, _sortOrder, _includeSubdirectories);
        if (_directoryImages.Count == 0)
        {
            SetTitleStatus("No supported images found in directory");
            return;
        }
        _currentImageIndex = 0;
        await LoadFile(_directoryImages[0]);
    }

    public async Task LoadFile(string filePath)
    {
        if (!ImageEditorService.IsSupportedFile(filePath))
        {
            SetTitleStatus("Unsupported file format");
            return;
        }

        // Commit any pending preview before navigating
        CommitPendingPreview();

        if (_hasUnsavedChanges && _editor.CanUndo)
        {
            var discard = ConfirmDialog != null
                ? await ConfirmDialog("Unsaved Changes", "You have unsaved changes. Discard them?")
                : true;
            if (!discard) return;
        }

        try
        {
            _editor.LoadImage(filePath);
            _currentFilePath = filePath;
            _hasUnsavedChanges = false;
            _suppressPreviewUpdate = true;
            ResetAdjustments();
            ResizePercentage = 100;
            _suppressPreviewUpdate = false;

            // Invalidate reusable bitmap since we loaded a new image
            InvalidateBitmapPool();

            _titleStatus = null;

            var existingIndex = _directoryImages.IndexOf(filePath);
            if (existingIndex >= 0)
            {
                _currentImageIndex = existingIndex;
            }
            else
            {
                var dir = Path.GetDirectoryName(filePath)!;
                _includeSubdirectories = false;
                RefreshDirectoryListing(dir, force: true);
                _currentImageIndex = _directoryImages.IndexOf(filePath);
            }

            RefreshDisplay();
            ReinitializeActiveMode();
        }
        catch (Exception ex)
        {
            SetTitleStatus($"Error loading: {ex.Message}");
        }
    }

    public async Task NavigateImage(int direction)
    {
        if (_directoryImages.Count == 0) return;

        int newIndex;

        if (_isShuffleMode && _shuffledOrder != null)
        {
            if (direction > 0)
            {
                if (_shufflePosition + 1 < _shuffledOrder.Count)
                {
                    _shufflePosition++;
                }
                else
                {
                    // All images shown — reshuffle, skip current to avoid repeat
                    var currentDirIndex = _shuffledOrder[_shufflePosition];
                    BuildShuffledOrder(currentDirIndex);
                    if (_shuffledOrder.Count <= 1)
                    {
                        if (IsContinuousMode && !IsSlideshowPaused)
                            RestartSlideshowTimer();
                        return;
                    }
                    _shufflePosition = 1;
                }
                newIndex = _shuffledOrder[_shufflePosition];
            }
            else
            {
                if (_shufflePosition > 0)
                {
                    _shufflePosition--;
                    newIndex = _shuffledOrder[_shufflePosition];
                }
                else
                {
                    if (IsContinuousMode && !IsSlideshowPaused)
                        RestartSlideshowTimer();
                    return;
                }
            }
        }
        else
        {
            newIndex = _currentImageIndex + direction;
            if (newIndex < 0) newIndex = _directoryImages.Count - 1;
            if (newIndex >= _directoryImages.Count) newIndex = 0;
        }

        if (newIndex != _currentImageIndex)
            await LoadFile(_directoryImages[newIndex]);

        if (IsContinuousMode && !IsSlideshowPaused)
            RestartSlideshowTimer();
    }

    // --- Edit operations ---

    [RelayCommand]
    private void RotateLeft()
    {
        CommitPendingPreview();
        _editor.RotateCounterClockwise();
        MarkChanged();
        InvalidateBitmapPool(); // dimensions swap on rotate
        RefreshDisplay();
        ReinitializeActiveMode();
    }

    [RelayCommand]
    private void RotateRight()
    {
        CommitPendingPreview();
        _editor.RotateClockwise();
        MarkChanged();
        InvalidateBitmapPool(); // dimensions swap on rotate
        RefreshDisplay();
        ReinitializeActiveMode();
    }

    [RelayCommand]
    private void AutoColor()
    {
        CommitPendingPreview();
        _editor.AutoColor();
        MarkChanged();
        RefreshDisplay();
        ReinitializeActiveMode();
    }

    [RelayCommand]
    private void Undo()
    {
        // Discard any pending preview first
        if (_editor.HasPreviewBase)
        {
            _editor.DiscardPreviewBase();
            _suppressPreviewUpdate = true;
            ResetAdjustments();
            ResizePercentage = 100;
            _suppressPreviewUpdate = false;
            InvalidateBitmapPool();
            RefreshDisplay();
            ReinitializeActiveMode();
            return;
        }

        if (_editor.Undo())
        {
            MarkChanged();
            InvalidateBitmapPool();
            RefreshDisplay();
            ReinitializeActiveMode();
        }
    }

    // --- Crop mode ---

    [RelayCommand]
    private void ToggleCropMode()
    {
        if (IsCropMode)
        {
            ApplyCropIfNeeded();
            IsCropMode = false;
        }
        else
        {
            CommitPendingPreview();
            ExitOtherModes();
            IsCropMode = true;
            if (_editor.HasImage)
            {
                CropX = 0;
                CropY = 0;
                CropWidth = _editor.ImageWidth;
                CropHeight = _editor.ImageHeight;
            }
        }
    }

    public void ApplyCropIfNeeded()
    {
        if (!_editor.HasImage || !IsCropMode) return;
        bool isFullImage = CropX == 0 && CropY == 0
            && CropWidth == _editor.ImageWidth && CropHeight == _editor.ImageHeight;
        if (!isFullImage)
        {
            _editor.Crop(CropX, CropY, CropWidth, CropHeight);
            MarkChanged();
            InvalidateBitmapPool();
            RefreshDisplay();
        }
    }

    public void ApplyCropAndStay()
    {
        if (!_editor.HasImage || !IsCropMode) return;
        ApplyCropIfNeeded();
        CropX = 0;
        CropY = 0;
        CropWidth = _editor.ImageWidth;
        CropHeight = _editor.ImageHeight;
    }

    // --- Strip removal mode ---

    [RelayCommand]
    private void ToggleStripMode()
    {
        if (IsStripMode)
        {
            ApplyStripIfNeeded();
            SwitchStripToCrop();
        }
        else
        {
            CommitPendingPreview();
            ExitOtherModes();
            IsStripMode = true;
            if (_editor.HasImage)
            {
                StripX = _editor.ImageWidth / 3;
                StripWidth = _editor.ImageWidth / 3;
            }
        }
    }

    public void ApplyStripIfNeeded()
    {
        if (!_editor.HasImage || !IsStripMode) return;
        if (StripWidth <= 0 || StripWidth >= _editor.ImageWidth) return;
        _editor.RemoveVerticalStrip(StripX, StripWidth);
        MarkChanged();
        InvalidateBitmapPool();
        RefreshDisplay();
    }

    public void ApplyStripAndStay()
    {
        if (!_editor.HasImage || !IsStripMode) return;
        ApplyStripIfNeeded();
        SwitchStripToCrop();
    }

    private void SwitchStripToCrop()
    {
        IsStripMode = false;
        IsCropMode = true;
        if (_editor.HasImage)
        {
            CropX = 0;
            CropY = 0;
            CropWidth = _editor.ImageWidth;
            CropHeight = _editor.ImageHeight;
        }
    }

    public void CancelCurrentMode()
    {
        if (IsAdjustMode && _editor.HasPreviewBase)
        {
            _editor.DiscardPreviewBase();
            _suppressPreviewUpdate = true;
            ResetAdjustments();
            _suppressPreviewUpdate = false;
            RefreshDisplay();
        }
        if (IsResizeMode && _editor.HasPreviewBase)
        {
            _editor.DiscardPreviewBase();
            _suppressPreviewUpdate = true;
            ResizePercentage = 100;
            _suppressPreviewUpdate = false;
            InvalidateBitmapPool();
            RefreshDisplay();
        }
        if (IsRotateMode && _editor.HasPreviewBase)
        {
            _editor.DiscardPreviewBase();
            _suppressPreviewUpdate = true;
            RotateDegrees = 0;
            _suppressPreviewUpdate = false;
            InvalidateBitmapPool();
            RefreshDisplay();
        }
        IsCropMode = false;
        IsStripMode = false;
        IsResizeMode = false;
        IsAdjustMode = false;
        IsRotateMode = false;
        IsPreviewActive = false;
    }

    // --- Resize mode ---

    [RelayCommand]
    private void ToggleResizeMode()
    {
        if (IsResizeMode)
        {
            CommitPendingPreview();
            IsResizeMode = false;
            IsPreviewActive = false;
        }
        else
        {
            CommitPendingPreview();
            ExitOtherModes();
            IsResizeMode = true;
            _suppressPreviewUpdate = true;
            ResizePercentage = 100;
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
    }

    // --- Adjust mode ---

    [RelayCommand]
    private void ToggleAdjustMode()
    {
        if (IsAdjustMode)
        {
            CommitPendingPreview();
            IsAdjustMode = false;
            IsPreviewActive = false;
        }
        else
        {
            CommitPendingPreview();
            ExitOtherModes();
            IsAdjustMode = true;
            _suppressPreviewUpdate = true;
            ResetAdjustments();
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
    }

    // --- Rotate by degree mode ---

    [RelayCommand]
    private void ToggleRotateMode()
    {
        if (IsRotateMode)
        {
            CommitPendingPreview();
            IsRotateMode = false;
            IsPreviewActive = false;
        }
        else
        {
            CommitPendingPreview();
            ExitOtherModes();
            IsRotateMode = true;
            _suppressPreviewUpdate = true;
            RotateDegrees = 0;
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
    }

    public void NudgeRotation(double deltaDegrees)
    {
        if (!IsRotateMode) return;
        RotateDegrees += deltaDegrees;
    }

    // --- Save ---

    [RelayCommand]
    private async Task Save()
    {
        if (_currentFilePath == null || !_editor.HasImage) return;

        bool wasAdjustMode = IsAdjustMode;
        bool wasResizeMode = IsResizeMode;
        bool wasRotateMode = IsRotateMode;
        CommitPendingPreview();

        try
        {
            _editor.SaveImage(_currentFilePath);
            _hasUnsavedChanges = false;
            SetTitleStatus("Saved");
        }
        catch (Exception ex)
        {
            SetTitleStatus($"Error saving: {ex.Message}");
        }

        ReEnterPreviewMode(wasAdjustMode, wasResizeMode, wasRotateMode);
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (!_editor.HasImage) return;

        bool wasAdjustMode = IsAdjustMode;
        bool wasResizeMode = IsResizeMode;
        bool wasRotateMode = IsRotateMode;
        CommitPendingPreview();

        var defaultName = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "image.png";
        var sourceDir = _currentFilePath != null ? Path.GetDirectoryName(_currentFilePath) : null;
        var filePath = SaveFileDialog != null ? await SaveFileDialog(defaultName, sourceDir) : null;
        if (filePath == null)
        {
            ReEnterPreviewMode(wasAdjustMode, wasResizeMode, wasRotateMode);
            return;
        }

        try
        {
            _editor.SaveImage(filePath);
            _currentFilePath = filePath;
            _hasUnsavedChanges = false;

            var dir = Path.GetDirectoryName(filePath)!;
            RefreshDirectoryListing(dir, force: true);
            _currentImageIndex = _directoryImages.IndexOf(filePath);

            SetTitleStatus("Saved");
        }
        catch (Exception ex)
        {
            SetTitleStatus($"Error saving: {ex.Message}");
        }

        ReEnterPreviewMode(wasAdjustMode, wasResizeMode, wasRotateMode);
    }

    private void ReEnterPreviewMode(bool wasAdjustMode, bool wasResizeMode, bool wasRotateMode = false)
    {
        if (wasAdjustMode && IsAdjustMode)
        {
            _suppressPreviewUpdate = true;
            ResetAdjustments();
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
        if (wasResizeMode && IsResizeMode)
        {
            _suppressPreviewUpdate = true;
            ResizePercentage = 100;
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
        if (wasRotateMode && IsRotateMode)
        {
            _suppressPreviewUpdate = true;
            RotateDegrees = 0;
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
    }

    // --- Delete ---

    [RelayCommand]
    private async Task DeleteFile()
    {
        if (_currentFilePath == null || !_editor.HasImage) return;

        var fileName = Path.GetFileName(_currentFilePath);
        var confirmed = ConfirmDialog != null
            ? await ConfirmDialog("Delete File", $"Delete \"{fileName}\"? This cannot be undone.")
            : false;
        if (!confirmed) return;

        var fileToDelete = _currentFilePath;
        var oldIndex = _currentImageIndex;

        // Remove from directory listing
        if (oldIndex >= 0 && oldIndex < _directoryImages.Count)
            _directoryImages.RemoveAt(oldIndex);

        // Fix up shuffle order after deletion
        if (_isShuffleMode && _shuffledOrder != null)
        {
            _shuffledOrder.Remove(oldIndex);
            for (int i = 0; i < _shuffledOrder.Count; i++)
            {
                if (_shuffledOrder[i] > oldIndex)
                    _shuffledOrder[i]--;
            }
            if (_shufflePosition >= _shuffledOrder.Count)
                _shufflePosition = Math.Max(0, _shuffledOrder.Count - 1);
        }

        // Navigate to next image (or previous if at end)
        if (_directoryImages.Count > 0)
        {
            var newIndex = oldIndex < _directoryImages.Count ? oldIndex : _directoryImages.Count - 1;
            _currentImageIndex = newIndex;
            await LoadFile(_directoryImages[newIndex]);
        }
        else
        {
            // No more images
            _editor.Dispose();
            _currentFilePath = null;
            _currentImageIndex = -1;
            _hasUnsavedChanges = false;
            DisplayImage = null;
            HasImage = false;
            CanUndo = false;
            ImageDimensions = "";
            StatusText = "No more images in directory";
            InvalidateBitmapPool();
            UpdateTitle();
        }

        // Delete the file after navigating away
        try
        {
            File.Delete(fileToDelete);
            SetTitleStatus($"Deleted {fileName}");
        }
        catch (Exception ex)
        {
            SetTitleStatus($"Error deleting: {ex.Message}");
        }
    }

    // --- Rename ---

    [RelayCommand]
    private async Task RenameFile()
    {
        if (_currentFilePath == null || !_editor.HasImage) return;

        var oldName = Path.GetFileName(_currentFilePath);
        var newName = TextInputDialog != null
            ? await TextInputDialog("Rename File", "New file name:", oldName)
            : null;
        if (newName == null || newName == oldName) return;

        var dir = Path.GetDirectoryName(_currentFilePath)!;
        var newPath = Path.Combine(dir, newName);

        if (File.Exists(newPath))
        {
            SetTitleStatus("A file with that name already exists");
            return;
        }

        try
        {
            File.Move(_currentFilePath, newPath);
            _currentFilePath = newPath;

            // Update directory listing
            if (_currentImageIndex >= 0 && _currentImageIndex < _directoryImages.Count)
                _directoryImages[_currentImageIndex] = newPath;

            SetTitleStatus($"Renamed to {newName}");
        }
        catch (Exception ex)
        {
            SetTitleStatus($"Error renaming: {ex.Message}");
        }
    }

    public async Task JumpToImage()
    {
        if (_directoryImages.Count == 0) return;

        var result = TextInputDialog != null
            ? await TextInputDialog("Jump To Image",
                $"Enter image number (1–{_directoryImages.Count}):",
                $"{_currentImageIndex + 1}")
            : null;
        if (result == null) return;

        if (int.TryParse(result, out int num) && num >= 1 && num <= _directoryImages.Count)
        {
            var idx = num - 1;
            if (idx != _currentImageIndex)
                await LoadFile(_directoryImages[idx]);
        }
        else
        {
            SetTitleStatus($"Invalid number — enter 1 to {_directoryImages.Count}");
        }
    }

    // --- Continuous/Slideshow mode ---

    public Action? EnterContinuousView { get; set; }
    public Action? ExitContinuousView { get; set; }

    public bool IsSlideshowPaused { get; private set; }

    public void HandleSpaceKey()
    {
        if (!IsContinuousMode)
        {
            StartContinuousMode();
        }
        else if (IsSlideshowPaused)
        {
            IsSlideshowPaused = false;
            RestartSlideshowTimer();
        }
        else
        {
            IsSlideshowPaused = true;
            _slideshowCts?.Cancel();
            _slideshowCts?.Dispose();
            _slideshowCts = null;
        }
    }

    private void StartContinuousMode()
    {
        if (!HasImage || _directoryImages.Count == 0) return;

        // Only one window may be in slideshow mode at a time
        if (App.ContinuousModeOwner != null && App.ContinuousModeOwner != this)
            return;

        CommitPendingPreview();
        ExitOtherModes();
        IsContinuousMode = true;
        IsSlideshowPaused = false;
        App.ContinuousModeOwner = this;
        EnterContinuousView?.Invoke();

        _slideshowCts?.Cancel();
        _slideshowCts?.Dispose();
        _slideshowCts = new CancellationTokenSource();
        _ = RunSlideshowLoop(_slideshowCts.Token);
    }

    private void RestartSlideshowTimer()
    {
        _slideshowCts?.Cancel();
        _slideshowCts?.Dispose();
        _slideshowCts = new CancellationTokenSource();
        _ = RunSlideshowLoop(_slideshowCts.Token);
    }

    public void StopContinuousMode()
    {
        if (!IsContinuousMode) return;
        _slideshowCts?.Cancel();
        _slideshowCts?.Dispose();
        _slideshowCts = null;
        IsContinuousMode = false;
        IsSlideshowPaused = false;
        _isShuffleMode = false;
        _shuffledOrder = null;
        _shufflePosition = 0;
        if (App.ContinuousModeOwner == this)
            App.ContinuousModeOwner = null;
        ExitContinuousView?.Invoke();
    }

    public void ToggleShuffleMode()
    {
        if (!IsContinuousMode) return;

        _isShuffleMode = !_isShuffleMode;

        if (_isShuffleMode)
        {
            BuildShuffledOrder(_currentImageIndex);
            SetTitleStatus("Shuffle on");
        }
        else
        {
            _shuffledOrder = null;
            _shufflePosition = 0;
            SetTitleStatus("Shuffle off");
        }
    }

    private void BuildShuffledOrder(int startIndex)
    {
        var count = _directoryImages.Count;
        _shuffledOrder = new List<int>(count);
        _shuffledOrder.Add(startIndex);

        var remaining = new List<int>(count - 1);
        for (int i = 0; i < count; i++)
        {
            if (i != startIndex)
                remaining.Add(i);
        }

        var rng = Random.Shared;
        for (int i = remaining.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
        }

        _shuffledOrder.AddRange(remaining);
        _shufflePosition = 0;
    }

    private async Task RunSlideshowLoop(CancellationToken ct)
    {
        var intervalMs = _appSettings.SlideshowIntervalSeconds * 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, ct);
                if (ct.IsCancellationRequested) break;
                await NavigateImage(1);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    // --- Helpers ---

    private void ExitOtherModes()
    {
        if (IsCropMode)
        {
            ApplyCropIfNeeded();
            IsCropMode = false;
        }
        if (IsStripMode)
        {
            ApplyStripIfNeeded();
            IsStripMode = false;
        }
        if (IsAdjustMode)
        {
            CommitPendingPreview();
            IsAdjustMode = false;
        }
        if (IsResizeMode)
        {
            CommitPendingPreview();
            IsResizeMode = false;
        }
        if (IsRotateMode)
        {
            CommitPendingPreview();
            IsRotateMode = false;
        }
        IsPreviewActive = false;
    }

    private void InvalidateBitmapPool()
    {
        // Force new WriteableBitmaps on next RefreshDisplay
        _bitmapPoolWidth = 0;
        _bitmapPoolHeight = 0;
    }

    private void RefreshDisplay()
    {
        if (!_editor.HasImage) return;

        int w = _editor.ImageWidth;
        int h = _editor.ImageHeight;

        // Reallocate both pool bitmaps if dimensions changed
        if (_bitmapPoolWidth != w || _bitmapPoolHeight != h)
        {
            for (int i = 0; i < 2; i++)
            {
                _bitmapPool[i]?.Dispose();
                _bitmapPool[i] = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            }
            _bitmapPoolWidth = w;
            _bitmapPoolHeight = h;
        }

        // Pick the buffer that is NOT currently displayed
        _bitmapPoolIndex = (_bitmapPoolIndex + 1) % 2;
        var wb = _bitmapPool[_bitmapPoolIndex]!;

        // Write pixels directly from ImageSharp into the locked framebuffer
        using (var fb = wb.Lock())
        {
            _editor.CopyPixelsToBgraDirect(fb.Address, fb.RowBytes);
        }

        // Always a different reference from what's currently bound, so Avalonia re-renders
        DisplayImage = wb;

        HasImage = _editor.HasImage;
        CanUndo = _editor.CanUndo || _editor.HasPreviewBase;
        ImagePixelWidthValue = w;
        ImagePixelHeightValue = h;
        ImageDimensions = $"{w} x {h}";
        UpdateTitle();
        UpdateStatusText();
    }

    private void MarkChanged()
    {
        _hasUnsavedChanges = true;
        CanUndo = _editor.CanUndo || _editor.HasPreviewBase;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "";
        var fileSize = "";
        if (_currentFilePath != null && File.Exists(_currentFilePath))
        {
            var bytes = new FileInfo(_currentFilePath).Length;
            fileSize = bytes switch
            {
                >= 1_073_741_824 => $" {bytes / 1_073_741_824.0:F1}gb",
                >= 1_048_576 => $" {bytes / 1_048_576.0:F1}mb",
                >= 1_024 => $" {bytes / 1_024.0:F1}kb",
                _ => $" {bytes}b"
            };
        }
        var modified = _hasUnsavedChanges ? " *" : "";
        var counter = _directoryImages.Count > 0 && _currentImageIndex >= 0
            ? (_isShuffleMode && _shuffledOrder != null
                ? $" ({_shufflePosition + 1} of {_shuffledOrder.Count} shuffled)"
                : $" ({_currentImageIndex + 1} of {_directoryImages.Count})")
            : "";
        var status = _titleStatus != null ? $" — {_titleStatus}" : "";
        Title = $"Cedar Image Editor - {name}{fileSize}{modified}{counter}{status}";
    }

    private void UpdateStatusText()
    {
        if (_currentFilePath == null) return;
        var name = Path.GetFileName(_currentFilePath);
        var counter = _directoryImages.Count > 0 && _currentImageIndex >= 0
            ? (_isShuffleMode && _shuffledOrder != null
                ? $" ({_shufflePosition + 1} of {_shuffledOrder.Count} shuffled)"
                : $" ({_currentImageIndex + 1} of {_directoryImages.Count})")
            : "";
        var status = _titleStatus != null ? $" — {_titleStatus}" : "";
        StatusText = $"{name}{counter} | {ImagePixelWidthValue}x{ImagePixelHeightValue}{status}";
    }

    private void SetTitleStatus(string message)
    {
        _titleStatus = message;
        UpdateTitle();
        UpdateStatusText();
    }

    private void ReinitializeActiveMode()
    {
        if (IsCropMode && _editor.HasImage)
        {
            CropX = 0;
            CropY = 0;
            CropWidth = _editor.ImageWidth;
            CropHeight = _editor.ImageHeight;
        }
        if (IsStripMode && _editor.HasImage)
        {
            SwitchStripToCrop();
        }
        if (IsResizeMode)
        {
            _suppressPreviewUpdate = true;
            ResizePercentage = 100;
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
        if (IsAdjustMode)
        {
            _suppressPreviewUpdate = true;
            ResetAdjustments();
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
        if (IsRotateMode)
        {
            _suppressPreviewUpdate = true;
            RotateDegrees = 0;
            _suppressPreviewUpdate = false;
            _editor.SavePreviewBase();
        }
    }

    [RelayCommand]
    private async Task ChangeSort()
    {
        if (SortDialog == null) return;
        var result = await SortDialog(_sortOrder);
        if (result == null || result == _sortOrder) return;

        _sortOrder = result.Value;
        _isShuffleMode = false;
        _shuffledOrder = null;
        _shufflePosition = 0;
        if (_currentDirectory != null)
        {
            _directoryImages = ImageEditorService.GetImagesInDirectory(_currentDirectory, _sortOrder, _includeSubdirectories);
            if (_directoryImages.Count > 0)
            {
                _currentImageIndex = 0;
                await LoadFile(_directoryImages[0]);
            }
        }
    }

    public async Task ReloadDirectory()
    {
        if (_currentDirectory == null) return;

        _isShuffleMode = false;
        _shuffledOrder = null;
        _shufflePosition = 0;

        var currentFile = _currentFilePath;
        RefreshDirectoryListing(_currentDirectory, force: true);

        if (_directoryImages.Count == 0)
        {
            SetTitleStatus("No images found in directory");
            return;
        }

        // Try to stay on the same image, otherwise load the first
        var idx = currentFile != null ? _directoryImages.IndexOf(currentFile) : -1;
        if (idx >= 0)
        {
            _currentImageIndex = idx;
            await LoadFile(_directoryImages[idx]);
        }
        else
        {
            _currentImageIndex = 0;
            await LoadFile(_directoryImages[0]);
        }
    }

    private void RefreshDirectoryListing(string directory, bool force = false)
    {
        if (!force && string.Equals(_currentDirectory, directory, StringComparison.OrdinalIgnoreCase))
            return;
        _currentDirectory = directory;
        _directoryImages = ImageEditorService.GetImagesInDirectory(directory, _sortOrder, _includeSubdirectories);
    }

    private void ResetAdjustments()
    {
        BrightnessValue = 1.0;
        ContrastValue = 1.0;
        SaturationValue = 1.0;
        HueValue = 0;
        GammaValue = 1.0;
    }

    public void Dispose()
    {
        if (App.ContinuousModeOwner == this)
            App.ContinuousModeOwner = null;
        _slideshowCts?.Cancel();
        _slideshowCts?.Dispose();
        _previewDebounceCts?.Cancel();
        _previewDebounceCts?.Dispose();
        _bitmapPool[0]?.Dispose();
        _bitmapPool[1]?.Dispose();
        _editor.Dispose();
    }
}
