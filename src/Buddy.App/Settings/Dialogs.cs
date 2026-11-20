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

    /// <summary>One choice of <see cref="PickAsync"/>: what is shown, a muted second line (e.g. the folder), and its key.</summary>
    public sealed record Choice(string Text, string Detail, string Key)
    {
        public override string ToString() => Detail.Length > 0 ? $"{Text}   ·  {Detail}" : Text;
    }

    /// <summary>
    /// A list with a search box above it (type to filter, ↓ to the list, Enter or double click to choose);
    /// the chosen key, or null if cancelled.
    /// </summary>
    public static async Task<string?> PickAsync(Window? owner, string title, string label, IReadOnlyList<Choice> choices)
    {
        var box = new TextBox { PlaceholderText = "Cerca…", Margin = new Thickness(0, 6, 0, 6) };
        var list = new ListBox { Height = 280, ItemsSource = choices };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 10, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", IsCancel = true, Width = 80, Margin = new Thickness(0, 10, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(list);
        panel.Children.Add(buttons);

        var window = FormWindow(title, panel, owner);
        window.Width = 460;
        string? chosen = null;

        void Filter()
        {
            var words = (box.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shown = choices.Where(c => words.All(w => c.ToString().Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            list.ItemsSource = shown;
            list.SelectedIndex = shown.Count > 0 ? 0 : -1;
        }

        void Choose()
        {
            if (list.SelectedItem is Choice choice)
            {
                chosen = choice.Key;
                window.Close();
            }
        }

        box.TextChanged += (_, _) => Filter();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.ItemCount > 0)
            {
                list.Focus();
                e.Handled = true;
            }
        };
        list.DoubleTapped += (_, _) => Choose();
        ok.Click += (_, _) => Choose();
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) =>
        {
            Filter();
            box.Focus();
        };

        await Show(window, owner);
        return chosen;
    }

    /// <summary>
    /// One field of <see cref="FormAsync"/>: its label, starting text, and whether it takes several lines. With
    /// <paramref name="Choices"/> it is a drop-down of those values (plus an empty one: "not set"). <paramref name="Prepare"/>
    /// runs on the field's text box once created (e.g. to accept pasted images).
    /// </summary>
    public sealed record Field(string Label, string Initial = "", bool Multiline = false, bool Required = false,
        IReadOnlyList<string>? Choices = null, double Height = 140, Action<TextBox>? Prepare = null);

    /// <summary>
    /// A small form (title, then each field); the values in order, or null if cancelled or a required field is empty.
    /// Ctrl+Enter confirms from a multi-line field.
    /// </summary>
    public static async Task<string[]?> FormAsync(Window? owner, string title, params Field[] fields)
    {
        var controls = fields.Select(Control (f) => f.Choices is { } choices
            ? new ComboBox
            {
                ItemsSource = choices.Prepend("").ToList(),
                SelectedItem = choices.FirstOrDefault(c => c.Equals(f.Initial, StringComparison.CurrentCultureIgnoreCase)) ?? "",
                Margin = new Thickness(0, 4, 0, 10),
                MinWidth = 200,
            }
            : new TextBox
            {
                Text = f.Initial,
                Margin = new Thickness(0, 4, 0, 10),
                AcceptsReturn = f.Multiline,
                TextWrapping = f.Multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
                Height = f.Multiline ? f.Height : double.NaN,
                VerticalContentAlignment = f.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            }).ToArray();
        var boxes = controls.OfType<TextBox>().ToArray();
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Prepare is { } prepare && controls[i] is TextBox box)
            {
                prepare(box);
            }
        }

        string Value(int i) => controls[i] switch
        {
            TextBox box => box.Text?.Trim() ?? "",
            ComboBox combo => combo.SelectedItem as string ?? "",
            _ => "",
        };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", IsCancel = true, Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14), Width = 460 };
        for (var i = 0; i < fields.Length; i++)
        {
            panel.Children.Add(new TextBlock { Text = fields[i].Label + (fields[i].Required ? " *" : "") });
            panel.Children.Add(controls[i]);
        }

        // A long form (e.g. a person's card) scrolls instead of growing past the screen; the buttons stay visible.
        var form = new DockPanel();
        buttons.Margin = new Thickness(14, 0, 14, 14);
        DockPanel.SetDock(buttons, Dock.Bottom);
        form.Children.Add(buttons);
        form.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 620, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var window = FormWindow(title, form, owner);
        window.Width = 500;
        var confirmed = false;
        void Confirm()
        {
            if (fields.Select((f, i) => !f.Required || Value(i).Length > 0).All(ok => ok))
            {
                confirmed = true;
                window.Close();
            }
        }

        ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => window.Close();
        foreach (var box in boxes.Where(b => b.AcceptsReturn))
        {
            box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    Confirm();
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        window.Opened += (_, _) => controls.FirstOrDefault()?.Focus();
        await Show(window, owner);
        return confirmed ? fields.Select((_, i) => Value(i)).ToArray() : null;
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

    /// <summary>
    /// A question with check boxes under the message: their states when the user confirms with <paramref name="yes"/>,
    /// null when cancelled.
    /// </summary>
    public static async Task<bool[]?> ConfirmWithOptionsAsync(Window? owner, string message, IReadOnlyList<(string Label, bool Checked)> options,
        string yes = "OK", string title = "Buddy")
    {
        var boxes = options.Select(o => new CheckBox
        {
            Content = new TextBlock { Text = o.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            IsChecked = o.Checked,
        }).ToArray();
        var ok = new Button { Content = yes, IsDefault = true, Margin = new Thickness(0, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
        panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var box in boxes)
        {
            panel.Children.Add(box);
        }

        panel.Children.Add(buttons);
        var window = FormWindow(title, new ScrollViewer { Content = panel, MaxHeight = 640 }, owner);
        window.Width = 520;
        var confirmed = false;
        ok.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        await Show(window, owner);
        return confirmed ? boxes.Select(b => b.IsChecked == true).ToArray() : null;
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
