using System.Text;
using Buddy.Core.Vault;

namespace Buddy.Core.Mentor;

/// <summary>
/// "/prepara Chicca rilascio di venerdì": everything known about a person (card, last episodes and what to remember,
/// the cards that wait on them, how the user communicates) turned by Claude into a plan for the conversation and a
/// draft of the first message. The card and the episodes are private: the draft must not quote or hint at them.
/// </summary>
public static class ConversationPrep
{
    /// <summary>
    /// The person named at the start of <paramref name="text"/> (one to three words: "Chicca", "Chiara Bianchi") and the
    /// topic after it; null when nobody in Persone is called so.
    /// </summary>
    public static (Person Person, string Topic)? Split(IReadOnlyList<Person> people, string text)
    {
        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var n = Math.Min(3, words.Length); n >= 1; n--)
        {
            if (People.Find(people, string.Join(' ', words.Take(n))) is { } person)
            {
                return (person, string.Join(' ', words.Skip(n)).Trim());
            }
        }

        return null;
    }

    public static string Prompt(Person person, string topic, IReadOnlyList<Episode> episodes, IReadOnlyList<PersonOnCard> cards,
        string? communicationProfile, DateOnly today)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($$"""
            Aiuti uno sviluppatore a preparare una conversazione di lavoro con un collega. Oggi è {{today:yyyy-MM-dd}}.
            Argomento: {{(topic.Length > 0 ? topic : "non indicato: deducilo dalle card qui sotto, o prepara un aggiornamento generale")}}

            Sotto trovi la scheda privata della persona, gli ultimi episodi con lei e le card che dipendono da lei. Rispondi in
            italiano, in markdown, breve (al massimo 20 righe), con queste parti:
            **Obiettivo** — una riga: cosa deve ottenere da questa conversazione.
            **Come impostarla** — 3-4 punti nell'ordine in cui dirli, adattati a "Come comunicare" e al rapporto.
            **Cosa portare** — dati, link, esempi da avere pronti (solo se servono).
            **Da evitare** — 1-2 punti, da quello che è andato storto negli episodi o da "Da ricordare".
            **Primo messaggio** — una bozza breve da mandarle (chat o mail, come preferisce lei), pronta da copiare.

            Regole:
            - La bozza non deve mai citare né far intuire la scheda, il rapporto, gli episodi o questi appunti: è privata.
            - Tono rispettoso e diretto; un disaccordo si dice una volta, con un argomento; niente chiusure secche.
            - Con un rapporto "delicato" o "critico": solo il necessario, per iscritto, con i fatti; se il tema è importante,
              suggerisci se conviene coinvolgere qualcun altro.
            - Non leggere né modificare file: basta quello che c'è qui.
            """);

        prompt.AppendLine().AppendLine("## Scheda della persona").AppendLine(person.Raw);

        if (episodes.Count > 0)
        {
            prompt.AppendLine().AppendLine("## Ultimi episodi con lei");
            foreach (var e in episodes)
            {
                prompt.AppendLine($"- {e.Date:yyyy-MM-dd} «{e.Title}»: {People.Inline(e.Facts)}"
                                  + (e.Reaction.Length > 0 ? $" — reazione: {People.Inline(e.Reaction)}" : "")
                                  + (e.Outcome.Length > 0 ? $" — com'è finita: {People.Inline(e.Outcome)}" : "")
                                  + (e.Lesson.Length > 0 ? $" — DA RICORDARE: {People.Inline(e.Lesson)}" : ""));
            }
        }

        if (cards.Count > 0)
        {
            prompt.AppendLine().AppendLine("## Card che dipendono da lei");
            foreach (var card in cards)
            {
                prompt.AppendLine($"- {(card.Item.Id is { } id ? id + " · " : "")}{card.Item.Title.Replace("`", "")} ({card.Column.Title})");
            }
        }

        if (!string.IsNullOrWhiteSpace(communicationProfile))
        {
            prompt.AppendLine().AppendLine("## Come comunica lo sviluppatore (dal suo profilo)").AppendLine(communicationProfile.Trim());
        }

        return prompt.ToString();
    }
}
