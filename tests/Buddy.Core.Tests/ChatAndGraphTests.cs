using Buddy.Core.Chat;
using Buddy.Core.Mentor;
using Buddy.Core.Text;
using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class CodeTokenizerTests
{
    [Test]
    public void Tokens_add_up_to_the_original_text()
    {
        const string code = "var x = \"ciao\"; // saluto\nint n = 42;";

        var tokens = CodeTokenizer.Tokenize(code, "csharp");

        Assert.That(string.Concat(tokens.Select(t => t.Text)), Is.EqualTo(code));
    }

    [Test]
    public void CSharp_keywords_strings_comments_and_numbers_are_recognized()
    {
        var tokens = CodeTokenizer.Tokenize("public class Foo { string s = \"a\"; } // fine\nreturn 3;", "c#");

        Assert.Multiple(() =>
        {
            Assert.That(tokens, Does.Contain(new CodeToken("public", TokenKind.Keyword)));
            Assert.That(tokens, Does.Contain(new CodeToken("Foo", TokenKind.Type)));
            Assert.That(tokens, Does.Contain(new CodeToken("\"a\"", TokenKind.String)));
            Assert.That(tokens, Does.Contain(new CodeToken("// fine", TokenKind.Comment)));
            Assert.That(tokens, Does.Contain(new CodeToken("3", TokenKind.Number)));
        });
    }

    [Test]
    public void Python_uses_hash_comments_and_sql_is_case_insensitive()
    {
        Assert.That(CodeTokenizer.Tokenize("def f(): # nota", "py"), Does.Contain(new CodeToken("# nota", TokenKind.Comment)));
        Assert.That(CodeTokenizer.Tokenize("SELECT * FROM t -- x", "sql"), Does.Contain(new CodeToken("SELECT", TokenKind.Keyword)));
    }

    [Test]
    public void Json_keys_are_told_apart_from_values()
    {
        var tokens = CodeTokenizer.Tokenize("{ \"nome\": \"Buddy\" }", "json");

        Assert.That(tokens.First(t => t.Text.StartsWith("\"nome\"")).Kind, Is.EqualTo(TokenKind.Tag));
        Assert.That(tokens, Does.Contain(new CodeToken("\"Buddy\"", TokenKind.String)));
    }
}

public class NoteGraphTests
{
    [Test]
    public void Wiki_and_markdown_links_become_edges_counted_once()
    {
        var notes = new List<VaultNote>
        {
            new("To-Do.md", "Vedi [[Analisi/Login|login]] e [[Glossario#Termini]], ancora [[Analisi/Login]]."),
            new("Analisi/Login.md", "Torna al [To-Do](../To-Do.md). ![[schema.png]]"),
            new("Glossario.md", "Nessun link"),
            new("Isolata.md", "Niente"),
        };

        var graph = NoteGraph.Build(notes);

        Assert.That(graph.Edges, Has.Count.EqualTo(2));
        Assert.That(graph.Nodes.Single(n => n.Title == "To-Do").Degree, Is.EqualTo(2));
        Assert.That(graph.Nodes.Single(n => n.Title == "Isolata").Degree, Is.EqualTo(0));
        Assert.That(graph.Nodes.Single(n => n.Title == "Login").Folder, Is.EqualTo("Analisi"));
        Assert.That(graph.Nodes.Any(n => n.Title == "schema.png"), Is.False, "images are not notes");
    }

    [Test]
    public void Links_to_notes_not_yet_created_are_missing_nodes()
    {
        var graph = NoteGraph.Build([new VaultNote("A.md", "[[Da scrivere]]")]);

        var missing = graph.Nodes.Single(n => n.Title == "Da scrivere");
        Assert.That(missing.IsMissing, Is.True);
        Assert.That(missing.Degree, Is.EqualTo(1));
    }
}

public class ChatCommandsTests
{
    private static CommandRegistry Registry()
    {
        var registry = new CommandRegistry();
        registry.Add(new ChatCommand("/stato", "scheda del task", "[ID]"), _ => Task.CompletedTask);
        registry.Add(new ChatCommand("/riapri", "riapre", Hidden: true), _ => Task.CompletedTask);
        foreach (var name in new[] { "/checkin", "/checkout", "/chiacchiera" })
        {
            registry.Add(new ChatCommand(name, "mentore", Group: CommandGroup.Mentor), _ => Task.CompletedTask);
        }

        return registry;
    }

    [Test]
    public void A_typo_of_a_known_command_is_caught()
    {
        Assert.That(Registry().Closest("/chiacchera", []), Is.EqualTo("/chiacchiera"));
        Assert.That(Registry().Closest("/lavoar", ["lavora"]), Is.EqualTo("/lavora"));
    }

