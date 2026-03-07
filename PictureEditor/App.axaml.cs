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
using Avalonia.Markup.Xaml;
using PictureEditor.Services;
using PictureEditor.ViewModels;
using PictureEditor.Views;

namespace PictureEditor;

public partial class App : Application
{
    private MainWindowViewModel? _vm;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Name = "Cedar Image Editor";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            _vm = new MainWindowViewModel();

            // Handle command-line arguments: file or directory path
            if (desktop.Args is { Length: > 0 })
            {
                var path = desktop.Args[0];
                if (File.Exists(path))
                    _vm.StartupFilePath = path;
                else if (Directory.Exists(path))
                    _vm.StartupFilePath = path;
            }

            // Register macOS Apple Event handler for "Open With" from Finder
            MacOSFileOpen.Register(filePath =>
            {
                if (_vm.HasImage)
                    _ = _vm.LoadFile(filePath);
                else
                    _vm.StartupFilePath = filePath;
            });

            var mainWindow = new MainWindow
            {
                DataContext = _vm,
            };
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnAboutClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var owner = desktop.MainWindow;
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
