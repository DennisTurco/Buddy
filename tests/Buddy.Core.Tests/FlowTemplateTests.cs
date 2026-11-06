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
}
