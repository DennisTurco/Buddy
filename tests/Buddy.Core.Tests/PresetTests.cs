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
