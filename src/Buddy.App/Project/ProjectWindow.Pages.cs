using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Buddy.Core.Repo;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// The pages built from the vault and the tools around it, all shown by one generic view of cards: your own pages
/// (sub-folders of Pagine/), Decisioni, Glossario, Costi, Settimana and the open PRs. Each page is a small provider that
/// returns a <see cref="PageContent"/>; the cards open their note (or the PR in the browser) with a click.
/// </summary>
public partial class ProjectWindow
{
    /// <summary>The built-in generic pages (also in <see cref="Views"/>).</summary>
    private static readonly HashSet<string> GenericPages = ["week", "costs", "decisions", "glossary", "people", "episodes", "calls", "registry", "commitments", "prs"];

    private const string PagePrefix = "page:";
    private const string NewPageKey = "page:+";

    /// <summary>Weeks back from this one, for the Settimana page (‹ ›).</summary>
    private int _weekOffset;

    private (DateTime At, IReadOnlyList<PullRequestInfo> List, string? Problem)? _prs;
    private (int Offset, DateTime At, PageContent Content)? _weekCache;
    private int _pageRender;

    private static bool IsGeneric(string key) => GenericPages.Contains(key) || key.StartsWith(PagePrefix, StringComparison.Ordinal);

    private string TitleOf(string key) =>
        key.StartsWith(PagePrefix, StringComparison.Ordinal) ? key[PagePrefix.Length..] : Views.FirstOrDefault(v => v.Key == key).Title ?? key;

    /// <summary>The side bar's extra entries (the group «Le tue pagine»): your pages, then "＋ Nuova pagina…".</summary>
    private IEnumerable<NavItem> CustomPageNav() =>
        CustomPages.List(_settings.ProjectPath)
            .Select(p => new NavItem(PagePrefix + p, $"📁  {p}", CustomPages.Notes(_vault, p).Count is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : ""))
            .Append(new NavItem(NewPageKey, "＋  Nuova pagina…", ""));

    private async Task CreatePageAsync()
    {
        var name = await Settings.Dialogs.AskAsync(this, "Nuova pagina",
            "Nome della pagina (diventa la cartella Pagine/<nome> nel vault; dentro ci metti le tue note):");
        if (name is null)
        {
            ShowView(_lastRealView);
            return;
        }

        try
        {
            var page = CustomPages.CreatePage(_settings.ProjectPath, name);
            Reload();
            ShowView(PagePrefix + page);
        }
        catch (IOException ex)
        {
            await Settings.Dialogs.ShowMessageAsync(this, ex.Message);
            ShowView(_lastRealView);
        }
    }

    private string _lastRealView = "overview";

    internal async Task RenamePageAsync(string page)
    {
        if (await Settings.Dialogs.AskAsync(this, "Rinomina pagina", "Nuovo nome della pagina:", page) is not { } name)
        {
            return;
        }

        string renamed = page;
        if (await WriteAsync(() => renamed = CustomPages.RenamePage(_settings.ProjectPath, page, name)))
        {
            _vault.Refresh();
            Reload();
            ShowView(PagePrefix + renamed);
        }
    }

    internal async Task DeletePageAsync(string page)
    {
        var count = CustomPages.Notes(_vault, page).Count;
        var what = count == 0 ? "La pagina è vuota." : $"Contiene {count} not{(count == 1 ? "a" : "e")}.";
        if (!await Settings.Dialogs.ConfirmAsync(this,
                $"Eliminare la pagina «{page}»? {what} Finisce nel cestino del vault (.trash), da cui si può recuperare.", "Elimina"))
        {
            return;
        }

        if (await WriteAsync(() => CustomPages.DeletePage(_settings.ProjectPath, page)))
        {
            _vault.Refresh();
            Reload();
            ShowView("overview");
        }
    }

    /// <summary>Fills the generic view for <paramref name="key"/>; slow sources (git, gh) are read in the background.</summary>
    private async Task RenderPageAsync(string key, bool refresh = false)
    {
        var render = ++_pageRender;
        var filter = Filter.Text?.Trim() ?? "";
        PageContent content;
        try
        {
            content = key switch
            {
                "decisions" => DecisionsPage(filter),
                "glossary" => GlossaryPage(filter),
                "people" => PeoplePage(filter),
                "episodes" => EpisodesPage(filter),
                "calls" => CallsPage(filter),
                "registry" => RegistryPage(filter),
                "commitments" => CommitmentsPage(filter),
                "costs" => CostsPage(filter),
                "week" => await WeekPageAsync(refresh),
                "prs" => await PullRequestsPageAsync(filter, refresh),
                _ when key.StartsWith(PagePrefix, StringComparison.Ordinal) => CustomPage(key[PagePrefix.Length..], filter),
                _ => new PageContent("", null, [], [], ""),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            content = new PageContent("", null, [], [new PageButton("↻ Riprova", "refresh")], $"Non riesco a preparare la pagina: {ex.Message}");
        }

        if (render != _pageRender || _view != key)
        {
            return; // another page (or a newer render) took over meanwhile
        }

        ViewSubtitle.Text = content.Subtitle;
        PageHeader.Markdown = content.Header ?? "";
        PageHeaderPanel.IsVisible = !string.IsNullOrWhiteSpace(content.Header);
        PageCards.ItemsSource = content.Cards;
        PageEmpty.Text = content.Empty;
        PageEmpty.IsVisible = content.Cards.Count == 0 && content.Empty.Length > 0;
        PageButtons.Children.Clear();
        foreach (var button in content.Buttons)
        {
            var control = new Button { Content = button.Label, Tag = button.Action, Classes = { "small" } };
            if (button.Tip is not null)
            {
                ToolTip.SetTip(control, button.Tip);
            }

            control.Click += OnPageButtonClick;
            PageButtons.Children.Add(control);
        }

        FitPageCards();
    }

    /// <summary>Two or three cards per row, whatever the window's width.</summary>
    private void FitPageCards()
    {
        var width = PageScroller.Bounds.Width;
        if (width > 0)
        {
            var perRow = width > 1250 ? 3 : width > 760 ? 2 : 1;
            Resources["PageCardWidth"] = Math.Floor((width - 4 - perRow * 12) / perRow);
        }
    }

    /// <summary>The target of a person's card on the Persone page: a click opens the edit form, not the note.</summary>
    private const string PersonTarget = "person:";

    private async void OnPageCardTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PageCard { Target: { } target }
            || Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(e.Source as Avalonia.Visual, includeSelf: true) is not null) // a button on the card
        {
            return;
        }

