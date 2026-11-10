using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// What each step of the flow looks like and which Claude Code skills act on it. A skill's button appears only when
/// the skill is installed (see <see cref="Buddy.Core.Claude.ClaudeSkills"/>).
/// </summary>
public static class Flow
{
    private static readonly Dictionary<string, string> Accents = new()
    {
        ["appunti"] = "#A29DB8",
        ["valutare"] = "#F2B544",
        ["todo"] = "#7FD1AE",
        ["attesa"] = "#8AB4F8",
        ["corso"] = "#FF8A65",
        ["controllare"] = "#B99BF5",
        ["fatti"] = "#5FBF77",
        ["rifiutati"] = "#E57373",
    };

    /// <summary>Steps of the open flow; Fatti and Rifiutati go to the archive.</summary>
    public static readonly string[] Active = ["appunti", "valutare", "todo", "attesa", "corso", "controllare"];

    /// <summary>The board's columns: In attesa has none, its cards sit in Da fare with a "⏸ In attesa" band.</summary>
    public static readonly string[] Board = ["appunti", "valutare", "todo", "corso", "controllare"];

    /// <summary><see cref="FlowAction.Skill"/> of the buttons that only change the task's state (⏸/▶), without Claude.</summary>
    public const string StateSkill = "@stato";

    /// <summary>Buttons that move a task between Da fare, In attesa and In corso; the command is the target column.</summary>
    public static IReadOnlyList<FlowAction> StateActions(string key) => key switch
    {
        "todo" =>
        [
            new FlowAction("▶ Inizia", StateSkill, "corso", "Lo sposta in «In corso» (▶ nella voce)"),
            new FlowAction("⏸ Metti in attesa", StateSkill, "attesa", "Resta in Da fare con la fascia «In attesa» (⏸ nella voce); cosa aspetta lo scrivi con ✏ Modifica"),
        ],
        "attesa" => [new FlowAction("▶ Non aspetta più", StateSkill, "todo", "Toglie ⏸: torna un task normale di Da fare")],
        "corso" =>
        [
            new FlowAction("⏸ Metti in attesa", StateSkill, "attesa", "Si è bloccato: torna in Da fare con la fascia «In attesa»"),
            new FlowAction("↩ Rimetti in Da fare", StateSkill, "todo", "Non ci stai più lavorando"),
        ],
        _ => [],
    };

    public static IBrush Accent(string key) => new SolidColorBrush(Color.Parse(Accents.GetValueOrDefault(key, "#A29DB8")));

    internal const string RejectAsk = "Motivo del rifiuto (e, se vuoi, «Riconsiderare se: …»):";

    /// <summary>Buttons on a whole column (no task id).</summary>
    public static IReadOnlyList<FlowAction> ColumnActions(string key, IReadOnlySet<string> skills) => Filter(skills, key switch
    {
        "appunti" => [new FlowAction("🗂 Smista", "smista", "/smista", "Claude porta ogni appunto in To-Do, Da valutare o In attesa")],
        "valutare" => [new FlowAction("🤔 Valuta", "valuta", "/valuta", "Claude te le propone una alla volta: approva, rimanda o rifiuta")],
        _ => [],
    });

