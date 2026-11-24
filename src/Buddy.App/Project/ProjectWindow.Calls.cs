using System.Text;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// The Trascrizioni page: the user's calls, one note each in Trascrizioni/ (see <see cref="CallTranscripts"/>). A new
/// transcript is saved as pasted, then Claude cleans and summarises it in the background (riassunto, decisioni, azioni,
/// punti aperti); the card shows it as soon as it is done. A click opens the note in the Note view.
/// </summary>
public partial class ProjectWindow
{
    /// <summary>Runs Claude once on a prompt (set by the app); null when Claude is not available.</summary>
    public Func<string, CancellationToken, Task<(bool Ok, string Text)>>? TranscriptSource { get; set; }

    /// <summary>The form of a new call (tests set it): title, date, people, task, text; null when cancelled.</summary>
    internal Func<Task<string[]?>>? ShowCallForm { get; set; }

    /// <summary>The calls Claude is working on now (relative paths), shown as ⏳ on their cards.</summary>
    private readonly HashSet<string> _callsInProgress = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last failure for a call (relative path → message), shown on its card until a retry.</summary>
    private readonly Dictionary<string, string> _callErrors = new(StringComparer.OrdinalIgnoreCase);

    private PageContent CallsPage(string filter)
    {
        var calls = CallTranscripts.List(_vault);
        var shown = calls.Where(c => filter.Length == 0 || c.Content.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var cards = shown.Select(c =>
        {
            var chips = new List<Chip>();
            if (_callsInProgress.Contains(c.RelativePath))
            {
                chips.Add(new Chip("⏳ Claude la sta sistemando…", ChipKind.Warm));
            }
            else if (_callErrors.ContainsKey(c.RelativePath))
            {
                chips.Add(new Chip("⚠ non sistemata", ChipKind.Hot));
            }
            else if (c.Pending)
            {
                chips.Add(new Chip("🧹 da sistemare", ChipKind.Warm));
            }

            if (c.Task is { } task)
            {
                chips.Add(new Chip(task, ChipKind.Plain));
            }

            if (c.Actions > 0)
            {
                chips.Add(new Chip($"☑ {c.Actions - c.OpenActions}/{c.Actions} azioni", c.OpenActions == 0 ? ChipKind.Good : ChipKind.Plain));
            }

            chips.AddRange(c.People.Select(p => new Chip($"👤 {p}", ChipKind.Plain)));
            var body = c.Summary.Length > 0
                ? c.Summary
                : _callErrors.TryGetValue(c.RelativePath, out var error)
                    ? $"_Claude non è riuscito a sistemarla: {error}_"
                    : "_Ancora da sistemare: Claude ne farà riassunto, decisioni e azioni._";
            return new PageCard(c.Title, c.Date == DateOnly.MinValue ? null : c.Date.ToString("dddd d MMMM yyyy", Italian), chips,
                NoteLinks.ToMarkdown(body.Length > 700 ? body[..700] + " …" : body), c.RelativePath, "#6AB7C9")
            {
                Actions = c.Tasks.Take(MaxCallButtons).Select(t => new PageCardAction(
                    (t.IsAction ? "🛠 " : "🤔 ") + Short(t.Text, 46), // an action to do, or an open point
                    $"{(t.IsAction ? "Azione" : "Punto aperto")}: {t.Text}\n\nClaude ci lavora in chat: cerca nel codice e nel vault e ti propone la soluzione (senza modificare nulla).",
                    CallTranscripts.SolveRequest(c, t, Path.Combine(_settings.ProjectPath, c.RelativePath)))).ToList(),
            };
        }).ToList();

        var pending = calls.Count(c => c.Pending && !_callsInProgress.Contains(c.RelativePath));
        var open = calls.Sum(c => c.OpenActions);
        var subtitle = new StringBuilder($"{calls.Count} call in {CallTranscripts.Folder}/");
        if (open > 0)
        {
            subtitle.Append($" · {open} azion{(open == 1 ? "e" : "i")} da fare");
        }

        if (_callsInProgress.Count > 0)
        {
            subtitle.Append($" · {_callsInProgress.Count} in lavorazione");
        }

        subtitle.Append(" · click su una call per leggerla tutta o modificarla");
        var buttons = new List<PageButton>
        {
            new("＋ Nuova trascrizione", "call:new", "Incolla il testo di una call (anche un export .vtt di Teams/Meet/Zoom): Claude la sistema e la riassume"),
        };
        if (pending > 0)
        {
            buttons.Add(new PageButton($"🧹 Sistema in sospeso ({pending})", "call:retry", "Fa sistemare a Claude le trascrizioni non ancora sistemate"));
        }

        buttons.Add(new PageButton("📂 Apri la cartella", "call:folder"));
        return new PageContent(subtitle.ToString(), null, cards, buttons,
            filter.Length > 0 ? "Nessuna call corrisponde alla ricerca."
                : "Ancora nessuna trascrizione: incollane una con «＋ Nuova trascrizione».");
    }

    /// <summary>At most this many "🧠" buttons on a call's card: the actions not done first, then the open points.</summary>
    private const int MaxCallButtons = 8;

    /// <summary>"Chiedere a Tony quali sinonimi usare…": a button's label, cut at a word.</summary>
    private static string Short(string text, int max) => PlainTitle(text, max);

    /// <summary>A card's button: its request goes to Claude in the chat (the board's own, or the bubble).</summary>
    private void OnPageCardActionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true; // the card under it must not open its note too
        if ((sender as Avalonia.Controls.Control)?.DataContext is PageCardAction action)
        {
            ClaudeRequested?.Invoke(action.Request);
        }
    }

