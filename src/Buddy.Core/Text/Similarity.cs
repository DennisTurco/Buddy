namespace Buddy.Core.Text;

/// <summary>How far two short strings are (typos in commands, near-identical note titles).</summary>
public static class Similarity
{
    /// <summary>Levenshtein distance, ignoring case.</summary>
    public static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
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

    /// <summary>
    /// The candidate most like <paramref name="text"/>: close in spelling (a quarter of the length at most) or one
    /// containing the other ("Glossario" for "Glossario del progetto"); null when nothing is close.
    /// </summary>
    public static string? Closest(string text, IEnumerable<string> candidates)
    {
        var best = candidates
            .Where(c => !c.Equals(text, StringComparison.OrdinalIgnoreCase))
            .Select(c => (Name: c, Score: c.Contains(text, StringComparison.OrdinalIgnoreCase) || text.Contains(c, StringComparison.OrdinalIgnoreCase)
                ? 0.5
                : Distance(text, c)))
            .OrderBy(c => c.Score)
            .FirstOrDefault();
        return best.Name is not null && best.Score <= Math.Max(2, text.Length / 4) ? best.Name : null;
    }
}
