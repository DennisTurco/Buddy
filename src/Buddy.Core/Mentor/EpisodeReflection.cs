using System.Text;
using System.Text.RegularExpressions;
using Buddy.Core.Vault;

namespace Buddy.Core.Mentor;

/// <summary>
/// A reflection on one episode (or on what keeps coming back across several) led by the local model: it never leaves
/// the computer. The model looks for what is underneath what happened (an expectation not met, a need, a trigger, what
/// was in the user's hands) by asking one question at a time, and closes with a short summary the user can save in the
/// episode as "Da ricordare", so it comes back the next time with those people.
/// </summary>
public static partial class EpisodeReflection
{
    /// <summary>Questions before the model is asked to wrap up, so a reflection does not go on forever.</summary>
    public const int MaxTurns = 8;

    /// <summary>The line that opens the summary in the model's last message.</summary>
    public const string SummaryMarker = "RIASSUNTO:";

    public const string SystemPrompt = """
        Aiuti uno sviluppatore a ripensare a un episodio successo al lavoro con dei colleghi, per capirlo e superarlo.
        Parli in italiano, con calore ma senza smancerie, come un buon mentore. Non sei uno psicologo e non fai diagnosi.

        Come procedi:
        - Una sola domanda per messaggio, breve (al massimo 3-4 righe in tutto). Aspetta la risposta prima di andare avanti.
        - Parti dai fatti, poi cerca cosa c'è sotto: cosa si aspettava e non è successo, cosa l'ha toccato davvero, cosa ha
          fatto scattare la reazione, cosa dipendeva da lui e cosa no, come può averla vissuta l'altra persona.
        - Fai ragionare lui: niente prediche, niente soluzioni pronte, niente giudizi né etichette su di lui o sugli altri.
          Non dare ragione o torto a nessuno. Se dice qualcosa di importante, rimandaglielo con parole sue.
        - Quando il problema di fondo è chiaro (o lui dice che basta, o dopo qualche scambio), chiudi: una frase su cosa
          avete capito e un passo concreto e piccolo da provare la prossima volta, poi su una riga a parte
          "RIASSUNTO: " seguito da UNA o DUE frasi brevi da ricordare (la causa e cosa fare diversamente), in prima persona.
        - Se emerge un malessere forte (non dorme, sta male da giorni, si sente senza via d'uscita), non approfondire da solo:
          digli con delicatezza che è il caso di parlarne con una persona di fiducia o con un professionista.
        Usa solo quello che c'è negli episodi e nelle sue risposte: non inventare fatti.
        Qui la filosofia aiuta spesso: per esempio gli stoici (cosa dipende da noi e cosa no, i giudizi sui fatti), ma anche
        altri, quando fanno vedere l'episodio da un'altra angolazione. Al massimo una citazione per messaggio.
        """ + Llm.Philosophy.Instruction;

    /// <summary>
    /// Who the user is: the people of an episode are their colleagues, and a small model otherwise takes the first name it
    /// reads (often a colleague's) for the user's.
    /// </summary>
    public static string WhoIsWho(string userName) =>
        (string.IsNullOrWhiteSpace(userName)
            ? "Non conosci il nome dello sviluppatore: non chiamarlo per nome."
            : $"Lo sviluppatore con cui parli si chiama {userName.Trim()}: se lo chiami per nome, usa solo questo.")
        + " Le persone elencate negli episodi sono i suoi colleghi, non lui: non rivolgerti a lui con il loro nome.";

    /// <summary>
    /// The request that turns the reflection into the episode's fields: only what came out of the user's answers and is
    /// not already written, in first person. The buddy adds it under what the user wrote, which stays as it is.
    /// </summary>
    public const string FieldsPrompt = """
        Da una riflessione su un episodio di lavoro ricavi cosa aggiungere a due campi dell'episodio, scritti in prima
        persona come li scriverebbe lo sviluppatore. Rispondi SOLO con un oggetto JSON:
        {"come_ho_reagito": "...", "com_e_finita": "..."}
        - "come_ho_reagito": come ha reagito e come si è sentito, cosa si aspettava, cosa l'ha toccato.
        - "com_e_finita": com'è andata a finire, cosa ha capito, cosa rifarebbe diversamente.
        Usa SOLO quello che ha detto lui nelle risposte, con parole sue quando puoi; niente interpretazioni del mentore.
        Non ripetere quello che i campi dicono già. Frasi brevi, al massimo 3 per campo. "" se non c'è niente di nuovo.
        """;

