using System.Globalization;
using System.Text;
using Buddy.Core.Vault;

namespace Buddy.Core.Chat;

/// <summary>
/// The registry of what is said between colleagues (<see cref="Said"/>) from the chat: /detto, /accordo, /impegno write
/// a line in Registro.md, /impegni lists who has to do what, and the prompt behind /chiedi.
/// </summary>
public sealed partial class AssistantCommands
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private IReadOnlyList<Person> AllPeople() =>
        vault.IsAvailable && vault.Notes.Where(n => n.Title.Equals(People.Note, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.RelativePath.Length).FirstOrDefault() is { } note
            ? People.Parse(note.Content)
            : [];

    /// <summary>The user's name as written in the registry: their name from the settings, or "io".</summary>
    private string Me => Settings.Mentor.UserName.Trim() is { Length: > 0 } name ? name : "io";

    /// <summary>
    /// "/detto Domenico: i test in PROD li decide lui #CleanEnergy" (and /accordo, /impegno … entro venerdì): one line in
    /// Registro.md, the person written with their full name when they have a card. Returns what to show in the chat.
    /// </summary>
    public string AddSaid(SaidKind kind, string input, DateOnly today)
    {
        var command = "/" + (kind == SaidKind.Info ? "detto" : Said.Word(kind));
        if (Said.ParseCommand(input, today) is not var (who, text, topic, due))
        {
            return kind == SaidKind.Impegno
                ? $"Scrivi chi deve fare cosa: `{command} Tony: mandarmi i sinonimi entro venerdì` (`io:` se tocca a te). `#tema` per l'argomento."
                : $"Scrivi chi l'ha detto e cosa: `{command} Domenico: i test in PROD li decide lui #CleanEnergy`.";
        }

        var person = Said.IsMe(who, Settings.Mentor.UserName) ? null : People.Find(AllPeople(), who);
        var name = Said.IsMe(who, Settings.Mentor.UserName) ? Me : person?.Name ?? who;
        try
        {
            var entry = Said.Add(Settings.ProjectPath, new SaidEntry(today, name, kind, text, topic, kind == SaidKind.Impegno ? due : null,
                false, Said.FileName, 0));
            var line = new StringBuilder($"📒 Nel registro: **{name}** · _{entry.KindWord}_ · {text}");
            if (entry.Due is { } day)
            {
                line.Append($" · entro {day.ToString("dddd d MMMM", Italian)}");
            }

            if (topic is not null)
            {
                line.Append($" · #{topic}");
            }

            if (person is null && !Said.IsMe(who, Settings.Mentor.UserName))
            {
                line.Append($"\n\n«{who}» non ha una scheda in Persone: aggiungila dalla pagina 👥 Persone se è un collega.");
            }

            return line.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Non riesco a scrivere nel registro: {ex.Message}";
        }
    }

    /// <summary>"/impegni [nome]": the open commitments, mine first, then who owes me what (late ones marked).</summary>
    public string Commitments(string name, DateOnly today)
    {
        var open = vault.IsAvailable ? Said.All(vault, Settings.ProjectPath).Where(e => e.IsOpenCommitment).ToList() : [];
        if (name.Trim().Length > 0)
        {
            var person = People.Find(AllPeople(), name);
            var who = person?.Name ?? name.Trim();
            open = open.Where(e => e.Who.Equals(who, StringComparison.CurrentCultureIgnoreCase)
                                   || (person is not null && People.Mentions(person).IsMatch(e.Who))).ToList();
        }

        if (open.Count == 0)
        {
            return name.Trim().Length > 0 ? $"Nessun impegno aperto per «{name.Trim()}»." : "Nessun impegno aperto: né tuoi né di altri. 🎉";
        }

        string Line(SaidEntry e) =>
            $"- {(e.IsLate(today) ? "⚠ " : "")}{(e.Due is { } due ? $"**{due.ToString("d MMM", Italian)}** · " : "")}{e.Text}"
            + $" *({(e.SourceTitle is { } source && source != Said.Note ? $"call «{source}», " : "")}{e.Date.ToString("d MMM", Italian)})*";

        var mine = open.Where(e => Said.IsMe(e.Who, Settings.Mentor.UserName)).OrderBy(e => e.Due ?? DateOnly.MaxValue).ToList();
        var others = open.Except(mine).GroupBy(e => e.Who.Length > 0 ? e.Who : "?").OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        var text = new StringBuilder("**Impegni aperti**\n");
        if (mine.Count > 0)
        {
            text.AppendLine().AppendLine("**Tocca a te**").AppendLine(string.Join("\n", mine.Select(Line)));
        }

        foreach (var group in others)
        {
            text.AppendLine().AppendLine($"**{group.Key}**").AppendLine(string.Join("\n", group.OrderBy(e => e.Due ?? DateOnly.MaxValue).Select(Line)));
        }

        return text.AppendLine().Append("Si spuntano dalla pagina ✅ Impegni della bacheca.").ToString();
    }

    /// <summary>The prompt behind "/chiedi …": the question with the registry, the calls and the people it is about.</summary>
    public string AskPrompt(string question, DateOnly today) =>
        SaidQuery.Prompt(question, vault.IsAvailable ? Said.All(vault, Settings.ProjectPath) : [],
            vault.IsAvailable ? CallTranscripts.List(vault) : [], AllPeople(), Settings.Mentor.UserName, Settings.ProjectPath, today);
}
