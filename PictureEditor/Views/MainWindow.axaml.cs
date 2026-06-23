using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PictureEditor.Services;
using PictureEditor.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace PictureEditor.Views;

public partial class MainWindow : Window
{
    private readonly WindowSettings _settings;
    private WindowState _preContinuousWindowState;
    private double _preContinuousWidth;
    private double _preContinuousHeight;

    // Move-mode key debounce — guards against sticky-key bursts and accidental key-bounce
    private DateTime _moveKeyBlockUntil = DateTime.MinValue;
    private Key _lastMoveKey = Key.None;
    private int _sameMoveKeyCount;
    private const int MoveKeyDebounceMs = 100;
    private const int MoveSameKeyLimit = 20;

    public MainWindow()
    {
        InitializeComponent();

        _settings = WindowSettings.Load();
        Width = _settings.Width;
        Height = _settings.Height;

        if (_settings.X != 0 || _settings.Y != 0)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)_settings.X, (int)_settings.Y);
        }

        if (_settings.IsMaximized)
            WindowState = WindowState.Maximized;

        // Use tunnel routing so we get key events before child controls
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Automatically show the folder picker when the window first opens
        Opened += OnWindowOpened;

        // Track window for numbered hotkeys (1-9) and title-bar prefix.
        Opened += (_, _) => App.RegisterWindow(this);
        Closed += (_, _) => App.UnregisterWindow(this);
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened; // only once
        if (DataContext is not MainWindowViewModel vm) return;

        // Yield to the event loop so macOS Apple Events (from "Open With")
        // have a chance to be delivered before we decide to show the folder dialog
        if (vm.StartupFilePath == null)
            await Task.Delay(150);

        if (vm.StartupFilePath != null)
        {
            var path = vm.StartupFilePath;
            vm.StartupFilePath = null;
            if (System.IO.File.Exists(path))
                await vm.LoadFile(path);
            else if (System.IO.Directory.Exists(path))
            {
                var images = ImageEditorService.GetImagesInDirectory(path);
                if (images.Count > 0)
                    await vm.LoadFile(images[0]);
            }
        }
        else
        {
            await vm.OpenFolderCommand.ExecuteAsync(null);
        }
    }

    // Set once the user has confirmed closing with unsaved edits, so the
    // programmatic Close() that follows the dialog isn't intercepted again.
    private bool _forceClose;

    /// <summary>
    /// Suppresses this window's unsaved-changes prompt on its next close.
    /// Used during an app-wide quit (Cmd+Q), where confirmation has already
    /// been handled centrally in <see cref="App.OnShutdownRequested"/>.
    /// </summary>
    internal void SuppressCloseConfirmation() => _forceClose = true;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // Guard against losing unsaved edits. OnClosing is synchronous, so we
        // cancel this pass, await the confirmation dialog, then re-issue Close()
        // if the user chooses to discard.
        if (!_forceClose && DataContext is MainWindowViewModel vm && vm.HasUnsavedChanges)
        {
            e.Cancel = true;
            var discard = await ShowConfirmDialog(
                "Unsaved Changes",
                "You have unsaved changes. Close without saving?");
            if (discard)
            {
                _forceClose = true;
                Close();
            }
            return;
        }

        base.OnClosing(e);

        _settings.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.X = Position.X;
            _settings.Y = Position.Y;
            _settings.Width = Width;
            _settings.Height = Height;
        }
        _settings.Save();

        // Dispose the ViewModel to release image resources
        (DataContext as IDisposable)?.Dispose();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.OpenFileDialog = ShowOpenFileDialog;
            vm.OpenFolderDialog = ShowOpenFolderDialog;
            vm.SaveFileDialog = ShowSaveFileDialog;
            vm.ConfirmDialog = ShowConfirmDialog;
            vm.TextInputDialog = ShowTextInputDialog;
            vm.SortDialog = ShowSortDialog;
            vm.EnterContinuousView = OnEnterContinuousView;
            vm.ExitContinuousView = OnExitContinuousView;
            vm.ResolveConflict = ResolveMoveConflict;
            vm.ShowMessageDialog = ShowInfoDialog;
            vm.ShowMoveHelpDialog = ShowMoveHelp;

            // Subscribe to IsPreviewActive changes for adaptive interpolation quality
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPreviewActive))
        {
            var vm = (MainWindowViewModel)sender!;
            var quality = vm.IsPreviewActive
                ? BitmapInterpolationMode.LowQuality
                : BitmapInterpolationMode.MediumQuality;
            RenderOptions.SetBitmapInterpolationMode(MainImage, quality);
        }
    }

    private void OnEnterContinuousView()
    {
        _preContinuousWindowState = WindowState;
        _preContinuousWidth = Width;
        _preContinuousHeight = Height;
        WindowState = WindowState.FullScreen;
    }

    private void OnExitContinuousView()
    {
        WindowState = _preContinuousWindowState;
        if (_preContinuousWindowState == WindowState.Normal)
        {
            Width = _preContinuousWidth;
            Height = _preContinuousHeight;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Don't steal Left/Right/Enter from focused input controls (NumericUpDown, TextBox, Slider)
        bool inputHasFocus = FocusManager?.GetFocusedElement() is
            NumericUpDown or TextBox or Avalonia.Controls.Primitives.RangeBase;

        // Cmd+W / Ctrl+W to close window
        if (e.Key == Key.W && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            Close();
            e.Handled = true;
            return;
        }

        // Tab toggles Edit ↔ Move (bare key only). Blocked during slideshow — Escape out first,
        // since slideshow + Move-mode would compose into a chrome-less state with auto-advancing
        // images firing letter-key moves on whatever the loop happens to land on.
        if (e.Key == Key.Tab && !inputHasFocus && e.KeyModifiers == KeyModifiers.None && !vm.IsContinuousMode)
        {
            vm.ToggleMode();
            e.Handled = true;
            return;
        }

        // Move-mode owns most keys when active. If it handles the key, stop here.
        if (vm.IsMoveMode && HandleMoveModeKey(vm, e, inputHasFocus)) return;

        switch (e.Key)
        {
            case Key.Left:
                if (!inputHasFocus)
                {
                    HandleNavigateAsync(vm, -1);
                    e.Handled = true;
                }
                break;
            case Key.Right:
                if (!inputHasFocus)
                {
                    HandleNavigateAsync(vm, 1);
                    e.Handled = true;
                }
                break;
            case Key.Up:
                if (!inputHasFocus && vm.IsRotateMode)
                {
                    vm.NudgeRotation(1);
                    e.Handled = true;
                }
                break;
            case Key.Down:
                if (!inputHasFocus && vm.IsRotateMode)
                {
                    vm.NudgeRotation(-1);
                    e.Handled = true;
                }
                break;
            case Key.Return:
                if (vm.IsCropMode && !inputHasFocus)
                {
                    vm.ApplyCropAndStay();
                    e.Handled = true;
                }
                else if (vm.IsStripMode && !inputHasFocus)
                {
                    vm.ApplyStripAndStay();
                    e.Handled = true;
                }
                break;
            case Key.Delete:
            case Key.Back:
                if (!inputHasFocus && vm.HasImage)
                {
                    HandleDeleteAsync(vm);
                    e.Handled = true;
                }
                break;
            case Key.Space:
                if (!inputHasFocus && vm.HasImage)
                {
                    vm.HandleSpaceKey();
                    e.Handled = true;
                }
                break;
            case Key.Multiply:
                if (!inputHasFocus && vm.IsContinuousMode)
                {
                    vm.ToggleShuffleMode();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (vm.IsContinuousMode)
                {
                    vm.StopContinuousMode();
                    Focus();
                    e.Handled = true;
                }
                else if (vm.IsCropMode || vm.IsStripMode || vm.IsResizeMode || vm.IsAdjustMode || vm.IsRotateMode)
                {
                    vm.CancelCurrentMode();
                    Focus();
                    e.Handled = true;
                }
                break;
            case Key.F2:
                if (vm.HasImage)
                {
                    HandleRenameAsync(vm);
                    e.Handled = true;
                }
                break;
            case Key.F1:
                ShowHelpDialog();
                e.Handled = true;
                break;
            case Key.F12:
                HandleReloadAsync(vm);
                e.Handled = true;
                break;
            case Key.N:
                if (e.KeyModifiers is KeyModifiers.Meta or KeyModifiers.Control)
                {
                    App.CreateNewWindow(sourceWindow: this);
                    e.Handled = true;
                }
                break;
            case Key.J:
                if (e.KeyModifiers is KeyModifiers.Meta or KeyModifiers.Control)
                {
                    HandleJumpToAsync(vm);
                    e.Handled = true;
                }
                break;
            case Key.X:
                if (e.KeyModifiers is KeyModifiers.Meta or KeyModifiers.Control && vm.HasImage)
                {
                    vm.ToggleStripModeCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.D1:
            case Key.D2:
            case Key.D3:
            case Key.D4:
            case Key.D5:
            case Key.D6:
            case Key.D7:
            case Key.D8:
            case Key.D9:
            case Key.NumPad1:
            case Key.NumPad2:
            case Key.NumPad3:
            case Key.NumPad4:
            case Key.NumPad5:
            case Key.NumPad6:
            case Key.NumPad7:
            case Key.NumPad8:
            case Key.NumPad9:
                if (!inputHasFocus)
                {
                    // Shift+8 in slideshow toggles shuffle (matches the * key).
                    if (e.Key == Key.D8 && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && vm.IsContinuousMode)
                    {
                        vm.ToggleShuffleMode();
                        e.Handled = true;
                    }
                    else if (e.KeyModifiers == KeyModifiers.None)
                    {
                        int n = e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9
                            ? e.Key - Key.NumPad0
                            : e.Key - Key.D0;
                        App.ActivateWindow(n);
                        e.Handled = true;
                    }
                }
                break;
        }
    }

    private async void HandleRenameAsync(MainWindowViewModel vm)
    {
        try { await vm.RenameFileCommand.ExecuteAsync(null); }
        catch (Exception ex) { vm.Title = $"Cedar Image Editor — Error: {ex.Message}"; }
    }

    private async void HandleJumpToAsync(MainWindowViewModel vm)
    {
        try { await vm.JumpToImage(); }
        catch (Exception ex) { vm.Title = $"Cedar Image Editor — Error: {ex.Message}"; }
    }

    private async void HandleReloadAsync(MainWindowViewModel vm)
    {
        try { await vm.ReloadDirectory(); }
        catch (Exception ex) { vm.Title = $"Cedar Image Editor — Error: {ex.Message}"; }
    }

    private async void HandleDeleteAsync(MainWindowViewModel vm)
    {
        try { await vm.DeleteFileCommand.ExecuteAsync(null); }
        catch (Exception ex) { vm.Title = $"Cedar Image Editor — Error: {ex.Message}"; }
    }

    private bool HandleMoveModeKey(MainWindowViewModel vm, KeyEventArgs e, bool inputHasFocus)
    {
        if (inputHasFocus) return false;

        // Hard pause between accepted keys (anti-bounce). Drops the key entirely.
        if (DateTime.UtcNow < _moveKeyBlockUntil)
        {
            e.Handled = true;
            return true;
        }

        // Stuck-key guard: same key fired too many times in a row.
        if (e.Key == _lastMoveKey) _sameMoveKeyCount++;
        else { _lastMoveKey = e.Key; _sameMoveKeyCount = 1; }

        if (_sameMoveKeyCount > MoveSameKeyLimit)
        {
            _sameMoveKeyCount = 0;
            _moveKeyBlockUntil = DateTime.UtcNow.AddMilliseconds(MoveKeyDebounceMs);
            e.Handled = true;
            return true;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (meta)
        {
            // Swallow Edit-only Cmd shortcuts so they don't fire the bound commands while
            // in Move mode. Mode-agnostic shortcuts (Cmd+O, Cmd+W, Cmd+N, Cmd+J, Cmd+digits)
            // pass through.
            bool isEditOnly = e.Key switch
            {
                Key.S or Key.Z or Key.L or Key.R or Key.A or Key.X => true,
                Key.C when shift => true,
                _ => false
            };
            if (isEditOnly)
            {
                e.Handled = true;
                return true;
            }
            return false;
        }

        string? label = e.Key switch
        {
            Key.A when !ctrl && !shift => "A",
            Key.T when !ctrl && !shift => "T",
            Key.E when !ctrl && !shift => "E",
            Key.Y when !shift => ctrl ? "YC" : "Y",
            Key.N => shift ? "NT" : ctrl ? "YN" : "N",
            Key.L => shift ? "LT" : ctrl ? "YL" : "L",
            Key.D => shift ? "DT" : ctrl ? "YD" : "D",
            Key.S => shift ? "ST" : ctrl ? "YS" : "S",
            Key.P => shift ? "PT" : ctrl ? "YP" : "P",
            Key.U => shift ? "UT" : ctrl ? "YU" : "U",
            _ => null
        };

        if (label != null)
        {
            _ = vm.MoveByLabel(label);
            _moveKeyBlockUntil = DateTime.UtcNow.AddMilliseconds(MoveKeyDebounceMs);
            e.Handled = true;
            return true;
        }

        switch (e.Key)
        {
            case Key.Escape:
                _ = vm.UndoLastMove();
                _moveKeyBlockUntil = DateTime.UtcNow.AddMilliseconds(MoveKeyDebounceMs);
                e.Handled = true;
                return true;

            case Key.Space:
                vm.ToggleDuplicateCheck();
                e.Handled = true;
                return true;

            case Key.Z:
            case Key.Right:
                HandleNavigateAsync(vm, 1);
                e.Handled = true;
                return true;

            case Key.Left:
                HandleNavigateAsync(vm, -1);
                e.Handled = true;
                return true;

            case Key.J when !ctrl && !shift:
                HandleJumpToAsync(vm);
                e.Handled = true;
                return true;

            case Key.F1:
                _ = ShowMoveHelp();
                e.Handled = true;
                return true;
        }

        // Common keys (Delete/Back, F2, F12, digits, Cmd+J, Cmd+N) fall through to the
        // shared switch below — same behavior in both modes.
        return false;
    }

    private async void HandleNavigateAsync(MainWindowViewModel vm, int direction)
    {
        try
        {
            await vm.NavigateImage(direction);
        }
        catch (Exception ex)
        {
            vm.Title = $"Cedar Image Editor — Error navigating: {ex.Message}";
        }
    }

    private void OnNewWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        App.CreateNewWindow(sourceWindow: this);
    }

    private void OnHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowHelpDialog();
    }

    private void OnMoveHintsClick(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _ = ShowMoveHelp();
    }

    private async Task ShowMoveHelp()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var t = vm.MoveTargets;

        string Line(string key, string folder) =>
            string.IsNullOrEmpty(folder) ? "" : $"\n  {key,-12} {folder}";

        var msg =
            "Tab toggles between Edit and Move modes.\n\nMove keys (current target folders):\n"
            + Line("E", t.FolderEdit)
            + Line("A", t.FolderA)
            + Line("N", t.FolderN)
            + Line("D", t.FolderD)
            + Line("L", t.FolderL)
            + Line("S", t.FolderS)
            + Line("P", t.FolderP)
            + Line("U", t.FolderU)
            + "\n"
            + Line("T", t.FolderT)
            + Line("Shift+N", t.FolderNT)
            + Line("Shift+D", t.FolderDT)
            + Line("Shift+L", t.FolderLT)
            + Line("Shift+S", t.FolderST)
            + Line("Shift+P", t.FolderPT)
            + Line("Shift+U", t.FolderUT)
            + "\n"
            + Line("Y", t.FolderY)
            + Line("Ctrl+Y", t.FolderYC)
            + Line("Ctrl+N", t.FolderYN)
            + Line("Ctrl+D", t.FolderYD)
            + Line("Ctrl+L", t.FolderYL)
            + Line("Ctrl+S", t.FolderYS)
            + Line("Ctrl+P", t.FolderYP)
            + Line("Ctrl+U", t.FolderYU)
            + "\n\nNavigation & file actions:"
            + "\n  Right / Z    Next image"
            + "\n  Left         Previous image"
            + "\n  Space        Toggle duplicate-check"
            + "\n  Escape       Undo last move"
            + "\n  Delete/Back  Delete file"
            + "\n  J            Jump to image #"
            + "\n  F2           Rename"
            + "\n  F12          Reload directory";

        var dialog = new Window
        {
            Title = "Move-mode Keys",
            Width = 600,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var okButton = new Button { Content = "_OK", Width = 80, IsDefault = true };
        okButton.Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return || e.Key == Key.Escape || e.Key == Key.F1)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        dialog.Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Margin = new Avalonia.Thickness(0, 12, 0, 0),
                    Children = { okButton },
                    [DockPanel.DockProperty] = Dock.Bottom
                },
                new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = msg,
                        FontFamily = new Avalonia.Media.FontFamily("Menlo,Consolas,monospace"),
                        TextWrapping = Avalonia.Media.TextWrapping.NoWrap
                    }
                }
            }
        };

        await dialog.ShowDialog(this);
    }

    private void ShowHelpDialog()
    {
        var dialog = new Window
        {
            Title = "Keyboard Shortcuts",
            Width = 420,
            Height = 710,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var mod = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX) ? "Cmd" : "Ctrl";

        var hotkeys = new (string Key, string Description)[]
        {
            ($"{mod}+N", "New Window"),
            ($"{mod}+O", "Open File"),
            ($"{mod}+Shift+O", "Open Folder"),
            ($"{mod}+S", "Save"),
            ($"{mod}+Shift+S", "Save As"),
            ($"{mod}+Z", "Undo"),
            ($"{mod}+L", "Rotate Left"),
            ($"{mod}+R", "Rotate Right"),
            ($"{mod}+Shift+C", "Crop Mode"),
            ($"{mod}+X", "Remove Strip Mode"),
            ($"{mod}+A", "Auto Color"),
            ($"{mod}+J", "Jump To Image"),
            ($"{mod}+W", "Close Window"),
            ("1 \u2013 9", "Activate Window 1\u20139"),
            ("Left / Right", "Navigate Images"),
            ("Up / Down", "Fine Rotate (in rotate mode)"),
            ("Enter", "Apply Crop / Strip"),
            ("Space", "Start/Stop Slideshow"),
            ("*", "Toggle Shuffle (in slideshow)"),
            ("Escape", "Cancel Current Mode"),
            ("F1", "Show This Help"),
            ("F2", "Rename Current File"),
            ("F12", "Reload Current Folder"),
            ("Delete", "Delete Current File"),
            ("Tab","Toggle edit/mpve mode")
        };

        var list = new StackPanel { Spacing = 4 };
        foreach (var (key, desc) in hotkeys)
        {
            list.Children.Add(new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = key,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        Width = 180,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = desc,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }
                }
            });
        }

        var closeButton = new Button
        {
            Content = "Close",
            Width = 80,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        closeButton.Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, ke) =>
        {
            if (ke.Key is Key.Escape or Key.Enter)
            {
                dialog.Close();
                ke.Handled = true;
            }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Keyboard Shortcuts",
                    FontSize = 20,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                },
                new ScrollViewer
                {
                    Content = list,
                    MaxHeight = 520
                },
                closeButton
            }
        };

        dialog.ShowDialog(this);
    }

    private async Task<string?> ShowOpenFileDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image Files")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" }
                }
            }
        });
        return files.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task<string?> ShowOpenFolderDialog()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Image Folder",
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task<string?> ShowSaveFileDialog(string defaultName, string? sourceDir)
    {
        var options = new FilePickerSaveOptions
        {
            Title = "Save Image As",
            SuggestedFileName = defaultName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                new FilePickerFileType("WebP") { Patterns = new[] { "*.webp" } }
            }
        };

        if (sourceDir != null)
        {
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(sourceDir);
        }

        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file?.Path.LocalPath;
    }

    private async Task<bool> ShowConfirmDialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 350,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Focusable = true
        };

        bool result = false;
        var yesButton = new Button { Content = "_Yes", Width = 80 };
        var noButton = new Button { Content = "_No", Width = 80 };

        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        noButton.Click += (_, _) => { result = false; dialog.Close(); };

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Y) { result = true; dialog.Close(); e.Handled = true; }
            else if (e.Key == Key.N || e.Key == Key.Escape) { result = false; dialog.Close(); e.Handled = true; }
            else if (e.Key == Key.Return) { result = true; dialog.Close(); e.Handled = true; }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 15,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { yesButton, noButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task ShowInfoDialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var okButton = new Button { Content = "_OK", Width = 80, IsDefault = true };
        okButton.Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return || e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 15,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { okButton }
                }
            }
        };

        await dialog.ShowDialog(this);
    }

    private CompareWindow? _activeCompareWindow;

    private async Task<ConflictResolution> ResolveMoveConflict(string sourceFile, string destinationFile)
    {
        // Open compare window showing the file already at the destination
        _activeCompareWindow?.Close();
        var compare = new CompareWindow();
        _activeCompareWindow = compare;

        try
        {
            compare.ShowImageForCompare(destinationFile);
            compare.Show(this);

            // Center main + compare as a pair on the primary screen, top-aligned
            int totalWidth = (int)Width + (int)compare.Width;
            int screenW = (int)(Screens.Primary?.Bounds.Width ?? 1920);
            int leftX = Math.Max(0, (screenW - totalWidth) / 2);
            Position = new PixelPoint(leftX, 0);
            compare.Position = new PixelPoint(leftX + (int)Width, 0);

            var replace = await ShowConfirmDialog("File exists",
                $"File already exists: {System.IO.Path.GetFileName(destinationFile)}\n\nReplace existing file?");

            if (replace) return ConflictResolution.Replace;

            var deleteSource = await ShowConfirmDialog("Delete file",
                $"Delete source file: {System.IO.Path.GetFileName(sourceFile)}?");

            return deleteSource ? ConflictResolution.DeleteSource : ConflictResolution.Cancel;
        }
        finally
        {
            compare.DisposeImage();
            compare.Close();
            if (_activeCompareWindow == compare) _activeCompareWindow = null;
        }
    }

    private async Task<string?> ShowTextInputDialog(string title, string label, string defaultValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Focusable = true
        };

        string? result = null;
        var textBox = new TextBox
        {
            Text = defaultValue,
            SelectionStart = 0,
            SelectionEnd = System.IO.Path.GetFileNameWithoutExtension(defaultValue).Length
        };

        var okButton = new Button { Content = "_OK", Width = 80 };
        var cancelButton = new Button { Content = "_Cancel", Width = 80 };

        void Submit() { result = textBox.Text; dialog.Close(); }

        okButton.Click += (_, _) => Submit();
        cancelButton.Click += (_, _) => dialog.Close();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { Submit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = label },
                textBox,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { okButton, cancelButton }
                }
            }
        };

        dialog.Opened += (_, _) => textBox.Focus();

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<ImageSortOrder?> ShowSortDialog(ImageSortOrder current)
    {
        var dialog = new Window
        {
            Title = "Sort Images",
            Width = 300,
            Height = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Focusable = true
        };

        ImageSortOrder? result = null;
        var options = new (string Label, ImageSortOrder Value)[]
        {
            ("Name (A \u2192 Z)", ImageSortOrder.NameAsc),
            ("Name (Z \u2192 A)", ImageSortOrder.NameDesc),
            ("Date Modified (Oldest)", ImageSortOrder.DateModifiedAsc),
            ("Date Modified (Newest)", ImageSortOrder.DateModifiedDesc),
            ("File Size (Smallest)", ImageSortOrder.SizeAsc),
            ("File Size (Largest)", ImageSortOrder.SizeDesc),
        };

        var radioGroup = new StackPanel { Spacing = 6 };
        RadioButton? firstButton = null;
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "Sort",
                IsChecked = value == current,
                Tag = value
            };
            if (firstButton == null) firstButton = rb;
            radioGroup.Children.Add(rb);
        }

        var okButton = new Button { Content = "_OK", Width = 80 };
        var cancelButton = new Button { Content = "_Cancel", Width = 80 };

        okButton.Click += (_, _) =>
        {
            foreach (var child in radioGroup.Children)
            {
                if (child is RadioButton rb && rb.IsChecked == true)
                {
                    result = (ImageSortOrder)rb.Tag!;
                    break;
                }
            }
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
            else if (e.Key == Key.Return) { okButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Sort images by:",
                    FontSize = 16,
                    FontWeight = Avalonia.Media.FontWeight.Bold
                },
                radioGroup,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { okButton, cancelButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }
}
