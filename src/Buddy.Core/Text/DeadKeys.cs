namespace Buddy.Core.Text;

/// <summary>
/// Dead-key composition of the US International layout: ' + e = é, ` + a = à, " + u = ü, ^ + o = ô, ~ + n = ñ,
/// ' + c = ç. The accent followed by a space gives the accent itself (' + space = '); followed by anything else it
/// gives both characters, as the layout does.
/// </summary>
public static class DeadKeys
{
    private static readonly Dictionary<char, string> Bases = new()
    {
        ['\''] = "aeiouycAEIOUYC",
        ['`'] = "aeiouAEIOU",
        ['"'] = "aeiouyAEIOU",
        ['^'] = "aeiouAEIOU",
        ['~'] = "aonAON",
    };

    private static readonly Dictionary<char, string> Composed = new()
    {
        ['\''] = "áéíóúýçÁÉÍÓÚÝÇ",
        ['`'] = "àèìòùÀÈÌÒÙ",
        ['"'] = "äëïöüÿÄËÏÖÜ",
        ['^'] = "âêîôûÂÊÎÔÛ",
        ['~'] = "ãõñÃÕÑ",
    };

    public static bool IsDeadKey(char accent) => Bases.ContainsKey(accent);

    /// <summary>The text typed by <paramref name="accent"/> followed by <paramref name="next"/>.</summary>
    public static string Compose(char accent, string next)
    {
        if (!Bases.TryGetValue(accent, out var bases))
        {
            return next;
        }

        if (next == " ")
        {
            return accent.ToString();
        }

        var index = next.Length == 1 ? bases.IndexOf(next[0]) : -1;
        return index >= 0 ? Composed[accent][index].ToString() : accent + next;
    }
}
