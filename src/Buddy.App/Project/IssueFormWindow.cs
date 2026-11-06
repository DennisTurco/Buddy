using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core.Tracker;

namespace Buddy.App.Project;

/// <summary>
/// "↗ Crea issue": the GitHub issue proposed from a card (title, body, labels), to check and change before it is
/// created in the repository.
/// </summary>
public sealed class IssueFormWindow : Window
{
    private readonly IssueDraft _draft;
    private IssueDraft? _result;

    public IssueFormWindow(string repository, IssueDraft draft)
    {
        _draft = draft;
        Title = "Crea issue su GitHub";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("form");

        TitleBox = new TextBox { Text = draft.Title };
        BodyBox = new TextBox { Text = draft.Body, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 220, VerticalContentAlignment = VerticalAlignment.Top };
        AssignBox = new CheckBox { Content = "Assegnala a me", IsChecked = draft.AssignToMe };
        var create = new Button { Content = "Crea issue", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Annulla", IsCancel = true };
        create.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close();
        TitleBox.TextChanged += (_, _) => create.IsEnabled = !string.IsNullOrWhiteSpace(TitleBox.Text);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                Confirm();
            }
        };

        var form = new StackPanel { Margin = new Thickness(18, 14), Spacing = 4 };
        form.Children.Add(new TextBlock
        {
            Classes = { "hint" },
            TextWrapping = TextWrapping.Wrap,
            Text = $"L'issue viene creata in {repository} con il tuo account di gh. La voce resta nella bacheca con il link all'issue, "
                   + "così non la reimporti.",
        });
        form.Children.Add(new TextBlock { Text = "Titolo", Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(TitleBox);
        form.Children.Add(new TextBlock { Text = "Descrizione (markdown)", Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(BodyBox);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };
        options.Children.Add(AssignBox);
        if (draft.Labels.Count > 0)
        {
            options.Children.Add(new TextBlock
            {
                Text = "Etichette: " + string.Join(", ", draft.Labels), Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0),
            });
        }

        form.Children.Add(options);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        form.Children.Add(buttons);
        Content = form;
    }

    internal TextBox TitleBox { get; }

    internal TextBox BodyBox { get; }

    internal CheckBox AssignBox { get; }

    /// <summary>The issue as written, or null if cancelled.</summary>
    public async Task<IssueDraft?> ShowAndWaitAsync(Window owner)
    {
        await ShowDialog(owner);
        return _result;
    }

    internal IssueDraft? Issue => string.IsNullOrWhiteSpace(TitleBox.Text)
        ? null
        : _draft with { Title = TitleBox.Text.Trim(), Body = BodyBox.Text?.Trim() ?? "", AssignToMe = AssignBox.IsChecked == true };

    private void Confirm()
    {
        if (Issue is { } issue)
        {
            _result = issue;
            Close();
        }
    }
}
