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
using System.Threading;
using System.Threading.Tasks;

namespace PictureEditor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ImageEditorService _editor = new();
    private string? _currentFilePath;
    private string? _currentDirectory;
    private List<string> _directoryImages = new();
    private int _currentImageIndex = -1;
    private bool _hasUnsavedChanges;
    private bool _suppressPreviewUpdate;
    private CancellationTokenSource? _previewDebounceCts;
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
    [ObservableProperty] private int _resizePercentage = 100;
    [ObservableProperty] private string _imageDimensions = "";
    [ObservableProperty] private int _imagePixelWidthValue;
    [ObservableProperty] private int _imagePixelHeightValue;

    // Tracks whether a preview render is in progress (for adaptive interpolation)
    [ObservableProperty] private bool _isPreviewActive;

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

    public Func<Task<string?>>? OpenFileDialog { get; set; }
    public Func<Task<string?>>? OpenFolderDialog { get; set; }
    public Func<string, Task<string?>>? SaveFileDialog { get; set; }
    public Func<string, string, Task<bool>>? ConfirmDialog { get; set; }

    // --- Real-time preview change handlers ---

    partial void OnBrightnessValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnContrastValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnSaturationValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnHueValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnGammaValueChanged(double value) => ScheduleAdjustmentPreview();
    partial void OnResizePercentageChanged(int value) => ScheduleResizePreview();

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

        _directoryImages = ImageEditorService.GetImagesInDirectory(folderPath);
        if (_directoryImages.Count == 0)
        {
            StatusText = "No supported images found in directory";
            return;
        }
        _currentImageIndex = 0;
        await LoadFile(_directoryImages[0]);
    }

    public async Task LoadFile(string filePath)
    {
        if (!ImageEditorService.IsSupportedFile(filePath))
        {
            StatusText = "Unsupported file format";
            return;
        }

        // Commit any pending preview before navigating
        CommitPendingPreview();

        if (_hasUnsavedChanges)
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

            RefreshDisplay();
            ReinitializeActiveMode();

            var dir = Path.GetDirectoryName(filePath)!;
            RefreshDirectoryListing(dir);
            _currentImageIndex = _directoryImages.IndexOf(filePath);

            Title = $"Cedar Image Editor - {Path.GetFileName(filePath)}";
            StatusText = $"{Path.GetFileName(filePath)} | {_editor.ImageWidth}x{_editor.ImageHeight}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading image: {ex.Message}";
        }
    }

    public async Task NavigateImage(int direction)
    {
        if (_directoryImages.Count == 0) return;

        var newIndex = _currentImageIndex + direction;
        if (newIndex < 0) newIndex = _directoryImages.Count - 1;
        if (newIndex >= _directoryImages.Count) newIndex = 0;

        if (newIndex != _currentImageIndex)
            await LoadFile(_directoryImages[newIndex]);
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
        IsCropMode = false;
        IsResizeMode = false;
        IsAdjustMode = false;
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

    // --- Save ---

    [RelayCommand]
    private async Task Save()
    {
        if (_currentFilePath == null || !_editor.HasImage) return;

        bool wasAdjustMode = IsAdjustMode;
        bool wasResizeMode = IsResizeMode;
        CommitPendingPreview();

        try
        {
            _editor.SaveImage(_currentFilePath);
            _hasUnsavedChanges = false;
            StatusText = $"Saved: {Path.GetFileName(_currentFilePath)}";
            UpdateTitle();
        }
        catch (Exception ex)
        {
            StatusText = $"Error saving: {ex.Message}";
        }

        // Re-enter preview base if still in a preview mode
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
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (!_editor.HasImage) return;

        bool wasAdjustMode = IsAdjustMode;
        bool wasResizeMode = IsResizeMode;
        CommitPendingPreview();

        var defaultName = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "image.png";
        var filePath = SaveFileDialog != null ? await SaveFileDialog(defaultName) : null;
        if (filePath == null)
        {
            ReEnterPreviewMode(wasAdjustMode, wasResizeMode);
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

            Title = $"Cedar Image Editor - {Path.GetFileName(filePath)}";
            StatusText = $"Saved: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error saving: {ex.Message}";
        }

        ReEnterPreviewMode(wasAdjustMode, wasResizeMode);
    }

    private void ReEnterPreviewMode(bool wasAdjustMode, bool wasResizeMode)
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
    }

    // --- Helpers ---

    private void ExitOtherModes()
    {
        if (IsCropMode)
        {
            ApplyCropIfNeeded();
            IsCropMode = false;
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
        StatusText = $"{Path.GetFileName(_currentFilePath)} | {w}x{h}";
        UpdateTitle();
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
        var modified = _hasUnsavedChanges ? " *" : "";
        Title = $"Cedar Image Editor - {name}{modified}";
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
    }

    private void RefreshDirectoryListing(string directory, bool force = false)
    {
        if (!force && string.Equals(_currentDirectory, directory, StringComparison.OrdinalIgnoreCase))
            return;
        _currentDirectory = directory;
        _directoryImages = ImageEditorService.GetImagesInDirectory(directory);
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
        _previewDebounceCts?.Cancel();
        _previewDebounceCts?.Dispose();
        _bitmapPool[0]?.Dispose();
        _bitmapPool[1]?.Dispose();
        _editor.Dispose();
    }
}
