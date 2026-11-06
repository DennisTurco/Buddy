using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// Moving a card between the flow's columns by hand (drag and drop in the board). Only between the columns whose items
/// share the "- [ ] ID · titolo" format: Da valutare, Da fare, In attesa (Da fare with "⏸") and Da controllare. Appunti
/// get their id and shape from /smista, closing and refusing go through /chiudi and /rifiuta, so those are refused here.
/// The item is written in the target note first, then removed from the source one, so a failure never loses it.
/// </summary>
public static partial class BoardMoves
{
    public static readonly IReadOnlySet<string> Movable = new HashSet<string> { "valutare", "todo", "attesa", "controllare" };

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

    /// <summary>Moves <paramref name="item"/> from <paramref name="from"/> to <paramref name="to"/>, in the vault.</summary>
    public static void Move(string projectPath, BoardColumn from, BoardItem item, BoardColumn to)
    {
        if (Refusal(from.Key, to.Key) is { } refusal)
        {
            throw new InvalidOperationException(refusal.Length == 0 ? "La voce è già lì." : refusal);
        }

        var moved = SetWaiting(item.Raw, to.Key == "attesa", item.Id);
        var source = Path.Combine(projectPath, from.NotePath);
        var target = Path.Combine(projectPath, to.NotePath);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            VaultWriter.ReplaceBlock(source, item.Raw, moved); // Da fare ↔ In attesa: only the ⏸ changes
            return;
        }

        VaultWriter.AppendItem(target, FirstSection(target) ?? "", moved);
        VaultWriter.ReplaceBlock(source, item.Raw, "");
    }

    /// <summary>Adds or removes the "⏸" that marks an item waiting, right after the id (or the checkbox).</summary>
    internal static string SetWaiting(string raw, bool waiting, string? id)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var first = Pause().Replace(lines[0], "");
        if (waiting)
        {
            var at = id is not null && first.IndexOf(id + " · ", StringComparison.OrdinalIgnoreCase) is var i and >= 0
                ? i + id.Length + 3
                : Box().Match(first) is { Success: true } box ? box.Index + box.Length : 0;
            first = first.Insert(at, "⏸ ");
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

    [GeneratedRegex(@"⏸\s*")]
    private static partial Regex Pause();

    [GeneratedRegex(@"^\s*- (?:\[[ xX]\] )?")]
    private static partial Regex Box();
}
