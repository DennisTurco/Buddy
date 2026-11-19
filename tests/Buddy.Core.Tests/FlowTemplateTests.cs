using Buddy.Core.Claude;
using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class FlowTemplateTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buddy-flow-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void The_built_in_template_has_every_project_note_and_the_shared_parts()
    {
        var paths = FlowTemplate.Load().Select(f => f.Path).ToList();
        Assert.That(paths, Is.SupersetOf(FlowTemplate.ProjectNotes));
        Assert.That(paths, Has.Some.StartsWith("Skill/").And.Some.StartsWith("Modelli/"));
    }

    [Test]
    public void A_new_vault_gets_the_project_notes_and_the_shared_template_with_links_to_the_project()
    {
        var project = Path.Combine(_root, "Vault", "Progetti", "Demo");
        var created = FlowTemplate.CreateProject(project, "Demo");

        var vault = Path.Combine(_root, "Vault");
        Assert.That(Directory.Exists(Path.Combine(vault, ".obsidian")), Is.True, "opens as an Obsidian vault");
        Assert.That(File.Exists(Path.Combine(project, "To-Do.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(vault, "Intelligenza Artificiale", "Template flusso", "Skill", "Skill lavora.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(project, "Skill", "Skill lavora.md")), Is.False, "skills stay shared");
        Assert.That(FlowTemplate.HasFlow(project), Is.True);
        Assert.That(created, Is.Not.Empty);

        var index = File.ReadAllText(Path.Combine(project, "00 Indice.md"));
        Assert.That(index, Does.Contain("[[Progetti/Demo/To-Do]]"));
        Assert.That(index, Does.Not.Contain("[[Intelligenza Artificiale/Template flusso/To-Do]]"));

        var scheda = File.ReadAllText(Path.Combine(project, "Scheda progetto.md"));
        Assert.That(scheda, Does.Contain("- **Nome:** Demo"));
        Assert.That(scheda, Does.Contain($"- **Cartella del progetto nel vault:** `{project}`"));
    }

    [Test]
    public void Existing_notes_are_never_overwritten()
    {
        var project = Path.Combine(_root, "Vault", "Progetti", "Demo");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "To-Do.md"), "mie note");

        FlowTemplate.CreateProject(project, "Demo");

        Assert.That(File.ReadAllText(Path.Combine(project, "To-Do.md")), Is.EqualTo("mie note"));
    }

    [Test]
    public void A_personal_template_inside_the_vault_is_used_as_the_shared_copy()
    {
        var vault = Path.Combine(_root, "Vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        var personal = Path.Combine(vault, "Modelli miei", "Flusso");
        Directory.CreateDirectory(personal);
        File.WriteAllText(Path.Combine(personal, "To-Do.md"), "Vedi [[Intelligenza Artificiale/Template flusso/Da controllare]] e [[Intelligenza Artificiale/Template flusso/Modelli/Nuovo task]]");
        File.WriteAllText(Path.Combine(personal, "Da controllare.md"), "x");

        var project = Path.Combine(vault, "Progetti", "Demo");
        FlowTemplate.CreateProject(project, "Demo", personal);

        Assert.That(File.ReadAllText(Path.Combine(project, "To-Do.md")),
            Is.EqualTo("Vedi [[Progetti/Demo/Da controllare]] e [[Modelli miei/Flusso/Modelli/Nuovo task]]"));
        Assert.That(Directory.Exists(Path.Combine(vault, "Intelligenza Artificiale")), Is.False, "no second copy");
    }

    [Test]
    public void A_fresh_project_is_up_to_date()
    {
        var project = Path.Combine(_root, "Vault", "Progetti", "Demo");
        FlowTemplate.CreateProject(project, "Demo");

        var update = FlowTemplate.CheckUpdate(project, home: Path.Combine(_root, "home"));

        Assert.That(update.Files, Is.Empty);
        Assert.That(update.HasUpdates, Is.False);
        Assert.That(update.TemplateVersion, Is.Not.Null);
    }

    [Test]
    public void An_update_adds_missing_notes_replaces_untouched_shared_notes_and_asks_for_edited_ones()
    {
        var vault = Path.Combine(_root, "Vault");
        var project = Path.Combine(vault, "Progetti", "Demo");
        FlowTemplate.CreateProject(project, "Demo");
        var shared = Path.Combine(vault, "Intelligenza Artificiale", "Template flusso");

        // An old template: a note it did not have, a shared note Buddy wrote (old version), one edited by hand.
        File.Delete(Path.Combine(project, "Persone.md"));
        var lavora = Path.Combine(shared, "Skill", "Skill lavora.md");
        var chiudi = Path.Combine(shared, "Skill", "Skill chiudi.md");
        File.WriteAllText(lavora, "vecchia versione");
        RecordInManifest(shared, "Skill/Skill lavora.md", "vecchia versione");
        File.WriteAllText(chiudi, "la mia versione");
        File.WriteAllText(Path.Combine(project, "To-Do.md"), "i miei task");

        var update = FlowTemplate.CheckUpdate(project, home: Path.Combine(_root, "home"));
        var changes = update.Files.ToDictionary(f => f.Path, f => f.Change);
        Assert.That(changes["Persone.md"], Is.EqualTo(TemplateChange.Added));
        Assert.That(changes["Skill/Skill lavora.md"], Is.EqualTo(TemplateChange.Updated));
        Assert.That(changes["Skill/Skill chiudi.md"], Is.EqualTo(TemplateChange.Edited));
        Assert.That(changes.ContainsKey("To-Do.md"), Is.False, "the project's content is never replaced");

        var result = FlowTemplate.ApplyUpdate(update, replaceEdited: false, new DateTime(2026, 11, 19), Path.Combine(_root, "home"));
        Assert.That(File.Exists(Path.Combine(project, "Persone.md")), Is.True);
        Assert.That(File.ReadAllText(lavora), Does.Not.Contain("vecchia versione"));
        Assert.That(File.ReadAllText(chiudi), Is.EqualTo("la mia versione"));
        Assert.That(File.ReadAllText(Path.Combine(project, "To-Do.md")), Is.EqualTo("i miei task"));
        Assert.That(result.Skipped.Select(f => f.Path), Is.EqualTo(new[] { "Skill/Skill chiudi.md" }));
        Assert.That(result.Backup, Is.Null);

        var again = FlowTemplate.CheckUpdate(project, home: Path.Combine(_root, "home"));
        Assert.That(again.Files.Select(f => f.Path), Is.EqualTo(new[] { "Skill/Skill chiudi.md" }));
        var replaced = FlowTemplate.ApplyUpdate(again, replaceEdited: true, new DateTime(2026, 11, 19, 10, 0, 0), Path.Combine(_root, "home"));
        Assert.That(File.ReadAllText(chiudi), Does.Not.Contain("la mia versione"));
        Assert.That(File.ReadAllText(Path.Combine(replaced.Backup!, "Skill", "Skill chiudi.md")), Is.EqualTo("la mia versione"));
        Assert.That(FlowTemplate.CheckUpdate(project, home: Path.Combine(_root, "home")).Files, Is.Empty);
    }

    [Test]
    public void Installed_skills_follow_the_template_and_hand_edited_ones_need_consent()
    {
        var home = Path.Combine(_root, "home");
        var project = Path.Combine(_root, "Vault", "Progetti", "Demo");
        FlowTemplate.CreateProject(project, "Demo");
        ClaudeSkills.InstallMissing(new BuddySettings(), home);

        var lavora = Path.Combine(home, ".claude", "skills", "lavora", "SKILL.md");
        var smista = Path.Combine(home, ".claude", "skills", "smista", "SKILL.md");
        var shared = Path.Combine(_root, "Vault", "Intelligenza Artificiale", "Template flusso");
        // lavora: an old copy installed by an older Buddy (no record), the same as the vault's old copy; smista: edited by hand.
        File.WriteAllText(lavora, "vecchia");
        File.WriteAllText(Path.Combine(shared, "Skill", "Skill lavora.md"), "vecchia");
        File.WriteAllText(smista, "mia");
        var skillManifest = Path.Combine(home, ".claude", "skills", ".buddy-template.json");
        var records = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(skillManifest))!;
        records.Remove("lavora");
        File.WriteAllText(skillManifest, System.Text.Json.JsonSerializer.Serialize(records));

        var update = FlowTemplate.CheckUpdate(project, home: home);
        var skills = update.Files.Where(f => f.Skill is not null).ToDictionary(f => f.Skill!, f => f.Change);
        Assert.That(skills["lavora"], Is.EqualTo(TemplateChange.Updated));
        Assert.That(skills["smista"], Is.EqualTo(TemplateChange.Edited));

        FlowTemplate.ApplyUpdate(update, replaceEdited: false, DateTime.Now, home);
        Assert.That(File.ReadAllText(lavora), Does.StartWith("---"));
        Assert.That(File.ReadAllText(smista), Is.EqualTo("mia"));
    }

    [Test]
    public void The_changelog_entries_after_the_project_version_go_to_Claude_and_the_version_is_written()
    {
        var project = Path.Combine(_root, "Vault", "Progetti", "Demo");
        FlowTemplate.CreateProject(project, "Demo");
        FlowTemplate.SetProjectVersion(project, "1.24");

        var update = FlowTemplate.CheckUpdate(project, home: Path.Combine(_root, "home"));
        Assert.That(update.ProjectVersion, Is.EqualTo("1.24"));
        Assert.That(update.VersionBehind, Is.True);
        Assert.That(update.CanAskClaude, Is.True);
        Assert.That(update.NewChangelog, Does.Contain("## 1.25").And.Contain("## 1.26").And.Not.Contain("## 1.24"));
        Assert.That(update.NewChangelog.IndexOf("## 1.25", StringComparison.Ordinal), Is.LessThan(update.NewChangelog.IndexOf("## 1.26", StringComparison.Ordinal)));
        Assert.That(FlowTemplate.UpdatePrompt(update, "/tmp/t"), Does.Contain("Mai cambiare il contenuto"));

        FlowTemplate.SetProjectVersion(project, update.TemplateVersion!);
        Assert.That(FlowTemplate.CheckUpdate(project, home: Path.Combine(_root, "home")).HasUpdates, Is.False);
    }

    private static void RecordInManifest(string folder, string key, string content)
    {
        var file = Path.Combine(folder, ".buddy-template.json");
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        map[key] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(map));
    }
}