    private async Task<string[]?> CallFormAsync()
    {
        if (ShowCallForm is { } stub)
        {
            return await stub();
        }

        var people = NoteTitled(People.Note) is { } note ? People.Parse(note.Content) : [];
        var names = people.Count == 0 ? "" : " — " + string.Join(", ", people.Take(12).Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name));
        return await Settings.Dialogs.FormAsync(this, "Nuova trascrizione",
            new Settings.Dialogs.Field("Titolo (vuoto: lo propone Claude)"),
            new Settings.Dialogs.Field("Quando (AAAA-MM-GG, «oggi», «ieri», «lunedì»…)", "oggi"),
            new Settings.Dialogs.Field("Partecipanti (separati da virgola; vuoto: li ricava Claude)" + names),
            new Settings.Dialogs.Field("Task collegato (ID, facoltativo)", _selected?.Item.Id ?? ""),
            new Settings.Dialogs.Field("Trascrizione (incolla il testo o l'export .vtt; Ctrl+Invio per salvare)", Multiline: true, Required: true, Height: 320));
    }

    /// <summary>"＋ Nuova trascrizione": the form, the note saved as pasted, then Claude cleans and summarises it.</summary>
    internal async Task NewCallAsync()
    {
        if (await CallFormAsync() is not { } values)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var when = values[1].Trim().ToLowerInvariant() switch
        {
            "" or "oggi" => today,
            "ieri" => today.AddDays(-1),
            var text => DateOnly.TryParseExact(text, "yyyy-MM-dd", out var exact) ? exact
                : Reminders.ParseDay(text, today) is { } day ? (day > today ? day.AddDays(-7) : day) : today, // "lunedì": the last one
        };
        var all = NoteTitled(People.Note) is { } note ? People.Parse(note.Content) : [];
        var people = values[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            // "io" is the user: their name, so Claude's list of who spoke does not add them a second time.
            .Select(n => n.ToLowerInvariant() is "io" or "me" && _settings.Mentor.UserName is { Length: > 0 } me ? me : People.Find(all, n)?.Name ?? n)
            .Distinct().ToList();
        var task = values[3].Trim();

        string? saved = null;
        if (!await WriteAsync(() => saved = CallTranscripts.Create(_settings.ProjectPath, values[0], when, people, task.Length > 0 ? task : null, values[4]))
            || saved is null)
        {
            return;
        }

        _vault.Refresh();
        _ = SummarizeCallAsync(saved);
        Reload();
    }

    /// <summary>"🧹 Sistema in sospeso": Claude again on every call not yet cleaned (one at a time).</summary>
    private async Task RetryPendingCallsAsync()
    {
        foreach (var call in CallTranscripts.List(_vault).Where(c => c.Pending && !_callsInProgress.Contains(c.RelativePath)).ToList())
        {
            await SummarizeCallAsync(call.RelativePath);
        }
    }

    /// <summary>
    /// Claude on one call, in the background: its answer becomes the note's sections (see <see cref="CallTranscripts.Apply"/>).
    /// A failure is kept on the card, with the note left "da sistemare" for a retry.
    /// </summary>
    internal async Task SummarizeCallAsync(string relativePath)
    {
        if (TranscriptSource is not { } source || !_callsInProgress.Add(relativePath))
        {
            if (TranscriptSource is null)
            {
                _callErrors[relativePath] = "Claude non è disponibile.";
            }

            return;
        }

        _callErrors.Remove(relativePath);
        RefreshCallsPage();
        try
        {
            var file = Path.Combine(_settings.ProjectPath, relativePath);
            var content = await File.ReadAllTextAsync(file);
            var call = CallTranscripts.Read(new VaultNote(relativePath, content));
            var people = NoteTitled(People.Note) is { } peopleNote ? People.Parse(peopleNote.Content) : [];
            var glossary = NoteTitled(Glossary.Note) is { } glossaryNote ? Glossary.Parse(glossaryNote.Content) : [];
            var (ok, text) = await source(CallTranscripts.Prompt(call, CallTranscripts.Original(content), people, glossary, _settings.Mentor.UserName), CancellationToken.None);
            if (!ok)
            {
                _callErrors[relativePath] = text.Length > 300 ? text[..300] + "…" : text;
                return;
            }

            if (CallTranscripts.Parse(text) is not { } summary)
            {
                _callErrors[relativePath] = "la risposta non era nel formato atteso.";
                return;
            }

            var saved = CallTranscripts.Apply(_settings.ProjectPath, relativePath, content, summary);

            // Then the people: a card for who was missing, "Sentita" on the call's day, and the changes to their cards
            // (made at once when they matter, proposed in the chat otherwise), as for an episode.
            ChatCommandRequested?.Invoke($"/trascrizione-persone {saved}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _callErrors[relativePath] = ex.Message;
        }
        finally
        {
            _callsInProgress.Remove(relativePath);
            _vault.Refresh();
            Reload();
        }
    }

    /// <summary>Shows the ⏳ chips at once when the Trascrizioni page is on screen.</summary>
    private void RefreshCallsPage()
    {
        if (_view == "calls")
        {
            _ = RenderPageAsync("calls");
        }
    }
}