    /// <summary>Buttons on one item, by the step it is in. Commands use {id} and, when there is a question, {input}.</summary>
    public static IReadOnlyList<FlowAction> CardActions(string key, IReadOnlySet<string> skills) => Filter(skills, key switch
    {
        "todo" =>
        [
            new FlowAction("🛠 Lavora", "lavora", "/lavora {id}", "Claude implementa il task e lo porta in Da controllare"),
            new FlowAction("🧭 Guidami", "guidami", "/guidami {id}", "Lo fai tu, con Claude che ti guida passo passo"),
            new FlowAction("📄 Analisi", "analisi", "/analisi {id}", "Claude scrive l'analisi di implementazione nel vault"),
            new FlowAction("🧪 Test manuali", "test-manuali", "/test-manuali {id}", "Claude scrive o aggiorna il documento dei test manuali (Test manuali/Test manuali ID)"),
            new FlowAction("🚫 Rifiuta…", "rifiuta", "/rifiuta {id} {input}", "Lo sposta in To-Do Rifiutati con il motivo", RejectAsk),
        ],
        "attesa" =>
        [
            new FlowAction("🛠 Lavora", "lavora", "/lavora {id}", "Non aspetta più: Claude lo implementa"),
            new FlowAction("🚫 Rifiuta…", "rifiuta", "/rifiuta {id} {input}", "Lo sposta in To-Do Rifiutati con il motivo", RejectAsk),
        ],
        "corso" =>
        [
            new FlowAction("🛠 Lavora", "lavora", "/lavora {id}", "Claude riprende il task e lo porta in Da controllare"),
            new FlowAction("🧭 Guidami", "guidami", "/guidami {id}", "Lo fai tu, con Claude che ti guida passo passo"),
            new FlowAction("🧪 Test manuali", "test-manuali", "/test-manuali {id}", "Claude scrive o aggiorna il documento dei test manuali (Test manuali/Test manuali ID)"),
            new FlowAction("🚫 Rifiuta…", "rifiuta", "/rifiuta {id} {input}", "Lo sposta in To-Do Rifiutati con il motivo", RejectAsk),
        ],
        "valutare" =>
        [
            new FlowAction("🤔 Valuta", "valuta", "/valuta {id}", "Claude te la ripropone: approva, rimanda o rifiuta"),
            new FlowAction("🚫 Rifiuta…", "rifiuta", "/rifiuta {id} {input}", "La sposta in To-Do Rifiutati con il motivo", RejectAsk),
        ],
        "controllare" =>
        [
            new FlowAction("✅ Chiudi…", "chiudi", "/chiudi {id} {input}", "Approvato: va in To-Do Fatti con il link alla PR", "Link o numero della PR:"),
            new FlowAction("🧪 Test manuali", "test-manuali", "/test-manuali {id}", "Claude scrive o aggiorna il documento dei test manuali (Test manuali/Test manuali ID)"),
            new FlowAction("🌐 Verifica web", "verifica-web", "/verifica-web {id}", "Claude prova la modifica nel browser seguendo «Come testare»"),
            new FlowAction("🔁 Rifai", "lavora", "/lavora {id}", "Non va: Claude lo rifà (scrivigli cosa non va)"),
        ],
        _ => [],
    });

    private static List<FlowAction> Filter(IReadOnlySet<string> skills, FlowAction[] actions) =>
        actions.Where(a => skills.Contains(a.Skill)).ToList();
}

/// <param name="Command">Sent to Claude: {id} is the task id, {input} the answer to <paramref name="Ask"/>.</param>
/// <param name="Ask">A question to ask first (e.g. the PR link), or null.</param>
public sealed record FlowAction(string Label, string Skill, string Command, string Tip, string? Ask = null);

/// <summary>A board column as shown: title, count, accent colour, its buttons and the cards that pass the filter.</summary>
public sealed class ColumnView(BoardColumn column, IReadOnlyList<CardView> cards, IReadOnlyList<FlowAction> actions)
{
    public BoardColumn Column { get; } = column;

    public string Header => $"{Column.Icon}  {Column.Title}";

    public IReadOnlyList<CardView> Cards { get; } = cards;

    public string Count => Cards.Count == Column.Items.Count ? $"{Cards.Count}" : $"{Cards.Count}/{Column.Items.Count}";

    public bool IsEmpty => Cards.Count == 0;

    public string EmptyText => Column.Items.Count == 0 ? EmptyFor(Column.Key) : "Nessuna voce con questo filtro";

    public IBrush Accent { get; } = Flow.Accent(column.Key);

    public string NoteTip => $"{Column.NotePath} — click per aprirla in Obsidian";

    public IReadOnlyList<FlowAction> Actions { get; } = actions;

