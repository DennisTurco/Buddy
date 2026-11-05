using System.Text.RegularExpressions;
using Buddy.Core.Repo;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Context;

/// <summary>
/// Keyword search over the repo's convention docs (e.g. docs/guidelines, AGENTS.md), split into heading sections.
/// Deterministic and cheap: the best sections are added to the local model context.
/// </summary>
public sealed partial class KnowledgeIndex(IOptions<BuddySettings> options, GitRepo repo)
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "come", "dove", "cosa", "quale", "quali", "quando", "perché", "sono", "della", "delle", "dello", "degli",
        "nella", "nelle", "sulla", "sulle", "questo", "questa", "quello", "quella", "anche", "devo", "posso",
        "fare", "faccio", "regola", "regole", "the", "and", "with", "what", "where", "which", "should",
        "stai", "sto", "bene", "grazie", "ciao", "abbiamo", "tutto", "tutti", "manca", "mancano", "ancora", "fatto",
        "serve", "servono", "lato", "punto", "task", "card", "dimmi", "sapere", "fammi", "vorrei",
        "vanno", "metto", "mettere", "usare", "uso", "aggiungo", "aggiungere", "gestisco", "gestire", "scrivo",
        "scrivere", "funziona", "bisogna", "dovrei", "possiamo",
    };

    private readonly Lock _gate = new();
    private (DateTime Stamp, List<IndexedSection> Sections)? _cache;

    public IReadOnlyList<KnowledgeSection> Search(string question, int maxResults = 3)
    {
        var terms = Terms(question);
        if (terms.Count == 0)
        {
            return [];
        }

        var sections = Sections();
        if (sections.Count == 0)
        {
            return [];
        }

        // Rare words ("seed") say more than common ones ("script"): each term weighs its inverse document frequency.
        // A word the docs never contain ("stai", a task number) weighs the most: the question is about something
        // else, so it counts against every section.
        var weights = terms.ToDictionary(t => t, t =>
        {
            var documents = sections.Count(s => s.Has(t));
            return Math.Log(1.0 + (double)sections.Count / Math.Max(1, documents));
        });
        var total = weights.Values.Sum();

        // A section must cover at least half of the question's weight, with two words when there are two or more:
        // one generic word ("codice") never pulls in a section.
        var required = Math.Min(2, terms.Count);
        return sections
            .Select(s => (Section: s, Matched: terms.Count(s.Has), Coverage: weights.Where(w => s.Has(w.Key)).Sum(w => w.Value) / total, Score: Score(s, weights)))
            .Where(x => x.Matched >= required && x.Coverage >= MinCoverage)
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x => x.Section.Section)
            .ToList();
    }

    /// <summary>Share of the question's weight a section must contain to be relevant.</summary>
    private const double MinCoverage = 0.5;

    internal static HashSet<string> Terms(string text) =>
        Words(text)
            .Where(w => w.Length >= 4 && !StopWords.Contains(w) && !w.All(char.IsDigit))
            .ToHashSet();

    private static IEnumerable<string> Words(string text) =>
        WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value);

    /// <summary>Sum of the weights of the terms found; a match in the heading or file name counts double.</summary>
    private static double Score(IndexedSection section, Dictionary<string, double> weights) =>
        weights.Sum(w => (section.Has(w.Key) ? w.Value : 0) + (section.TitleHas(w.Key) ? w.Value : 0));

    /// <summary>A section with its words, matched whole (a short word never matches inside a longer one).</summary>
    private sealed class IndexedSection(KnowledgeSection section)
    {
        private readonly HashSet<string> _words = Words(section.Text).ToHashSet();
        private readonly HashSet<string> _titleWords = Words(section.Heading + " " + section.RelativePath).ToHashSet();

        public KnowledgeSection Section { get; } = section;

        public bool Has(string term) => Contains(_words, term) || Contains(_titleWords, term);

        public bool TitleHas(string term) => Contains(_titleWords, term);

        /// <summary>Whole word, or for long words the same stem ("template" / "templates", "migrazione" / "migrazioni").</summary>
        private static bool Contains(HashSet<string> words, string term) =>
            words.Contains(term) || (term.Length >= 6 && words.Any(w => w.Length >= term.Length - 1 && w.StartsWith(term[..^1], StringComparison.Ordinal)));
    }

    private List<IndexedSection> Sections()
    {
        var files = Files().ToList();
        var stamp = files.Count == 0 ? DateTime.MinValue : files.Max(File.GetLastWriteTimeUtc);
        lock (_gate)
        {
            if (_cache is { } cache && cache.Stamp == stamp)
            {
                return cache.Sections;
            }

            var sections = files.SelectMany(Split).Select(s => new IndexedSection(s)).ToList();
            _cache = (stamp, sections);
            return sections;
        }
    }

    private IEnumerable<string> Files()
    {
        if (!repo.IsAvailable)
        {
            yield break;
        }

        foreach (var relative in options.Value.Repo.KnowledgePaths)
        {
            var path = Path.Combine(repo.RootPath, relative);
            if (File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    private IEnumerable<KnowledgeSection> Split(string file)
    {
        var relative = Path.GetRelativePath(repo.RootPath, file);
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException)
        {
            yield break;
        }

        var heading = Path.GetFileNameWithoutExtension(file);
        var lines = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('#') && lines.Count > 0)
            {
                yield return new KnowledgeSection(relative, heading, string.Join('\n', lines).Trim(), file);
                lines.Clear();
            }

            if (line.StartsWith('#'))
            {
                heading = line.TrimStart('#', ' ');
            }

            lines.Add(line);
        }

        if (lines.Count > 0)
        {
            yield return new KnowledgeSection(relative, heading, string.Join('\n', lines).Trim(), file);
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}

public sealed record KnowledgeSection(string RelativePath, string Heading, string Text, string FullPath = "");
