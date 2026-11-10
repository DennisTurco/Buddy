using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// Moving a card between the flow's columns by hand (drag and drop in the board). Only between the columns whose items
/// share the "- [ ] ID · titolo" format: Da valutare, Da fare, In attesa (Da fare with "⏸"), In corso (Da fare with "▶")
/// and Da controllare. Appunti get their id and shape from /smista; closing and refusing are not written here but go
/// through /chiudi and /rifiuta (see <see cref="ClosingSkill"/>), which also record the PR, the cost or the reason.
/// The item is written in the target note first, then removed from the source one, so a failure never loses it.
/// </summary>
public static partial class BoardMoves
{
    public static readonly IReadOnlySet<string> Movable = new HashSet<string> { "valutare", "todo", "attesa", "corso", "controllare" };

    /// <summary>
    /// The skill that takes an item dropped on Fatti or Rifiutati there (/chiudi or /rifiuta), or null when that is not
    /// how it can get there: only tasks are closed (not ideas still to evaluate), anything in the flow can be refused.
    /// </summary>
    public static string? ClosingSkill(string from, string to) => (from, to) switch
    {
        ("todo" or "attesa" or "corso" or "controllare", "fatti") => "chiudi",
        (_, "rifiutati") when Movable.Contains(from) => "rifiuta",
        _ => null,
    };

    /// <summary>Why the move is not done by hand (null when it can be).</summary>
    public static string? Refusal(string from, string to) => (from, to) switch
    {
        _ when from == to => "",
        ("appunti", _) => "Gli appunti diventano task con /smista (che dà l'ID e sceglie dove metterli).",
        (_, "fatti") => "Per chiudere un task usa /chiudi ID (scrive la PR e il costo).",
        (_, "rifiutati") => "Per rifiutare usa /rifiuta ID motivo.",
        (_, "appunti") => "Un task non torna tra gli appunti: spostalo in «Da valutare».",
        ("fatti" or "rifiutati", _) => "I task chiusi o rifiutati restano nell'archivio.",
        _ when !Movable.Contains(from) || !Movable.Contains(to) => "Spostamento non previsto.",
        _ => null,
    };

    /// <summary>
    /// <see cref="Move"/>, keeping what the two notes looked like before: the returned <see cref="MoveUndo"/> puts
    /// them back, as long as nothing else changed them in between.
    /// </summary>
    public static MoveUndo MoveWithUndo(string projectPath, BoardColumn from, BoardItem item, BoardColumn to)
    {
        var paths = new[] { from.NotePath, to.NotePath }
            .Select(p => Path.Combine(projectPath, p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var before = paths.ToDictionary(p => p, Read);
        Move(projectPath, from, item, to);
        return new MoveUndo(item.Id ?? item.Title, to.Title, paths.Select(p => (p, before[p], Read(p))).ToList());
    }

    private static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>Moves <paramref name="item"/> from <paramref name="from"/> to <paramref name="to"/>, in the vault.</summary>
    public static void Move(string projectPath, BoardColumn from, BoardItem item, BoardColumn to)
    {
        if (Refusal(from.Key, to.Key) is { } refusal)
        {
            throw new InvalidOperationException(refusal.Length == 0 ? "La voce è già lì." : refusal);
        }

        var moved = SetState(item.Raw, to.Key switch { "attesa" => Waiting, "corso" => Working, _ => null }, item.Id);
        var source = Path.Combine(projectPath, from.NotePath);
        var target = Path.Combine(projectPath, to.NotePath);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            VaultWriter.ReplaceBlock(source, item.Raw, moved); // Da fare ↔ In attesa ↔ In corso: only the ⏸/▶ changes
            return;
        }

        VaultWriter.AppendItem(target, FirstSection(target) ?? "", moved);
        VaultWriter.ReplaceBlock(source, item.Raw, "");
    }

    public const string Waiting = "⏸";

    public const string Working = "▶";

    /// <summary>Adds or removes the "⏸" that marks an item waiting, right after the id (or the checkbox).</summary>
    internal static string SetWaiting(string raw, bool waiting, string? id) => SetState(raw, waiting ? Waiting : null, id);

    /// <summary>
    /// Puts <paramref name="marker"/> ("⏸" waiting, "▶" in progress) right after the id (or the checkbox), removing
    /// the other one; null removes both (a plain Da fare item).
    /// </summary>
    public static string SetState(string raw, string? marker, string? id)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var first = Markers().Replace(lines[0], "");
        if (marker is not null)
        {
            var at = id is not null && first.IndexOf(id + " · ", StringComparison.OrdinalIgnoreCase) is var i and >= 0
                ? i + id.Length + 3
                : Box().Match(first) is { Success: true } box ? box.Index + box.Length : 0;
            first = first.Insert(at, marker + " ");
        }

        lines[0] = first;
        return string.Join('\n', lines);
    }

    /// <summary>The first "## " heading of a note (Task, Voci…), where the items live.</summary>
    private static string? FirstSection(string path)
    {
        var inFence = false;
        foreach (var line in File.ReadLines(path))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                return line[3..].Trim();
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?:⏸|▶\uFE0F?)\s*")]
    private static partial Regex Markers();

    [GeneratedRegex(@"^\s*- (?:\[[ xX]\] )?")]
    private static partial Regex Box();
}

/// <summary>
/// Undoes a card moved by hand: the notes it touched get back their exact previous content. Refused (nothing written)
/// when one of them changed after the move — by Obsidian, Claude or another edit — so nothing is ever overwritten.
/// </summary>
public sealed class MoveUndo(string item, string column, IReadOnlyList<(string Path, byte[]? Before, byte[]? After)> notes)
{
    /// <summary>"T-026 → Da fare", for the message that offers the undo.</summary>
    public string Description => $"{item} → {column}";

    /// <summary>Puts the notes back; returns null when done, or why it was not.</summary>
    public string? Undo()
    {
        foreach (var (path, _, after) in notes)
        {
            var now = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (!(now is null ? after is null : after is not null && now.AsSpan().SequenceEqual(after)))
            {
                return $"«{Path.GetFileNameWithoutExtension(path)}» è cambiata dopo lo spostamento: non la tocco, sistemala a mano.";
            }
        }

        foreach (var (path, before, _) in notes)
        {
            if (before is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, before);
            }
        }

        return null;
    }
}
