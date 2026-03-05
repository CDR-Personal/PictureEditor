using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PictureEditor.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PictureEditor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ImageEditorService _editor = new();
    private string? _currentFilePath;
    private List<string> _directoryImages = new();
    private int _currentImageIndex = -1;
    private bool _hasUnsavedChanges;
    private bool _suppressPreviewUpdate;

    [ObservableProperty] private Bitmap? _displayImage;
    [ObservableProperty] private string _title = "Picture Editor";
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

    partial void OnBrightnessValueChanged(double value) => UpdateAdjustmentPreview();
    partial void OnContrastValueChanged(double value) => UpdateAdjustmentPreview();
    partial void OnSaturationValueChanged(double value) => UpdateAdjustmentPreview();
    partial void OnHueValueChanged(double value) => UpdateAdjustmentPreview();
    partial void OnGammaValueChanged(double value) => UpdateAdjustmentPreview();
    partial void OnResizePercentageChanged(int value) => UpdateResizePreview();

    private void UpdateAdjustmentPreview()
    {
        if (_suppressPreviewUpdate || !IsAdjustMode || !_editor.HasPreviewBase) return;
        _editor.RestoreFromPreviewBase();
        _editor.ApplyAdjustmentsNoUndo(
            (float)BrightnessValue, (float)ContrastValue, (float)SaturationValue,
            (float)HueValue, (float)GammaValue);
        RefreshDisplay();
    }

    private void UpdateResizePreview()
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
            RefreshDisplay();
            ReinitializeActiveMode();

            var dir = Path.GetDirectoryName(filePath)!;
            _directoryImages = ImageEditorService.GetImagesInDirectory(dir);
            _currentImageIndex = _directoryImages.IndexOf(filePath);

            Title = $"Picture Editor - {Path.GetFileName(filePath)}";
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
        RefreshDisplay();
        ReinitializeActiveMode();
    }

    [RelayCommand]
    private void RotateRight()
    {
        CommitPendingPreview();
        _editor.RotateClockwise();
        MarkChanged();
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
            RefreshDisplay();
            ReinitializeActiveMode();
            return;
        }

        if (_editor.Undo())
        {
            MarkChanged();
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
            // Turning off: apply crop if selection differs from full image
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
            RefreshDisplay();
        }
    }

    public void ApplyCropAndStay()
    {
        if (!_editor.HasImage || !IsCropMode) return;
        ApplyCropIfNeeded();
        // Re-initialize crop for the new image dimensions
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
            RefreshDisplay();
        }
        IsCropMode = false;
        IsResizeMode = false;
        IsAdjustMode = false;
    }

    // --- Resize mode ---

    [RelayCommand]
    private void ToggleResizeMode()
    {
        if (IsResizeMode)
        {
            CommitPendingPreview();
            IsResizeMode = false;
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

        // Commit pending preview changes before saving
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
            // User cancelled - re-enter preview base if needed
            ReEnterPreviewMode(wasAdjustMode, wasResizeMode);
            return;
        }

        try
        {
            _editor.SaveImage(filePath);
            _currentFilePath = filePath;
            _hasUnsavedChanges = false;

            var dir = Path.GetDirectoryName(filePath)!;
            _directoryImages = ImageEditorService.GetImagesInDirectory(dir);
            _currentImageIndex = _directoryImages.IndexOf(filePath);

            Title = $"Picture Editor - {Path.GetFileName(filePath)}";
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
    }

    private void RefreshDisplay()
    {
        if (!_editor.HasImage) return;
        var bytes = _editor.GetCurrentImageBytes();
        using var ms = new MemoryStream(bytes);
        DisplayImage?.Dispose();
        DisplayImage = new Bitmap(ms);
        HasImage = _editor.HasImage;
        CanUndo = _editor.CanUndo || _editor.HasPreviewBase;
        ImagePixelWidthValue = _editor.ImageWidth;
        ImagePixelHeightValue = _editor.ImageHeight;
        ImageDimensions = $"{_editor.ImageWidth} x {_editor.ImageHeight}";
        StatusText = $"{Path.GetFileName(_currentFilePath)} | {_editor.ImageWidth}x{_editor.ImageHeight}";
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
        Title = $"Picture Editor - {name}{modified}";
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

    private void ResetAdjustments()
    {
        BrightnessValue = 1.0;
        ContrastValue = 1.0;
        SaturationValue = 1.0;
        HueValue = 0;
        GammaValue = 1.0;
    }
}
