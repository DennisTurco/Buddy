using System.Text;
using System.Text.Json;
using Buddy.Core.Vault;

namespace Buddy.Core.Mentor;

/// <summary>
/// A change to a person's card read from an episode. <paramref name="Important"/>: a macroscopic event (an open
/// conflict, an escalation, trust broken or rebuilt, a change of role, an important agreement): applied at once.
/// The others are proposed, with a button.
/// </summary>
public sealed record CardChange(string Person, string Field, string Mode, string Value, bool Important, string Reason);

/// <summary>Someone in the episode (text or screenshots) without a card in Persone: added with what the episode says.</summary>
public sealed record NewPerson(string Name, string Role, string Description);

/// <summary>What the analysis of an episode found: changes for the people's cards, people to add and a line about the user.</summary>
public sealed record EpisodeFindings(IReadOnlyList<CardChange> Changes, string AboutMe, IReadOnlyList<NewPerson>? NewPeople = null, string Lesson = "")
{
    public IReadOnlyList<NewPerson> NewPeople { get; init; } = NewPeople ?? [];
}

/// <summary>
/// Reads an episode with the cards of the people in it (claude -p, no files): what to change on those cards, and what it
/// says about how the user communicates and reacts. The answer is JSON, checked here: unknown people or fields and
/// relationships outside the scale are dropped.
/// </summary>
public static class EpisodeAnalysis
{
    public const int MaxChanges = 4;

    public const int MaxNewPeople = 5;

    /// <param name="images">Screenshots of the episode (full paths): Claude reads them as part of the facts.</param>
    public static string Prompt(Episode episode, IReadOnlyList<Person> people, string? profile, IReadOnlyList<string>? images = null)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("""
            Aiuti uno sviluppatore a lavorare meglio con i colleghi. Ti racconta un episodio successo al lavoro; sotto ci sono le
            schede delle persone coinvolte (nota Persone del suo vault, privata). Leggi e rispondi SOLO con un oggetto JSON:
            {"modifiche": [{"persona": "Nome Cognome", "campo": "rapporto" | "come comunicare" | "note", "modo": "imposta" | "aggiungi",
              "valore": "...", "importante": true | false, "motivo": "..."}], "su_di_me": "...", "da_ricordare": "...",
             "nuove_persone": [{"nome": "Nome Cognome", "compito": "...", "descrizione": "..."}]}

            "da_ricordare": UNA frase breve, pratica, da tenere a mente la prossima volta con queste persone (es. "mandarle i
            dati prima della riunione", "chiedere il perché prima di insistere"). Ricavala da "cosa rifarei diversamente" se
            c'è, altrimenti da cosa ha funzionato o no. Vuoto se l'episodio non insegna niente.

            "nuove_persone": colleghi citati nell'episodio (nel testo o negli screenshot) che NON hanno una scheda qui sotto.
            Nome e cognome se si leggono, altrimenti il nome come compare. "compito" e "descrizione" solo se l'episodio li dice
            (ruolo, di cosa si occupa), altrimenti "". Non includere lo sviluppatore stesso. [] se non ce ne sono.

            Regole per "modifiche" (al massimo 4, anche nessuna: [] se l'episodio non cambia niente):
            - Solo cose utili per lavorare insieme: come comunicare, cosa evitare, quando sentirla, cosa sa o decide. Niente tratti di
              personalità, diagnosi, giudizi morali, salute, vita privata. Scrivi osservazioni sul comportamento ("risponde meglio
              per iscritto"), non etichette ("è aggressiva").
            - "rapporto" si imposta solo a uno di: ottimo, buono, da costruire, delicato, critico. Cambialo solo se l'episodio lo
              giustifica davvero, di un passo alla volta salvo eventi gravi.
            - "come comunicare" e "note": "aggiungi" una voce breve (una frase), "imposta" solo per correggere una voce sbagliata.
              Non ripetere quello che la scheda dice già.
            - "importante": true solo per eventi macroscopici: conflitto aperto, escalation a un responsabile, fiducia rotta o
              ricostruita, cambio di ruolo o di responsabilità, accordo importante. Un episodio normale è false (resta una proposta).
            - "persona": il nome esatto come nell'intestazione della scheda. Se una persona non ha scheda, non proporre modifiche per lei.
            - "motivo": una frase che cita il fatto dell'episodio.

            "su_di_me": una o due frasi su come lo sviluppatore ha comunicato o reagito (cosa ha funzionato, cosa potrebbe fare
            diversamente), senza giudicarlo e senza etichette psicologiche; vuoto se l'episodio non dice niente su di lui.
            Non modificare file. Non leggere file, tranne gli screenshot dell'episodio se sono elencati sotto.
            """);
        prompt.AppendLine().AppendLine($"## Episodio del {episode.Date:yyyy-MM-dd}: {episode.Title}");
        void Part(string label, string value)
        {
            if (value.Trim().Length > 0)
            {
                prompt.AppendLine($"{label}: {People.Inline(value)}");
            }
        }

