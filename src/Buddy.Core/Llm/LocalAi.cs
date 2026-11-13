using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Llm;

/// <summary>What the router made of a message.</summary>
public enum RouteKind
{
    /// <summary>A question for the notes: the local model answers.</summary>
    Local,

    /// <summary>Work on the code, analysis, long explanations: Claude's.</summary>
    Claude,

    /// <summary>One of the buddy's own actions (<see cref="LocalAi.Actions"/>).</summary>
    Action,
}

/// <param name="Action">For <see cref="RouteKind.Action"/>: appunto, stato, cerca, oggi or modifiche.</param>
/// <param name="Text">What the action needs: the appunto's text, the task id, the words to search.</param>
public sealed record RouteDecision(RouteKind Kind, string? Action = null, string Text = "");

/// <summary>A card's fields as the local model filled them from pasted text; empty when not found.</summary>
public sealed record CardDraft(
    string Title, string DoneWhen, string Context, string Where, string Details,
    IReadOnlyList<string> Tags, string Priority, string Estimate);

/// <summary>Where an appunto could go: <paramref name="Column"/> is To-Do, Da valutare, In attesa or Rifiutare.</summary>
public sealed record SortProposal(int Number, string Column, string Reason);

/// <summary>
/// The local model's jobs besides the chat (see <see cref="LocalAiSettings"/>): short, well-defined tasks that a small
/// model does well when its answer is constrained (JSON) and checked. Each method returns null when the job is off,
/// Ollama is not reachable or the answer is unusable: the caller then does what it did before.
/// </summary>
public sealed class LocalAi
{
    /// <summary>The buddy's actions the router can pick.</summary>
    public static readonly string[] Actions = ["appunto", "stato", "cerca", "oggi", "modifiche"];

    public static readonly string[] SortColumns = ["To-Do", "Da valutare", "In attesa", "Rifiutare"];

    private readonly Func<IReadOnlyList<LlmMessage>, bool, CancellationToken, Task<string>> _complete;
    private readonly IOptions<BuddySettings> _options;

    public LocalAi(OllamaClient ollama, IOptions<BuddySettings> options)
        : this((messages, json, ct) => ollama.CompleteAsync(messages, json, Model(options.Value), ct), options)
    {
    }

    /// <summary>With a stand-in for the model (tests).</summary>
    internal LocalAi(Func<IReadOnlyList<LlmMessage>, bool, CancellationToken, Task<string>> complete, IOptions<BuddySettings> options)
    {
        _complete = complete;
        _options = options;
    }

    public LocalAiSettings Settings => _options.Value.LocalAi;

    private static string Model(BuddySettings settings) =>
        string.IsNullOrWhiteSpace(settings.LocalAi.Model) ? settings.Ollama.Model : settings.LocalAi.Model;

    // ---- Router -----------------------------------------------------------------------------------------------

    private const string RouterPrompt = """
        Smisti i messaggi scritti a un assistente da scrivania per sviluppatori. Rispondi SOLO con un oggetto JSON:
        {"tipo": "azione" | "locale" | "claude", "azione": "appunto" | "stato" | "cerca" | "oggi" | "modifiche" | "", "testo": "..."}

        tipo "azione": l'utente chiede una di queste cose:
        - appunto: segnare, annotare, ricordarsi, aggiungere agli appunti qualcosa. testo = la cosa da annotare, scritta pulita e completa.
        - stato: a che punto è un task. testo = l'ID del task se c'è (es. NUSR-2700, T-012), altrimenti "".
        - cerca: cercare nelle note. testo = le parole da cercare.
        - oggi: cosa c'è oggi, riepilogo della giornata. testo = "".
        - modifiche: quali file ha modificato nel repository. testo = "".
        tipo "locale": domanda breve sulle note del progetto (task, decisioni, glossario, stato) o due chiacchiere.
        tipo "claude": scrivere, modificare, spiegare o correggere codice, analisi, debug, qualunque cosa richieda di leggere il repository o ragionare a lungo.
        Nel dubbio tra "locale" e "claude" scegli "locale"; scegli "azione" solo se la richiesta è chiara.

        Esempi:
        "segnami che domani devo sentire Chiara per i permessi" -> {"tipo":"azione","azione":"appunto","testo":"Sentire Chiara per i permessi (domani)"}
        "a che punto è NUSR-2700?" -> {"tipo":"azione","azione":"stato","testo":"NUSR-2700"}
        "dove abbiamo scritto della retention dei dati?" -> {"tipo":"azione","azione":"cerca","testo":"retention dati"}
        "cosa c'è da fare oggi" -> {"tipo":"azione","azione":"oggi","testo":""}
        "chi ha assegnato il task del report reclami?" -> {"tipo":"locale","azione":"","testo":""}
        "perché il test di login fallisce con NullReferenceException?" -> {"tipo":"claude","azione":"","testo":""}
        "aggiungi un campo canale al form dei reclami" -> {"tipo":"claude","azione":"","testo":""}
        """;

