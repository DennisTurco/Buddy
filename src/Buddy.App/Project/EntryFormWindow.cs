using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core.Vault;

using Buddy.App.Chat;

namespace Buddy.App.Project;

/// <summary>
/// "＋" on a board column: an appunto (title, details, tags) or a card for Da fare (with "⏸ In attesa" for a task on
/// hold) or Da controllare, with the same fields Claude writes (priority, estimate, Fatto quando, Come testare…). The
/// result is turned into the note's format by <see cref="BoardEntries"/>. "✏ Modifica" on a card opens the same form
/// filled in with the item (<see cref="BoardEdits"/>), with "Salva" instead of "Aggiungi".
/// </summary>
public sealed class EntryFormWindow : Window
{
    private readonly string _kind;
    private readonly bool _editing;
    private readonly CheckBox _waiting = new() { Content = "⏸ In attesa: deciso, ma non per ora", Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _waitingFields = new() { Spacing = 2 };
    private readonly TextBox _note = Box("Il testo dopo « — » nella voce (es. Dubbio: …)");
    private readonly TextBox _title = Box("Titolo breve");

    internal TextBox TitleBox => _title;

    internal TextBox DoneWhenBox => _doneWhen;

    internal IReadOnlyList<ToggleButton> TagButtons => _tagButtons;
    private readonly TextBox _details = Box("Scrivi liberamente: ogni riga diventa una riga dell'appunto", multiline: true);
    private readonly TextBox _id;
    private readonly ComboBox _priority = new() { ItemsSource = BoardEntries.Priorities, SelectedIndex = 1, MinWidth = 80 };

    /// <summary>"—" in the priority list when editing: an item without priority keeps having none.</summary>
    private const string NoPriority = "—";
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

    /// <param name="kind">The form: appunti, todo, attesa (todo with "In attesa" ticked) or controllare.</param>
    /// <param name="nextId">The id used when the ID field is left empty.</param>
    /// <param name="knownTags">The common tags plus those already used on the board.</param>
    /// <param name="existing">When editing: the item as an entry (<see cref="BoardEdits.ToEntry"/>), shown in the fields.</param>
    /// <param name="canWait">Whether "In attesa" is offered (not for an idea of Da valutare).</param>
    public EntryFormWindow(string kind, string nextId, IEnumerable<string> knownTags, NewEntry? existing = null, bool canWait = true)
    {
        _kind = kind == "attesa" ? "todo" : kind;
        _editing = existing is not null;
        _id = Box($"Vuoto = {nextId} (o la chiave del tracker)");
        var editName = existing is { Id.Length: > 0 } ? existing.Id : existing?.Title;
        Title = (_editing, kind) switch
        {
            (true, _) => $"Modifica {editName}",
            (_, "appunti") => "Nuovo appunto",
            (_, "attesa") => "Nuovo task in attesa ⏸",
            (_, "controllare") => "Nuova voce da controllare",
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
        if (existing is { Note.Length: > 0 })
        {
            Add(form, "Nota", _note);
        }

        switch (_kind)
        {
            case "appunti":
                Add(form, "Dettagli", _details);
                break;
            case "todo":
                if (canWait)
                {
                    form.Children.Add(_waiting);
                    Add(_waitingFields, "In attesa di", _waitingFor);
                    Add(_waitingFields, "Oppure fino al", _until);
                    _waitingFields.Children.Add(new TextBlock { Classes = { "hint" }, Text = "Se scrivi cosa aspetta, la data non viene usata." });
                    form.Children.Add(_waitingFields);
                    _waitingFields.IsVisible = false;
                    _waiting.IsCheckedChanged += (_, _) => _waitingFields.IsVisible = _waiting.IsChecked == true;
                    if (kind == "attesa")
                    {
                        _priority.SelectedIndex = 2;
                        _waiting.IsChecked = true;
                    }
                }

                Add(form, "Priorità e stima", Pair(_priority, _estimate));
                Add(form, "Fatto quando", _doneWhen);
                Add(form, "Contesto", _context);
                Add(form, "Dove", _where);
                Add(form, "Assegnato da", _assignedBy);
                Add(form, "Tipo", _type);
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

        var create = new Button { Content = _editing ? "Salva" : "Aggiungi", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Annulla", IsCancel = true };
        create.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(create);
        right.Children.Add(cancel);
        if (_editing)
        {
            // Everything the form does not show (Accordi, Costo, links…) stays as it is; the text is there for the rest.
            var raw = new Button { Content = Icons.Label("📝 Modifica il testo"), Classes = { "small" }, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(raw, "Apre la voce come testo markdown, con tutte le sue righe");
            raw.Click += (_, _) =>
            {
                RawRequested = true;
                Close();
            };
            DockPanel.SetDock(raw, Dock.Left);
            buttons.Children.Add(raw);
        }

        buttons.Children.Add(right);

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
        if (existing is not null)
        {
            Fill(existing);
        }
    }

    /// <summary>"📝 Modifica il testo" was chosen: the caller opens the item as markdown instead.</summary>
    public bool RawRequested { get; private set; }

    private void Fill(NewEntry entry)
    {
        _id.Text = entry.Id;
        _id.IsReadOnly = true;
        _title.Text = entry.Title;
        _note.Text = entry.Note;
        _details.Text = entry.Details;
        _priority.ItemsSource = BoardEntries.Priorities.Prepend(NoPriority).ToList();
        _priority.SelectedItem = BoardEntries.Priorities.Contains(entry.Priority) ? entry.Priority : NoPriority;
        _estimate.Text = entry.Estimate;
        _type.Text = entry.Type;
        _context.Text = entry.Context;
        _where.Text = entry.Where;
        _doneWhen.Text = entry.DoneWhen;
        _assignedBy.Text = entry.AssignedBy;
        _waiting.IsChecked = entry.Waiting;
        _waitingFor.Text = entry.WaitingFor;
        if (entry.Until is { } until)
        {
            _until.SelectedDate = until.ToDateTime(TimeOnly.MinValue);
        }

        _changed.Text = entry.Changed;
        _files.Text = entry.Files;
        _howToTest.Text = entry.HowToTest;
        _commit.Text = entry.Commit;
        foreach (var button in _tagButtons)
        {
            button.IsChecked = entry.Tags.Contains((string)button.Content!, StringComparer.OrdinalIgnoreCase);
        }

        _otherTags.Text = string.Join(", ", entry.Tags.Where(t => !_tagButtons.Any(b => string.Equals((string)b.Content!, t, StringComparison.OrdinalIgnoreCase))));
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
        Priority = _priority.SelectedItem as string is { } priority ? (priority == NoPriority ? "" : priority) : _editing ? "" : "P2",
        Note = Text(_note),
        Estimate = Text(_estimate),
        Type = Text(_type),
        Context = Text(_context),
        Where = Text(_where),
        DoneWhen = _doneWhen.Text ?? "",
        AssignedBy = Text(_assignedBy),
        Waiting = _kind == "todo" && _waiting.IsChecked == true,
        WaitingFor = _waiting.IsChecked == true ? Text(_waitingFor) : "",
        Until = _waiting.IsChecked == true && _until.SelectedDate is { } until ? DateOnly.FromDateTime(until) : null,
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
