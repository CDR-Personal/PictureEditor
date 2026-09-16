using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Input;
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

    private static LayoutStore? _layouts;

    /// <summary>The user's named screen layouts, loaded once per run.</summary>
    internal static LayoutStore Layouts => _layouts ??= LayoutStore.Load();

    /// <summary>
    /// The named layout currently in use, or null when the windows on screen don't
    /// belong to one (Default, a file launch, or a layout that has been deleted).
    /// </summary>
    internal static string? ActiveLayoutName { get; private set; }

    private static bool NameEquals(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
                // A file arriving while the chooser is still up wins — the user asked
                // for that image, not for a layout.
                DismissLayoutChooser();
                var active = desktop.Windows.FirstOrDefault(w => w.IsActive);
                CreateNewWindow(filePath, active);
            });

            // A file launch ("Open With", a path argument) means "show me this image" —
            // it opens a single window and never asks about layouts.
            if (startupPath != null)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel { StartupFilePath = startupPath }
                };
            }
            else if (Layouts.Layouts.Count == 0)
            {
                // Nothing saved to choose between — start exactly as before named layouts.
                desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            }
            else
            {
                StartWithLayoutChooser(desktop);
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
    /// Asks which saved layout to open before anything is on screen, then opens it.
    /// </summary>
    private static void StartWithLayoutChooser(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // The chooser is the only window in existence, so closing it would otherwise read
        // as "last window closed" and end the app before an editor window ever appears.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var chosen = await ShowLayoutChooser(
                    null, "Choose a Screen Layout", "Choose a screen layout:", includeDefault: true);

                if (chosen != null)
                {
                    SetActiveLayout(chosen.Name);
                    await RestoreLayout(desktop, chosen.Windows);
                }
            }
            catch
            {
                // Whatever went wrong, the app must not be left running invisibly with
                // shutdown held open and no window to quit from.
            }
            finally
            {
                // Default, a dismissed chooser, or a layout whose folders have all gone.
                EnsureWindowOpen(desktop);
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
        });
    }

    /// <summary>Opens a plain window when nothing else is on screen — an empty app would exit.</summary>
    private static void EnsureWindowOpen(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.Windows.OfType<MainWindow>().Any()) return;

        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        desktop.MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Reopens a saved set of windows — geometry, monitor, directory, sort order and
    /// current image each. Callers are responsible for holding shutdown open while this
    /// runs and for opening a fallback window if it restores nothing.
    /// </summary>
    private static async Task RestoreLayout(
        IClassicDesktopStyleApplicationLifetime desktop, IReadOnlyList<WindowLayout> windows)
    {
        // A window whose directory has gone away is dropped — unless it is the only one,
        // where the app falls back to normal operation and lets the user pick a folder.
        var entries = windows.Count > 1
            ? windows.Where(l => l.Directory == null || Directory.Exists(l.Directory)).ToList()
            : windows.ToList();

        if (entries.Count == 0)
            return;

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

        // Their monitors are gone. Ask before dropping them onto the main display, now
        // that the windows above are on screen to own the question.
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

    private static Window? _openChooser;

    /// <summary>
    /// Closes the startup chooser as if Default had been picked. Used when Finder sends
    /// an "Open With" event while the chooser is still waiting for an answer.
    /// </summary>
    private static void DismissLayoutChooser() => _openChooser?.Close();

    /// <summary>
    /// The numbered layout list. Returns the chosen layout, or null for Default when
    /// <paramref name="includeDefault"/> is set and for cancel when it is not.
    /// </summary>
    private static async Task<NamedLayout?> ShowLayoutChooser(
        Window? owner, string title, string heading, bool includeDefault)
    {
        var layouts = Layouts.Layouts.ToList();
        if (layouts.Count == 0) return null;

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            CanResize = false,
            Focusable = true
        };

        NamedLayout? result = null;
        var group = new StackPanel { Spacing = 6 };
        var choices = new List<RadioButton>();

        void AddChoice(string label, NamedLayout? value)
        {
            var rb = new RadioButton { Content = label, GroupName = "Layout", Tag = value };
            group.Children.Add(rb);
            choices.Add(rb);
        }

        // Default is key 0 so the user's own layouts keep the 1-9 numbering either way.
        if (includeDefault)
            AddChoice("0 - Default (start fresh)", null);

        for (int i = 0; i < layouts.Count; i++)
            AddChoice($"{i + 1} - {layouts[i].Name}", layouts[i]);

        var preferred = choices.FirstOrDefault(c => c.Tag is NamedLayout n && NameEquals(n.Name, Layouts.LastUsed))
                        ?? choices.FirstOrDefault(c => c.Tag is NamedLayout)
                        ?? choices[0];
        preferred.IsChecked = true;

        void Accept(NamedLayout? value)
        {
            result = value;
            dialog.Close();
        }

        void AcceptChecked() =>
            Accept(choices.FirstOrDefault(c => c.IsChecked == true)?.Tag as NamedLayout);

        var okButton = new Button { Content = "_OK", Width = 80 };
        var cancelButton = new Button { Content = includeDefault ? "_Default" : "_Cancel", Width = 80 };

        okButton.Click += (_, _) => AcceptChecked();
        cancelButton.Click += (_, _) => Accept(null);

        dialog.KeyDown += (_, e) =>
        {
            var digit = DigitFromKey(e.Key);
            if (digit == 0)
            {
                // Only meaningful where Default is on the list; ignored otherwise.
                if (includeDefault) Accept(null);
                e.Handled = true;
            }
            else if (digit > 0)
            {
                if (digit <= layouts.Count) Accept(layouts[digit - 1]);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { Accept(null); e.Handled = true; }
            else if (e.Key == Key.Return) { AcceptChecked(); e.Handled = true; }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = heading,
                    FontSize = 15,
                    FontWeight = FontWeight.Bold,
                    TextWrapping = TextWrapping.Wrap
                },
                group,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { okButton, cancelButton }
                }
            }
        };

        dialog.Opened += (_, _) => preferred.Focus();

        _openChooser = dialog;
        try
        {
            await ShowDialogOrWindow(dialog, owner);
        }
        finally
        {
            _openChooser = null;
        }

        return result;
    }

    /// <summary>The digit a key stands for, across the number row and the keypad; -1 for anything else.</summary>
    private static int DigitFromKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => -1
    };

    private static List<MainWindow> OpenWindows() =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.Windows.OfType<MainWindow>().ToList() ?? new List<MainWindow>();

    /// <summary>
    /// Stores <paramref name="captured"/> under a new name, replacing an existing layout
    /// when all nine slots are taken. Returns the name used, or null if the user backed out.
    /// </summary>
    private static async Task<string?> SaveAsNewLayout(Window? owner, List<WindowLayout> captured)
    {
        if (Layouts.IsFull)
        {
            var replace = await ShowLayoutChooser(owner, "Replace a Layout",
                $"All {LayoutStore.MaxLayouts} layout slots are in use.\nChoose one to replace:",
                includeDefault: false);

            if (replace == null) return null;

            replace.Windows = captured;
            Layouts.Save();
            return replace.Name;
        }

        while (true)
        {
            var entered = await ShowTextInputDialog(owner, "Save Layout", "Name for this layout:", "");
            if (string.IsNullOrWhiteSpace(entered)) return null;

            var name = entered.Trim();
            if (name.Length > LayoutStore.MaxNameLength)
                name = name[..LayoutStore.MaxNameLength];

            if (Layouts.IsNameTaken(name))
            {
                await ShowInfoDialog(owner, "Name Already Used",
                    $"There is already a layout called \"{name}\".\n\nPlease choose a different name.");
                continue;
            }

            Layouts.Layouts.Add(new NamedLayout { Name = name, Windows = captured });
            Layouts.Save();
            return name;
        }
    }

    private static void SetActiveLayout(string? name)
    {
        ActiveLayoutName = name;
        if (name != null)
            Layouts.LastUsed = name;
        Layouts.Save();
    }

    /// <summary>
    /// The layout question asked on the way out: update the layout in use, or offer to
    /// save the current arrangement as a new one. Answering No writes nothing at all.
    /// </summary>
    internal static async Task PromptSaveLayout(Window owner)
    {
        var windows = OpenWindows();
        if (windows.Count == 0) return;

        // Captured while every window is still open and undisposed.
        var captured = windows.Select(w => w.CaptureLayout()).ToList();
        var active = Layouts.Find(ActiveLayoutName);

        if (active != null)
        {
            if (await ShowConfirmDialog(owner, "Update Layout", $"Update current layout ({active.Name})?"))
            {
                active.Windows = captured;
                SetActiveLayout(active.Name);
            }
            return;
        }

        if (!await ShowConfirmDialog(owner, "Save Layout", "Save this layout?"))
            return;

        var saved = await SaveAsNewLayout(owner, captured);
        if (saved != null)
            SetActiveLayout(saved);
    }

    // --- File > Layouts menu ------------------------------------------------

    /// <summary>False, having said so, when there is nothing for the menu to act on.</summary>
    private static async Task<bool> RequireSavedLayouts(Window owner, string title)
    {
        if (Layouts.Layouts.Count > 0) return true;

        await ShowInfoDialog(owner, title,
            "No layouts have been saved yet.\n\nUse \"Save Layout As...\" to create one.");
        return false;
    }

    /// <summary>Saves the windows on screen under a new name and makes it the layout in use.</summary>
    internal static async Task SaveLayoutAs(Window owner)
    {
        var windows = OpenWindows();
        if (windows.Count == 0) return;

        var saved = await SaveAsNewLayout(owner, windows.Select(w => w.CaptureLayout()).ToList());
        if (saved == null) return;

        SetActiveLayout(saved);
        await ShowInfoDialog(owner, "Layout Saved", $"Saved as \"{saved}\".");
    }

    /// <summary>Asks which layout to open, then swaps the windows on screen for it.</summary>
    internal static async Task SwitchLayout(Window owner)
    {
        if (!await RequireSavedLayouts(owner, "Switch Layout")) return;

        var chosen = await ShowLayoutChooser(owner, "Switch Layout", "Open which layout?", includeDefault: false);
        if (chosen != null)
            await SwitchToLayout(owner, chosen);
    }

    /// <summary>Closes everything on screen and opens <paramref name="chosen"/> in its place.</summary>
    internal static async Task SwitchToLayout(Window owner, NamedLayout chosen)
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (NameEquals(chosen.Name, ActiveLayoutName)) return;

        var windows = OpenWindows();

        // Offer to keep the layout being left behind, the same question quitting asks.
        var active = Layouts.Find(ActiveLayoutName);
        if (active != null &&
            await ShowConfirmDialog(owner, "Update Layout", $"Update current layout ({active.Name})?"))
        {
            active.Windows = windows.Select(w => w.CaptureLayout()).ToList();
            Layouts.Save();
        }

        var unsavedCount = windows.Count(
            w => w.DataContext is MainWindowViewModel vm && vm.HasUnsavedChanges);
        if (unsavedCount > 0)
        {
            var message = unsavedCount == 1
                ? "You have unsaved changes. Switch layouts without saving?"
                : $"{unsavedCount} windows have unsaved changes. Switch layouts without saving?";
            if (!await ShowConfirmDialog(owner, "Switch Layout", message))
                return;
        }

        SetActiveLayout(chosen.Name);

        // Closing every window at once would end the app under OnLastWindowClose before
        // the new layout has anything on screen.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        foreach (var w in windows)
        {
            // Both questions have been answered above — don't let each window re-ask.
            w.SuppressCloseConfirmation();
            w.Close();
        }

        await RestoreLayout(desktop, chosen.Windows);
        EnsureWindowOpen(desktop);
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    internal static async Task RenameLayout(Window owner)
    {
        if (!await RequireSavedLayouts(owner, "Rename Layout")) return;

        var target = await ShowLayoutChooser(owner, "Rename Layout", "Rename which layout?", includeDefault: false);
        if (target == null) return;

        while (true)
        {
            var entered = await ShowTextInputDialog(owner, "Rename Layout", "New name:", target.Name);
            if (string.IsNullOrWhiteSpace(entered)) return;

            var name = entered.Trim();
            if (name.Length > LayoutStore.MaxNameLength)
                name = name[..LayoutStore.MaxNameLength];

            // Re-typing the same name (or just its casing) isn't a collision with itself.
            if (!NameEquals(name, target.Name) && Layouts.IsNameTaken(name))
            {
                await ShowInfoDialog(owner, "Name Already Used",
                    $"There is already a layout called \"{name}\".\n\nPlease choose a different name.");
                continue;
            }

            if (NameEquals(ActiveLayoutName, target.Name)) ActiveLayoutName = name;
            if (NameEquals(Layouts.LastUsed, target.Name)) Layouts.LastUsed = name;
            target.Name = name;
            Layouts.Save();
            return;
        }
    }

    internal static async Task DeleteLayout(Window owner)
    {
        if (!await RequireSavedLayouts(owner, "Delete Layout")) return;

        var target = await ShowLayoutChooser(owner, "Delete Layout", "Delete which layout?", includeDefault: false);
        if (target == null) return;

        if (!await ShowConfirmDialog(owner, "Delete Layout",
                $"Delete the layout \"{target.Name}\"?\n\nThe windows open now are not affected."))
            return;

        Layouts.Layouts.Remove(target);
        if (NameEquals(ActiveLayoutName, target.Name)) ActiveLayoutName = null;
        if (NameEquals(Layouts.LastUsed, target.Name)) Layouts.LastUsed = null;
        Layouts.Save();
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

    internal static async Task<bool> ShowConfirmDialog(Window? owner, string title, string message)
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

        await ShowDialogOrWindow(dialog, owner);
        return result;
    }

    /// <summary>
    /// Prompts for a line of text. <paramref name="owner"/> may be null, for the layout
    /// prompts that run before any editor window exists.
    /// </summary>
    internal static async Task<string?> ShowTextInputDialog(
        Window? owner, string title, string label, string defaultValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            CanResize = false,
            Focusable = true
        };

        string? result = null;
        var textBox = new TextBox
        {
            Text = defaultValue,
            SelectionStart = 0,
            // Selecting up to the extension keeps "Save As" renames quick; for a plain
            // value that length is the whole string, and for an empty one it is zero.
            SelectionEnd = Path.GetFileNameWithoutExtension(defaultValue).Length
        };

        var okButton = new Button { Content = "_OK", Width = 80 };
        var cancelButton = new Button { Content = "_Cancel", Width = 80 };

        void Submit() { result = textBox.Text; dialog.Close(); }

        okButton.Click += (_, _) => Submit();
        cancelButton.Click += (_, _) => dialog.Close();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Return) { Submit(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { dialog.Close(); e.Handled = true; }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = label },
                textBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { okButton, cancelButton }
                }
            }
        };

        dialog.Opened += (_, _) => textBox.Focus();

        await ShowDialogOrWindow(dialog, owner);
        return result;
    }

    /// <summary>A message with a single OK button, usable before any window exists.</summary>
    internal static async Task ShowInfoDialog(Window? owner, string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 350,
            MinHeight = 140,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            CanResize = false,
            Focusable = true
        };

        var okButton = new Button { Content = "_OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        okButton.Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key is Avalonia.Input.Key.Return or Avalonia.Input.Key.Escape)
            {
                dialog.Close();
                e.Handled = true;
            }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 15,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                okButton
            }
        };

        await ShowDialogOrWindow(dialog, owner);
    }

    /// <summary>
    /// Shows a dialog modally when there is something to be modal to, and as a plain
    /// window otherwise — the layout chooser and the restore prompts both run when the
    /// app has no window on screen yet.
    /// </summary>
    private static async Task ShowDialogOrWindow(Window dialog, Window? owner)
    {
        if (owner != null)
        {
            await dialog.ShowDialog(owner);
            return;
        }

        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        await closed.Task;
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
