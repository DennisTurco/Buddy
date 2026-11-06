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
    private static readonly HashSet<string> GenericPages = ["week", "costs", "decisions", "glossary", "prs"];

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

    /// <summary>The side bar's extra entries: your pages, then "＋ Nuova pagina…".</summary>
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

    private void OnPageCardTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PageCard { Target: { } target })
        {
            return;
        }

        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
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

        var cards = new List<PageCard>();
        for (var day = monday; day <= sunday; day = day.AddDays(1))
        {
            var chips = new List<Chip>();
            var body = new StringBuilder();
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
                body.Length > 0 ? body.ToString() : (chips.Count == 0 ? "*niente di registrato*" : null), null,
                day == today ? "#7FD1AE" : "#A0A4B8"));
        }

        var weekCost = spending.Where(d => d.Key >= monday && d.Key <= sunday).Sum(d => d.Value.Cost);
        var content = new PageContent(
            $"Settimana dal {monday.ToString("d MMMM", Italian)} al {sunday.ToString("d MMMM", Italian)} · {commits.Values.Sum(c => c.Count)} commit · "
            + $"{closed.Count} task chiusi o in revisione · {weekCost.ToString("0.00", Italian)} $ con Claude",
            null, cards,
            [
                new PageButton("‹ Settimana prima", "week:prev"),
                new PageButton("Settimana dopo ›", "week:next"),
                new PageButton("↻ Aggiorna", "refresh"),
            ],
            "Niente in questa settimana.");
        _weekCache = (_weekOffset, DateTime.Now, content);
        return content;
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