    /// <summary>The episode's fields as they were before the reflection, and the user's answers to each question.</summary>
    public static string FieldsInput(Episode episode, IReadOnlyList<Llm.LlmMessage> thread)
    {
        var text = new StringBuilder($"## Episodio: {episode.Title}\n");
        text.AppendLine($"Cosa è successo: {People.Inline(episode.Facts)}");
        text.AppendLine($"Come ho reagito (già scritto): {(episode.Reaction.Trim().Length > 0 ? People.Inline(episode.Reaction) : "(vuoto)")}");
        text.AppendLine($"Com'è finita (già scritto): {(episode.Outcome.Trim().Length > 0 ? People.Inline(episode.Outcome) : "(vuoto)")}");
        text.AppendLine().AppendLine("## Riflessione");
        foreach (var (question, answer) in Exchanges(thread))
        {
            text.AppendLine($"Mentore: {Split(question).Text.ReplaceLineEndings(" ")}");
            text.AppendLine($"Lui: {answer.ReplaceLineEndings(" ")}");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The model's questions with the user's answers (the opening and the wrap-up requests left out).</summary>
    public static IReadOnlyList<(string Question, string Answer)> Exchanges(IReadOnlyList<Llm.LlmMessage> thread)
    {
        var exchanges = new List<(string, string)>();
        for (var i = 1; i + 1 < thread.Count; i++)
        {
            if (thread[i].Role == "assistant" && thread[i + 1].Role == "user" && thread[i + 1].Content != WrapUp)
            {
                exchanges.Add((thread[i].Content, thread[i + 1].Content));
            }
        }

        return exchanges;
    }

    /// <summary>
    /// The episode with what the reflection added: under what the user had written (kept as it was), or in place of an
    /// empty field. <paramref name="original"/> is the episode before the reflection, so running it again after each
    /// answer never adds the same thing twice.
    /// </summary>
    public static Episode WithFields(Episode current, Episode original, string reaction, string outcome)
    {
        static string Join(string before, string added) =>
            added.Trim().Length == 0 ? before.Trim() : before.Trim().Length == 0 ? added.Trim() : $"{before.Trim()} {added.Trim()}";

        return current with
        {
            Reaction = Join(original.Reaction, reaction),
            Outcome = Join(original.Outcome, outcome),
        };
    }

    /// <summary>The first message: the episode as written, and the request to open with one question.</summary>
    public static string Opening(Episode episode)
    {
        var text = new StringBuilder("Ripensiamo a questo episodio. Inizia con UNA domanda.\n\n");
        AppendEpisode(text, episode);
        return text.ToString().TrimEnd();
    }

    /// <summary>The first message for several episodes: look for what comes back (same person, same reaction, same situation).</summary>
    public static string PatternOpening(IReadOnlyList<Episode> episodes)
    {
        var text = new StringBuilder("""
            Questi sono i miei ultimi episodi. Cerca cosa si ripete (le stesse persone, la stessa mia reazione, lo stesso tipo
            di situazione) e aiutami a capire perché. Dimmi in una frase cosa noti, poi fammi UNA domanda.

            """);
        foreach (var episode in episodes)
        {
            text.AppendLine();
            AppendEpisode(text, episode);
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendEpisode(StringBuilder text, Episode episode)
    {
        text.AppendLine($"## {episode.Date:yyyy-MM-dd} · {episode.Title}");
        void Part(string label, string value)
        {
            if (value.Trim().Length > 0)
            {
                text.AppendLine($"{label}: {People.Inline(value)}");
            }
        }

        Part("Colleghi coinvolti (non sono io)", string.Join(", ", episode.People));
        Part("Cosa è successo", episode.Facts);
        Part("Come ho reagito", episode.Reaction);
        Part("Com'è finita", episode.Outcome);
        Part("Da ricordare (finora)", episode.Lesson);
    }

    /// <summary>Sent as the user's message when the reflection has gone on long enough or the user wants to stop.</summary>
    public const string WrapUp = "Chiudiamo qui: riassumi cosa abbiamo capito e scrivi la riga RIASSUNTO.";

    /// <summary>"basta", "fine", "chiudiamo"…: the user wants to stop.</summary>
    public static bool WantsToStop(string answer) => StopWords().IsMatch(answer.Trim());

    /// <summary>The model's message without the summary line, and the summary when there is one.</summary>
    public static (string Text, string? Summary) Split(string reply)
    {
        var match = Summary().Match(reply);
        if (!match.Success)
        {
            return (reply.Trim(), null);
        }

        var summary = match.Groups["text"].Value.Trim().Trim('"', '«', '»', '*').Trim();
        var text = reply.Remove(match.Index, match.Length).Trim();
        return (text, summary.Length > 0 ? summary : null);
    }

    /// <summary>
    /// The episode worth offering a reflection on, if any: one or two days old (it had time to settle), not yet offered,
    /// and either ended badly or with someone already in another episode of the last two months.
    /// </summary>
    public static Episode? ToOffer(IReadOnlyList<Episode> episodes, DateOnly today, ICollection<string> offered) =>
        episodes
            .Where(e => today.DayNumber - e.Date.DayNumber is >= 1 and <= 3 && !offered.Contains(e.Key))
            .FirstOrDefault(e => IsTense(e) || Recurring(episodes, e, today));

    /// <summary>The episode reads like it hurt: a conflict, a strong reaction, or it did not get resolved.</summary>
    public static bool IsTense(Episode episode) =>
        Tense().IsMatch($"{episode.Title}\n{episode.Facts}\n{episode.Reaction}\n{episode.Outcome}");

    private static bool Recurring(IReadOnlyList<Episode> episodes, Episode episode, DateOnly today) =>
        episode.People.Count > 0
        && episodes.Any(other => other != episode && today.DayNumber - other.Date.DayNumber <= 60
                                 && other.People.Any(p => episode.People.Contains(p, StringComparer.CurrentCultureIgnoreCase)));

    /// <summary>The episodes a pattern reflection reads: the last two months, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<Episode> Recent(IReadOnlyList<Episode> episodes, DateOnly today, int max = 8) =>
        episodes.Where(e => today.DayNumber - e.Date.DayNumber <= 60).Take(max).ToList();

    /// <summary>The episode named by key ("2026-11-16 · Titolo"), by date, or by words of its title; null when none fits.</summary>
    public static Episode? Find(IReadOnlyList<Episode> episodes, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return episodes.FirstOrDefault(e => e.Key.Equals(name, StringComparison.CurrentCultureIgnoreCase))
               ?? episodes.FirstOrDefault(e => e.Date.ToString("yyyy-MM-dd") == name)
               ?? episodes.FirstOrDefault(e => name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .All(w => e.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                             || e.People.Any(p => p.Contains(w, StringComparison.CurrentCultureIgnoreCase))));
    }

    [GeneratedRegex(@"^\s*\**\s*RIASSUNTO\s*:?\s*\**\s*:?\s*(?<text>.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Summary();

    [GeneratedRegex(@"^(basta|fine|stop|chiudiamo|chiudi|ok basta|per ora basta|/fine)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StopWords();

    [GeneratedRegex(@"litig|discuss|tes[oa]\b|tension|arrabbi|nervos|alzat[oa] la voce|urla|frustr|delus|offes|ferit|male\b|pianto|piangere|ansi[ae]|conflitt|scontr|escalation|non (si è )?risolt|rimast[oa] mal|imbarazz|umiliat|ignorat|scavalcat", RegexOptions.IgnoreCase)]
    private static partial Regex Tense();
}
