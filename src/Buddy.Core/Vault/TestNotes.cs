using System.Globalization;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// The tests kept in the vault by the flow's skills:
/// - the "Test web" note lists the saved browser scripts, its "Test web" folder holds the screenshots of each run
///   ("T-002-passo1-esercizi.jpg");
/// - the "Test manuali" folder holds one checklist per task ("Test manuali T-003.md"), ticked by the user.
/// </summary>
public static partial class TestNotes
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>The scripts in the "## Script" section of the "Test web" note.</summary>
    public static IReadOnlyList<WebTestScript> Scripts(VaultIndex vault)
    {
        var note = vault.Notes.Where(n => string.Equals(n.Title, "Test web", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.RelativePath.Length).FirstOrDefault();
        if (note is null)
        {
            return [];
        }

        var section = Section(ProjectBoard.Body(note.Content), "Script");
        return MarkdownBlocks.Split(section)
            .Select(b => (Line: b.FirstLine, Match: ScriptLine().Match(b.FirstLine)))
            .Where(x => x.Match.Success)
            .Select(x => ParseScript(x.Match.Groups["name"].Value, x.Match.Groups["rest"].Value) with { Raw = x.Line, NotePath = note.RelativePath })
            .ToList();
    }

    /// <summary>Images in any "Test web" folder of the vault, newest first.</summary>
    public static IReadOnlyList<WebTestScreenshot> Screenshots(string projectPath)
    {
        if (!Directory.Exists(projectPath))
        {
            return [];
        }

        return Directory.EnumerateDirectories(projectPath, "Test web", SearchOption.AllDirectories)
            .Where(d => !Path.GetFileName(Path.GetDirectoryName(d) ?? "").StartsWith('.'))
            .SelectMany(d => Directory.EnumerateFiles(d))
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var match = ScreenshotName().Match(name);
                return match.Success
                    ? new WebTestScreenshot(f, match.Groups["id"].Value.ToUpperInvariant(),
                        int.Parse(match.Groups["step"].Value, CultureInfo.InvariantCulture),
                        match.Groups["caption"].Value.Replace('-', ' ').Trim(), File.GetLastWriteTime(f))
                    : new WebTestScreenshot(f, null, null, name.Replace('-', ' '), File.GetLastWriteTime(f));
            })
            .OrderByDescending(s => s.Taken)
            .ToList();
    }

    /// <summary>The checklists in the "Test manuali" folder(s), most recently updated first.</summary>
    public static IReadOnlyList<ManualTestDoc> ManualTests(VaultIndex vault, string taskIdPattern)
    {
        var id = new Regex(taskIdPattern, RegexOptions.IgnoreCase);
        return vault.Notes
            .Where(n => n.RelativePath.Replace('\\', '/').Split('/').SkipLast(1).Any(f => f.Equals("Test manuali", StringComparison.OrdinalIgnoreCase)))
            .Select(n => ParseManualTest(n, id))
            .OrderByDescending(d => d.Updated ?? DateOnly.MinValue)
            .ThenBy(d => d.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static ManualTestDoc ParseManualTest(VaultNote note, Regex taskId)
    {
        var lines = note.Content.Replace("\r\n", "\n").Split('\n');
        var (frontMatter, start) = FrontMatter(lines);
        var sections = new List<ManualTestSection>();
        var intro = new List<string>();
        ManualTestSection? current = null;
        var inComment = false;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("<!--", StringComparison.Ordinal))
            {
                inComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (inComment)
            {
                inComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = new ManualTestSection(line[3..].Trim(), []);
                sections.Add(current);
                continue;
            }

            var check = CheckLine().Match(line);
            if (current is null)
            {
                if (!check.Success)
                {
                    intro.Add(line);
                    continue;
                }

                current = new ManualTestSection("", []);
                sections.Add(current);
            }

            if (check.Success)
            {
                var text = check.Groups["text"].Value.Trim();
                current.Lines.Add(new ManualTestLine(i, line, text, IsCheck: true,
                    Checked: check.Groups["mark"].Value is "x" or "X",
                    IsRedo: text.Contains("Da rifare", StringComparison.OrdinalIgnoreCase),
                    IsStruck: text.StartsWith("~~", StringComparison.Ordinal),
                    Indented: check.Groups["indent"].Value.Length > 0));
            }
            else
            {
                current.Lines.Add(new ManualTestLine(i, line, line, false, false, false, false, false));
            }
        }

        var task = frontMatter.GetValueOrDefault("task") is { Length: > 0 } t ? t.Trim().ToUpperInvariant()
            : taskId.Match(note.Title) is { Success: true } m ? m.Value.ToUpperInvariant()
            : null;
        DateOnly? updated = DateOnly.TryParseExact(frontMatter.GetValueOrDefault("updated")?.Trim(), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        return new ManualTestDoc(note.RelativePath, note.Title, task, updated, string.Join('\n', intro).Trim(), sections);
    }

    private static WebTestScript ParseScript(string name, string rest)
    {
        var codes = InlineCode().Matches(rest).Select(m => m.Groups[1].Value).ToList();
        var covers = Covers().Match(rest) is { Success: true } c ? c.Groups["text"].Value.Trim().TrimEnd('·').Trim() : null;
        var run = LastRun().Match(rest);
        DateOnly? lastRun = run.Success && DateOnly.TryParseExact(run.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
        bool? passed = run.Success ? run.Groups["mark"].Value switch { "✅" => true, "❌" => false, _ => null } : null;
        var outcome = run.Success ? run.Groups["after"].Value.Trim() : null;
        return new WebTestScript(name.Trim(), codes.ElementAtOrDefault(0), codes.ElementAtOrDefault(1), covers, lastRun, passed,
            string.IsNullOrWhiteSpace(outcome) ? null : outcome);
    }

    /// <summary>The lines under "## <paramref name="heading"/>" up to the next heading of the same level.</summary>
    private static string Section(string body, string heading)
    {
        var lines = body.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal) && l[3..].Trim().Equals(heading, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return body;
        }

        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join('\n', lines[(start + 1)..(end < 0 ? lines.Length : end)]);
    }

    private static (Dictionary<string, string> Values, int BodyStart) FrontMatter(string[] lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (values, 0);
        }

        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
        {
            return (values, 0);
        }

        foreach (var line in lines[1..end])
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && !char.IsWhiteSpace(line[0]))
            {
                values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return (values, end + 1);
    }

    [GeneratedRegex(@"^[-*] \*\*(?<name>[^*]+)\*\*(?<rest>.*)$")]
    private static partial Regex ScriptLine();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"Cosa copre:\s*(?<text>.+?)(?=\s·\s*Ultima esecuzione|$)")]
    private static partial Regex Covers();

    [GeneratedRegex(@"Ultima esecuzione:\s*(?<date>\d{4}-\d{2}-\d{2})\s*(?<mark>✅|❌)?(?<after>.*)$")]
    private static partial Regex LastRun();

    [GeneratedRegex(@"^(?<id>[A-Za-z]{1,10}-\d+)-passo(?<step>\d+)(?:-(?<caption>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotName();

    [GeneratedRegex(@"^(?<indent>\s*)[-*] \[(?<mark>[ xX])\] (?<text>.*)$")]
    private static partial Regex CheckLine();
}

/// <param name="Path">The script file, relative to the scripts folder.</param>
/// <param name="Command">How to run it (e.g. "npm run test:t002"), when written.</param>
/// <param name="Passed">Outcome of the last run: true ✅, false ❌, null unknown.</param>
/// <param name="Raw">The script's line in the note, to record a run in place.</param>
public sealed record WebTestScript(string Name, string? Path, string? Command, string? Covers, DateOnly? LastRun, bool? Passed, string? Outcome)
{
    public string Raw { get; init; } = "";

    /// <summary>The "Test web" note the script is listed in, relative to the vault.</summary>
    public string NotePath { get; init; } = "";
}

/// <param name="TaskId">From the file name "T-002-passo1-…"; null for other images.</param>
public sealed record WebTestScreenshot(string FullPath, string? TaskId, int? Step, string Caption, DateTime Taken);

public sealed record ManualTestDoc(string RelativePath, string Title, string? TaskId, DateOnly? Updated, string Intro, IReadOnlyList<ManualTestSection> Sections)
{
    private IEnumerable<ManualTestLine> Cases => Sections.SelectMany(s => s.Lines).Where(l => l.IsCheck && !l.IsStruck);

    public int Total => Cases.Count();

    public int Done => Cases.Count(l => l.Checked);

    /// <summary>"Da rifare dopo il …" lines still to do.</summary>
    public int Redo => Cases.Count(l => l.IsRedo && !l.Checked);
}

public sealed record ManualTestSection(string Title, List<ManualTestLine> Lines);

/// <param name="Index">Line number in the note (0-based), to tick it in place.</param>
/// <param name="Raw">The line as written, to check nobody changed it before ticking.</param>
public sealed record ManualTestLine(int Index, string Raw, string Text, bool IsCheck, bool Checked, bool IsRedo, bool IsStruck, bool Indented);
