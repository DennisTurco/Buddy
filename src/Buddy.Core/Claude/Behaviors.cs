namespace Buddy.Core.Claude;

/// <summary>
/// A preferred behaviour of Claude in the flow, switched on or off in the settings. <paramref name="SkillDefault"/> is
/// what the skills already do; only a behaviour switched away from it becomes an instruction, so the prompt stays short.
/// </summary>
/// <param name="WhenOn">The instruction when it is on and the skill would not do it (null: nothing to say).</param>
/// <param name="WhenOff">The instruction when it is off and the skill would do it (null: nothing to say).</param>
public sealed record Behavior(string Key, string Label, string Description, bool SkillDefault, string? WhenOn, string? WhenOff);

/// <summary>
/// What Claude is told and allowed besides the request itself: the preferred behaviours, your own notes, quick
/// replies and the project's guided mode (no edits to the code, teaching instead). The chat session passes them as
/// --append-system-prompt and --disallowedTools; they weigh more than the skills' own steps.
/// </summary>
public static class Behaviors
{
    public const string ManualTests = "test-manuali";

    public static IReadOnlyList<Behavior> All { get; } =
    [
        new(ManualTests, "Test manuali a fine lavoro", "/lavora scrive sempre il documento dei test manuali, senza dover aggiungere «test».",
            SkillDefault: false,
            WhenOn: "A fine /lavora scrivi sempre il documento dei test manuali (Test manuali/Test manuali <ID>.md), anche se il comando non dice «test».",
            WhenOff: null),
        new("piano", "Piano sempre prima", "Prima di toccare il codice mostra il piano e aspetta l'ok, anche per modifiche piccole.",
            SkillDefault: false,
            WhenOn: "Prima di modificare il codice mostra sempre il piano e aspetta il mio ok, anche per modifiche piccole.",
            WhenOff: null),
        new("build-test", "Compila e prova prima di consegnare", "Compila ed esegue i test automatici pertinenti prima della consegna, e riporta l'esito.",
            SkillDefault: false,
            WhenOn: "Prima di consegnare compila il progetto ed esegui i test automatici pertinenti alla modifica; riporta l'esito nel riepilogo.",
            WhenOff: null),
        new("spiega", "Spiegami cosa hai fatto", "A fine lavoro, un breve riepilogo didattico: cosa è cambiato, perché, e un concetto da imparare.",
            SkillDefault: false,
            WhenOn: "A fine lavoro aggiungi un breve riepilogo didattico: cosa hai cambiato, perché, e un concetto o una tecnica che vale la pena imparare da questa modifica.",
            WhenOff: null),
        new("revisione", "Revisione automatica del codice", "A fine lavoro rivede le modifiche (es. plugin code-review) e corregge i problemi certi.",
            SkillDefault: true,
            WhenOn: null,
            WhenOff: "Salta la revisione automatica del codice a fine lavoro."),
        new("test-web", "Proponi il test nel browser", "Se la modifica si vede nel browser, chiede se fare il test web.",
            SkillDefault: true,
            WhenOn: null,
            WhenOff: "Non proporre il test web nel browser a fine lavoro."),
        new("commit-pr", "Commit e PR pronti", "Alla consegna prepara il messaggio di commit e titolo e descrizione della PR.",
            SkillDefault: true,
            WhenOn: null,
            WhenOff: "Alla consegna non preparare il messaggio di commit né titolo e descrizione della PR: li scrivo io."),
        new("commento-card", "Commento per la card", "Alla consegna prepara il commento per la card del tracker, per il PO.",
            SkillDefault: true,
            WhenOn: null,
            WhenOff: "Alla consegna non preparare il commento per la card del tracker."),
        new("costo", "Costo alla consegna", "Alla consegna scrive quanto del limite di Claude è servito.",
            SkillDefault: true,
            WhenOn: null,
            WhenOff: "Alla consegna non calcolare il costo (uso del piano)."),
        new("fuori-perimetro", "Domande sul fuori perimetro", "Se vede altro da sistemare, prepara la domanda per chi ha assegnato il task.",
            SkillDefault: true,
            WhenOn: null,
            WhenOff: "Se vedi altro da sistemare fuori dal task, non preparare la domanda per chi l'ha assegnato: limitati a nominarlo in una riga."),
        new("breve", "Risposte brevi", "Niente premesse né riassunti lunghi: risposte asciutte, elenchi corti.",
            SkillDefault: false,
            WhenOn: "Rispondi in modo sintetico: niente premesse, niente riassunti di quello che hai appena detto, elenchi corti.",
            WhenOff: null),
    ];

    public static bool IsOn(BuddySettings settings, Behavior behavior) =>
        settings.Behaviors.TryGetValue(behavior.Key, out var on) ? on : behavior.SkillDefault;

