using Buddy.Core.Context;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class VaultTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("buddy-vault-").FullName;
        File.WriteAllText(Path.Combine(_root, "Stato attuale.md"), "# Stato\nSto lavorando su NUSR-1.");
        File.WriteAllText(Path.Combine(_root, "To-Do.md"), """
            # To-Do

            - [ ] NUSR-1 · Primo task
              - dettaglio uno
              - collegato a NUSR-22
            - [ ] NUSR-22 · Secondo task
              - dettaglio due
            - [ ] NUSR-11 · Terzo task
            """);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void FindBlocks_returns_the_item_with_its_nested_lines_only()
    {
        using var vault = new VaultIndex(Settings());

        var blocks = vault.FindBlocks("NUSR-1").Where(b => b.RelativePath == "To-Do.md").ToList();

        Assert.That(blocks, Has.Count.EqualTo(1));
        Assert.That(blocks[0].Text, Does.StartWith("- [ ] NUSR-1 · Primo task"));
        Assert.That(blocks[0].Text, Does.Contain("collegato a NUSR-22"));
        Assert.That(blocks[0].Text, Does.Not.Contain("Secondo task"));
    }

    [Test]
    public void FindBlocks_does_not_match_longer_ids()
    {
        using var vault = new VaultIndex(Settings());

        Assert.That(vault.FindBlocks("NUSR-1").Select(b => b.Text), Has.None.Contains("Terzo task"));
    }

    [Test]
    public void ContextBuilder_includes_task_blocks_and_always_included_notes()
    {
        using var vault = new VaultIndex(Settings());
        var builder = new ContextBuilder(vault, Settings(), TimeProvider.System);

        var context = builder.Build("a che punto è nusr-22?");

        Assert.That(context.Text, Does.Contain("Secondo task"));
        Assert.That(context.Text, Does.Contain("Sto lavorando su NUSR-1"));
        Assert.That(context.Sources.Select(s => s.Label), Is.EqualTo(new[] { "To-Do.md (NUSR-22)", "Stato attuale.md" }));
        Assert.That(context.Sources.All(s => s.Link!.StartsWith("obsidian://open?path=")), Is.True);
        var footer = context.SourcesMarkdown();
        Assert.That(footer, Does.StartWith("Fonti: [To-Do.md (NUSR-22)](obsidian://"));
        Assert.That(footer, Does.Not.Contain("Stato attuale"), "always-included notes are background, not sources");
        Assert.That(builder.Build("come stai?", currentTaskId: "NUSR-22").SourcesMarkdown(), Is.Null, "the branch task not named is background");
        Assert.That(context.Truncated, Is.False);
    }

    [Test]
    public void ContextBuilder_respects_the_budget()
    {
        File.WriteAllText(Path.Combine(_root, "Stato attuale.md"), new string('x', 5_000));
        using var vault = new VaultIndex(Settings(maxChars: 1_000));
        var builder = new ContextBuilder(vault, Settings(maxChars: 1_000), TimeProvider.System);

        var context = builder.Build("ciao");

        Assert.That(context.Truncated, Is.True);
        Assert.That(context.Text.Length, Is.LessThan(1_200));
    }

    private IOptions<BuddySettings> Settings(int maxChars = 12_000) =>
        Options.Create(new BuddySettings { ProjectPath = _root, MaxContextChars = maxChars, AlwaysIncludedNotes = ["Stato attuale.md"] });
}