    /// <summary>The column takes items written by hand ("＋": appunti, Da fare, In attesa, Da controllare).</summary>
    public bool CanAdd => Buddy.Core.Vault.BoardEntries.Sections.ContainsKey(Column.Key);

    public string AddTip => Column.Key == "appunti" ? "Nuovo appunto: titolo, dettagli e tag" : $"Nuova voce in «{Column.Title}», con tutti i campi";

    public bool HasActions => Actions.Count > 0 && Column.Items.Count > 0;

    private static string EmptyFor(string key) => key switch
    {
        "appunti" => "Inbox vuota ✨",
        "todo" => "Niente da fare: tutto in pari 🎉",
        "corso" => "Niente in corso: trascina qui un task o usa ▶ Inizia",
        "controllare" => "Niente da rivedere",
        _ => "Vuota",
    };
}

/// <summary>One item: a compact card on the board, the full view in the side panel.</summary>
public sealed class CardView : INotifyPropertyChanged
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");
    private bool _isSelected;

    /// <param name="pullRequest">The open PR of the card's task, if known: its CI (and review) becomes a chip.</param>
    /// <param name="manualTests">Cases ticked and total in the task's manual tests document, if it has one.</param>
    public CardView(BoardItem item, BoardColumn column, DateOnly today, Buddy.Core.Repo.PullRequestInfo? pullRequest = null,
        (int Done, int Total)? manualTests = null)
    {
        Item = item;
        Column = column;
        Accent = Flow.Accent(column.Key);
        PullRequest = pullRequest;
        var chips = BuildChips(item, today);
        if (pullRequest is { } pr)
        {
            chips.Insert(0, new Chip(
                (pr.Checks switch { "success" => "✅ CI", "failure" => "❌ CI", "pending" => "⏳ CI", _ => "PR" }) + $" #{pr.Number}"
                + (pr.Review == "CHANGES_REQUESTED" ? " · ✏" : pr.Review == "APPROVED" ? " · 👍" : ""),
                pr.Checks switch { "success" => ChipKind.Good, "failure" => ChipKind.Hot, "pending" => ChipKind.Warm, _ => ChipKind.Plain }));
        }

        // Manual tests of an open task: how far along, without opening it (green when all ticked).
        if (manualTests is { Total: > 0 } manual && !item.Done && !item.Rejected)
        {
            chips.Insert(0, new Chip($"🧪 {manual.Done}/{manual.Total}",
                manual.Done == manual.Total ? ChipKind.Good : manual.Done > 0 ? ChipKind.Warm : ChipKind.Plain));
        }

        Chips = chips;
        WaitingBanner = item.IsDue(today) ? "🔓 Non aspetta più" : "⏸ In attesa";
        Priority = item.Field("priorita", "priority") is { } p && Regex.Match(p, @"\d") is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 9;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BoardItem Item { get; }

    public BoardColumn Column { get; }

    public IBrush Accent { get; }

    public string? Id => Item.Id;

    public bool HasId => Item.Id is not null;

    /// <summary>The item's tags ([tag:: bug] or [tag:: bug, ui]), shown as coloured labels next to the id.</summary>
    public IReadOnlyList<TagOption> TagChips => Item.TagList.Select(t => new TagOption(t, Tags.Brush(t), false)).ToList();

    public bool HasTag => Item.TagList.Count > 0;

    public string Title => Item.Title.Replace("`", "");

    /// <summary>A ⏸ task, shown in Da fare with a band and its own colour.</summary>
    public bool IsWaiting => Column.Key == "attesa";

    /// <summary>"⏸ In attesa", or "🔓 Non aspetta più" once its "fino" day has come (the date itself is a chip).</summary>
    public string WaitingBanner { get; }

    public bool IsClosed => Item.Done || Item.Rejected;

    public string DateText => Item.Date is { } date ? date.ToString("d MMM yyyy", Culture) : "";

    public string? Note => Item.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Item.Note);

    public IReadOnlyList<Chip> Chips { get; }

    /// <summary>The open pull request of the card's task (GitHub or Azure Repos), when there is one.</summary>
    public Buddy.Core.Repo.PullRequestInfo? PullRequest { get; }

    public bool HasChips => Chips.Count > 0;

    /// <summary>1 for P1 … 9 when there is no priority: the board sorts "Da fare" by it.</summary>
    public int Priority { get; }

    public string ColumnLabel => $"{Column.Icon} {Column.Title}";

    public string DetailsMarkdown => string.Join('\n', Item.Details.Select(d => "- " + NoteLinks.ToMarkdown(d)));

    public bool HasDetails => Item.Details.Count > 0;

    public string NoteMarkdown => NoteLinks.ToMarkdown(Item.Note ?? "");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <summary>Same item after a reload (the board is rebuilt): same note and same text, or same id.</summary>
    public bool SameAs(CardView other) =>
        Column.Key == other.Column.Key && (Item.Raw == other.Item.Raw || (Id is not null && Id == other.Id));

    private static List<Chip> BuildChips(BoardItem item, DateOnly today)
    {
        var chips = new List<Chip>();
        if (item.Field("priorita", "priority") is { } priority)
        {
            chips.Add(new Chip(priority, priority.Contains('1') ? ChipKind.Hot : priority.Contains('2') ? ChipKind.Warm : ChipKind.Plain));
        }

        if (item.Due is { } due)
        {
            var late = item.IsDue(today) && !item.Done && !item.Rejected;
            chips.Add(new Chip($"{(late ? "⏰" : "📅")} {item.DueLabel} {due.ToString("d MMM", Culture)}", late ? ChipKind.Hot : ChipKind.Plain));
        }

        if (item.Field(Buddy.Core.Vault.Reminders.Field) is { } remind && DateOnly.TryParse(remind, CultureInfo.InvariantCulture, out var remindOn))
        {
            chips.Add(new Chip($"🔔 {remindOn.ToString("d MMM", Culture)}", remindOn <= today ? ChipKind.Warm : ChipKind.Plain));
        }

        if (item.Field("stima", "estimate") is { } estimate)
        {
            chips.Add(new Chip($"⏱ {estimate}", ChipKind.Plain));
        }

        if (item.Field("consiglio") is { } advice)
        {
            chips.Add(new Chip($"consiglio {advice}", ChipKind.Plain));
        }

        if (item.Field("tipo", "type") is { } type)
        {
            chips.Add(new Chip(type, ChipKind.Plain));
        }

        if (item.Rework > 0)
        {
            chips.Add(new Chip($"🔁 {item.Rework}", item.Rework > 1 ? ChipKind.Warm : ChipKind.Plain));
        }

        chips.AddRange(item.Tags.Select(t => new Chip(t, t.StartsWith("PR", StringComparison.OrdinalIgnoreCase) ? ChipKind.Good : ChipKind.Plain)));
        return chips;
    }
}

