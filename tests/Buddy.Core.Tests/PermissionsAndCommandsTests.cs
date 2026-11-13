using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class PermissionsAndCommandsTests
{
    private static readonly string MissingVault = Path.Combine(Path.GetTempPath(), "buddy-missing-vault");

    private static List<string> Args(ClaudeSettings claude) =>
        new ClaudeSession(Options.Create(new BuddySettings { Claude = claude }), _ => Task.FromResult(PermissionDecision.Allowed))
            .BuildArguments(null).ToList();

    private static List<string> ValuesOf(List<string> args, string flag) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == flag).Select(x => args[x.i + 1]).ToList();

    [Test]
    public void Active_rules_become_allowed_and_disallowed_tools_and_switched_off_ones_are_left_out()
    {
        var args = Args(new ClaudeSettings
        {
            AutoAllowTools = ["Glob"],
            ToolRules =
            [
                new() { Tool = "Read" },
                new() { Tool = " Grep ", Enabled = false },
                new() { Tool = "Bash(git commit:*)", Allow = false },
                new() { Tool = "Bash(git push:*)", Allow = false, Enabled = false },
                new() { Tool = "" },
            ],
        });

        Assert.That(ValuesOf(args, "--allowedTools"), Is.EqualTo(new[] { "Glob", "Read" }));
        Assert.That(ValuesOf(args, "--disallowedTools"), Is.EqualTo(new[] { "Bash(git commit:*)" }));
    }

    [Test]
    public void The_suggested_rules_block_commit_and_push()
    {
        var denied = ClaudeSettings.SuggestedRules.Where(r => !r.Allow).Select(r => r.Tool).ToList();
        Assert.That(denied, Is.SupersetOf(new[] { "Bash(git commit:*)", "Bash(git push:*)", "Bash(gh pr create:*)" }));
    }

    [Test]
    public async Task A_disabled_command_only_says_it_is_off_and_is_not_suggested()
    {
        var settings = new BuddySettings { ProjectPath = MissingVault, Assistant = { DisabledCommands = ["/oggi", "/lavora"] } };
        var conversation = ModelCommandTests.Create(settings);

        await conversation.SendAsync("/OGGI");
        await conversation.SendAsync("/lavora 12");

        Assert.That(conversation.Entries.Where(e => e.Speaker != Speaker.User).Select(e => e.Text),
            Has.All.Contain("è disattivato"));
        Assert.That(conversation.Mode, Is.EqualTo(ChatMode.Local), "nothing went to Claude");
        Assert.That(conversation.Commands.All.Select(c => c.Name), Does.Not.Contain("/oggi"));
        Assert.That(conversation.Commands.Everything.Select(c => c.Name), Does.Contain("/oggi"));
        Assert.That(conversation.Commands.Matching("/la", ["lavora", "lancia"]).Select(c => c.Name), Is.EqualTo(new[] { "/lancia" }));
    }

    [Test]
    public async Task Riavvia_saves_and_restarts()
    {
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = MissingVault });
        var restarted = 0;
        conversation.RestartRequested = () => restarted++;

        await conversation.SendAsync("/riavvia");

        Assert.That(restarted, Is.EqualTo(1));
        Assert.That(conversation.Commands.All.Select(c => c.Name), Does.Contain("/riavvia"));
    }

    [Test]
    public void No_heavy_command_means_none_and_not_the_defaults()
    {
        Assert.That(new ClaudeSettings().EffectiveHeavyCommands(), Does.Contain("/lavora"));
        Assert.That(new ClaudeSettings { HeavyCommands = [ClaudeSettings.NoHeavyCommand] }.EffectiveHeavyCommands(), Does.Not.Contain("/lavora"));
    }
}