    /// <summary>Where a message goes; null when the router is off or the model's answer is unusable.</summary>
    public async Task<RouteDecision?> RouteAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!Settings.Router || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        using var json = await AskJsonAsync(RouterPrompt, message, cancellationToken);
        if (json is null)
        {
            return null;
        }

        var root = json.RootElement;
        var kind = Str(root, "tipo").ToLowerInvariant();
        var action = Str(root, "azione").ToLowerInvariant();
        var text = Str(root, "testo");
        return kind switch
        {
            "azione" when Actions.Contains(action) && (action != "appunto" || text.Length > 0) => new RouteDecision(RouteKind.Action, action, text),
            "claude" => new RouteDecision(RouteKind.Claude),
            _ => new RouteDecision(RouteKind.Local),
        };
    }

    // ---- Card form --------------------------------------------------------------------------------------------

    private const string CardPrompt = """
        Ricevi il testo di una card di lavoro, spesso incollato da Jira o da un PDF: righe spezzate a metà frase,
        elenchi con simboli strani (, o, •), intestazioni. Rimettilo in ordine per un modulo. Rispondi SOLO con JSON:
        {"titolo": "...", "fatto_quando": "...", "contesto": "...", "dove": "...", "dettagli": "...", "tag": ["..."], "priorita": "P1|P2|P3|", "stima": "..."}

        Regole:
        - Non inventare niente: usa solo il testo ricevuto. Un campo che non trovi resta "" (o [] per i tag).
        - Ricongiungi le righe spezzate in frasi intere; separa i paragrafi con una riga vuota; gli elenchi con "- ".
        - Lascia identici ID (es. NUSR-2700), link markdown [testo](url), nomi di campi e valori.
        - titolo: breve (massimo 10 parole). fatto_quando: i criteri concreti per dire che è finito, come elenco "- ".
        - contesto: da dove viene la richiesta (ticket, persona, link). dove: file, schermate o punti del sistema citati.
        - dettagli: il resto della descrizione, ripulito. tag: 0-3 parole minuscole (es. feature, bug, ui) solo se evidenti.
        - priorita e stima solo se scritte nel testo.
        """;

    /// <summary>The form's fields filled from what was written or pasted in it; null when off or unusable.</summary>
    public async Task<CardDraft?> DraftCardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Settings.FormAssist || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var json = await AskJsonAsync(CardPrompt, text, cancellationToken);
        if (json is null)
        {
            return null;
        }

        var root = json.RootElement;
        var tags = root.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim().ToLowerInvariant())
                .Where(x => x.Length is > 0 and <= 20 && !x.Contains(' ')).Distinct().Take(3).ToList()
            : [];
        var priority = Str(root, "priorita").ToUpperInvariant();
        var draft = new CardDraft(Str(root, "titolo"), Str(root, "fatto_quando"), Str(root, "contesto"), Str(root, "dove"),
            Str(root, "dettagli"), tags, priority is "P1" or "P2" or "P3" ? priority : "", Str(root, "stima"));
        return draft.Title.Length + draft.DoneWhen.Length + draft.Details.Length + draft.Context.Length == 0 ? null : draft;
    }

    // ---- Small texts ------------------------------------------------------------------------------------------

    private const string TitlePrompt = """
        Scrivi un titolo per questa conversazione: al massimo 7 parole, in italiano, senza virgolette né punto finale,
        che dica di cosa si parla (es. "Retention dati nel report reclami"). Rispondi solo con il titolo.
        """;

    /// <summary>A short title for a saved conversation; null when off or unusable.</summary>
    public async Task<string?> TitleAsync(IEnumerable<string> messages, CancellationToken cancellationToken = default)
    {
        if (!Settings.ConversationTitles)
        {
            return null;
        }

        var text = string.Join("\n---\n", messages.Select(m => m.Length > 400 ? m[..400] + "…" : m).Take(6));
        var title = (await AskTextAsync(TitlePrompt, text, cancellationToken))?.Split('\n')[0].Trim().Trim('"', '«', '»', '*', '#', ' ', '.');
        return title is { Length: > 2 and <= 80 } ? title : null;
    }

    private const string SortPrompt = """
        Sono gli appunti veloci di uno sviluppatore, numerati. Per ognuno proponi dove spostarlo:
        - "To-Do": è un lavoro chiaro da fare;
        - "Da valutare": è un'idea o una proposta da decidere;
        - "In attesa": dipende da qualcun altro o da un evento;
        - "Rifiutare": non serve più o è un doppione.
        Rispondi SOLO con JSON: {"proposte": [{"n": 1, "colonna": "To-Do", "perche": "motivo in massimo 10 parole"}]}
        Una proposta per ogni appunto, nell'ordine.
        """;

    /// <summary>Where each appunto could go (nothing is moved); null when off or unusable.</summary>
    public async Task<IReadOnlyList<SortProposal>?> SortAsync(IReadOnlyList<string> appunti, CancellationToken cancellationToken = default)
    {
        if (!Settings.SortSuggestions || appunti.Count == 0)
        {
            return null;
        }

        var list = string.Join('\n', appunti.Select((a, i) => $"{i + 1}. {(a.Length > 300 ? a[..300] + "…" : a)}"));
        using var json = await AskJsonAsync(SortPrompt, list, cancellationToken);
        if (json is null || !json.RootElement.TryGetProperty("proposte", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var proposals = new List<SortProposal>();
        foreach (var item in items.EnumerateArray())
        {
            var number = item.TryGetProperty("n", out var n) && n.TryGetInt32(out var value) ? value : proposals.Count + 1;
            var column = SortColumns.FirstOrDefault(c => c.Equals(Str(item, "colonna"), StringComparison.OrdinalIgnoreCase));
            if (column is not null && number >= 1 && number <= appunti.Count && proposals.All(p => p.Number != number))
            {
                proposals.Add(new SortProposal(number, column, Str(item, "perche")));
            }
        }

        return proposals.Count == 0 ? null : proposals.OrderBy(p => p.Number).ToList();
    }

    /// <summary>
    /// The /commit or /pr draft written locally from the same prompt Claude would get; null when off, the prompt is
    /// too big for a small model, or the answer is unusable.
    /// </summary>
    public async Task<string?> DraftAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (!Settings.LocalDrafts || prompt.Length > Settings.LocalDraftMaxChars)
        {
            return null;
        }

        var draft = await AskTextAsync("Sei un assistente che scrive messaggi di commit e descrizioni di PR. Segui le istruzioni.", prompt, cancellationToken);
        return draft is { Length: > 10 } ? draft.Trim() : null;
    }

    private const string CondensePrompt = """
        Riassumi questo allegato (log, stack trace, output) per un altro assistente che deve capire il problema.
        Tieni ALLA LETTERA: messaggi di errore ed eccezioni, codici, percorsi di file con numeri di riga, nomi di
        metodi e classi del codice dell'utente, valori e orari rilevanti. Togli le righe ripetute e quelle dei framework.
        Al massimo 60 righe. Rispondi solo con il riassunto.
        """;

    /// <summary>A long attachment condensed for Claude; null when off, short enough, or unusable.</summary>
    public async Task<string?> CondenseAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Settings.CondenseAttachments || text.Length <= Settings.CondenseAboveChars)
        {
            return null;
        }

        var condensed = await AskTextAsync(CondensePrompt, text.Length > 60_000 ? text[..60_000] : text, cancellationToken);
        return condensed is { Length: > 40 } && condensed.Length < text.Length / 2 ? condensed.Trim() : null;
    }

    private const string DictationPrompt = """
        È una frase dettata a voce e trascritta. Correggi solo punteggiatura, maiuscole, intercalari ("ehm", "cioè",
        "tipo") e parole ripetute per errore. Non cambiare il senso, non aggiungere niente, non rispondere alla frase.
        Rispondi solo con il testo corretto.
        """;

    /// <summary>The dictation tidied up; null when off or when the model changed it too much to trust.</summary>
    public async Task<string?> CleanDictationAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Settings.CleanDictation || text.Trim().Length < 12)
        {
            return null;
        }

        var cleaned = (await AskTextAsync(DictationPrompt, text, cancellationToken))?.Trim().Trim('"', '«', '»');

        // A rewrite, an answer or a summary is not a clean-up: the original is kept.
        return cleaned is { Length: > 0 } && cleaned.Length >= text.Length * 0.6 && cleaned.Length <= text.Length * 1.3 + 10 ? cleaned : null;
    }

    // ---- Calls ------------------------------------------------------------------------------------------------

    private async Task<JsonDocument?> AskJsonAsync(string system, string user, CancellationToken cancellationToken)
    {
        if (await AskAsync(system, user, json: true, cancellationToken) is not { } answer)
        {
            return null;
        }

        // Small models sometimes wrap the object in a sentence or a code fence.
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(answer[start..(end + 1)]);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> AskTextAsync(string system, string user, CancellationToken cancellationToken) =>
        await AskAsync(system, user, json: false, cancellationToken) is { } answer && answer.Trim().Length > 0
            ? StripFence(answer.Trim())
            : null;

    /// <summary>Longest wait for one job (a cold model takes ~10 s to load): after that the caller goes on without it.</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    private async Task<string?> AskAsync(string system, string user, bool json, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return await _complete([LlmMessage.System(system), LlmMessage.User(user)], json, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null; // Ollama off, the model missing or too slow: the caller does what it did before
        }
    }

    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var lines = text.Split('\n').Skip(1).ToList();
        if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines).Trim();
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
}
