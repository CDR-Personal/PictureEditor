using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PictureEditor.Services;
using PictureEditor.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PictureEditor.Views;

public partial class MainWindow : Window
{
    private readonly WindowSettings _settings;

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
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
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
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Don't steal Left/Right/Enter from focused input controls (NumericUpDown, TextBox, Slider)
        bool inputHasFocus = FocusManager?.GetFocusedElement() is
            NumericUpDown or TextBox or Avalonia.Controls.Primitives.RangeBase;

        switch (e.Key)
        {
            case Key.Left:
                if (!inputHasFocus)
                {
                    _ = vm.NavigateImage(-1);
                    e.Handled = true;
                }
                break;
            case Key.Right:
                if (!inputHasFocus)
                {
                    _ = vm.NavigateImage(1);
                    e.Handled = true;
                }
                break;
            case Key.Return:
                if (vm.IsCropMode && !inputHasFocus)
                {
                    vm.ApplyCropAndStay();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (vm.IsCropMode || vm.IsResizeMode || vm.IsAdjustMode)
                {
                    vm.CancelCurrentMode();
                    // Return focus to the window so subsequent keys work
                    Focus();
                    e.Handled = true;
                }
                break;
            case Key.F1:
                ShowHelpDialog();
                e.Handled = true;
                break;
        }
    }

    private void OnHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowHelpDialog();
    }

    private void ShowHelpDialog()
    {
        var dialog = new Window
        {
            Title = "Keyboard Shortcuts",
            Width = 420,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var mod = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX) ? "Cmd" : "Ctrl";

        var hotkeys = new (string Key, string Description)[]
        {
            ($"{mod}+O", "Open File"),
            ($"{mod}+Shift+O", "Open Folder"),
            ($"{mod}+S", "Save"),
            ($"{mod}+Shift+S", "Save As"),
            ($"{mod}+Z", "Undo"),
            ($"{mod}+L", "Rotate Left"),
            ($"{mod}+R", "Rotate Right"),
            ($"{mod}+C", "Crop Mode"),
            ($"{mod}+Shift+A", "Auto Color"),
            ("Left / Right", "Navigate Images"),
            ("Enter", "Apply Crop"),
            ("Escape", "Cancel Current Mode"),
            ("F1", "Show This Help"),
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
                    MaxHeight = 340
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

    private async Task<string?> ShowSaveFileDialog(string defaultName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image As",
            SuggestedFileName = defaultName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                new FilePickerFileType("WebP") { Patterns = new[] { "*.webp" } }
            }
        });
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
            CanResize = false
        };

        bool result = false;
        var yesButton = new Button { Content = "Yes", Width = 80 };
        var noButton = new Button { Content = "No", Width = 80 };

        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        noButton.Click += (_, _) => { result = false; dialog.Close(); };

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
}
