namespace Buddy.Core.Tests;

public class PresetTests
{
    [Test]
    public void Preset_round_trips_the_project_fields_and_leaves_the_rest()
    {
        var settings = new BuddySettings
        {
            ProjectPath = "vault-a",
            AlwaysIncludedNotes = ["Stato.md"],
            Claude = { WorkingDirectory = "repo-a", Model = "sonnet" },
            Repo = { KnowledgePaths = ["docs"], Groups = ["DB=db"], CommitInstructions = "conv A" },
            Assistant = { TaskActions = ["/lavora {id}"] },
            Appearance = { Name = "Bob" },
        };
        var presetA = ProjectPreset.From(settings, "A");

        new ProjectPreset { Name = "B", ProjectPath = "vault-b", ClaudeWorkingDirectory = "repo-b" }.ApplyTo(settings);
        Assert.That(settings.ProjectPath, Is.EqualTo("vault-b"));
        Assert.That(settings.Repo.Groups, Is.Empty);
        Assert.That(settings.ActivePreset, Is.EqualTo("B"));

        presetA.ApplyTo(settings);
        Assert.That(settings.ProjectPath, Is.EqualTo("vault-a"));
        Assert.That(settings.Claude.WorkingDirectory, Is.EqualTo("repo-a"));
        Assert.That(settings.Repo.CommitInstructions, Is.EqualTo("conv A"));
        Assert.That(settings.Assistant.TaskActions, Is.EqualTo(new[] { "/lavora {id}" }));
        Assert.That(settings.Claude.Model, Is.EqualTo("sonnet"), "model is global, not part of the preset");
        Assert.That(settings.Appearance.Name, Is.EqualTo("Bob"), "appearance is global");
    }

    [Test]
    public void Preset_is_a_copy_not_a_reference()
    {
        var settings = new BuddySettings { Repo = { Groups = ["DB=db"] } };
        var preset = ProjectPreset.From(settings, "A");

        settings.Repo.Groups.Add("UI=ui");

        Assert.That(preset.Repo.Groups, Has.Count.EqualTo(1));
    }
}

public class ProjectSwitchingTests
{
    [Test]
    public void New_project_keeps_the_current_one_and_becomes_active()
    {
        var vault = Directory.CreateTempSubdirectory("buddy-vault-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(vault, "To-Do.md"), "");
            var settings = new BuddySettings { ProjectPath = "/vaults/ContaEdu", Repo = { Path = "/code/contaedu", CommitInstructions = "conv" } };

            var preset = ProjectPreset.New("Buddy", vault, "/code/buddy", settings);
            settings.SwitchTo(preset);

            Assert.That(settings.ActivePreset, Is.EqualTo("Buddy"));
            Assert.That(settings.ProjectPath, Is.EqualTo(vault));
            Assert.That(settings.AlwaysIncludedNotes, Is.EqualTo(new[] { "To-Do.md" }), "only the flow notes the vault has");
            Assert.That(settings.Repo.CommitInstructions, Is.EqualTo("conv"), "conventions carried over");
            Assert.That(settings.Presets.Select(p => p.Name), Is.EquivalentTo(new[] { "ContaEdu", "Buddy" }),
                "the project left had no preset: one is made from its folder name");
            Assert.That(settings.Presets.Single(p => p.Name == "ContaEdu").ProjectPath, Is.EqualTo("/vaults/ContaEdu"));

            settings.ProjectPath = vault + "-edited";
            settings.SwitchTo(settings.Presets.Single(p => p.Name == "ContaEdu"));
            Assert.That(settings.ProjectPath, Is.EqualTo("/vaults/ContaEdu"));
            Assert.That(settings.Presets.Single(p => p.Name == "Buddy").ProjectPath, Is.EqualTo(vault + "-edited"), "edits saved on the way out");
        }
        finally
        {
            Directory.Delete(vault, recursive: true);
        }
    }
}
