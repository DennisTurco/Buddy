using Buddy.Core.Chat;
using Buddy.Core.Context;
using Buddy.Core.Repo;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class AssistantTests
{
    private string _vault = "";
    private string _repo = "";

    [SetUp]
    public void SetUp()
    {
        _vault = Directory.CreateTempSubdirectory("buddy-vault-").FullName;
        _repo = Directory.CreateTempSubdirectory("buddy-repo-").FullName;
        File.WriteAllText(Path.Combine(_vault, "Da controllare.md"), """
            # Da controllare

            - [ ] NUSR-7 · Blocco login [priorita:: P2] [stima:: 8h]
              - manca il test dell'app
            """);
        Directory.CreateDirectory(Path.Combine(_repo, "docs", "guidelines"));
        File.WriteAllText(Path.Combine(_repo, "docs", "guidelines", "database.md"), """
            # Database

            ## Seed data
            Gli script di seed vanno nei progetti SSDT, sotto _Data.

            ## Migrazioni DbUp
            Gli script DbUp esistenti sono immutabili.
            """);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_vault, recursive: true);
        Directory.Delete(_repo, recursive: true);
    }

    [TestCase("NUSR-2367-Numero-massimo-di-login-errati", "NUSR-2367")]
    [TestCase("feature/nusr-12-fix", "NUSR-12")]
    [TestCase("main", null)]
    [TestCase(null, null)]
    public void Task_id_comes_from_the_branch_name(string? branch, string? expected) =>
        Assert.That(GitRepo.TaskIdFrom(branch, new BuddySettings().TaskIdPattern), Is.EqualTo(expected));

    [Test]
    public void Porcelain_status_is_parsed()
    {
        var files = GitRepo.ParseStatus(" M a/b.cs\n?? new.txt\nD  old.sql\nR  x.vb -> y.vb\n");

        Assert.That(files, Is.EqualTo(new[]
        {
            new ChangedFile("a/b.cs", "modificato"),
            new ChangedFile("new.txt", "nuovo"),
            new ChangedFile("old.sql", "eliminato"),
            new ChangedFile("y.vb", "rinominato"),
        }));
    }

    [Test]
    public void Changes_are_grouped_by_prefix_in_rule_order()
    {
        var groups = ChangeGroups.Group(
            [new("Common/Database/x.sql", "modificato"), new("User30/sources/User.Client/a.ts", "nuovo"), new("README.md", "modificato")],
            ["Frontend=User30/sources/User.Client", "Database=Common/Database"]);

        Assert.That(groups.Select(g => g.Group), Is.EqualTo(new[] { "Frontend", "Database", ChangeGroups.Other }));
    }

    [Test]
    public void Describe_task_reads_title_and_note_from_the_vault()
    {
        var commands = Commands();

        Assert.That(commands.DescribeTask("NUSR-7")!.ToString(), Is.EqualTo("NUSR-7 · Blocco login (Da controllare)"));
        Assert.That(commands.DescribeTask("NUSR-99")!.ToString(), Is.EqualTo("NUSR-99"));
    }

    [Test]
    public void Vault_search_returns_matching_lines_with_obsidian_links()
    {
        var result = Commands().Search("test app");

        Assert.That(result, Does.Contain("1 risultati"));
        Assert.That(result, Does.Contain("obsidian://open?path="));
        Assert.That(result, Does.Contain("manca il test dell'app"));
    }

    [Test]
    public void Knowledge_search_finds_the_matching_guideline_section()
    {
        var settings = Settings();
        var knowledge = new KnowledgeIndex(settings, new GitRepo(settings, TimeProvider.System));

        var sections = knowledge.Search("dove vanno gli script di seed?");

        Assert.That(sections.First().Heading, Is.EqualTo("Seed data"));
    }

    [TestCase("come stai?")]
    [TestCase("cosa manca da sviluppare lato codice per la 2367? abbiamo tutto?")]
    public void Knowledge_search_ignores_questions_about_something_else(string question)
    {
        var settings = Settings();
        var knowledge = new KnowledgeIndex(settings, new GitRepo(settings, TimeProvider.System));

        Assert.That(knowledge.Search(question), Is.Empty);
    }

    [Test]
    public void A_bare_task_number_means_the_branch_task()
    {
        var settings = Settings();
        using var vault = new VaultIndex(settings);
        var builder = new ContextBuilder(vault, settings, TimeProvider.System);

        var context = builder.Build("e la 7 a che punto è?", currentTaskId: "NUSR-7");

        Assert.That(context.Sources.Select(s => s.Label), Has.Some.Contains("(NUSR-7)"));
    }

    [Test]
    public void Context_uses_the_branch_task_when_the_question_names_none()
    {
        var settings = Settings();
        using var vault = new VaultIndex(settings);
        var builder = new ContextBuilder(vault, settings, TimeProvider.System);

        var context = builder.Build("cosa manca?", currentTaskId: "NUSR-7", attachment: "stack trace qui");

        Assert.That(context.Text, Does.Contain("manca il test dell'app"));
        Assert.That(context.Text, Does.Contain("stack trace qui"));
        Assert.That(context.Sources.Select(s => s.Label), Does.Contain("allegato"));
    }

    [Test]
    public void Dropped_files_inline_text_and_keep_images_for_claude()
    {
        var text = Path.Combine(_repo, "error.log");
        var image = Path.Combine(_repo, "shot.png");
        File.WriteAllText(text, "NullReferenceException at Foo");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);

        var attachment = Attachment.FromFiles([text, image], maxChars: 1000);

        Assert.That(attachment.Images, Has.Count.EqualTo(1));
        Assert.That(attachment.ToPromptText(1000), Does.Contain("NullReferenceException").And.Contain(image));
        Assert.That(attachment.Preview, Is.EqualTo("1 immagine · 2 file"));
    }

    private AssistantCommands Commands()
    {
        var settings = Settings();
        return new AssistantCommands(new VaultIndex(settings), new GitRepo(settings, TimeProvider.System), settings);
    }

    private IOptions<BuddySettings> Settings() => Options.Create(new BuddySettings
    {
        ProjectPath = _vault,
        Repo = new RepoSettings { Path = _repo, KnowledgePaths = ["docs/guidelines"] },
    });
}