public enum ChipKind
{
    Plain,
    Good,
    Warm,
    Hot,
}

public sealed record Chip(string Text, ChipKind Kind)
{
    public bool IsGood => Kind == ChipKind.Good;

    public bool IsWarm => Kind == ChipKind.Warm;

    public bool IsHot => Kind == ChipKind.Hot;
}

/// <summary>A number on the overview: click to go to its view.</summary>
public sealed record TileView(string Key, string Label, string Value, string Sub, IBrush Accent, string Target)
{
    public bool HasSub => Sub.Length > 0;
}

/// <summary>An item that needs a look now, with why.</summary>
public sealed record AttentionView(CardView Card, string Reason);

/// <summary>One folder in the graph legend, with its colour.</summary>
public sealed record GraphLegendItem(string Folder, Avalonia.Media.IBrush Color);

/// <summary>A side-bar entry.</summary>
public sealed record NavItem(string Key, string Label, string Badge)
{
    public bool HasBadge => Badge.Length > 0;
}

/// <summary>A "Stato attuale" line: label and markdown value.</summary>
public sealed record StatusView(string Label, string Value)
{
    public string Markdown => NoteLinks.ToMarkdown(Value);
}

/// <summary>A vault note in the notes list.</summary>
public sealed record NoteView(VaultNote Note, string? Root = null)
{
    public string Title => Note.Title;