        if (target.StartsWith(PersonTarget, StringComparison.Ordinal))
        {
            await EditPersonAsync(target[PersonTarget.Length..]);
        }
        else if (target.StartsWith(EpisodeTarget, StringComparison.Ordinal))
        {
            await EditEpisodeAsync(target[EpisodeTarget.Length..]);
        }
        else if (Path.IsPathRooted(target) && File.Exists(target))
        {
            Shell.Open(target); // a note outside the vault (Episodi.md in the journal)
        }
        else if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Shell.Open(target);
        }
        else
        {
            OpenInNotesView(target);
        }
    }

    private async void OnPageButtonClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string action)
        {
            return;
        }

        switch (action)
        {
            case "refresh":
                await RenderPageAsync(_view, refresh: true);
                break;
            case "week:prev":
                _weekOffset++;
                await RenderPageAsync(_view);
                break;
            case "week:recap" when _weekRecap is { } recap:
                ClaudeRequested?.Invoke(recap);
                break;
            case "week:next":
                _weekOffset = Math.Max(0, _weekOffset - 1);
                await RenderPageAsync(_view);
                break;
            case "decision:new":
                await NewDecisionAsync();
                break;
            case "term:new":
                await NewTermAsync();
                break;
            case "person:new":
                await NewPersonAsync();
                break;
            case "person:contacted":
                await MarkContactedAsync();
                break;
            case "person:prepare":
                await PrepareConversationAsync();
                break;
            case "person:note" when NoteTitled(People.Note) is { } peopleNote:
                OpenInNotesView(peopleNote.RelativePath);
                break;
            case "episode:new":
                await NewEpisodeAsync();
                break;
            case "episode:file":
                var episodesFile = Episodes.PathFor(_settings);
                Shell.Open(File.Exists(episodesFile) ? episodesFile : Path.GetDirectoryName(episodesFile)!);
                break;
            case "said:new:info":
                await NewSaidAsync(SaidKind.Info);
                break;
            case "said:new:accordo":
                await NewSaidAsync(SaidKind.Accordo);
                break;
            case "said:new:impegno":
                await NewSaidAsync(SaidKind.Impegno);
                break;
            case "said:ask":
                await AskSaidAsync();
                break;
            case "said:file" when NoteTitled(Said.Note) is { } registry:
                OpenInNotesView(registry.RelativePath);
                break;
            case "said:file":
                if (await WriteAsync(() => File.WriteAllText(Said.PathFor(_settings.ProjectPath), Said.Header)))
                {
                    _vault.Refresh();
                    Reload();
                    OpenInNotesView(Said.FileName);
                }

                break;
            case "call:new":
                await NewCallAsync();
                break;
            case "call:retry":
                await RetryPendingCallsAsync();
                break;
            case "call:folder":
                var calls = Path.Combine(_settings.ProjectPath, CallTranscripts.Folder);
                Directory.CreateDirectory(calls);
                Shell.Open(calls);
                break;
            case "note:new" when _view.StartsWith(PagePrefix, StringComparison.Ordinal):
                await NewPageNoteAsync(_view[PagePrefix.Length..]);
                break;
            case "page:rename" when _view.StartsWith(PagePrefix, StringComparison.Ordinal):
                await RenamePageAsync(_view[PagePrefix.Length..]);
                break;
            case "page:delete" when _view.StartsWith(PagePrefix, StringComparison.Ordinal):
                await DeletePageAsync(_view[PagePrefix.Length..]);
                break;
            case "page:folder" when _view.StartsWith(PagePrefix, StringComparison.Ordinal):
                var folder = Path.Combine(_settings.ProjectPath, CustomPages.Folder, _view[PagePrefix.Length..]);
                Directory.CreateDirectory(folder);
                Shell.Open(folder);
                break;
        }
    }

    // ---- Your pages -------------------------------------------------------------------------------------------

    private PageContent CustomPage(string page, string filter)
    {
        var notes = CustomPages.Notes(_vault, page)
            .Where(n => filter.Length == 0 || n.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                           || n.Content.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var cards = notes.Select(n =>
        {
            var body = CustomPages.Body(n);
            var created = CustomPages.Created(n);
            var task = Regex.Match(n.Content, @"^task:\s*(\S+)", RegexOptions.Multiline) is { Success: true } t ? t.Groups[1].Value : null;
            var chips = new List<Chip>();
            if (task is not null)
            {
                chips.Add(new Chip(task, ChipKind.Plain));
            }

            return new PageCard(n.Title, created?.ToString("d MMMM yyyy", Italian), chips,
                NoteLinks.ToMarkdown(body.Length > 600 ? body[..600] + " …" : body), n.RelativePath, "#B99BF5");
        }).ToList();
        return new PageContent(
            $"{notes.Count} not{(notes.Count == 1 ? "a" : "e")} in {CustomPages.Folder}/{page} · click su una nota per leggerla o modificarla",
            null, cards,
            [
                new PageButton("＋ Nuova nota", "note:new", "Scrivi una nota in questa pagina (titolo e testo, in markdown)"),
                new PageButton("📂 Apri la cartella", "page:folder"),
                new PageButton("✏ Rinomina", "page:rename", "Cambia il nome della pagina (e della sua cartella)"),
                new PageButton("🗑 Elimina", "page:delete", "Sposta la pagina e le sue note nel cestino del vault (.trash)"),
            ],
            filter.Length > 0 ? "Nessuna nota corrisponde alla ricerca." : "Ancora nessuna nota: aggiungine una con «＋ Nuova nota».");
    }

    private async Task NewPageNoteAsync(string page)
    {
        var values = await Settings.Dialogs.FormAsync(this, $"Nuova nota in «{page}»",
            new Settings.Dialogs.Field("Titolo", Required: true),
            new Settings.Dialogs.Field("Testo (markdown; Ctrl+Invio per salvare)", Multiline: true));
        if (values is null)
        {
            return;
        }

        if (await WriteAsync(() => CustomPages.CreateNote(_settings.ProjectPath, page, values[0], values[1])))
        {
            AfterEdit();
        }
    }

    /// <summary>A card title from markdown: no bold or code marks, cut at a word near <paramref name="max"/> characters.</summary>
    private static string PlainTitle(string markdown, int max)
    {
        var text = Regex.Replace(markdown, @"\*\*|__|`|\[\[|\]\]", "").Trim();
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', max);
        return text[..(cut > max / 2 ? cut : max)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    // ---- Decisioni ------------------------------------------------------------------------------------------

    private VaultNote? NoteTitled(string title) =>
        _vault.Notes.Where(n => n.Title.Equals(title, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.RelativePath.Length).FirstOrDefault();

    private PageContent DecisionsPage(string filter)
    {
        var note = NoteTitled(Decisions.Note);
        var all = note is null ? [] : Decisions.Parse(note.Content, _settings.TaskIdPattern);
        var shown = all.Where(d => filter.Length == 0 || d.Raw.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var cards = shown.Select(d => new PageCard(
            PlainTitle(d.What, 140),
            d.Date?.ToString("d MMMM yyyy", Italian),
            d.TaskId is null ? [] : [new Chip(d.TaskId, ChipKind.Plain)],
            NoteLinks.ToMarkdown((PlainTitle(d.What, 140).EndsWith('…') ? d.What + "\n\n" : "") + (d.Why is null ? "" : $"**Perché:** {d.Why}")),
            note?.RelativePath,
            "#F2B544")).ToList();
        var tasks = all.Where(d => d.TaskId is not null).Select(d => d.TaskId).Distinct().Count();
        return new PageContent(
            note is null ? "Nel vault non c'è ancora la nota «Decisioni»: la crea il primo «Registra decisione»."
                : $"{all.Count} decisioni su {tasks} task · le scelte fatte e perché, che Claude non deve contraddire",
            null, cards,
            [new PageButton("＋ Registra decisione", "decision:new", "Una riga in Decisioni.md: cosa, perché e il task")],
            filter.Length > 0 ? "Nessuna decisione corrisponde alla ricerca." : "Nessuna decisione registrata.");
    }

    private async Task NewDecisionAsync()
    {
        var task = (_selected?.Item.Id) ?? "";
        var values = await Settings.Dialogs.FormAsync(this, "Registra una decisione",
            new Settings.Dialogs.Field("Cosa si è deciso", Required: true, Multiline: true),
            new Settings.Dialogs.Field("Perché (il motivo, chi l'ha deciso)", Multiline: true),
            new Settings.Dialogs.Field("Task (es. NUSR-2367, facoltativo)", task));
        if (values is null)
        {
            return;
        }

        var line = Decisions.Format(DateOnly.FromDateTime(DateTime.Today), values[0], values[1], values[2].Length > 0 ? values[2].ToUpperInvariant() : null);
        var path = NoteTitled(Decisions.Note) is { } note ? FullPath(note.RelativePath) : FullPath(Decisions.Note + ".md");
        if (await WriteAsync(() =>
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, $"# Decisioni\n\nScelte fatte e perché.\n\n## {Decisions.Section}\n\n");
                }

                VaultWriter.InsertItemFirst(path, Decisions.Section, line);
            }))
        {
            AfterEdit();
        }
    }

    // ---- Glossario ------------------------------------------------------------------------------------------

    private PageContent GlossaryPage(string filter)
    {
        var note = NoteTitled(Glossary.Note);
        var terms = note is null ? [] : Glossary.Parse(note.Content);
        var others = _vault.Notes.Where(n => note is null || n.RelativePath != note.RelativePath).ToList();
        var cards = terms
            .Where(t => filter.Length == 0 || t.Term.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                           || t.Meaning.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .Select(t =>
            {
                var pattern = new Regex($@"(?<!\w){Regex.Escape(t.Term)}(?!\w)", RegexOptions.IgnoreCase);
                var usedIn = others.Where(n => pattern.IsMatch(n.Content)).Select(n => n.Title).ToList();
                var chips = new List<Chip> { new(usedIn.Count == 0 ? "non citato nelle note" : $"in {usedIn.Count} not{(usedIn.Count == 1 ? "a" : "e")}", usedIn.Count == 0 ? ChipKind.Warm : ChipKind.Plain) };
                var markdown = t.Meaning + (usedIn.Count == 0 ? "" : $"\n\nCitato in: {string.Join(", ", usedIn.Take(6).Select(u => $"[[{u}]]"))}{(usedIn.Count > 6 ? "…" : "")}");
                var target = t.Links.Select(NoteTitled).OfType<VaultNote>().FirstOrDefault()?.RelativePath ?? note?.RelativePath;
                return new PageCard(t.Term, null, chips, NoteLinks.ToMarkdown(markdown), target, "#7FD1AE");
            }).ToList();
        return new PageContent(
            note is null ? "Nel vault non c'è ancora la nota «Glossario»: la crea il primo «Nuovo termine»."
                : $"{terms.Count} termini del progetto con il loro significato · click per la nota collegata",
            null, cards,
            [new PageButton("＋ Nuovo termine", "term:new", "Una riga in Glossario.md: termine e significato")],
            filter.Length > 0 ? "Nessun termine corrisponde alla ricerca." : "Glossario vuoto.");
    }

    private async Task NewTermAsync()
    {
        var values = await Settings.Dialogs.FormAsync(this, "Nuovo termine del glossario",
            new Settings.Dialogs.Field("Termine", Required: true),
            new Settings.Dialogs.Field("Significato (anche [[Nota collegata]])", Required: true, Multiline: true));
        if (values is null)
        {
            return;
        }

        var path = NoteTitled(Glossary.Note) is { } note ? FullPath(note.RelativePath) : FullPath(Glossary.Note + ".md");
        if (await WriteAsync(() =>
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, $"# Glossario\n\nTermini del progetto con un significato preciso.\n\n## {Glossary.Section}\n\n");
                }

                VaultWriter.AppendItem(path, Glossary.Section, Glossary.Format(values[0], values[1]));
            }))
        {
            AfterEdit();
        }
    }

    // ---- Persone ----------------------------------------------------------------------------------------------

    private PageContent PeoplePage(string filter)
    {
        var note = NoteTitled(People.Note);
        var people = note is null ? [] : People.Parse(note.Content);
        var others = _vault.Notes.Where(n => note is null || n.RelativePath != note.RelativePath).ToList();
        var onCards = _board is null ? [] : People.OnCards(_board, people);
        var episodes = Episodes.Read(Episodes.PathFor(_settings));
        var said = Said.All(_vault, _settings.ProjectPath); // the registry and the calls: what each one said or owes
        var cards = people
            .Where(p => filter.Length == 0 || p.Raw.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .Select(p =>
            {
                var theirs = onCards.Where(c => c.Person == p).ToList();
                // Full name or nickname: a first name alone would count every namesake.
                var pattern = new Regex($@"(?<!\w)(?:{string.Join("|", p.Nicknames.Prepend(p.Name).Select(Regex.Escape))})(?!\w)", RegexOptions.IgnoreCase);
                var citedIn = others.Where(n => pattern.IsMatch(n.Content)).Select(n => n.Title).ToList();
                var today = DateOnly.FromDateTime(DateTime.Today);
                var chips = new List<Chip>
                {
                    new("👥 " + p.LastContactLabel(today),
                        theirs.Any(c => c.Column.Key == "attesa") && (p.LastContact is not { } last || today.DayNumber - last.DayNumber >= People.StaleContactDays)
                            ? ChipKind.Warm
                            : ChipKind.Plain),
                };
                if (p.Relationship.Length > 0)
                {
                    chips.Add(new Chip($"🤝 {p.Relationship}", p.Relationship switch
                    {
                        "ottimo" or "buono" => ChipKind.Good,
                        "delicato" => ChipKind.Warm,
                        "critico" => ChipKind.Hot,
                        _ => ChipKind.Plain,
                    }));
                }

                var lived = Episodes.Involving(episodes, p);
                var theirSaid = SaidBy(said, p);
                var owes = theirSaid.Where(e => e.IsOpenCommitment).ToList();
                if (owes.Count > 0)
                {
                    chips.Add(new Chip($"✅ ti deve {owes.Count} cos{(owes.Count == 1 ? "a" : "e")}", owes.Any(e => e.IsLate(today)) ? ChipKind.Hot : ChipKind.Warm));
                }

                if (lived.Count > 0)
                {
                    chips.Add(new Chip($"📓 {lived.Count} episod{(lived.Count == 1 ? "io" : "i")}", ChipKind.Plain));
                }

                if (theirs.Count > 0)
                {
                    chips.Add(new Chip($"👤 {theirs.Count} card", theirs.Any(c => c.Column.Key == "attesa") ? ChipKind.Warm : ChipKind.Plain));
                }

                if (citedIn.Count > 0)
                {
                    chips.Add(new Chip($"in {citedIn.Count} not{(citedIn.Count == 1 ? "a" : "e")}", ChipKind.Plain));
                }

                if (p.Notes.Length == 0)
                {
                    chips.Add(new Chip("senza note su quando sentirla", ChipKind.Warm));
                }

                var body = new StringBuilder();
                // A list stays a list (each line an item); a single line is inline after its label.
                void Section(string? label, string value)
                {
                    if (value.Length == 0)
                    {
                        return;
                    }

                    var text = People.IsList(value) ? People.AsList(value) : value;
                    body.AppendLine(label is null ? text : People.IsList(value) ? $"**{label}:**\n\n{text}" : $"**{label}:** {text}").AppendLine();
                }

                Section(null, p.Description);
                Section("Come comunicare", p.Communication);
                Section("Note", p.Notes);
                if (Episodes.LessonLine(episodes, p) is { Length: > 0 } lesson)
                {
                    body.AppendLine(lesson).AppendLine();
                }

                if (owes.Count > 0)
                {
                    Section("Ti deve", string.Join("\n", owes.Take(4).Select(e => $"- {(e.IsLate(today) ? "⚠ " : "")}{(e.Due is { } due ? $"entro {due.ToString("d MMM", Italian)} · " : "")}{e.Text}")));
                }

                var recent = theirSaid.Where(e => !e.IsOpenCommitment).Take(3).ToList();
                if (recent.Count > 0)
                {
                    Section("Ultime cose dette", string.Join("\n", recent.Select(e => $"- {e.Date.ToString("d MMM", Italian)} · *{e.KindWord}* · {e.Text}")));
                }

                if (lived.Count > 0)
                {
                    Section("Ultimi episodi", string.Join("\n", lived.Take(3).Select(e => $"- {e.Date.ToString("d MMM", Italian)} · {e.Title}")));
                }

                if (theirs.Count > 0)
                {
                    body.AppendLine("**Card:** " + string.Join(", ", theirs.Select(c =>
                        $"{(c.Item.Id ?? PlainTitle(c.Item.Title, 40)).Replace("`", "")} ({c.Column.Title})"))).AppendLine();
                }

                if (citedIn.Count > 0)
                {
                    body.AppendLine($"Citata in: {string.Join(", ", citedIn.Take(6).Select(c => $"[[{c}]]"))}{(citedIn.Count > 6 ? "…" : "")}");
                }

                return new PageCard(p.Display, p.Role.Length > 0 ? p.Role : null, chips, NoteLinks.ToMarkdown(body.ToString().TrimEnd()),
                    PersonTarget + p.Name, "#E39BC4")
                {
                    Actions = [new PageCardAction("🧠 Cosa ci siamo detti", $"Claude riassume cosa ti sei detto con {p.Name}: accordi, impegni aperti, decisioni, con le fonti",
                        Buddy.Core.Vault.SaidQuery.Prompt($"Cosa ci siamo detti con {p.Name}? Accordi, impegni aperti (suoi e miei), decisioni e cose utili da ricordare, dalla più recente.",
                            said, CallTranscripts.List(_vault), people, _settings.Mentor.UserName, _settings.ProjectPath, today))],
                };
            }).ToList();
        return new PageContent(
            note is null ? "Nel vault non c'è ancora la nota «Persone»: la crea la prima «Nuova persona»."
                : $"{people.Count} persone del progetto · click su una persona per modificarla · chiedi al Buddy «con chi parlo di …?»",
            null, cards,
            [
                new PageButton("＋ Nuova persona", "person:new", "Una scheda in Persone.md: nome, soprannome, compito, descrizione e note"),
                new PageButton("🗣 Prepara conversazione…", "person:prepare",
                    "Come impostare una conversazione con una persona e la bozza del primo messaggio (anche /prepara in chat)"),
                new PageButton("✅ Ho sentito…", "person:contacted", "Segna che oggi hai sentito una persona (anche /sentita nome in chat)"),
                new PageButton("📄 Apri la nota", "person:note", "La nota Persone.md così com'è scritta nel vault"),
            ],
            filter.Length > 0 ? "Nessuna persona corrisponde alla ricerca." : "Ancora nessuna persona: aggiungine una con «＋ Nuova persona».");
    }

    /// <summary>The form of a person, empty for a new one or filled in to edit; null when cancelled.</summary>
    internal Func<PersonFields?, Task<PersonFields?>>? ShowPersonForm { get; set; }

    private async Task<PersonFields?> PersonFormAsync(PersonFields? person)
    {
        if (ShowPersonForm is { } stub)
        {
            return await stub(person); // tests
        }

        var values = await Settings.Dialogs.FormAsync(this, person is null ? "Nuova persona" : $"Modifica «{person.Name}»",
            new Settings.Dialogs.Field("Nome", person?.Name ?? "", Required: true),
            new Settings.Dialogs.Field("Soprannome (come la chiamate di solito; più di uno separati da virgola)", person?.Nicknames ?? ""),
            new Settings.Dialogs.Field("Compito (ruolo nel progetto, es. product owner, DBA, tester)", person?.Role ?? ""),
            new Settings.Dialogs.Field("Descrizione (di cosa si occupa, per cosa sentirla)", person?.Description ?? "", Multiline: true, Height: 80),
            new Settings.Dialogs.Field("Rapporto (cosa serve per lavorarci bene, non un giudizio sulla persona)", person?.Relationship ?? "",
                Choices: People.Relationships),
            new Settings.Dialogs.Field("Come comunicare (cosa funziona: per iscritto, brevi, con i dati, orari…)", person?.Communication ?? "",
                Multiline: true, Height: 80),
            new Settings.Dialogs.Field("Note (quando contattarla, cosa chiederle; Ctrl+Invio per salvare)", person?.Notes ?? "", Multiline: true, Height: 100));
        return values is null ? null : new PersonFields(values[0], values[1], values[2], values[3], values[6], values[4], values[5], person?.LastContact);
    }

    private async Task NewPersonAsync()
    {
        if (await PersonFormAsync(null) is not { } person)
        {
            return;
        }

        var path = NoteTitled(People.Note) is { } note ? FullPath(note.RelativePath) : FullPath(People.Note + ".md");
        if (await WriteAsync(() => People.Add(path, People.Format(person))))
        {
            AfterEdit();
        }
    }

    /// <summary>
    /// "✏ Modifica…": the same form, filled in; the person's section is rewritten (the date of the last contact kept).
    /// With the page filtered down to one person (e.g. from Ctrl+K) that one, otherwise it asks who.
    /// </summary>
    internal async Task EditPersonAsync(string? name = null)
    {
        if (NoteTitled(People.Note) is not { } note)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Aggiungi prima qualcuno con «＋ Nuova persona».");
            return;
        }

        var people = People.Parse(note.Content);
        var filter = Filter.Text?.Trim() ?? "";
        var shown = people.Where(p => filter.Length == 0 || p.Raw.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        name ??= shown.Count == 1 ? shown[0].Name
            : await Settings.Dialogs.AskAsync(this, "Modifica persona",
                "Chi vuoi modificare? (nome o soprannome)\nPersone: " + string.Join(", ", people.Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name)) + ".");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (People.Find(people, name) is not { } person)
        {
            await Settings.Dialogs.ShowMessageAsync(this, $"Nelle Persone non trovo «{name.Trim()}».");
            return;
        }

        if (await PersonFormAsync(person.Fields) is not { } edited)
        {
            return;
        }

        if (await WriteAsync(() => People.Update(FullPath(note.RelativePath), person.Name, edited)))
        {
            if (filter.Equals(person.Name, StringComparison.CurrentCultureIgnoreCase) && edited.Name.Trim() != person.Name)
            {
                Filter.Text = edited.Name.Trim(); // renamed while the page showed just them: keep showing them
            }

            AfterEdit();
        }
    }

    /// <summary>"🗣 Prepara conversazione…": who and about what, then /prepara in the chat (Claude answers there).</summary>
    private async Task PrepareConversationAsync()
    {
        var people = NoteTitled(People.Note) is { } note ? People.Parse(note.Content) : [];
        var filter = Filter.Text?.Trim() ?? "";
        var shown = people.Where(p => filter.Length > 0 && p.Raw.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var text = await Settings.Dialogs.AskAsync(this, "Prepara una conversazione",
            "Con chi e di cosa? (es. «Chicca rilascio di venerdì»)\nPersone: "
            + string.Join(", ", people.Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name)) + ".",
            shown.Count == 1 ? (shown[0].Nicknames.Count > 0 ? shown[0].Nicknames[0] : shown[0].Name) + " " : "");
        if (!string.IsNullOrWhiteSpace(text))
        {
            ChatCommandRequested?.Invoke($"/prepara {text.Trim()}");
        }
    }

    private async Task MarkContactedAsync()
    {
        if (NoteTitled(People.Note) is not { } note)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Aggiungi prima qualcuno con «＋ Nuova persona».");
            return;
        }

        var people = People.Parse(note.Content);
        var name = await Settings.Dialogs.AskAsync(this, "Ho sentito…",
            "Chi hai sentito oggi? (nome o soprannome)\nPersone: " + string.Join(", ", people.Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name)) + ".");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Person? marked = null;
        if (await WriteAsync(() => marked = People.MarkContacted(FullPath(note.RelativePath), name, DateOnly.FromDateTime(DateTime.Today))))
        {
            if (marked is null)
            {
                await Settings.Dialogs.ShowMessageAsync(this, $"Nelle Persone non trovo «{name.Trim()}».");
                return;
            }

            AfterEdit();
        }
    }

    // ---- Episodi ----------------------------------------------------------------------------------------------

    private PageContent EpisodesPage(string filter)
    {
        var path = Episodes.PathFor(_settings);
        var episodes = Episodes.Read(path);
        var cards = episodes
            .Where(e => filter.Length == 0 || e.Raw.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .Select(e =>
            {
                var chips = e.People.Select(p => new Chip($"👤 {p}", ChipKind.Plain)).ToList();
                if (e.Changes.Count > 0)
                {
                    chips.Add(new Chip($"✏ {e.Changes.Count} modific{(e.Changes.Count == 1 ? "a" : "he")} alle schede", ChipKind.Warm));
                }

                var body = new StringBuilder();
                void Section(string label, string value)
                {
                    if (value.Length > 0)
                    {
                        body.AppendLine(People.IsList(value) ? $"**{label}:**\n\n{People.AsList(value)}" : $"**{label}:** {value}").AppendLine();
                    }
                }

                Section("Cosa è successo", e.Facts);
                Section("Come ho reagito", e.Reaction);
                Section("Com'è finita", e.Outcome);
                Section("Su di me", e.AboutMe);
                Section("💡 Da ricordare", e.Lesson);
                if (e.Changes.Count > 0)
                {
                    Section("Modifiche alle schede", string.Join("\n", e.Changes.Select(c => "- " + c)));
                }

                // Screenshots (![[…]]) are found in the episodes' folder, where the form saved them.
                return new PageCard(e.Title, e.Date.ToString("dddd d MMMM yyyy", Italian), chips,
                    NoteLinks.ToMarkdown(body.ToString().TrimEnd(), EpisodesFolder, EpisodesFolder), EpisodeTarget + e.Key, "#C9A86A");
            }).ToList();
        var where = string.IsNullOrWhiteSpace(_settings.Mentor.JournalPath) ? "nella cartella del progetto" : "nel tuo diario";
        return new PageContent(
            $"{episodes.Count} episodi · in {Episodes.FileName} {where}, privati · dopo ogni episodio l'AI aggiorna le schede delle Persone (se è importante) o te lo propone",
            null, cards,
            [
                new PageButton("＋ Nuovo episodio", "episode:new", "Cosa è successo, con chi, come hai reagito e com'è finita (anche /episodio testo in chat)"),
                new PageButton("📄 Apri il file", "episode:file", $"{Episodes.FileName} così com'è scritto"),
            ],
            filter.Length > 0 ? "Nessun episodio corrisponde alla ricerca." : "Ancora nessun episodio: raccontane uno con «＋ Nuovo episodio» o con /episodio in chat.");
    }

    /// <summary>The folder of Episodi.md (the journal, or the project): its images go in its Allegati folder.</summary>
    private string EpisodesFolder => Path.GetDirectoryName(Path.GetFullPath(Episodes.PathFor(_settings)))!;

    /// <summary>The form of an episode, empty for a new one or filled in to edit; null when cancelled (tests set it).</summary>
    internal Func<Episode?, Task<Episode?>>? ShowEpisodeForm { get; set; }

    /// <summary>The target of an episode's card on the Episodi page: a click opens its form, not the file.</summary>
    private const string EpisodeTarget = "episode:";

    private async Task<Episode?> EpisodeFormAsync(Episode? episode)
    {
        if (ShowEpisodeForm is { } stub)
        {
            return await stub(episode);
        }

        var people = NoteTitled(People.Note) is { } note ? People.Parse(note.Content) : [];
        var names = people.Count == 0 ? "" : " — " + string.Join(", ", people.Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name));
        // Screenshots (a chat, an email…) pasted with Ctrl+V or dropped: saved next to Episodi.md, in its Allegati folder.
        var folder = EpisodesFolder;
        void Images(TextBox box) => EnableImages(box, folder);
        var values = await Settings.Dialogs.FormAsync(this, episode is null ? "Nuovo episodio" : $"Episodio «{episode.Title}»",
            new Settings.Dialogs.Field("Quando (AAAA-MM-GG, «oggi», «ieri», «lunedì»…)", episode?.Date.ToString("yyyy-MM-dd") ?? "oggi"),
            new Settings.Dialogs.Field("Titolo (in breve)", episode?.Title ?? "", Required: true),
            new Settings.Dialogs.Field("Persone coinvolte (separate da virgola)" + names, string.Join(", ", episode?.People ?? [])),
            new Settings.Dialogs.Field("Cosa è successo (solo i fatti; Ctrl+V incolla anche uno screenshot)", episode?.Facts ?? "", Multiline: true,
                Required: true, Height: 110, Prepare: Images),
            new Settings.Dialogs.Field("Come ho reagito, come mi sono sentito", episode?.Reaction ?? "", Multiline: true, Height: 80, Prepare: Images),
            new Settings.Dialogs.Field("Com'è finita; cosa rifarei diversamente", episode?.Outcome ?? "", Multiline: true, Height: 80, Prepare: Images));
        if (values is null)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var when = values[0].Trim().ToLowerInvariant() switch
        {
            "" or "oggi" => today,
            "ieri" => today.AddDays(-1),
            var text => Reminders.ParseDay(text, today) is { } day ? (day > today ? day.AddDays(-7) : day) : episode?.Date ?? today, // "lunedì": the last one
        };
        var named = values[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(n => People.Find(people, n)?.Name ?? n).Distinct().ToList();

        // Editing keeps what the analysis wrote (Su di me, the changes made to the cards).
        return new Episode(when, values[1].Trim(), named, values[3], values[4], values[5], episode?.AboutMe ?? "", episode?.Changes ?? [],
            Lesson: episode?.Lesson ?? "");
    }

    /// <summary>
    /// "＋ Nuovo episodio": the form, then the episode is saved and the chat analyses it (/episodio-analizza): the cards of
    /// the people involved change at once for a macroscopic event, otherwise the changes are proposed there.
    /// </summary>
    internal async Task NewEpisodeAsync()
    {
        if (await EpisodeFormAsync(null) is not { } episode)
        {
            return;
        }

        Episode? saved = null;
        if (await WriteAsync(() => saved = Episodes.Add(Episodes.PathFor(_settings), episode)) && saved is not null)
        {
            ChatCommandRequested?.Invoke($"/episodio-analizza {saved.Key}");
            Reload();
        }
    }

    /// <summary>
    /// A click on an episode: its form, filled in. Saved in place; when what happened (facts, reaction, outcome) changed,
    /// the chat analyses it again (a change already on a card is not made twice).
    /// </summary>
    internal async Task EditEpisodeAsync(string key)
    {
        var path = Episodes.PathFor(_settings);
        if (Episodes.Read(path).FirstOrDefault(e => e.Key == key) is not { } episode)
        {
            await Settings.Dialogs.ShowMessageAsync(this, $"L'episodio «{key}» non è più in {Episodes.FileName}: la nota è cambiata.");
            return;
        }

        if (await EpisodeFormAsync(episode) is not { } edited)
        {
            return;
        }

        if (edited.Key != episode.Key && Episodes.Read(path).Any(e => e.Key.Equals(edited.Key, StringComparison.CurrentCultureIgnoreCase)))
        {
            await Settings.Dialogs.ShowMessageAsync(this, $"C'è già un episodio «{edited.Key}»: cambia il titolo.");
            return;
        }

        Episode? saved = null;
        if (await WriteAsync(() => saved = Episodes.Update(path, episode.Key, _ => edited)) && saved is not null)
        {
            if (edited.Facts != episode.Facts || edited.Reaction != episode.Reaction || edited.Outcome != episode.Outcome
                || !edited.People.SequenceEqual(episode.People))
            {
                ChatCommandRequested?.Invoke($"/episodio-analizza {saved.Key}");
            }

            Reload();
        }
    }

    // ---- Costi ------------------------------------------------------------------------------------------------

    private PageContent CostsPage(string filter)
    {
        var header = new StringBuilder();
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_usage is { } usage)
        {
            var days = usage.Days;
            decimal Sum(Func<DateOnly, bool> when) => days.Where(d => when(d.Key)).Sum(d => d.Value.Cost);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            header.AppendLine($"**Oggi** {usage.TodayCost.ToString("0.00", Italian)} $ · **questa settimana** {Sum(d => d >= monday).ToString("0.00", Italian)} $ · "
                              + $"**questo mese** {Sum(d => d.Year == today.Year && d.Month == today.Month).ToString("0.00", Italian)} $")
                .AppendLine();

            header.AppendLine("| Settimana | Spesa | Turni |").AppendLine("|---|---|---|");
            var first = days.Count == 0 ? today : days.Keys.Min();
            for (var week = 0; week < 8 && monday.AddDays(-7 * week).AddDays(6) >= first; week++)
            {
                var start = monday.AddDays(-7 * week);
                var inWeek = days.Where(d => d.Key >= start && d.Key < start.AddDays(7)).ToList();
                var cost = inWeek.Sum(d => d.Value.Cost);
                header.AppendLine($"| {start.ToString("d MMM", Italian)} | {Bar(cost, 10m)} {cost.ToString("0.00", Italian)} $ | {inWeek.Sum(d => d.Value.Turns)} |");
            }

            var models = usage.Models.OrderByDescending(m => m.Value.Cost).ToList();
            if (models.Count > 0)
            {
                header.AppendLine().AppendLine("| Modello | Turni | Spesa | Media a turno |").AppendLine("|---|---|---|---|");
                foreach (var (model, stat) in models)
                {
                    header.AppendLine($"| {model} | {stat.Turns} | {stat.Cost.ToString("0.00", Italian)} $ | {(stat.Turns == 0 ? 0 : stat.Cost / stat.Turns).ToString("0.000", Italian)} $ |");
                }
            }

            header.AppendLine().AppendLine("*Lo storico per giorno e per modello parte da quando il Buddy ha iniziato a registrarlo.*");
        }

        // Tasks by Claude cost, from the "costo: …" tag the flow writes on the item.
        var tasks = (_board?.Columns ?? [])
            .SelectMany(c => c.Items.Select(i => (Column: c, Item: i)))
            .Where(x => x.Item.Id is not null && x.Item.Cost is > 0)
            .GroupBy(x => x.Item.Id!)
            .Select(g => g.OrderByDescending(x => x.Item.Date ?? DateOnly.MinValue).First())
            .Where(x => filter.Length == 0 || x.Item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                           || x.Item.Id!.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Item.Cost)
            .ToList();
        var total = tasks.Sum(t => t.Item.Cost ?? 0);
        var cards = tasks.Select(t =>
        {
            var chips = new List<Chip> { new($"💳 {t.Item.Cost!.Value.ToString("0.00", Italian)} $", t.Item.Cost > 10 ? ChipKind.Hot : t.Item.Cost > 4 ? ChipKind.Warm : ChipKind.Plain), new(t.Column.Title, ChipKind.Plain) };
            if (t.Item.Field("stima") is { } estimate)
            {
                chips.Add(new Chip($"⏱ stima {estimate}", ChipKind.Plain));
            }

            if (t.Item.Rework > 0)
            {
                chips.Add(new Chip($"🔁 {t.Item.Rework}", ChipKind.Warm));
            }

            return new PageCard($"{t.Item.Id} · {t.Item.Title.Replace("`", "")}", t.Item.Date?.ToString("d MMMM yyyy", Italian), chips, null,
                t.Column.NotePath, total > 0 && t.Item.Cost >= total / Math.Max(1, tasks.Count) * 2 ? "#E8857A" : "#B99BF5");
        }).ToList();
        return new PageContent(
            $"Quanto hai speso con Claude · {tasks.Count} task con il costo segnato, {total.ToString("0.00", Italian)} $ in tutto",
            header.ToString(), cards, [],
            "Nessun task ha ancora il costo segnato (lo scrive /chiudi nella voce: «costo: 7,22 $»).");
    }

    private static string Bar(decimal value, decimal full)
    {
        var blocks = full <= 0 ? 0 : (int)Math.Round(Math.Min(1, value / full) * 10);
        return new string('█', blocks) + new string('░', 10 - blocks);
    }

    // ---- Settimana --------------------------------------------------------------------------------------------

    private async Task<PageContent> WeekPageAsync(bool refresh)
    {
        if (!refresh && _weekCache is { } cached && cached.Offset == _weekOffset && DateTime.Now - cached.At < TimeSpan.FromMinutes(2))
        {
            return cached.Content;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - 7 * _weekOffset);
        var sunday = monday.AddDays(6);

        // Commits of the user in the week, per day ("2026-11-03 subject").
        _repo ??= new GitRepo(Microsoft.Extensions.Options.Options.Create(_settings), TimeProvider.System);
        var commits = new Dictionary<DateOnly, List<string>>();
        if (_repo.IsAvailable)
        {
            var log = await Task.Run(() => _repo.GetLogAsync(TimeSpan.FromDays(today.DayNumber - monday.DayNumber + 1)));
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Length > 11 && DateOnly.TryParse(line[..10], CultureInfo.InvariantCulture, out var day) && day >= monday && day <= sunday)
                {
                    (commits.TryGetValue(day, out var list) ? list : commits[day] = []).Add(line[11..]);
                }
            }
        }

        var closed = (_board?.Columns ?? []).Where(c => c.Key is "fatti" or "controllare")
            .SelectMany(c => c.Items.Select(i => (Column: c, Item: i)))
            .Where(x => x.Item.Date is { } d && d >= monday && d <= sunday)
            .ToList();
        var spending = _usage?.Days ?? new Dictionary<DateOnly, Buddy.Core.Claude.ClaudeUsage.ModelStat>();
        var diary = string.IsNullOrWhiteSpace(_settings.Mentor.JournalPath) ? null : Path.Combine(_settings.Mentor.JournalPath, "Diario");

        // Who you last heard on each day (only the latest contact per person is kept, in the Persone note).
        var people = NoteTitled(People.Note) is { } peopleNote ? People.Parse(peopleNote.Content) : [];

        // Not only the code: commitments done and taken, calls, agreements and decisions, episodes with colleagues.
        var said = Said.All(_vault, _settings.ProjectPath);
        var activity = WeekActivity.Collect(said, CallTranscripts.List(_vault), Episodes.Read(Episodes.PathFor(_settings)), monday, sunday);
        var week = new StringBuilder(); // the week as shown, for the recap

        var cards = new List<PageCard>();
        for (var day = monday; day <= sunday; day = day.AddDays(1))
        {
            var chips = new List<Chip>();
            var body = new StringBuilder();
            foreach (var person in people.Where(p => p.LastContact == day))
            {
                chips.Add(new Chip($"👥 {(person.Nicknames.Count > 0 ? person.Nicknames[0] : person.Name)}", ChipKind.Plain));
            }

            if (spending.TryGetValue(day, out var spent) && spent.Cost > 0)
            {
                chips.Add(new Chip($"💳 {spent.Cost.ToString("0.00", Italian)} $", spent.Cost > 3 ? ChipKind.Warm : ChipKind.Plain));
            }

            if (commits.TryGetValue(day, out var dayCommits))
            {
                chips.Add(new Chip($"⎇ {dayCommits.Count} commit", ChipKind.Plain));
                foreach (var subject in dayCommits.Take(4))
                {
                    body.AppendLine($"- {subject.Replace("`", "")}");
                }

                if (dayCommits.Count > 4)
                {
                    body.AppendLine($"- …e altri {dayCommits.Count - 4}");
                }
            }

            foreach (var (column, item) in closed.Where(x => x.Item.Date == day))
            {
                chips.Add(new Chip(column.Key == "fatti" ? $"✅ {item.Id} chiuso" : $"🔍 {item.Id} in revisione", ChipKind.Good));
            }

            if (activity.TryGetValue(day, out var did))
            {
                AddActivity(did, chips, body, today);
            }

            var mood = diary is null ? null : Mood(Path.Combine(diary, $"{day:yyyy-MM-dd}.md"));
            if (mood is { } m)
            {
                chips.Add(new Chip($"{(m >= 4 ? "🙂" : m == 3 ? "😐" : "🙁")} umore {m}/5", m >= 4 ? ChipKind.Good : m <= 2 ? ChipKind.Hot : ChipKind.Plain));
            }

            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (weekend && chips.Count == 0)
            {
                continue;
            }

            cards.Add(new PageCard(day.ToString("dddd d MMMM", Italian), day == today ? "oggi" : null, chips,
                body.Length > 0 ? NoteLinks.ToMarkdown(body.ToString()) : (chips.Count == 0 ? "*niente di registrato*" : null), null,
                day == today ? "#7FD1AE" : "#A0A4B8"));
            if (chips.Count > 0 || body.Length > 0)
            {
                week.AppendLine($"### {day.ToString("dddd d MMMM", Italian)}")
                    .AppendLine(string.Join(" · ", chips.Select(c => c.Text)))
                    .AppendLine(body.ToString().TrimEnd()).AppendLine();
            }
        }

        var weekCost = spending.Where(d => d.Key >= monday && d.Key <= sunday).Sum(d => d.Value.Cost);
        var done = activity.Values.Sum(a => a.Done.Count);
        var calls = activity.Values.Sum(a => a.Calls.Count);
        var agreed = activity.Values.Sum(a => a.Said.Count(e => e.Kind is SaidKind.Accordo or SaidKind.Decisione));
        _weekRecap = WeekActivity.RecapPrompt(monday, sunday, week.ToString(), said.Where(e => e.IsOpenCommitment).ToList(), today, _settings.Mentor.UserName);
        var content = new PageContent(
            $"Settimana dal {monday.ToString("d MMMM", Italian)} al {sunday.ToString("d MMMM", Italian)} · {commits.Values.Sum(c => c.Count)} commit · "
            + $"{closed.Count} task chiusi o in revisione · {done} impegni chiusi · {calls} call · {agreed} accordi e decisioni · "
            + $"{weekCost.ToString("0.00", Italian)} $ con Claude",
            null, cards,
            [
                new PageButton("‹ Settimana prima", "week:prev"),
                new PageButton("Settimana dopo ›", "week:next"),
                new PageButton("🧠 Recap della settimana", "week:recap",
                    "Claude scrive il recap da tutto quello che vedi qui: commit, task, impegni, call, accordi e decisioni, e cosa resta aperto"),
                new PageButton("↻ Aggiorna", "refresh"),
            ],
            "Niente in questa settimana.");
        _weekCache = (_weekOffset, DateTime.Now, content);
        return content;
    }

    /// <summary>The request behind "🧠 Recap della settimana", for the week on screen.</summary>
    private string? _weekRecap;

    /// <summary>A day's activity besides the commits, as chips and short lists on its card (at most 4 per list).</summary>
    private static void AddActivity(DayActivity did, List<Chip> chips, StringBuilder body, DateOnly today)
    {
        string Source(SaidEntry e) => e.SourceTitle is { } title && title != Said.Note ? $" *(call «{title}»)*" : "";
        void List(string heading, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0)
            {
                return;
            }

            body.AppendLine().AppendLine($"**{heading}**").AppendLine();
            foreach (var line in lines.Take(4))
            {
                body.AppendLine($"- {line}");
            }

            if (lines.Count > 4)
            {
                body.AppendLine($"- …e altri {lines.Count - 4}");
            }
        }

        if (did.Done.Count > 0)
        {
            chips.Add(new Chip($"✅ {did.Done.Count} impegn{(did.Done.Count == 1 ? "o chiuso" : "i chiusi")}", ChipKind.Good));
        }

        if (did.Calls.Count > 0)
        {
            chips.Add(new Chip($"🎙 {did.Calls.Count} call", ChipKind.Plain));
        }

        var agreed = did.Said.Where(e => e.Kind is SaidKind.Accordo or SaidKind.Decisione).ToList();
        if (agreed.Count > 0)
        {
            chips.Add(new Chip($"🤝 {agreed.Count} accord{(agreed.Count == 1 ? "o o decisione" : "i e decisioni")}", ChipKind.Plain));
        }

        if (did.Episodes.Count > 0)
        {
            chips.Add(new Chip($"📓 {did.Episodes.Count} episod{(did.Episodes.Count == 1 ? "io" : "i")}", ChipKind.Plain));
        }

        List("Impegni chiusi", did.Done.Select(e => $"✔ {(e.Who.Length > 0 ? $"**{e.Who}** · " : "")}{e.Text}{Source(e)}").ToList());
        List("Call", did.Calls.Select(c => $"🎙 [[{Path.GetFileNameWithoutExtension(c.RelativePath)}|{c.Title}]]"
                                            + (c.People.Count > 0 ? $" con {string.Join(", ", c.People)}" : "")).ToList());
        List("Accordi e decisioni", agreed.Select(e => $"{(e.Kind == SaidKind.Decisione ? "⚖" : "🤝")} {(e.Who.Length > 0 ? $"**{e.Who}** · " : "")}{e.Text}{Source(e)}").ToList());
        List("Impegni presi", did.Taken.Where(e => !e.Done).Select(e => $"☐ {(e.Who.Length > 0 ? $"**{e.Who}** · " : "")}{e.Text}"
                                                                    + (e.Due is { } due ? $" · entro {due.ToString("d MMM", Italian)}{(due < today ? " ⚠" : "")}" : "")).ToList());
        List("Cose dette", did.Said.Where(e => e.Kind == SaidKind.Info).Select(e => $"💬 {(e.Who.Length > 0 ? $"**{e.Who}** · " : "")}{e.Text}").ToList());
        List("Episodi", did.Episodes.Select(e => $"📓 {e.Title}").ToList());
    }

    /// <summary>The mood of a day from its journal note ("umore 4", "4/5" in the check-out), or null.</summary>
    internal static int? Mood(string diaryFile)
    {
        if (!File.Exists(diaryFile))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(diaryFile);
            var match = Regex.Match(text, @"umore[^0-9\n]{0,20}([1-5])\b|\b([1-5])\s*/\s*5\b", RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // ---- PR ---------------------------------------------------------------------------------------------------

    private async Task<PageContent> PullRequestsPageAsync(string filter, bool refresh)
    {
        await LoadPullRequestsAsync(refresh);
        if (_prs is null)
        {
            return new PageContent("", null, [], [new PageButton("↻ Riprova", "refresh")], "Non riesco a leggere le PR.");
        }

        var (_, prs, error) = _prs!.Value;
        var columns = (_board?.Columns ?? []).SelectMany(c => c.Items.Where(i => i.Id is not null).Select(i => (Id: i.Id!, c.Title))).ToList();
        var cards = prs
            .Where(p => filter.Length == 0 || p.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                           || p.Branch.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .Select(p =>
            {
                var chips = new List<Chip>();
                if (p.IsDraft)
                {
                    chips.Add(new Chip("bozza", ChipKind.Plain));
                }

                if (p.ChecksLabel.Length > 0)
                {
                    chips.Add(new Chip(p.ChecksLabel, p.Checks == "failure" ? ChipKind.Hot : p.Checks == "success" ? ChipKind.Good : ChipKind.Warm));
                }

                if (p.ReviewLabel.Length > 0)
                {
                    chips.Add(new Chip(p.ReviewLabel, p.Review == "APPROVED" ? ChipKind.Good : p.Review == "CHANGES_REQUESTED" ? ChipKind.Hot : ChipKind.Plain));
                }

                var task = p.TaskId is { } id && columns.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { Id: not null } where
                    ? $"Task **{id}** in «{where.Title}»"
                    : p.TaskId is { } loose ? $"Task {loose} (non trovato nella bacheca)" : null;
                if (p.TaskId is { } taskId)
                {
                    chips.Add(new Chip(taskId, ChipKind.Plain));
                }

                return new PageCard($"#{p.Number} · {p.Title}",
                    $"{p.Branch}{(p.Updated is { } u ? $" · aggiornata {u.LocalDateTime.ToString("d MMM HH:mm", Italian)}" : "")}",
                    chips, task, p.Url, p.Checks == "failure" ? "#E8857A" : "#6FA8DC");
            }).ToList();
        return new PageContent(
            error is null ? $"Le tue PR aperte su GitHub ({prs.Count}) · click per aprirla nel browser" : "PR aperte su GitHub",
            null, cards, [new PageButton("↻ Aggiorna", "refresh", "Rilegge le PR con gh")],
            error ?? "Nessuna PR aperta a tuo nome. 🎉");
    }
}