    public static bool IsOn(BuddySettings settings, string key) =>
        All.FirstOrDefault(b => b.Key == key) is { } behavior && IsOn(settings, behavior);

    /// <summary>The guided mode's instructions: the developer writes the code, Claude teaches.</summary>
    public const string GuidedModeInstruction = """
        MODALITÀ GUIDA (impostata per questo progetto): lo sviluppatore vuole imparare scrivendo lui il codice.
        - Non modificare i file del codice: non hai i permessi per farlo e non cercare altre strade (comandi della shell, script). Puoi leggere tutto e aggiornare le note del flusso.
        - Guida come un mentore: prima il perché e i concetti, poi i passi uno alla volta; fai domande e dai indizi graduali invece della soluzione completa.
        - Quando lo sviluppatore scrive codice, rivedilo: cosa va bene, cosa migliorare e perché.
        - Se chiede esplicitamente la soluzione, puoi mostrarla in chat come esempio spiegato, ma non scriverla nei file.
        - /lavora vale come /guidami.
        """;

    /// <summary>
    /// Everything appended to Claude's system prompt for the chat session: guided mode, the behaviours switched
    /// away from the skills', the user's own notes and the quick replies; null when there is nothing.
    /// </summary>
    public static string? SystemPrompt(BuddySettings settings)
    {
        var parts = new List<string>();
        if (settings.GuidedMode)
        {
            parts.Add(GuidedModeInstruction);
        }

        var preferences = All
            .Select(b => IsOn(settings, b) ? (b.SkillDefault ? null : b.WhenOn) : (b.SkillDefault ? b.WhenOff : null))
            .OfType<string>()
            .Select(text => "- " + text)
            .ToList();
        if (settings.BehaviorNotes.Trim() is { Length: > 0 } notes)
        {
            preferences.AddRange(notes.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? line : "- " + line));
        }

        if (preferences.Count > 0)
        {
            parts.Add("Preferenze dell'utente per il lavoro (valgono più dei passi delle skill):\n" + string.Join('\n', preferences));
        }

        if (settings.Assistant.QuickReplies)
        {
            parts.Add(Chat.QuickReplies.Instruction);
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>
    /// What Claude may never use: the permission table's deny rules and, in guided mode, editing the code: Edit and
    /// Write in the code folders (the vault stays writable for the flow's notes). Without a known code folder, every
    /// edit is denied.
    /// </summary>
    public static IReadOnlyList<string> DeniedTools(BuddySettings settings)
    {
        var denied = settings.Claude.DeniedTools().ToList();
        if (!settings.GuidedMode)
        {
            return denied;
        }

        var vault = string.IsNullOrWhiteSpace(settings.ProjectPath) ? null : Path.GetFullPath(settings.ProjectPath);
        var code = new[] { settings.Repo.Path, settings.Claude.WorkingDirectory }
            .Concat(settings.Claude.AdditionalDirectories)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(Path.GetFullPath)
            .Where(d => vault is null || !(d.Equals(vault, StringComparison.OrdinalIgnoreCase) || d.StartsWith(vault + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rules = code.Count == 0
            ? ["Edit", "Write", "NotebookEdit"]
            : code.SelectMany(d => new[] { "Edit", "Write", "NotebookEdit" }.Select(tool => $"{tool}({ClaudeOneShot.AbsoluteRulePath(d)}/**)"));
        return denied.Concat(rules).Distinct().ToList();
    }

    /// <summary>The permission mode; in guided mode never one that runs commands without asking.</summary>
    public static string PermissionMode(BuddySettings settings) =>
        settings.GuidedMode && settings.Claude.PermissionMode is "bypassPermissions" or "auto" or "acceptEdits"
            ? "default"
            : settings.Claude.PermissionMode;

    /// <summary>
    /// A task command as it should run: in guided mode /lavora becomes /guidami; with the manual tests behaviour on,
    /// /lavora gets "test". Null when nothing changes.
    /// </summary>
    public static string? Rewrite(BuddySettings settings, string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith("/lavora", StringComparison.OrdinalIgnoreCase)
            || (trimmed.Length > 7 && trimmed[7] != ' '))
        {
            return null;
        }

        var rest = trimmed[7..].Trim();
        if (settings.GuidedMode)
        {
            var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.Equals("autonomo", StringComparison.OrdinalIgnoreCase) && !w.Equals("test", StringComparison.OrdinalIgnoreCase));
            return ("/guidami " + string.Join(' ', words)).Trim();
        }

        if (IsOn(settings, ManualTests) && !rest.Split(' ').Contains("test", StringComparer.OrdinalIgnoreCase))
        {
            return ("/lavora " + rest).Trim() + " test";
        }

        return null;
    }
}
