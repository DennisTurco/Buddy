using System.Text;
using Buddy.Core.Vault;

namespace Buddy.App.Project;

/// <summary>
/// The organisational side of the board: 📒 Registro (what was said between colleagues: information, agreements,
/// commitments, decisions, from Registro.md and from the calls) and ✅ Impegni (who has to do what, by when; ticked
/// where they are written). See <see cref="Said"/>.
/// </summary>
public partial class ProjectWindow
{
    private const string SaidDone = "said:done:";
    private const string SaidUndone = "said:undone:";

    /// <summary>The form of a new entry (tests set it): who, what, topic, by when; null when cancelled.</summary>
    internal Func<SaidKind, Task<string[]?>>? ShowSaidForm { get; set; }

    private string? Me => _settings.Mentor.UserName.Trim() is { Length: > 0 } name ? name : null;

    /// <summary>What a person said or owes: entries where they are "who" (by name or nickname).</summary>
    private static List<SaidEntry> SaidBy(IReadOnlyList<SaidEntry> said, Person person)
    {
        var mentions = People.Mentions(person);
        return said.Where(e => e.Who.Length > 0 && (e.Who.Equals(person.Name, StringComparison.CurrentCultureIgnoreCase) || mentions.IsMatch(e.Who))).ToList();
    }

    private string SourceLabel(SaidEntry e) =>
        e.SourceTitle is { } title && title != Said.Note ? $"call «{title}»" : "registro";

    // ---- 📒 Registro -------------------------------------------------------------------------------------------

