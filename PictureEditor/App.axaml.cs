using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using PictureEditor.Services;
using PictureEditor.ViewModels;
using PictureEditor.Views;

namespace PictureEditor;

public partial class App : Application
{
    private static App? _instance;

    /// <summary>
    /// Tracks which ViewModel (if any) is currently in continuous/slideshow mode.
    /// Only one window may be in slideshow mode at a time.
    /// </summary>
    public static MainWindowViewModel? ContinuousModeOwner { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Name = "Cedar Image Editor";
        _instance = this;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.ShutdownRequested += OnShutdownRequested;

            string? startupPath = null;
            if (desktop.Args is { Length: > 0 })
            {
                var path = desktop.Args[0];
                if (File.Exists(path) || Directory.Exists(path))
                    startupPath = path;
            }

            // Register macOS Apple Event handler for "Open With" from Finder
            MacOSFileOpen.Register(filePath =>
            {
                CreateNewWindow(filePath);
            });

            var vm = new MainWindowViewModel();
            if (startupPath != null)
                vm.StartupFilePath = startupPath;

            var mainWindow = new MainWindow { DataContext = vm };
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates a new editor window, optionally loading the given file or directory.
    /// </summary>
    public static MainWindow CreateNewWindow(string? filePath = null)
    {
        var vm = new MainWindowViewModel();
        if (filePath != null)
            vm.StartupFilePath = filePath;

        var window = new MainWindow { DataContext = vm };
        window.Show();
        return window;
    }

    private bool _shutdownConfirmed;

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var openWindows = desktop.Windows.OfType<MainWindow>().Count();
        if (openWindows <= 1 || _shutdownConfirmed) return;

        e.Cancel = true;

        var owner = desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive)
                    ?? desktop.Windows.OfType<MainWindow>().First();

        var confirmed = await ShowConfirmDialog(owner, "Quit Cedar Image Editor",
            $"There are {openWindows} windows open. Quit the application?");

        if (confirmed)
        {
            _shutdownConfirmed = true;
            desktop.Shutdown();
        }
    }

    private static async Task<bool> ShowConfirmDialog(Window owner, string title, string message)
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
            if (e.Key == Avalonia.Input.Key.Y) { result = true; dialog.Close(); e.Handled = true; }
            else if (e.Key is Avalonia.Input.Key.N or Avalonia.Input.Key.Escape) { result = false; dialog.Close(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Return) { result = true; dialog.Close(); e.Handled = true; }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 15,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { yesButton, noButton }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    private void OnAboutClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var owner = desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive)
                    ?? desktop.Windows.OfType<MainWindow>().FirstOrDefault();
        if (owner == null) return;

        var exePath = Environment.ProcessPath
            ?? Assembly.GetExecutingAssembly().Location;
        var buildDate = !string.IsNullOrEmpty(exePath)
            ? File.GetLastWriteTime(exePath)
            : (DateTime?)null;

        var dialog = new Window
        {
            Title = "About Cedar Image Editor",
            Width = 350,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var closeButton = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        closeButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "Cedar Image Editor",
                    FontSize = 20,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = buildDate.HasValue
                        ? $"Built: {buildDate:MMMM d, yyyy h:mm tt}"
                        : "Build date unavailable",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Opacity = 0.7
                },
                closeButton
            }
        };

        dialog.ShowDialog(owner);
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