    public string Folder => Path.GetDirectoryName(Note.RelativePath) is { Length: > 0 } folder ? folder : "";

    public bool HasFolder => Folder.Length > 0;

    /// <summary>The note for the viewer: no front matter, Obsidian links turned into clickable ones.</summary>
    public string Markdown => NoteLinks.ToMarkdown(StripFrontMatter(Note.Content), FullFolder, Root);

    /// <summary>The note's folder on disk, where its images are; null without a project folder.</summary>
    public string? FullFolder => Root is null ? null : Path.GetDirectoryName(Path.Combine(Root, Note.RelativePath));

    internal static string StripFrontMatter(string content)
    {
        var text = content.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return text;
        }

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        return end < 0 ? text : text[(end + 4)..].TrimStart('-', '\n');
    }
}

/// <summary>
/// Obsidian's [[Note]], [[Folder/Note|alias]], ![[image.png]], callouts and comments in plain markdown: links use the
/// "note:" scheme, which the project window opens in its notes view.
/// </summary>
public static partial class NoteLinks
{
    public const string Scheme = "note:";

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"];

    /// <param name="folder">The note's folder: relative images (![alt](image.png)) are read from there.</param>
    /// <param name="root">The project folder, searched for ![[image.png]] embeds that are not next to the note.</param>
    public static string ToMarkdown(string text, string? folder = null, string? root = null)
    {
        text = HtmlComment().Replace(text, "");

        // The viewer has no idea where the note is: give relative images an absolute file:// address.
        if (folder is not null)
        {
            text = MarkdownImage().Replace(text, m =>
            {
                var path = m.Groups["path"].Value.Trim('<', '>');
                return path.Contains(':') || ImagePath(Uri.UnescapeDataString(path), folder, null) is not { } full
                    ? m.Value
                    : $"![{m.Groups["alt"].Value}]({new Uri(full).AbsoluteUri})";
            });
        }

        // Callout header "> [!warning] Credenziali" → "> ⚠️ **Credenziali**".
        text = Callout().Replace(text, m =>
        {
            var icon = m.Groups["type"].Value.ToLowerInvariant() switch
            {
                "warning" or "caution" or "attention" => "⚠️",
                "tip" or "hint" => "💡",
                "example" => "📎",
                "danger" or "error" or "bug" => "⛔",
                "question" or "help" or "faq" => "❓",
                "success" or "check" or "done" => "✅",
                _ => "ℹ️",
            };
            var title = m.Groups["title"].Value.Trim();
            return $"{m.Groups["quote"].Value}{icon} **{(title.Length > 0 ? title : m.Groups["type"].Value)}**";
        });

        return WikiLink().Replace(text, m =>
        {
            var target = m.Groups["target"].Value.Trim();
            var name = Path.GetFileName(target.Replace('\\', '/'));
            if (m.Groups["embed"].Success)
            {
                return ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()) && ImagePath(target, folder, root) is { } full
                    ? $"![{name}]({new Uri(full).AbsoluteUri})"
                    : $"🖼 {name}";
            }

            var label = m.Groups["alias"].Success ? m.Groups["alias"].Value.Trim() : name;
            return $"[{label.Replace("[", "").Replace("]", "")}]({Scheme}{Uri.EscapeDataString(target)})";
        });
    }

    /// <summary>The note title a "note:" link points to, or null for any other link.</summary>
    public static string? Target(string link) =>
        link.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Uri.UnescapeDataString(link[Scheme.Length..]).Replace('\\', '/'))
            : null;

    /// <summary>
    /// The image file for a reference: next to the note, then (like Obsidian's shortest-path embeds) from the project
    /// folder, then anywhere under it by file name. Null when it does not exist.
    /// </summary>
    private static string? ImagePath(string reference, string? folder, string? root)
    {
        reference = reference.Replace('\\', '/').TrimStart('/');
        foreach (var baseFolder in new[] { folder, root })
        {
            if (baseFolder is not null && Path.GetFullPath(Path.Combine(baseFolder, reference)) is var full && File.Exists(full))
            {
                return full;
            }
        }

        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(root, Path.GetFileName(reference), SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<path><[^>]+>|[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^(?<quote>>\s*)\[!(?<type>\w+)\][+-]?(?<title>[^\n]*)$", RegexOptions.Multiline)]
    private static partial Regex Callout();

    [GeneratedRegex(@"(?<embed>!)?\[\[(?<target>[^\]|#]+)(?:#[^\]|]*)?(?:\|(?<alias>[^\]]+))?\]\]")]
    private static partial Regex WikiLink();
}

/// <summary>A step in the flow bar of the overview: its share of all items.</summary>
public sealed record SegmentView(string Label, int Count, IBrush Accent)
{
    public Avalonia.Controls.GridLength Width => new(Math.Max(Count, 0), Avalonia.Controls.GridUnitType.Star);

    public string Text => $"{Label} {Count}";
}

/// <summary>One week of the activity chart: items added and tasks closed, with bar heights in pixels.</summary>
public sealed record WeekView(string Label, int Added, int Closed, double AddedHeight, double ClosedHeight, bool IsCurrent)
{
    public string Tip => $"Settimana del {Label}: {Added} voci nuove, {Closed} task chiusi";

    public bool HasAdded => Added > 0;

    public bool HasClosed => Closed > 0;
}

/// <summary>One plan limit drawn like Claude Code's usage panel: label, reset time and share on one line, the bar below.</summary>
public sealed record LimitView(string Label, string Reset, string PercentText, double Percent, bool IsWarning, bool HasPercent)
{
    public static LimitView From(Buddy.Core.Claude.ClaudeRateLimit limit) => new(
        limit.Label, limit.ResetText, limit.PercentText, Math.Clamp((limit.Utilization ?? 0) * 100, 0, 100),
        limit.IsWarning || limit.IsBlocked || limit.Utilization >= 0.8, limit.Utilization is not null);
}

/// <summary>Builds the overview's chart data from the board.</summary>
public static class Activity
{
    /// <summary>
    /// The last <paramref name="weeks"/> weeks (Monday first): "added" counts the dates of items still in the flow or
    /// rejected (the day they were written), "closed" the dates in Fatti (the day they were approved).
    /// </summary>
    public static List<WeekView> Weeks(ProjectBoard board, DateOnly today, int weeks = 8, double maxHeight = 90)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var starts = Enumerable.Range(0, weeks).Select(i => monday.AddDays(-7 * (weeks - 1 - i))).ToList();
        int WeekOf(DateOnly date) => starts.FindLastIndex(s => date >= s);

        var added = new int[weeks];
        var closed = new int[weeks];
        foreach (var column in board.Columns)
        {
            foreach (var item in column.Items.Where(i => i.Date is not null))
            {
                var week = WeekOf(item.Date!.Value);
                if (week < 0 || item.Date > today)
                {
                    continue;
                }

                if (column.Key == "fatti")
                {
                    closed[week]++;
                }
                else
                {
                    added[week]++;
                }
            }
        }

        var max = Math.Max(1, Math.Max(added.Max(), closed.Max()));
        var culture = CultureInfo.GetCultureInfo("it-IT");
        return starts.Select((start, i) => new WeekView(
            start.ToString("d MMM", culture), added[i], closed[i],
            Math.Max(added[i] > 0 ? 4 : 0, added[i] * maxHeight / max),
            Math.Max(closed[i] > 0 ? 4 : 0, closed[i] * maxHeight / max),
            i == weeks - 1)).ToList();
    }
}

/// <summary>A manual test checklist in the list: task, progress, what is left.</summary>
public sealed class ManualDocView(ManualTestDoc doc, string? taskTitle)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");

    public ManualTestDoc Doc { get; } = doc;

    public string Title => Doc.TaskId is { } id && taskTitle is not null ? $"{id} · {taskTitle}" : Doc.TaskId ?? Doc.Title;

    public string? TaskId => Doc.TaskId;

    public double Percent => Doc.Total == 0 ? 0 : Doc.Done * 100.0 / Doc.Total;

    public bool IsComplete => Doc.Total > 0 && Doc.Done == Doc.Total;

    public string Progress => Doc.Total == 0 ? "nessun caso"
        : IsComplete ? $"✅ tutti i {Doc.Total} casi"
        : $"{Doc.Done}/{Doc.Total} casi" + (Doc.Redo > 0 ? $" · 🔁 {Doc.Redo} da rifare" : "");

    public string Updated => Doc.Updated is { } d ? "aggiornato il " + d.ToString("d MMM yyyy", Culture) : "";
}

