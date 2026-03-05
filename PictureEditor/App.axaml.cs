using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.IO;
using System.Linq;
using Avalonia.Markup.Xaml;
using PictureEditor.ViewModels;
using PictureEditor.Views;

namespace PictureEditor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            var vm = new MainWindowViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = vm,
            };
            desktop.MainWindow = mainWindow;

            // Handle command-line arguments: file or directory path
            if (desktop.Args is { Length: > 0 })
            {
                var path = desktop.Args[0];
                mainWindow.Opened += async (_, _) =>
                {
                    if (File.Exists(path))
                        await vm.LoadFile(path);
                    else if (Directory.Exists(path))
                    {
                        var images = Services.ImageEditorService.GetImagesInDirectory(path);
                        if (images.Count > 0)
                            await vm.LoadFile(images[0]);
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
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
