using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Mentor;

/// <summary>One topic of the pills: how many were given and how the reviews went.</summary>
public sealed record SkillTopic(string Topic, int Pills, int Right, int Partial, int Wrong)
{
    public int Reviews => Right + Partial + Wrong;

    /// <summary>0–1: right answers count 1, partial ½; null until a review was done.</summary>
    public double? Score => Reviews == 0 ? null : (Right + Partial * 0.5) / Reviews;
}

/// <summary>
/// What you know well and what to go over again, from Pillole.md ("- 2026-11-05 · argomento · titolo — ripassi:
/// 2026-11-06 ✓, …"): one row per topic with the reviews' outcome.
/// </summary>
public static partial class SkillsMap
{
    public static IReadOnlyList<SkillTopic> Parse(string pills)
    {
        var topics = new Dictionary<string, (int Pills, int Right, int Partial, int Wrong)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match line in PillLine().Matches(pills))
        {
            var topic = line.Groups["topic"].Value.Trim();
            var reviews = line.Groups["reviews"].Value;
            var current = topics.GetValueOrDefault(topic);
            topics[topic] = (current.Pills + 1,
                current.Right + reviews.Count(c => c == '✓'),
                current.Partial + reviews.Count(c => c == '~'),
                current.Wrong + reviews.Count(c => c == '✗'));
        }

        return topics.Select(t => new SkillTopic(t.Key, t.Value.Pills, t.Value.Right, t.Value.Partial, t.Value.Wrong))
            .OrderByDescending(t => t.Score ?? -1)
            .ThenByDescending(t => t.Pills)
            .ToList();
    }

    /// <summary>A table with a bar per topic, strongest first, and the ones to go over again.</summary>
    public static string ToMarkdown(IReadOnlyList<SkillTopic> topics)
    {
        if (topics.Count == 0)
        {
            return "Ancora nessuna pillola in Pillole.md: la mappa si riempie con le pillole e i ripassi.";
        }

        var sb = new StringBuilder("| Argomento | Padronanza | Pillole | Ripassi |\n|---|---|---|---|\n");
        foreach (var topic in topics)
        {
            var bar = topic.Score is { } score ? Bar(score) : "· · · · · (da ripassare)";
            sb.AppendLine($"| {topic.Topic} | {bar} | {topic.Pills} | {(topic.Reviews == 0 ? "—" : $"{topic.Right} ✓ · {topic.Partial} ~ · {topic.Wrong} ✗")} |");
        }

        var weak = topics.Where(t => t.Score is < 0.6).Select(t => t.Topic).ToList();
        if (weak.Count > 0)
        {
            sb.AppendLine().AppendLine($"Da riprendere: **{string.Join(", ", weak)}** (`/ripasso` ne propone uno).");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Bar(double score)
    {
        var full = (int)Math.Round(score * 5);
        return new string('█', full) + new string('░', 5 - full) + $" {score * 100:0}%";
    }

    [GeneratedRegex(@"^-\s*\d{4}-\d{2}-\d{2}\s*·\s*(?<topic>[^·\n]+?)\s*·[^\n]*?(?:ripassi:(?<reviews>[^\n]*))?$", RegexOptions.Multiline)]
    private static partial Regex PillLine();
}
