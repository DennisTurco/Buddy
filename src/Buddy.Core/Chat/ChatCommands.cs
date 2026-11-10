namespace Buddy.Core.Chat;

/// <summary>Who answers a "/" command, which decides where it works and how the user's line is shown.</summary>
public enum CommandGroup
{
    /// <summary>The conversation's own: works in the work chat; the command writes the user's line itself.</summary>
    Chat,

    /// <summary>
    /// The mentor's: works in the side chat ☕ too. The conversation adds the user's line before the answer, in the
    /// chat the answer went to.
    /// </summary>
    Mentor,
}

/// <summary>One "/" command of the buddy, as the suggestions, Ctrl+K, /aiuto and /mentore show it.</summary>
/// <param name="Arguments">What may follow the name (e.g. "[ID]"), for the help; null when nothing does.</param>
/// <param name="Hidden">Runs, but is not suggested nor listed (e.g. /riapri, used by buttons).</param>
public sealed record ChatCommand(string Name, string Description, string? Arguments = null,
    CommandGroup Group = CommandGroup.Chat, bool Hidden = false)
{
    /// <summary>The name with its arguments, e.g. "/stato [ID]".</summary>
    public string Usage => Arguments is null ? Name : $"{Name} {Arguments}";
}

/// <summary>A command as typed: "/cerca login" → Command "/cerca", Rest "login".</summary>
public sealed record CommandCall(string Command, string Rest, string Input, Attachment? Attachment);

/// <summary>A command and what runs it; <see cref="Run"/> returns false when the message should go on to the models.</summary>
public sealed record RegisteredCommand(ChatCommand Command, Func<CommandCall, Task<bool>> Run);

/// <summary>
/// The buddy's "/" commands, each registered once with its name, description and handler by whoever answers it
/// (the conversation, the mentor). Suggestions, Ctrl+K, /aiuto, /mentore and the typo check all read from here.
/// </summary>
public sealed class CommandRegistry
{
    private readonly List<RegisteredCommand> _commands = [];

    /// <summary>The commands to show, in the order they were registered.</summary>
    public IReadOnlyList<ChatCommand> All => _commands.Where(c => !c.Command.Hidden).Select(c => c.Command).ToList();

    /// <summary>Registers a command that is always handled once run.</summary>
    public void Add(ChatCommand command, Func<CommandCall, Task> run) =>
        AddConditional(command, async call =>
        {
            await run(call);
            return true;
        });

    /// <summary>Registers a command whose handler may let the message go on (false), e.g. to Claude.</summary>
    public void AddConditional(ChatCommand command, Func<CommandCall, Task<bool>> run)
    {
        if (!command.Name.StartsWith('/') || command.Name.Contains(' '))
        {
            throw new ArgumentException($"Nome di comando non valido: «{command.Name}».", nameof(command));
        }

        if (Find(command.Name) is not null)
        {
            throw new InvalidOperationException($"Il comando {command.Name} è già registrato.");
        }

        _commands.Add(new RegisteredCommand(command, run));
    }

    public RegisteredCommand? Find(string name) =>
        _commands.FirstOrDefault(c => c.Command.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line per command of <paramref name="group"/>: "/stato [ID] — scheda del task…".</summary>
    public string Help(CommandGroup group) =>
        string.Join('\n', All.Where(c => c.Group == group).Select(c => $"{c.Usage} — {c.Description}"));

    /// <summary>
    /// For a "/" command that is neither registered nor an installed skill: the known one it is probably a typo of
    /// (e.g. "/chiacchera" → "/chiacchiera"), or null when nothing is close enough.
    /// </summary>
    public string? Closest(string command, IEnumerable<string> skills)
    {
        var known = _commands.Select(c => c.Command.Name).Concat(skills.Select(s => "/" + s)).ToList();
        if (known.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var best = known.Select(k => (Name: k, Distance: Distance(command.ToLowerInvariant(), k.ToLowerInvariant())))
            .OrderBy(k => k.Distance)
            .FirstOrDefault();
        var allowed = command.Length <= 5 ? 1 : 2;
        return best.Name is not null && best.Distance <= allowed ? best.Name : null;
    }

    /// <summary>Commands starting with <paramref name="prefix"/> (e.g. "/pi"): the buddy's first, then skills and templates.</summary>
    public IReadOnlyList<ChatCommand> Matching(string prefix, IEnumerable<string> skills, int max = 6,
        IEnumerable<(string Name, string Text)>? templates = null)
    {
        if (!prefix.StartsWith('/') || prefix.Contains(' '))
        {
            return [];
        }

        var own = All;
        var all = own.Concat(skills
                .Where(s => own.All(b => !b.Name.Equals("/" + s, StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(s => new ChatCommand("/" + s, "skill di Claude")))
            .Concat((templates ?? []).Select(t => new ChatCommand("/" + t.Name, "tuo modello: " + (t.Text.Length > 50 ? t.Text[..50] + "…" : t.Text))));
        return all.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                              && !c.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .ToList();
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }
}
