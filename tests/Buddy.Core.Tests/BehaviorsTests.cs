using Buddy.Core.Claude;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class BehaviorsTests
{
    private static List<string> Args(BuddySettings settings) =>
        new ClaudeSession(Options.Create(settings), _ => Task.FromResult(PermissionDecision.Allowed)).BuildArguments(null).ToList();

    private static List<string> ValuesOf(List<string> args, string flag) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == flag).Select(x => args[x.i + 1]).ToList();

    [Test]
    public void Only_behaviours_switched_away_from_the_skills_become_instructions()
    {
        var settings = new BuddySettings { Assistant = { QuickReplies = false } };
        Assert.That(Behaviors.SystemPrompt(settings), Is.Null, "as the skills do: nothing to say");

        settings.Behaviors = new() { [Behaviors.ManualTests] = true, ["revisione"] = false };
        settings.BehaviorNotes = "Nomi delle variabili in inglese\n- Un test unitario per ogni bug";
        var prompt = Behaviors.SystemPrompt(settings)!;

        Assert.That(prompt, Does.Contain("documento dei test manuali").And.Contain("Salta la revisione automatica"));
        Assert.That(prompt, Does.Contain("- Nomi delle variabili in inglese\n- Un test unitario per ogni bug"));
        Assert.That(prompt, Does.Not.Contain("test web"), "untouched behaviours say nothing");
    }

    [Test]
    public void The_session_passes_one_system_prompt_with_quick_replies_and_preferences()
    {
        var args = Args(new BuddySettings { Behaviors = new() { ["breve"] = true } });

        var prompts = ValuesOf(args, "--append-system-prompt");
        Assert.That(prompts, Has.Count.EqualTo(1));
        Assert.That(prompts[0], Does.Contain("sintetico").And.Contain("<!-- risposte:"));
    }

    [Test]
    public void Guided_mode_denies_editing_the_code_but_not_the_notes_and_always_asks()
    {
        var root = Path.GetTempPath();
        var settings = new BuddySettings
        {
            GuidedMode = true,
            ProjectPath = Path.Combine(root, "vault"),
            Repo = { Path = Path.Combine(root, "repo") },
            Claude = { PermissionMode = "bypassPermissions", AdditionalDirectories = [Path.Combine(root, "vault", "Allegati")] },
        };

        var args = Args(settings);
        var denied = ValuesOf(args, "--disallowedTools");
        var repoRule = ClaudeOneShot.AbsoluteRulePath(Path.Combine(root, "repo"));

        Assert.That(denied, Is.SupersetOf(new[] { $"Edit({repoRule}/**)", $"Write({repoRule}/**)" }));
        Assert.That(denied, Has.None.Contain("vault"), "the flow's notes stay writable");
        Assert.That(args, Does.Not.Contain("--permission-mode"), "never bypass in guided mode");
        Assert.That(ValuesOf(args, "--append-system-prompt").Single(), Does.Contain("MODALITÀ GUIDA"));
    }

    [Test]
    public void Guided_mode_without_a_code_folder_denies_every_edit()
    {
        var denied = Behaviors.DeniedTools(new BuddySettings { GuidedMode = true });
        Assert.That(denied, Is.SupersetOf(new[] { "Edit", "Write" }));
    }

    [TestCase(false, false, "/lavora NUSR-1", null)]
    [TestCase(false, true, "/lavora NUSR-1", "/lavora NUSR-1 test")]
    [TestCase(false, true, "/lavora NUSR-1 autonomo test", null)]
    [TestCase(true, true, "/lavora NUSR-1 autonomo test", "/guidami NUSR-1")]
    [TestCase(true, false, "/lavorazione", null)]
    [TestCase(true, false, "/stato NUSR-1", null)]
    public void Lavora_is_rewritten_for_guided_mode_and_the_manual_tests_preference(bool guided, bool manualTests, string input, string? expected)
    {
        var settings = new BuddySettings { GuidedMode = guided, Behaviors = manualTests ? new() { [Behaviors.ManualTests] = true } : [] };

        Assert.That(Behaviors.Rewrite(settings, input), Is.EqualTo(expected));
    }

    [Test]
    public void Guided_mode_travels_with_the_project()
    {
        var settings = new BuddySettings { GuidedMode = true };
        var preset = ProjectPreset.From(settings, "Corso");
        var other = new BuddySettings();

        preset.ApplyTo(other);

        Assert.That(other.GuidedMode, Is.True);
    }
}
