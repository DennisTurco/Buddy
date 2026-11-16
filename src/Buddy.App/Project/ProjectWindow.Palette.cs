using Avalonia.Input;
using Avalonia.Interactivity;
using Buddy.Core.Chat;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// Ctrl+K: one box to reach anything — the views and your pages, the people (and their cards), the notes, the cards of the board, the saved chat
/// conversations and the chat commands. Results are ranked by where the text matches (start of the title first).
/// </summary>
public partial class ProjectWindow
{
    private const int PaletteMax = 14;

    /// <summary>A chat command chosen in Ctrl+K (e.g. "/riapri …", "/checkin"): the app sends it and shows the chat.</summary>
    public event Action<string>? ChatCommandRequested;

    /// <summary>The chat's "/" commands, set by the app; Ctrl+K lists them.</summary>
    public CommandRegistry? Commands { get; set; }

    internal void OpenPalette()
    {
        Palette.IsVisible = true;
        PaletteBox.Text = "";
        PaletteBox.Focus();
        UpdatePalette();
    }

    /// <summary>The "🔎 Cerca ovunque… Ctrl+K" box in the header: the palette, for who does not know the shortcut.</summary>
    private void OnPaletteOpenerClick(object? sender, RoutedEventArgs e) => OpenPalette();

    private void ClosePalette()
    {
        Palette.IsVisible = false;
        PaletteList.ItemsSource = null;
    }

    private void SetUpPalette()
    {
        PaletteBox.TextChanged += (_, _) => UpdatePalette();
        PaletteBox.AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        PaletteList.DoubleTapped += (_, _) => OpenPaletteItem();
        PaletteList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenPaletteItem();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        var count = PaletteList.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                PaletteList.SelectedIndex = Math.Min(count - 1, PaletteList.SelectedIndex + 1);
                break;
            case Key.Up when count > 0:
                PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1);
                break;
            case Key.Enter:
                OpenPaletteItem();
                break;
            case Key.Escape:
                ClosePalette();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OpenPaletteItem()
    {
        if (PaletteList.SelectedItem is PaletteItem item)
        {
            ClosePalette();
            item.Open();
        }
    }

    private void UpdatePalette()
    {
        var query = PaletteBox.Text?.Trim() ?? "";
        var items = PaletteItems(query).OrderByDescending(i => i.Score).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).Take(PaletteMax).ToList();
        PaletteList.ItemsSource = items;
        PaletteList.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    /// <summary>How well <paramref name="text"/> matches: 3 title starts with it, 2 a word starts with it, 1 contains it.</summary>
    private static int Score(string query, string text, string? more = null)
    {
        if (query.Length == 0)
        {
            return 1;
        }

        if (text.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 30;
        }

        var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index > 0 && !char.IsLetterOrDigit(text[index - 1]))
        {
            return 20;
        }

        return index >= 0 ? 10 : more?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true ? 3 : 0;
    }

    private IEnumerable<PaletteItem> PaletteItems(string query)
    {
        // Views and your pages.
        foreach (var (key, label, title) in Views)
        {
            if (Score(query, title) is var s and > 0)
            {
                yield return new PaletteItem(label.Split(' ')[0], title, null, "vista", () => ShowView(key), s + 2);
            }
        }

        foreach (var page in CustomPages.List(_settings.ProjectPath))
        {
            if (Score(query, page) is var s and > 0)
            {
                yield return new PaletteItem("📁", page, $"{CustomPages.Folder}/{page}", "pagina", () => ShowView(PagePrefix + page), s + 2);
            }
        }

        if (query.Length == 0)
        {
            yield break; // with nothing typed, just the views: the rest would be a wall of everything
        }

        // Cards of the board (by id or title).
        foreach (var column in _board?.Columns ?? [])
        {
            foreach (var item in column.Items)
            {
                var title = item.Id is { } id ? $"{id} · {item.Title}" : item.Title;
                if (Score(query, title.Replace("`", ""), item.Note) is var s and > 0)
                {
                    var card = _cards.FirstOrDefault(c => c.Item == item);
                    yield return new PaletteItem(column.Icon, title.Replace("`", ""), column.Title, "task", () =>
                    {
                        ShowView(column.Key is "fatti" or "rifiutati" ? "archive" : "board");
                        if (card is not null)
                        {
                            Select(card);
                        }
                    }, s + (item.Id?.Equals(query, StringComparison.OrdinalIgnoreCase) == true ? 50 : 0));
                }
            }
        }

        // People (by name or nickname; role and description count less), then the cards that depend on them.
        var people = NoteTitled(People.Note) is { } peopleNote ? People.Parse(peopleNote.Content) : [];
        var onCards = people.Count > 0 && _board is not null ? People.OnCards(_board, people) : [];
        foreach (var person in people)
        {
            var score = person.Nicknames.Prepend(person.Name).Max(n => Score(query, n));
            if (score == 0)
            {
                score = Score(query, "", $"{person.Role} {person.Description}");
            }

            if (score == 0)
            {
                continue;
            }

            var role = person.Role.Length > 0 ? $"{person.Role} · " : "";
            yield return new PaletteItem("👤", person.Display, role + person.LastContactLabel(DateOnly.FromDateTime(DateTime.Today)), "persona", () =>
            {
                ShowView("people");
                Filter.Text = person.Name; // the page shows just this person, and their form opens on it
                _ = EditPersonAsync(person.Name);
            }, score + 1);

            foreach (var card in onCards.Where(c => c.Person == person))
            {
                var title = (card.Item.Id is { } id ? $"{id} · {card.Item.Title}" : card.Item.Title).Replace("`", "");
                var view = _cards.FirstOrDefault(c => c.Item == card.Item);
                yield return new PaletteItem("👤", title, $"{card.Column.Title} · dipende da {person.Display}", "task", () =>
                {
                    ShowView("board");
                    if (view is not null)
                    {
                        Select(view);
                    }
                }, score);
            }
        }

        // Notes.
        foreach (var note in _notes)
        {
            if (Score(query, note.Title, note.Note.Content) is var s and > 0)
            {
                yield return new PaletteItem("📄", note.Title, note.HasFolder ? note.Folder : null, "nota", () => OpenInNotesView(note.Note.RelativePath), s);
            }
        }

        // Saved chat conversations.
        var archive = new ConversationArchive(Path.Combine(SettingsStore.Folder, "conversations"));
        foreach (var saved in archive.List(40))
        {
            if (Score(query, saved.Title, string.Join(' ', saved.Entries.Select(e => e.Text))) is var s and > 0)
            {
                yield return new PaletteItem("💬", saved.Title, saved.Label, "conversazione", () => ChatCommandRequested?.Invoke($"/riapri {saved.Id}"), s - 1);
            }
        }

        // Chat commands.
        foreach (var command in Commands?.All ?? [])
        {
            if (Score(query.TrimStart('/'), command.Name.TrimStart('/'), command.Description) is var s and > 0)
            {
                yield return new PaletteItem("⌨", command.Name, command.Description, "comando", () => ChatCommandRequested?.Invoke(command.Name), s - 2);
            }
        }
    }
}
