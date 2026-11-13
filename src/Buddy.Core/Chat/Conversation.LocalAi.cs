using Buddy.Core.Llm;
using Microsoft.Extensions.Logging;

namespace Buddy.Core.Chat;

/// <summary>
/// The local model's jobs in the chat (<see cref="LocalAiSettings"/>): it reads a message first and runs the buddy's
/// actions itself, searches the notes by meaning, drafts small commits, condenses long attachments for Claude and
/// gives the saved conversations a title. Each one falls back to what the chat did before when it is off or Ollama
/// does not answer.
/// </summary>
public sealed partial class Conversation
{
    private readonly Context.SemanticIndex? _semantic;
    private readonly HashSet<string> _titling = [];

    /// <summary>The local model's jobs, for the windows too (card form, dictation); null in tests without it.</summary>
    public LocalAi? LocalAi { get; }

    // ---- Router -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A message that is not a command, read by the local model first. Returns true when it was handled here:
    /// an action of the buddy (asked for confirmation when it writes), or a job for Claude while in local mode
    /// (offered with a button: Claude is never called without the user choosing it).
    /// </summary>
    private async Task<bool> TryRouteAsync(string input, Attachment? attachment)
    {
        var settings = _options.Value.LocalAi;
        if (LocalAi is null || attachment is not null || !settings.Router || (Mode == ChatMode.Claude && !settings.RouterInClaudeMode))
        {
            return false;
        }

        PetState = PetState.Think;
        var decision = await LocalAi.RouteAsync(input);
        PetState = PetState.Idle;
        if (decision is null)
        {
            return false;
        }

        _log.LogDebug("Router: {Kind} {Action} «{Text}»", decision.Kind, decision.Action, decision.Text);
        var inClaude = Mode == ChatMode.Claude;
        switch (decision)
        {
            // Writes: always confirmed, with the way back to the model the user was talking to.
            case { Kind: RouteKind.Action, Action: "appunto" }:
                Add(new ChatEntry(Speaker.User, input));
                Add(new ChatEntry(Speaker.Local, $"Lo aggiungo a To-do Appunti?\n\n> {decision.Text}")
                {
                    Actions =
                    [
                        new ChatAction("📝 Aggiungi", $"/appunto {decision.Text}", "Scrive l'appunto in To-do Appunti"),
                        inClaude
                            ? new ChatAction("No, mandalo a Claude", $"/c {input}", "Il messaggio va a Claude così com'è")
                            : new ChatAction("No, rispondimi", $"/l {input}", "Il modello locale risponde al messaggio"),
                    ],
                });
                return true;

            // Reads are instant and harmless, but in Claude mode the user asked Claude: leave it to Claude.
            case { Kind: RouteKind.Action } when !inClaude:
                await RunRoutedActionAsync(decision, input);
                return true;

            case { Kind: RouteKind.Claude } when !inClaude:
                Add(new ChatEntry(Speaker.User, input));
                Add(new ChatEntry(Speaker.Local, "Mi sembra un lavoro per Claude: legge il codice e ragiona meglio di me.")
                {
                    Actions =
                    [
                        new ChatAction("Chiedi a Claude", $"/c {input}", "Passa a Claude e gli manda il messaggio"),
                        new ChatAction("Prova tu", $"/l {input}", "Risponde il modello locale, con le note del progetto"),
                    ],
                });
                return true;

            default:
                return false;
        }
    }

    /// <summary>A read action picked by the router, answered like its command but under the user's own words.</summary>
    private async Task RunRoutedActionAsync(RouteDecision decision, string input)
    {
        var (command, rest) = decision.Action switch
        {
            "stato" => ("/stato", decision.Text),
            "cerca" => ("/cerca", decision.Text.Length > 0 ? decision.Text : input),
            "oggi" => ("/oggi", ""),
            _ => ("/modifiche", ""),
        };

        if (_options.Value.Assistant.IsDisabled(command) || Commands.Find(command) is not { } registered)
        {
            await AskLocalAsync(input, attachment: null);
            return;
        }

        await RunAsync(registered, new CommandCall(command, rest, input, null));
        if (Entries.LastOrDefault() is { Speaker: Speaker.Local } answer && answer.Actions.Count == 0)
        {
            OnUi(() => answer.Actions = [new ChatAction("Non era questo", $"/l {input}", $"Ho usato {command}: rispondo alla domanda invece")]);
        }
    }

    // ---- Search -----------------------------------------------------------------------------------------------