    private PageContent RegistryPage(string filter)
    {
        var all = Said.All(_vault, _settings.ProjectPath);
        var shown = all.Where(e => filter.Length == 0
                                   || $"{e.Who} {e.KindWord} {e.Text} {e.Topic} {e.SourceTitle}".Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cards = shown.GroupBy(e => e.Date).Take(40).Select(day =>
        {
            var body = new StringBuilder();
            foreach (var e in day)
            {
                body.Append("- ")
                    .Append(e.Kind switch { SaidKind.Impegno => e.Done ? "✔ " : e.IsLate(today) ? "⚠ " : "☐ ", SaidKind.Decisione => "⚖ ", SaidKind.Accordo => "🤝 ", _ => "💬 " })
                    .Append(e.Who.Length > 0 ? $"**{e.Who}** · " : "")
                    .Append(e.Text)
                    .Append(e.Due is { } due ? $" · entro {due.ToString("d MMM", Italian)}" : "")
                    .Append(e.Topic is { } topic ? $" · #{topic}" : "")
                    .AppendLine($" *({SourceLabel(e)})*");
            }

            var kinds = day.GroupBy(e => e.Kind).Select(g => new Chip($"{g.Count()} {Said.Word(g.Key)}", g.Key == SaidKind.Impegno && g.Any(e => e.IsOpenCommitment) ? ChipKind.Warm : ChipKind.Plain)).ToList();
            var fromCall = day.Select(e => e.Source).Distinct().FirstOrDefault(s => s != Said.FileName);
            return new PageCard(day.Key.ToString("dddd d MMMM yyyy", Italian), null, kinds, NoteLinks.ToMarkdown(body.ToString().TrimEnd()),
                fromCall ?? Said.FileName, "#8FB8E8");
        }).ToList();

        var open = all.Count(e => e.IsOpenCommitment);
        return new PageContent(
            $"{all.Count} voci · {open} impegn{(open == 1 ? "o" : "i")} apert{(open == 1 ? "o" : "i")} · da {Said.FileName} e dalle call in {CallTranscripts.Folder}/ · in chat: /detto, /accordo, /impegno, /chiedi",
            null, cards,
            [
                new PageButton("＋ Detto…", "said:new:info", "Un'informazione detta da qualcuno (anche /detto Nome: cosa)"),
                new PageButton("＋ Accordo…", "said:new:accordo", "Un accordo preso (anche /accordo Nome: cosa)"),
                new PageButton("＋ Impegno…", "said:new:impegno", "Chi deve fare cosa ed entro quando (anche /impegno Nome: cosa entro venerdì)"),
                new PageButton("🧠 Chiedi…", "said:ask", "Una domanda su cosa ci si è detti: risponde Claude dal registro e dalle call, con le fonti"),
                new PageButton("📄 Apri il registro", "said:file", $"{Said.FileName} così com'è scritto"),
            ],
            filter.Length > 0 ? "Nessuna voce corrisponde alla ricerca."
                : "Ancora niente: scrivi la prima voce con «＋ Detto…» o in chat con /detto Nome: cosa. Le call trascritte compaiono qui da sole.");
    }

    // ---- ✅ Impegni --------------------------------------------------------------------------------------------

    private PageContent CommitmentsPage(string filter)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var all = Said.All(_vault, _settings.ProjectPath).Where(e => e.Kind == SaidKind.Impegno).ToList();
        var open = all.Where(e => !e.Done && (filter.Length == 0 || $"{e.Who} {e.Text} {e.Topic} {e.SourceTitle}".Contains(filter, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var recentlyDone = all.Where(e => e.Done && e.Date >= today.AddDays(-14)).Take(10).ToList();

        PageCard Card(string title, string? subtitle, IReadOnlyList<SaidEntry> items, string accent)
        {
            var sorted = items.OrderBy(e => e.Due ?? DateOnly.MaxValue).ThenBy(e => e.Date).ToList();
            var body = string.Join("\n", sorted.Select(e =>
                $"- {(e.IsLate(today) ? "⚠ **scaduto** · " : "")}{(e.Due is { } due ? $"**entro {due.ToString("d MMM", Italian)}** · " : "")}{e.Text}"
                + $" *({SourceLabel(e)}, {e.Date.ToString("d MMM", Italian)})*"));
            var late = sorted.Count(e => e.IsLate(today));
            var chips = new List<Chip> { new($"{sorted.Count} apert{(sorted.Count == 1 ? "o" : "i")}", ChipKind.Plain) };
            if (late > 0)
            {
                chips.Add(new Chip($"⚠ {late} scadut{(late == 1 ? "o" : "i")}", ChipKind.Hot));
            }

            var actions = sorted.Take(8).Select(e => new PageCardAction("✅ " + Short(e.Text, 40), $"Segna come fatto: {e.Text}\n(lo spunta in {SourceLabel(e)})", "")
            {
                Local = SaidDone + e.Key,
            }).ToList();
            // (To have Claude work on one of yours: its 🛠 button on the call's card, in Trascrizioni.)
            return new PageCard(title, subtitle, chips, NoteLinks.ToMarkdown(body), null, accent) { Actions = actions };
        }

        var cards = new List<PageCard>();
        var mineItems = open.Where(e => Said.IsMe(e.Who, Me)).ToList();
        if (mineItems.Count > 0)
        {
            cards.Add(Card("Tocca a te", "quello che hai promesso", mineItems, "#B99BF5"));
        }

        var people = NoteTitled(People.Note) is { } note ? People.Parse(note.Content) : [];
        foreach (var group in open.Except(mineItems).GroupBy(e => People.Find(people, e.Who)?.Name ?? (e.Who.Length > 0 ? e.Who : "Senza nome"))
                     .OrderByDescending(g => g.Any(e => e.IsLate(today))).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var role = People.Find(people, group.Key)?.Role;
            cards.Add(Card(group.Key, string.IsNullOrWhiteSpace(role) ? "ti deve" : $"{role} · ti deve", group.ToList(), "#7FD1AE"));
        }

        if (recentlyDone.Count > 0 && filter.Length == 0)
        {
            cards.Add(new PageCard("Fatti di recente", "ultime due settimane", [new Chip($"{recentlyDone.Count}", ChipKind.Good)],
                NoteLinks.ToMarkdown(string.Join("\n", recentlyDone.Select(e => $"- ✔ {(e.Who.Length > 0 ? $"**{e.Who}** · " : "")}{e.Text} *({SourceLabel(e)})*"))), null, "#5FBF77")
            {
                Actions = recentlyDone.Take(5).Select(e => new PageCardAction("↶ " + Short(e.Text, 40), $"Rimetti da fare: {e.Text}", "") { Local = SaidUndone + e.Key }).ToList(),
            });
        }

        var lateTotal = open.Count(e => e.IsLate(today));
        return new PageContent(
            $"{open.Count} impegni aperti · {mineItems.Count} tuoi" + (lateTotal > 0 ? $" · ⚠ {lateTotal} scaduti" : "")
            + " · dal registro e dalle azioni delle call · ✅ li spunta dove sono scritti",
            null, cards,
            [
                new PageButton("＋ Impegno…", "said:new:impegno", "Chi deve fare cosa ed entro quando (anche /impegno Nome: cosa entro venerdì)"),
                new PageButton("🧠 Chiedi…", "said:ask", "Una domanda su impegni, accordi e cose dette: risponde Claude con le fonti"),
            ],
            filter.Length > 0 ? "Nessun impegno corrisponde alla ricerca."
                : "Nessun impegno aperto. Si aggiungono con «＋ Impegno…», con /impegno in chat, o arrivano dalle azioni delle call.");
    }

    // ---- Actions -----------------------------------------------------------------------------------------------

    /// <summary>"＋ Detto…", "＋ Accordo…", "＋ Impegno…": the form, then one line in Registro.md.</summary>
    internal async Task NewSaidAsync(SaidKind kind)
    {
        string[]? values;
        if (ShowSaidForm is { } stub)
        {
            values = await stub(kind);
        }
        else
        {
            var people = NoteTitled(People.Note) is { } note ? People.Parse(note.Content) : [];
            var names = people.Count == 0 ? "" : " — " + string.Join(", ", people.Take(10).Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name));
            var fields = new List<Settings.Dialogs.Field>
            {
                new(kind == SaidKind.Impegno ? "Chi deve farlo («io» se tocca a te)" + names : "Chi l'ha detto" + names, Required: true),
                new(kind switch { SaidKind.Impegno => "Cosa deve fare", SaidKind.Accordo => "Cosa avete concordato", _ => "Cosa ha detto" },
                    Multiline: true, Required: true, Height: 80),
                new("Tema (facoltativo: progetto, cliente, argomento)"),
            };
            if (kind == SaidKind.Impegno)
            {
                fields.Add(new Settings.Dialogs.Field("Entro (AAAA-MM-GG, «venerdì», «domani», «+3»; facoltativo)"));
            }

            values = await Settings.Dialogs.FormAsync(this, kind switch
            {
                SaidKind.Impegno => "Nuovo impegno",
                SaidKind.Accordo => "Nuovo accordo",
                _ => "Cosa è stato detto",
            }, [.. fields]);
        }

        if (values is null)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var who = values[0].Trim();
        var person = Said.IsMe(who, Me) ? null : (NoteTitled(People.Note) is { } peopleNote ? People.Find(People.Parse(peopleNote.Content), who) : null);
        var name = Said.IsMe(who, Me) ? Me ?? "io" : person?.Name ?? who;
        var due = kind == SaidKind.Impegno && values.Length > 3 && values[3].Trim().Length > 0 ? Said.ParseDay(values[3], today) : null;
        if (await WriteAsync(() => Said.Add(_settings.ProjectPath, new SaidEntry(today, name, kind, values[1].Trim(),
                values[2].Trim() is { Length: > 0 } topic ? topic : null, due, false, Said.FileName, 0))))
        {
            _vault.Refresh();
            Reload();
        }
    }

    /// <summary>"✅ …" on an Impegni card: ticked where it is written (the registry or the call's note); "↶" unticks.</summary>
    private async Task SetSaidDoneAsync(string key, bool done)
    {
        if (Said.All(_vault, _settings.ProjectPath).FirstOrDefault(e => e.Key == key) is not { } entry)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Non trovo più quell'impegno: la nota è cambiata. Ricarico.");
            Reload();
            return;
        }

        if (await WriteAsync(() => Said.SetDone(_settings.ProjectPath, entry, done)))
        {
            _vault.Refresh();
            Reload();
        }
    }

    /// <summary>"🧠 Chiedi…": the question, then /chiedi in the chat.</summary>
    private async Task AskSaidAsync()
    {
        if (await Settings.Dialogs.AskAsync(this, "Chiedi al Buddy",
                "Cosa vuoi sapere? (es. «cosa ci siamo detti con Domenico sui test in PROD?», «cosa è ancora aperto su CleanEnergy?»)") is { } question
            && question.Trim().Length > 0)
        {
            ChatCommandRequested?.Invoke($"/chiedi {question.Trim()}");
        }
    }

    /// <summary>A card's own action the board runs: tick or untick a commitment.</summary>
    private async Task RunLocalCardActionAsync(string local)
    {
        if (local.StartsWith(SaidDone, StringComparison.Ordinal))
        {
            await SetSaidDoneAsync(local[SaidDone.Length..], done: true);
        }
        else if (local.StartsWith(SaidUndone, StringComparison.Ordinal))
        {
            await SetSaidDoneAsync(local[SaidUndone.Length..], done: false);
        }
    }
}