/// <summary>A section of a checklist: title, its own progress and its lines.</summary>
public sealed record ManualSectionView(string Title, string Progress, IReadOnlyList<object> Items)
{
    public bool HasTitle => Title.Length > 0;
}

/// <summary>Plain lines between the cases (notes, queries, rollback), shown as markdown.</summary>
public sealed record ManualTextView(string Markdown);

/// <summary>
/// One case of a checklist. Ticking it writes "- [x]" in the note through <see cref="Toggle"/>; when the write is
/// refused (the note changed meanwhile) the box goes back.
/// </summary>
public sealed class CheckView(ManualTestLine line, Func<CheckView, bool, bool> toggle) : INotifyPropertyChanged
{
    private bool _checked = line.Checked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ManualTestLine Line { get; private set; } = line;

    /// <summary>The case without markdown marks, for the check box.</summary>
    public string Text { get; } = Regex.Replace(line.Text, @"\*\*|`|~~", "");

    public bool IsRedo => Line.IsRedo;

    public bool IsStruck => Line.IsStruck;

    public Avalonia.Thickness Indent => new(Line.Indented ? 26 : 0, 1, 0, 1);

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            if (toggle(this, value))
            {
                _checked = value;
                Line = Line with
                {
                    Checked = value,
                    Raw = Regex.Replace(Line.Raw, @"\[[ xX]\]", value ? "[x]" : "[ ]", RegexOptions.None, TimeSpan.FromSeconds(1)),
                };
            }