    /// <summary>"/cerca": the lines with the words at once, then the notes about the same thing in other words.</summary>
    private async Task SearchAsync(CommandCall call)
    {
        Instant(call.Input, _commands.Search(call.Rest));
        if (_semantic is null || call.Rest.Trim().Length == 0 || !_options.Value.LocalAi.SemanticSearch)
        {
            return;
        }

        var hits = await _semantic.SearchAsync(call.Rest, max: 5);
        var words = call.Rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fresh = hits.Where(h => !words.All(w => h.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var lines = fresh.Select(h =>
        {
            var snippet = string.Join(' ', h.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            snippet = snippet.Length > 180 ? snippet[..180] + "…" : snippet;
            return $"- [{h.Title} › {h.Heading}]({_commands.ObsidianUri(h.RelativePath)}): {snippet}";
        });
        Add(new ChatEntry(Speaker.Local, "**Per significato** (senza le stesse parole)\n\n" + string.Join('\n', lines)));
        SaveToArchive();
    }

    // ---- Drafts, attachments, titles --------------------------------------------------------------------------

    /// <summary>
    /// /commit and /pr written by the local model when the changes are small; true when it did. "/commit claude"
    /// (or a big diff, or no answer) leaves it to Claude.
    /// </summary>
    private async Task<bool> DraftLocallyAsync(CommandCall call, string prompt)
    {
        if (LocalAi is null || call.Rest.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        PetState = PetState.Think;
        var draft = await LocalAi.DraftAsync(prompt);
        PetState = PetState.Idle;
        if (draft is null)
        {
            return false;
        }

        Add(new ChatEntry(Speaker.User, call.Input));
        Add(new ChatEntry(Speaker.Local, draft + "\n\n_Bozza del modello locale._")
        {
            Actions = [new ChatAction("Rifallo con Claude", $"{call.Command} claude", "Claude legge il diff e scrive la bozza")],
        });
        SaveToArchive();
        return true;
    }

    /// <summary>A long attachment, condensed by the local model before it goes to Claude (said in the chat).</summary>
    private async Task<Attachment?> CondenseForClaudeAsync(Attachment? attachment)
    {
        if (LocalAi is null || attachment?.Text is not { } text || text.Length <= _options.Value.LocalAi.CondenseAboveChars)
        {
            return attachment;
        }

        AddSystem($"📎 Allegato lungo ({text.Length:N0} caratteri): lo riassumo in locale prima di mandarlo a Claude…");
        if (await LocalAi.CondenseAsync(text) is not { } condensed)
        {
            return attachment;
        }

        AddSystem($"📎 Riassunto: {condensed.Length:N0} caratteri invece di {text.Length:N0}. (Si spegne in Impostazioni → AI locale.)");
        return attachment with { Text = $"[Allegato di {text.Length} caratteri riassunto dal modello locale: errori, file e righe sono riportati alla lettera]\n{condensed}" };
    }

    /// <summary>Once a conversation has a few messages, the local model gives it a title for the list of saved ones.</summary>
    private void TitleInBackground()
    {
        if (LocalAi is null || Archive is not { } archive || !_options.Value.LocalAi.ConversationTitles)
        {
            return;
        }

        var id = _conversationId;
        var messages = Entries.Where(e => e.Speaker is Speaker.User or Speaker.Claude or Speaker.Local && e.Text.Length > 0)
            .Select(e => e.Text).ToList();
        if (messages.Count < 3 || !_titling.Add(id))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (archive.Find(id) is { AiTitle: null } && await LocalAi.TitleAsync(messages) is { } title)
                {
                    archive.SetAiTitle(id, title);
                }
            }
            finally
            {
                OnUi(() => _titling.Remove(id));
            }
        });
    }

    // ---- Appunti ----------------------------------------------------------------------------------------------

    /// <summary>"/suggerisci": where each open appunto could go, proposed by the local model; nothing is moved.</summary>
    private async Task SuggestSortingAsync(CommandCall call)
    {
        var appunti = _commands.OpenAppunti();
        if (appunti.Count == 0)
        {
            Instant(call.Input, "Non ci sono appunti aperti in To-do Appunti.");
            return;
        }

        Add(new ChatEntry(Speaker.User, call.Input));
        PetState = PetState.Think;
        var proposals = LocalAi is null ? null : await LocalAi.SortAsync(appunti);
        PetState = PetState.Idle;
        if (proposals is null)
        {
            Add(new ChatEntry(Speaker.Local, "Il modello locale non ha risposto (Ollama è avviato?). Puoi usare la skill /smista con Claude."));
            return;
        }

        var lines = proposals.Select(p =>
        {
            var title = appunti[p.Number - 1].Split(" — ")[0];
            return $"| {p.Number} | {(title.Length > 60 ? title[..60] + "…" : title)} | **{p.Column}** | {p.Reason} |";
        });
        Add(new ChatEntry(Speaker.Local,
            $"**Dove sposterei gli appunti** (proposta: non ho spostato niente)\n\n| # | Appunto | Dove | Perché |\n|---|---|---|---|\n{string.Join('\n', lines)}")
        {
            Actions = [new ChatAction("Smista con Claude", "/smista", "La skill /smista sposta davvero le voci, chiedendoti conferma")],
        });
        SaveToArchive();
    }
}
