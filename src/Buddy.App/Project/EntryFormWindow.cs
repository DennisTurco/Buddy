using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// "＋" on a board column: an appunto (title, details, tags) or a card for Da fare, In attesa or Da controllare,
/// with the same fields Claude writes (priority, estimate, Fatto quando, Come testare…). The result is turned into
/// the note's format by <see cref="BoardEntries"/>.
/// </summary>
public sealed class EntryFormWindow : Window
{
    private readonly string _kind;
    private readonly TextBox _title = Box("Titolo breve");

    internal TextBox TitleBox => _title;

    internal TextBox DoneWhenBox => _doneWhen;

    internal IReadOnlyList<ToggleButton> TagButtons => _tagButtons;
    private readonly TextBox _details = Box("Scrivi liberamente: ogni riga diventa una riga dell'appunto", multiline: true);
    private readonly TextBox _id;
    private readonly ComboBox _priority = new() { ItemsSource = BoardEntries.Priorities, SelectedIndex = 1, MinWidth = 80 };
    private readonly TextBox _estimate = new() { Text = "1h", Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _type = Box("Facoltativo: uno dei tipi della Scheda progetto");
    private readonly TextBox _context = Box("Link a una nota o una frase");
    private readonly TextBox _where = Box("File e punti di ingresso, se li sai");
    private readonly TextBox _doneWhen = Box("Risultato concreto e verificabile", multiline: true);
    private readonly TextBox _assignedBy = Box("Chi ha assegnato il task");
    private readonly TextBox _waitingFor = Box("Es. rilascio 2.0, risposta di Persona A");
    private readonly CalendarDatePicker _until = new() { SelectedDate = DateTime.Today.AddDays(14), Margin = new Thickness(0, 4) };
    private readonly TextBox _changed = Box("Cosa è cambiato", multiline: true);
    private readonly TextBox _files = Box("File toccati");
    private readonly TextBox _howToTest = Box("1. apri X  2. fai Y  3. deve succedere Z", multiline: true);
    private readonly TextBox _commit = Box("feat: …");
    private readonly TextBox _otherTags = Box("Altri tag, separati da virgola");
    private readonly List<ToggleButton> _tagButtons = [];
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private NewEntry? _result;

    /// <param name="kind">The column: appunti, todo, attesa or controllare.</param>
    /// <param name="nextId">The id used when the ID field is left empty.</param>
    /// <param name="knownTags">The common tags plus those already used on the board.</param>
    public EntryFormWindow(string kind, string nextId, IEnumerable<string> knownTags)
    {
        _kind = kind;
        _id = Box($"Vuoto = {nextId} (o la chiave del tracker)");
        Title = kind switch
        {
            "appunti" => "Nuovo appunto",
            "attesa" => "Nuovo task in attesa ⏸",
            "controllare" => "Nuova voce da controllare",
            _ => "Nuovo task",
        };
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 860;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("form");

        var form = new StackPanel { Spacing = 2 };
        if (kind != "appunti")
        {
            Add(form, "ID", _id);
        }

        Add(form, "Titolo *", _title);
        switch (kind)
        {
            case "appunti":
                Add(form, "Dettagli", _details);
                break;
            case "todo":
                Add(form, "Priorità e stima", Pair(_priority, _estimate));
                Add(form, "Fatto quando", _doneWhen);
                Add(form, "Contesto", _context);
                Add(form, "Dove", _where);
                Add(form, "Assegnato da", _assignedBy);
                Add(form, "Tipo", _type);
                break;
            case "attesa":
                _priority.SelectedIndex = 2;
                Add(form, "In attesa di", _waitingFor);
                Add(form, "Oppure fino al", _until);
                form.Children.Add(new TextBlock { Classes = { "hint" }, Text = "Se scrivi cosa aspetta, la data non viene usata." });
                Add(form, "Priorità e stima", Pair(_priority, _estimate));
                Add(form, "Fatto quando", _doneWhen);
                Add(form, "Contesto", _context);
                break;
            case "controllare":
                Add(form, "Cambiato", _changed);
                Add(form, "File", _files);
                Add(form, "Come testare", _howToTest);
                Add(form, "Commit", _commit);
                break;
        }

        var tags = new WrapPanel();
        foreach (var tag in knownTags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var button = new ToggleButton
            {
                Content = tag,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 3),
                FontSize = 12,
                CornerRadius = new CornerRadius(10),
                BorderBrush = Tags.Brush(tag),
                BorderThickness = new Thickness(2),
            };
            _tagButtons.Add(button);
            tags.Children.Add(button);
        }

        Add(form, "Tag", tags);
        form.Children.Add(_otherTags);
        form.Children.Add(_error);

        var create = new Button { Content = "Aggiungi", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Annulla", IsCancel = true };
        create.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(18, 14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = form, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                Confirm();
            }
        };
        Opened += (_, _) => _title.Focus();
    }

    /// <summary>What was filled in, or null if cancelled.</summary>
    public async Task<NewEntry?> ShowAndWaitAsync(Window owner)
    {
        await ShowDialog(owner);
        return _result;
    }

    /// <summary>The entry as filled in so far (also what "Aggiungi" returns).</summary>
    internal NewEntry Entry => new()
    {
        Id = Text(_id),
        Title = Text(_title),
        Details = _details.Text ?? "",
        Tags = _tagButtons.Where(b => b.IsChecked == true).Select(b => (string)b.Content!)
            .Concat(BoardEntries.ParseTags(_otherTags.Text ?? ""))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Priority = _priority.SelectedItem as string ?? "P2",
        Estimate = Text(_estimate),
        Type = Text(_type),
        Context = Text(_context),
        Where = Text(_where),
        DoneWhen = _doneWhen.Text ?? "",
        AssignedBy = Text(_assignedBy),
        WaitingFor = Text(_waitingFor),
        Until = _kind == "attesa" && _until.SelectedDate is { } until ? DateOnly.FromDateTime(until) : null,
        Changed = _changed.Text ?? "",
        Files = Text(_files),
        HowToTest = _howToTest.Text ?? "",
        Commit = Text(_commit),
    };

    internal void Confirm()
    {
        if (Text(_title).Length == 0)
        {
            _error.Text = "Scrivi un titolo.";
            _error.IsVisible = true;
            _title.Focus();
            return;
        }

        _result = Entry;
        Close();
    }

    private static string Text(TextBox box) => box.Text?.Trim() ?? "";

    private static TextBox Box(string placeholder, bool multiline = false) => new()
    {
        PlaceholderText = placeholder,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Height = multiline ? 80 : double.NaN,
        VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
    };

    private static void Add(Panel form, string label, Control control)
    {
        form.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(control);
    }

    private static StackPanel Pair(Control first, Control second)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4) };
        row.Children.Add(first);
        row.Children.Add(second);
        return row;
    }
}