            // Notify in both cases: a refused write puts the box back.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}

/// <summary>A saved browser script from the "Test web" note.</summary>
public sealed record ScriptView(WebTestScript Script)
{
    public string RunTip => $"Lancia «{WebTestRun.CommandOf(Script)}» nella cartella degli script, senza Claude";

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");

    public string Name => Script.Name;

    /// <summary>What the script covers, plus any remark written after the outcome ("Nota: …").</summary>
    public string? Covers => string.Join("\n", new[] { Script.Covers, Remark }.Where(t => !string.IsNullOrWhiteSpace(t))).Replace("`", "");

    public bool HasCovers => !string.IsNullOrWhiteSpace(Covers);

    /// <summary>The same text as markdown (bold, code, [[links]]), one paragraph per part.</summary>
    public string CoversMarkdown => NoteLinks.ToMarkdown(string.Join("\n\n",
        new[] { Script.Covers, Remark }.Where(t => !string.IsNullOrWhiteSpace(t))));

    /// <summary>"(2/2, senza invio)" from "(2/2, senza invio). Nota: …": the short outcome for the chip.</summary>
    private string? Outcome => Script.Outcome is { } o && o.StartsWith('(') && o.IndexOf(')') is > 0 and var end ? o[..(end + 1)] : Script.Outcome is { Length: <= 30 } shortOne ? shortOne : null;

    private string? Remark => Script.Outcome is { } o && Outcome is { } shown && o.Length > shown.Length ? o[shown.Length..].Trim(' ', '.', '·') : Outcome is null ? Script.Outcome : null;

