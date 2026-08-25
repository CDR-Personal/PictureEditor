using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
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

    // Open MainWindows in the order they were opened. Index 0 == window "1".
    private static readonly List<MainWindow> _openWindows = new();

    public static void RegisterWindow(MainWindow window)
    {
        if (_openWindows.Contains(window)) return;
        _openWindows.Add(window);
        RenumberWindows();
    }

    public static void UnregisterWindow(MainWindow window)
    {
        if (_openWindows.Remove(window))
            RenumberWindows();
    }

    private static void RenumberWindows()
    {
        for (int i = 0; i < _openWindows.Count; i++)
        {
            if (_openWindows[i].DataContext is MainWindowViewModel vm)
                vm.WindowNumber = i + 1;
        }
    }

    /// <summary>
    /// True when <paramref name="window"/> is the only one still open. Windows are
    /// unregistered on Closed, so a window asking this from OnClosing still counts itself.
    /// </summary>
    public static bool IsLastWindow(MainWindow window) =>
        _openWindows.Count == 1 && _openWindows[0] == window;

    /// <summary>Brings the Nth open window (1-based) to the front and focuses it.</summary>
    public static void ActivateWindow(int number)
    {
        if (number < 1 || number > _openWindows.Count) return;
        var w = _openWindows[number - 1];
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Activate();
        w.Focus();
    }

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
                var active = desktop.Windows.FirstOrDefault(w => w.IsActive);
                CreateNewWindow(filePath, active);
            });

            // A file launch ("Open With", a path argument) means "show me this image" —
            // it opens a single window and leaves any saved layout untouched.
            var layout = startupPath == null ? SessionLayout.Load() : null;

            if (layout != null)
            {
                RestoreLayout(desktop, layout);
            }
            else
            {
                var vm = new MainWindowViewModel();
                if (startupPath != null)
                    vm.StartupFilePath = startupPath;

                desktop.MainWindow = new MainWindow { DataContext = vm };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates a new editor window, optionally loading the given file or directory.
    /// </summary>
    public static MainWindow CreateNewWindow(string? filePath = null, Window? sourceWindow = null)
    {
        var vm = new MainWindowViewModel();
        if (filePath != null)
            vm.StartupFilePath = filePath;

        var window = new MainWindow { DataContext = vm };
        window.Show();

        if (sourceWindow != null)
            PositionNearWindow(window, sourceWindow);

        return window;
    }

    private static void PositionNearWindow(Window newWindow, Window source)
    {
        const int cascadeOffset = 30;
        var screen = source.Screens.ScreenFromWindow(source);
        if (screen == null) return;

        var work = screen.WorkingArea;
        var scale = screen.Scaling;
        var physW = (int)(newWindow.ClientSize.Width * scale);
        var physH = (int)(newWindow.ClientSize.Height * scale);

        int x = Math.Max(work.X, Math.Min(source.Position.X + cascadeOffset, work.Right - physW));
        int y = Math.Max(work.Y, Math.Min(source.Position.Y + cascadeOffset, work.Bottom - physH));

        newWindow.Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// Reopens the windows saved by the last "Save current layout?" — geometry, monitor,
    /// directory, sort order and current image each.
    /// </summary>
    private static void RestoreLayout(IClassicDesktopStyleApplicationLifetime desktop, SessionLayout layout)
    {
        var entries = layout.Windows;

        // A window whose directory has gone away is dropped — unless it is the only one,
        // where the app falls back to normal operation and lets the user pick a folder.
        if (entries.Count > 1)
            entries = entries.Where(l => l.Directory == null || Directory.Exists(l.Directory)).ToList();

        if (entries.Count == 0)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            return;
        }

        // Build every window up front: Screens is readable from a constructed window
        // before it is shown, and we need the monitor list to decide what to show.
        var pending = entries
            .Select(l => (Layout: l, Window: new MainWindow
            {
                DataContext = new MainWindowViewModel { RestoreState = l }
            }))
            .ToList();

        var screens = pending[0].Window.Screens;

        var ready = new List<(WindowLayout Layout, MainWindow Window)>();
        var homeless = new List<(WindowLayout Layout, MainWindow Window)>();
        foreach (var item in pending)
            (FindScreen(screens, item.Layout) != null ? ready : homeless).Add(item);

        // Show in saved order — RegisterWindow fires on Opened, so show order is what
        // assigns the 1-9 window numbers.
        foreach (var (l, w) in ready)
        {
            w.ApplyLayout(l);
            w.Show();
            w.ApplyLayoutAfterShow(l);
        }

        if (ready.Count > 0)
            desktop.MainWindow = ready[0].Window;

        if (homeless.Count == 0)
            return;

        // Their monitors are gone. Ask before dropping them onto the main display,
        // deferred so the windows above are on screen first. Explicit shutdown while we
        // do this, so a confirmation dialog closing cannot be mistaken for the last
        // window closing when nothing was restorable.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Dispatcher.UIThread.Post(async () =>
        {
            int cascade = 0;
            foreach (var (l, w) in homeless)
            {
                var owner = desktop.Windows.OfType<MainWindow>().FirstOrDefault();
                var name = l.Directory != null ? Path.GetFileName(l.Directory) : null;
                var subject = string.IsNullOrEmpty(name) ? "A saved window" : $"The window showing \"{name}\"";

                var restore = await ShowConfirmDialog(owner, "Restore Window",
                    $"{subject} was on a monitor that is no longer connected.\n\n" +
                    "Restore it on the main display?");

                if (!restore)
                {
                    (w.DataContext as IDisposable)?.Dispose();
                    continue;
                }

                // Position only once it is on screen — its saved coordinates point at a
                // monitor that no longer exists.
                w.ApplyLayout(l, applyPosition: false);
                w.Show();
                PositionOnPrimary(w, l, cascade++);
                w.ApplyLayoutAfterShow(l, applyPosition: false);
                desktop.MainWindow ??= w;
            }

            // Never end up with nothing open — that would exit the app immediately.
            if (!desktop.Windows.OfType<MainWindow>().Any())
                desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };

            desktop.MainWindow?.Show();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        });
    }

    /// <summary>
    /// The monitor a saved window belongs to — matched by name where the platform
    /// reports one, otherwise by exact bounds. Null when it is no longer connected.
    /// </summary>
    private static Screen? FindScreen(Screens screens, WindowLayout layout)
    {
        // No monitor was recorded (layout written before this existed, or the window
        // wasn't on any screen) — treat the main display as its home rather than asking.
        if (string.IsNullOrEmpty(layout.ScreenName) && layout.ScreenWidth == 0)
            return screens.Primary ?? screens.All.FirstOrDefault();

        var all = screens.All;

        if (!string.IsNullOrEmpty(layout.ScreenName))
        {
            var byName = all.Where(s => s.DisplayName == layout.ScreenName).ToList();
            if (byName.Count == 1) return byName[0];
            // Identical names (a matched pair of displays) — bounds break the tie.
            if (byName.Count > 1) return byName.FirstOrDefault(s => BoundsMatch(s, layout)) ?? byName[0];
        }

        return all.FirstOrDefault(s => BoundsMatch(s, layout));
    }

    private static bool BoundsMatch(Screen screen, WindowLayout layout) =>
        screen.Bounds.X == layout.ScreenX && screen.Bounds.Y == layout.ScreenY &&
        screen.Bounds.Width == layout.ScreenWidth && screen.Bounds.Height == layout.ScreenHeight;

    /// <summary>
    /// Places a window whose original monitor is gone fully inside the main display,
    /// cascading when several land there at once.
    /// </summary>
    private static void PositionOnPrimary(MainWindow window, WindowLayout layout, int cascadeIndex)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen == null) return;

        var work = screen.WorkingArea;
        var scale = screen.Scaling;
        var physW = (int)(layout.Width * scale);
        var physH = (int)(layout.Height * scale);
        var inset = 30 * (cascadeIndex + 1);

        int x = Math.Max(work.X, Math.Min(work.X + inset, work.Right - physW));
        int y = Math.Max(work.Y, Math.Min(work.Y + inset, work.Bottom - physH));

        window.Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// Asks whether to remember the windows that are open right now. Yes records them all;
    /// No forgets any layout saved on an earlier run, so the next launch starts fresh.
    /// </summary>
    internal static async Task PromptSaveLayout(Window owner)
    {
        var windows = (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.OfType<MainWindow>().ToList();
        if (windows == null || windows.Count == 0) return;

        var save = await ShowConfirmDialog(owner, "Save Layout", "Save current layout?");

        if (save)
            new SessionLayout { Windows = windows.Select(w => w.CaptureLayout()).ToList() }.Save();
        else
            SessionLayout.Clear();
    }

    private bool _shutdownConfirmed;

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (_shutdownConfirmed) return;

        var mainWindows = desktop.Windows.OfType<MainWindow>().ToList();
        var unsavedCount = mainWindows.Count(
            w => w.DataContext is MainWindowViewModel vm && vm.HasUnsavedChanges);

        if (mainWindows.Count == 0) return;

        // There is always a dialog to show now, so cancel this pass and re-issue the
        // shutdown once the user has answered.
        e.Cancel = true;

        var owner = mainWindows.FirstOrDefault(w => w.IsActive) ?? mainWindows.First();

        // A single window with no unsaved edits has nothing to warn about — it goes
        // straight to the layout question.
        if (mainWindows.Count > 1 || unsavedCount > 0)
        {
            // Unsaved edits take priority over the multi-window notice.
            string message = unsavedCount switch
            {
                1 => "You have unsaved changes. Quit without saving?",
                > 1 => $"{unsavedCount} windows have unsaved changes. Quit without saving?",
                _ => $"There are {mainWindows.Count} windows open. Quit the application?"
            };

            if (!await ShowConfirmDialog(owner, "Quit Cedar Image Editor", message))
                return; // quit abandoned — nothing recorded
        }

        // Captured while every window is still open and undisposed.
        await PromptSaveLayout(owner);

        // Confirmation handled centrally — keep each window from prompting again
        // as Shutdown() closes them one by one.
        foreach (var w in mainWindows)
            w.SuppressCloseConfirmation();
        _shutdownConfirmed = true;
        desktop.Shutdown();
    }

    private static async Task<bool> ShowConfirmDialog(Window? owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 350,
            MinHeight = 150,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
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

        if (owner != null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            // No window on screen yet (restoring a layout whose monitors are all gone),
            // so there is nothing to be modal to.
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

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
