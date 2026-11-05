using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Buddy.App.Settings;

/// <summary>Small dialogs built in code: "type a name", a message, file and folder pickers.</summary>
public static class Dialogs
{
    /// <summary>Asks for a line of text; null if cancelled or left empty.</summary>
    public static async Task<string?> AskAsync(Window? owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 10) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", IsCancel = true, Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = FormWindow(title, panel, owner);
        var confirmed = false;
        ok.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        await Show(window, owner);
        return confirmed && box.Text?.Trim() is { Length: > 0 } name ? name : null;
    }

    /// <summary>A yes/no question; true only for <paramref name="yes"/>.</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, string message, string yes = "Sì", string title = "Buddy")
    {
        var ok = new Button { Content = yes, Margin = new Thickness(0, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", IsDefault = true, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(buttons);

        var window = FormWindow(title, panel, owner);
        var confirmed = false;
        ok.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        await Show(window, owner);
        return confirmed;
    }

    public static async Task ShowMessageAsync(Window? owner, string message, string title = "Buddy")
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(ok);

        var window = FormWindow(title, panel, owner);
        ok.Click += (_, _) => window.Close();
        await Show(window, owner);
    }

    public static async Task<string?> PickFolderAsync(Window owner, string? current)
    {
        var options = new FolderPickerOpenOptions { AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(current);
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickFileAsync(Window owner, string? current, string filterName, params string[] patterns)
    {
        var options = new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        };
        var folder = string.IsNullOrWhiteSpace(current) ? null : Path.GetDirectoryName(current);
        if (folder is not null && Directory.Exists(folder))
        {
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static Window FormWindow(string title, Control content, Window? owner)
    {
        var window = new Window
        {
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.Height,
            Width = 380,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Topmost = true,
        };
        window.Classes.Add("form");
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };
        return window;
    }

    /// <summary>Modal on the owner when there is one; otherwise a normal window awaited until closed.</summary>
    private static Task Show(Window window, Window? owner)
    {
        if (owner is not null)
        {
            return window.ShowDialog(owner);
        }

        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        return closed.Task;
    }
}