    [Test]
    public void Known_and_unrelated_commands_are_left_alone()
    {
        Assert.That(Registry().Closest("/chiacchiera", []), Is.Null);
        Assert.That(Registry().Closest("/riapri", []), Is.Null);
        Assert.That(Registry().Closest("/compact", []), Is.Null);
    }

    [Test]
    public void Suggestions_match_the_prefix()
    {
        var matches = Registry().Matching("/ch", ["chiudi"]).Select(c => c.Name).ToList();

        Assert.That(matches, Is.EquivalentTo(new[] { "/checkin", "/checkout", "/chiacchiera", "/chiudi" }));
    }

    [Test]
    public void Hidden_commands_run_but_are_not_suggested_nor_listed()
    {
        var registry = Registry();

        Assert.That(registry.Find("/riapri"), Is.Not.Null);
        Assert.That(registry.Matching("/ri", []), Is.Empty);
        Assert.That(registry.Help(CommandGroup.Chat), Does.Not.Contain("/riapri"));
    }

    [Test]
    public void A_command_is_registered_only_once()
    {
        var registry = Registry();

        Assert.Throws<InvalidOperationException>(() => registry.Add(new ChatCommand("/STATO", "doppione"), _ => Task.CompletedTask));
    }

    [Test]
    public void Help_lists_each_command_with_its_arguments()
    {
        Assert.That(Registry().Help(CommandGroup.Chat), Is.EqualTo("/stato [ID] — scheda del task"));
    }

    [Test]
    public async Task Aiuto_lists_every_command_of_the_chat()
    {
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = Path.Combine(Path.GetTempPath(), "buddy-missing-vault") });

        await conversation.SendAsync("/aiuto");

        var help = conversation.Entries.Last(e => e.Speaker == Speaker.System).Text;
        foreach (var command in conversation.Commands.All.Where(c => c.Group == CommandGroup.Chat))
        {
            Assert.That(help, Does.Contain(command.Usage + " — "));
        }
    }

    [Test]
    public async Task Salta_without_a_question_stays_in_the_buddy()
    {
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = Path.Combine(Path.GetTempPath(), "buddy-missing-vault") });

        await conversation.SendAsync("/salta");

        Assert.That(conversation.Mode, Is.EqualTo(ChatMode.Local));
        Assert.That(conversation.Entries.Select(e => e.Text), Is.EqualTo(new[] { "/salta", "Nessuna domanda da saltare." }));
    }
}

public class MentorDialogueTests
{
    [Test]
    public void A_reply_with_a_question_keeps_the_dialogue_open()
    {
        Assert.That(MentorPrompts.AsksSomething("Bravo. Cosa pensi che faccia il database?"), Is.True);
        Assert.That(MentorPrompts.AsksSomething("Ottimo, a domani!"), Is.False);
    }
}

public class ModelCommandTests
{
    internal static Conversation Create(BuddySettings settings)
    {
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var vault = new VaultIndex(options);
        var repo = new Buddy.Core.Repo.GitRepo(options, TimeProvider.System);
        var local = new Buddy.Core.Llm.LocalAssistant(
            new Buddy.Core.Llm.OllamaClient(new HttpClient(), options),
            new Buddy.Core.Context.ContextBuilder(vault, options, TimeProvider.System), options);
        return new Conversation(local, new Buddy.Core.Claude.ClaudeAuth(options),
            new AssistantCommands(vault, repo, options), repo, options);
    }

    [Test]
    public async Task Modello_switches_the_claude_and_the_local_model()
    {
        var settings = new BuddySettings { ProjectPath = Path.Combine(Path.GetTempPath(), "buddy-missing-vault") };
        var conversation = Create(settings);
        var changed = new List<string?>();
        conversation.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await conversation.SendAsync("/modello haiku");
        Assert.That(settings.Claude.Model, Is.EqualTo("haiku"));
        Assert.That(conversation.Mode, Is.EqualTo(ChatMode.Claude));

        await conversation.SendAsync("/modello locale qwen2.5:7b");
        Assert.That(settings.Ollama.Model, Is.EqualTo("qwen2.5:7b"));
        Assert.That(conversation.Mode, Is.EqualTo(ChatMode.Local));

        await conversation.SendAsync("/modello predefinito");
        Assert.That(settings.Claude.Model, Is.EqualTo(""));
        Assert.That(changed, Does.Contain(nameof(Conversation.ClaudeModel)).And.Contain(nameof(Conversation.LocalModel)));
    }
}
