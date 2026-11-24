using System.Text;
using System.Text.RegularExpressions;
using Buddy.Core.Vault;

namespace Buddy.Core.Mentor;

/// <summary>
/// What a call says about the people in it, for their cards in Persone: the request to Claude after a transcript is
/// cleaned. The answer has the JSON of <see cref="EpisodeAnalysis"/> ("modifiche", "nuove_persone"), read with its parser.
/// </summary>
public static class CallPeople
{
    /// <summary>The call's text Claude reads: the summary and the cleaned transcript (not the original), cut if very long.</summary>
    public const int MaxChars = 24000;

    public static string Prompt(CallTranscript call, IReadOnlyList<Person> people, string? me)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("""
            Aiuti uno sviluppatore a tenere aggiornate le schede dei colleghi (nota Persone del suo vault, privata). Qui sotto
            c'è una sua call di lavoro, già riassunta e trascritta, e le schede delle persone coinvolte. Leggi e rispondi SOLO
            con un oggetto JSON:
            {"modifiche": [{"persona": "Nome Cognome", "campo": "ruolo" | "si occupa di" | "rapporto" | "come comunicare" | "note",
              "modo": "imposta" | "aggiungi", "valore": "...", "importante": true | false, "motivo": "..."}],
             "nuove_persone": [{"nome": "Nome Cognome", "compito": "...", "descrizione": "..."}]}

            Cosa cercare nella call:
            - ruolo e responsabilità: chi decide cosa, chi si occupa di quale sistema o ambiente, chi va sentito per cosa
              ("per i test in PROD decide Domenico" → note di Domenico, o "si occupa di" se è il suo compito);
            - come lavorare con loro: cosa è utile sapere per la prossima volta (preferisce per iscritto, va avvisato prima…);
            - il rapporto, solo se la call lo cambia davvero (un conflitto, un chiarimento importante).

            "nuove_persone": colleghi che parlano o sono citati nella call e NON hanno una scheda qui sotto. Nome e cognome se
            si capiscono, altrimenti il nome come compare. "compito" e "descrizione" solo se la call li dice. Non includere lo
            sviluppatore stesso. [] se non ce ne sono.

            Regole per "modifiche" (al massimo 4, anche nessuna: [] se la call non dice niente di nuovo sulle persone):
            - Solo cose utili per lavorare insieme. Niente tratti di personalità, diagnosi, giudizi, salute, vita privata.
              Osservazioni sul comportamento, mai etichette.
            - Non ripetere quello che la scheda dice già. Una frase breve per voce.
            - "rapporto" si imposta solo a uno di: ottimo, buono, da costruire, delicato, critico, e solo se la call lo giustifica.
            - "importante": true solo per cose che vanno scritte subito: un cambio di ruolo o di responsabilità detto
              chiaramente, un accordo importante, un conflitto aperto. Il resto è false (resta una proposta da approvare).
            - "persona": il nome esatto come nell'intestazione della scheda; senza scheda niente modifiche (va in nuove_persone).
            - "motivo": una frase che cita cosa è stato detto nella call.
            Non modificare file e non leggerne: hai tutto qui sotto.
            """);
        if (!string.IsNullOrWhiteSpace(me))
        {
            prompt.AppendLine($"Lo sviluppatore è {me.Trim()} («io» nei partecipanti): non proporre modifiche né schede per lui.");
        }

        prompt.AppendLine().AppendLine($"## Call del {call.Date:yyyy-MM-dd}: {call.Title}");
        if (call.People.Count > 0)
        {
            prompt.AppendLine($"Partecipanti: {string.Join(", ", call.People)}");
        }

        if (call.Task is { } task)
        {
            prompt.AppendLine($"Task: {task}");
        }

        prompt.AppendLine().AppendLine(Body(call));
        prompt.AppendLine().AppendLine("## Schede delle persone coinvolte");
        if (people.Count == 0)
        {
            prompt.AppendLine("(nessuna: le persone della call non hanno una scheda)");
        }

        foreach (var person in people)
        {
            prompt.AppendLine().AppendLine(person.Raw);
        }

        return prompt.ToString();
    }

    /// <summary>The note without front matter, title and original text: summary, decisions, actions, cleaned transcript.</summary>
    internal static string Body(CallTranscript call)
    {
        var text = call.Content.ReplaceLineEndings("\n");
        text = Regex.Replace(text, @"\A---\n[\s\S]*?\n---[ \t]*\n", "");
        text = Regex.Replace(text, @"^#[ \t]+[^\n]*\n", "", RegexOptions.Multiline);
        var original = text.IndexOf(CallTranscripts.OriginalHeading, StringComparison.Ordinal);
        if (original >= 0)
        {
            text = text[..original];
        }

        text = text.Trim();
        return text.Length > MaxChars ? text[..MaxChars] + "\n[…trascrizione tagliata…]" : text;
    }
}
