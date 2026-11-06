namespace Buddy.Core.Chat;

public sealed record ChatCommand(string Name, string Description);

/// <summary>The "/" commands the bubble suggests while typing: the buddy's own, the mentor's and the installed skills.</summary>
public static class ChatCommands
{
    public static readonly IReadOnlyList<ChatCommand> BuiltIn =
    [
        new("/c", "passa a Claude (e invia il testo)"),
        new("/l", "torna al modello locale"),
        new("/stato", "scheda del task (senza ID: quello del ramo)"),
        new("/oggi", "le note di oggi"),
        new("/cerca", "cerca nel vault"),
        new("/modifiche", "file modificati, per area"),
        new("/commit", "bozza del messaggio di commit"),
        new("/pr", "bozza di titolo e descrizione della PR"),
        new("/stop", "interrompe la risposta in corso"),
        new("/nuova", "nuova conversazione"),
        new("/uso", "uso di Claude e limiti del piano"),
        new("/modello", "mostra o cambia il modello (Claude o locale)"),
        new("/aggiorna", "riavvia con la versione appena compilata"),
        new("/log", "ultimi avvisi ed errori del Buddy (/log tutto: ultime righe)"),
        new("/appunto", "aggiunge una riga a To-do Appunti (anche Shift+🎤)"),
        new("/riprendi", "dove ero rimasto: task, ultima conversazione, modifiche, test"),
        new("/modelli", "i tuoi modelli di richiesta (/modelli nome = testo)"),
        new("/aiuto", "elenco dei comandi"),
        new("/checkin", "mentore: piano della giornata"),
        new("/checkout", "mentore: com'è andata oggi"),
        new("/pillola", "mentore: una pillola di conoscenza"),
        new("/ripasso", "mentore: ripasso di una pillola"),
        new("/chiacchiera", "mentore: due chiacchiere"),
        new("/retro", "mentore: retrospettiva settimanale"),
        new("/focus", "mentore: blocco di concentrazione [min]"),
        new("/andamento", "mentore: le ultime settimane"),
        new("/competenze", "mentore: mappa delle competenze dalle pillole"),
        new("/obiettivi", "mentore: gli obiettivi del trimestre (/obiettivi nuovi)"),
        new("/salta", "salta la domanda del mentore"),
        new("/mentore", "comandi del mentore"),
    ];

    /// <summary>
    /// For a "/" command that is neither built-in nor an installed skill: the known one it is probably a typo of
    /// (e.g. "/chiacchera" → "/chiacchiera"), or null when nothing is close enough.
    /// </summary>
    public static string? Closest(string command, IEnumerable<string> skills)
    {
        var known = BuiltIn.Select(c => c.Name).Concat(skills.Select(s => "/" + s)).ToList();
        if (known.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var best = known.Select(k => (Name: k, Distance: Distance(command.ToLowerInvariant(), k.ToLowerInvariant())))
            .OrderBy(k => k.Distance)
            .FirstOrDefault();
        var allowed = command.Length <= 5 ? 1 : 2;
        return best.Name is not null && best.Distance <= allowed ? best.Name : null;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }

    /// <summary>Commands starting with <paramref name="prefix"/> (e.g. "/pi"), built-in first, then skills.</summary>
    public static IReadOnlyList<ChatCommand> Matching(string prefix, IEnumerable<string> skills, int max = 6,
        IEnumerable<(string Name, string Text)>? templates = null)
    {
        if (!prefix.StartsWith('/') || prefix.Contains(' '))
        {
            return [];
        }

        var all = BuiltIn.Concat(skills
            .Where(s => BuiltIn.All(b => !b.Name.Equals("/" + s, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(s => new ChatCommand("/" + s, "skill di Claude")))
            .Concat((templates ?? []).Select(t => new ChatCommand("/" + t.Name, "tuo modello: " + (t.Text.Length > 50 ? t.Text[..50] + "…" : t.Text))));
        return all.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                              && !c.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .ToList();
    }
}