        Part("Persone", string.Join(", ", episode.People));
        Part("Cosa è successo", episode.Facts);
        Part("Come ho reagito", episode.Reaction);
        Part("Com'è finita", episode.Outcome);
        if (images is { Count: > 0 })
        {
            prompt.AppendLine().AppendLine("Screenshot dell'episodio (una chat, una mail…): aprili con lo strumento Read, fanno parte dei fatti.");
            foreach (var image in images)
            {
                prompt.AppendLine($"- {image}");
            }
        }

        prompt.AppendLine().AppendLine("## Schede delle persone coinvolte");
        if (people.Count == 0)
        {
            prompt.AppendLine("(nessuna: le persone citate non hanno una scheda)");
        }

        foreach (var person in people)
        {
            prompt.AppendLine().AppendLine(person.Raw);
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            prompt.AppendLine().AppendLine("## Come comunica lo sviluppatore (dal suo profilo)").AppendLine(profile.Trim());
        }

        return prompt.ToString();
    }

    /// <summary>The findings in the answer; null when it holds no usable JSON.</summary>
    public static EpisodeFindings? Parse(string answer, IReadOnlyList<Person> people)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(answer[start..(end + 1)]);
            var root = json.RootElement;
            var changes = new List<CardChange>();
            if (root.TryGetProperty("modifiche", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    string Str(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
                    var field = Str("campo").ToLowerInvariant();
                    var mode = Str("modo").ToLowerInvariant() is "imposta" ? "imposta" : "aggiungi";
                    var value = Str("valore");
                    if (People.Find(people, Str("persona")) is not { } person || !People.ChangeableFields.Contains(field) || value.Length == 0
                        || (field == "rapporto" && !People.Relationships.Contains(value.ToLowerInvariant())))
                    {
                        continue;
                    }

                    var important = item.TryGetProperty("importante", out var flag) && flag.ValueKind == JsonValueKind.True;
                    changes.Add(new CardChange(person.Name, field, field == "rapporto" ? "imposta" : mode, value, important, Str("motivo")));
                }
            }

            var aboutMe = root.TryGetProperty("su_di_me", out var me) && me.ValueKind == JsonValueKind.String ? me.GetString()!.Trim() : "";
            var newPeople = new List<NewPerson>();
            if (root.TryGetProperty("nuove_persone", out var added) && added.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in added.EnumerateArray())
                {
                    string Str(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
                    var name = Str("nome");
                    if (name.Length >= 2 && People.Find(people, name) is null && !newPeople.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
                    {
                        newPeople.Add(new NewPerson(name, Str("compito"), Str("descrizione")));
                    }
                }
            }

            var lesson = root.TryGetProperty("da_ricordare", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()!.Trim() : "";
            return new EpisodeFindings(changes.Take(MaxChanges).ToList(), aboutMe, newPeople.Take(MaxNewPeople).ToList(), lesson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The chat command that makes (or undoes) one change: "/scheda Nome | campo | modo | valore". Hidden: it is what the
    /// buttons under the analysis send.
    /// </summary>
    public static string Command(string person, string field, string mode, string value) =>
        $"/scheda {person} | {field} | {mode} | {value.ReplaceLineEndings(LineBreak).Replace("|", "/")}";

    /// <summary>A line break inside a command's value (a list restored by an undo).</summary>
    public const string LineBreak = " ⏎ ";

    /// <summary>The parts of "/scheda Nome | campo | modo | valore" (after the command), or null when malformed.</summary>
    public static (string Person, string Field, string Mode, string Value)? ParseCommand(string rest)
    {
        var parts = rest.Split('|', 4, StringSplitOptions.TrimEntries);
        return parts.Length == 4 && parts[0].Length > 0
            ? (parts[0], parts[1].ToLowerInvariant(), parts[2].ToLowerInvariant(), parts[3].Replace(LineBreak.Trim(), "\n").Replace(" \n ", "\n").Trim())
            : null;
    }

    /// <summary>The undo of a change that was applied, from the field's value before it.</summary>
    public static string UndoCommand(CardChange change, string before) =>
        change.Field == "rapporto" || change.Mode == "imposta"
            ? Command(change.Person, change.Field, "imposta", before)
            : Command(change.Person, change.Field, "togli", change.Value);

    /// <summary>How a change is told: "Chiara Bianchi · rapporto → critico (motivo)".</summary>
    public static string Describe(CardChange change, string? before = null) =>
        $"{change.Person} · {change.Field}"
        + (change.Field == "rapporto"
            ? $": {(string.IsNullOrEmpty(before) ? "" : before + " ")}→ {change.Value}"
            : change.Mode == "imposta" ? $" → «{change.Value}»" : $" + «{change.Value}»")
        + (change.Reason.Length > 0 ? $" ({change.Reason})" : "");
}