    public IReadOnlyList<Chip> Chips => new[]
        {
            Script.LastRun is { } run
                ? new Chip($"{(Script.Passed == false ? "❌" : Script.Passed == true ? "✅" : "▶")} {run.ToString("d MMM yyyy", Culture)}"
                           + (Outcome is { } o ? $" {o}" : ""), Script.Passed == false ? ChipKind.Hot : Script.Passed == true ? ChipKind.Good : ChipKind.Plain)
                : null,
            Script.Path is { } path ? new Chip($"📄 {path}", ChipKind.Plain) : null,
            Script.Command is { } command ? new Chip($"▶ {command}", ChipKind.Plain) : null,
        }
        .OfType<Chip>()
        .ToList();
}

/// <summary>The screenshots of one task's browser test.</summary>
public sealed record ShotGroupView(string Title, IReadOnlyList<ShotView> Shots);

public sealed record ShotView(WebTestScreenshot Shot, Avalonia.Media.Imaging.Bitmap? Thumbnail)
{
    public string Caption => Shot.Step is { } step ? $"{step} · {Shot.Caption}" : Shot.Caption;

    public string Tip => $"{Path.GetFileName(Shot.FullPath)} — click per aprirla";
}

/// <summary>
/// Item tags ([tag:: bug] in the note): a few common ones with fixed colours; any other name gets a stable colour
/// from the same palette.
/// </summary>
public static class Tags
{
    public static readonly (string Name, string Color)[] Defaults =
    [
        ("ticket", "#8AB4F8"),
        ("feature", "#7FD1AE"),
        ("bug", "#E57373"),
        ("analisi", "#F2B544"),
        ("miglioria", "#B99BF5"),
        ("refactoring", "#A29DB8"),
        ("documentazione", "#80CBC4"),
        ("supporto", "#F48FB1"),
    ];

    private static readonly string[] Palette = ["#8AB4F8", "#7FD1AE", "#F2B544", "#B99BF5", "#80CBC4", "#F48FB1", "#FFAB91", "#C5E1A5"];

    public static IBrush Brush(string tag)
    {
        var known = Defaults.FirstOrDefault(d => d.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
        var color = known.Name is not null
            ? known.Color
            : Palette[(int)((uint)tag.ToLowerInvariant().Aggregate(17, (h, c) => (h * 31) + c) % Palette.Length)];
        return new SolidColorBrush(Color.Parse(color));
    }
}

/// <summary>A tag in the side panel's picker or the board's filter bar.</summary>
public sealed record TagOption(string Name, IBrush Brush, bool IsSelected, int Count = 0)
{
    public string Label => Count > 0 ? $"{Name} {Count}" : Name;
}

/// <summary>
/// A card of a generic page (your pages, Decisioni, Glossario, PR, Settimana, Costi): title, chips, markdown body.
/// <paramref name="Target"/> is the note opened by a click (or an http link, opened in the browser).
/// </summary>
public sealed record PageCard(string Title, string? Subtitle, IReadOnlyList<Chip> Chips, string? Markdown, string? Target, string? Accent = null)
{
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    public bool HasChips => Chips.Count > 0;

    public bool HasMarkdown => !string.IsNullOrWhiteSpace(Markdown);

    public bool HasTarget => Target is not null;

    public Avalonia.Media.IBrush AccentBrush => Accent is { } color
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color))
        : Avalonia.Media.Brushes.Transparent;
}

/// <summary>A button in a page's tool bar: what it says and the action it runs.</summary>
public sealed record PageButton(string Label, string Action, string? Tip = null);

/// <summary>What a generic page shows: a line under the title, an optional markdown header (tables), the cards.</summary>
public sealed record PageContent(string Subtitle, string? Header, IReadOnlyList<PageCard> Cards, IReadOnlyList<PageButton> Buttons, string Empty);

/// <summary>A result of the Ctrl+K box: what it is, and what Enter does with it.</summary>
public sealed record PaletteItem(string Icon, string Title, string? Subtitle, string Kind, Action Open, int Score)
{
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
}
