using System.Text;
using System.Text.RegularExpressions;
using Buddy.Core.Vault;

namespace Buddy.Core.Tracker;

/// <summary>What a new GitHub issue will say: proposed from a board card, editable before it is created.</summary>
public sealed record IssueDraft(string Title, string Body, IReadOnlyList<string> Labels, bool AssignToMe = true);

/// <summary>
/// The opposite of the import: a board card becomes a GitHub issue of the project's repository (created with
/// `gh issue create`), and the card keeps its link in [issue:: url], so it is not exported twice nor imported back.
/// </summary>
public static partial class IssueFromCard
{
    public const string Field = "issue";

    /// <summary>
    /// The tracker card the board card is tied to: its [issue:: …] field, or the link of the card it was imported
    /// from, anywhere in it (a GitHub issue, a Jira card, an Azure Boards work item).
    /// </summary>
    public static string? LinkOf(BoardItem item) =>
        item.Field(Field) is { } field ? field : TrackerUrlRegex().Match(item.Raw) is { Success: true } m ? m.Value : null;

    /// <summary>"Issue #12", "Jira ABC-12", "Azure #345": what the link points to, short enough for a button.</summary>
    public static string Label(string url)
    {
        if (Regex.Match(url, @"/browse/([A-Za-z][A-Za-z0-9_]*-\d+)") is { Success: true } jira)
        {
            return "Jira " + jira.Groups[1].Value;
        }

        if (Regex.Match(url, @"/_workitems/edit/(\d+)") is { Success: true } azure)
        {
            return "Azure #" + azure.Groups[1].Value;
        }

        return url.Contains("/issues/", StringComparison.Ordinal) ? "Issue " + Number(url) : "Link";
    }

    /// <summary>The tracker's name for a tile ("GitHub", "Jira", "Azure"), or "Link".</summary>
    public static string Source(string url) =>
        url.Contains("/browse/", StringComparison.Ordinal) ? "Jira"
        : url.Contains("/_workitems/", StringComparison.Ordinal) ? "Azure"
        : url.Contains("github.com/", StringComparison.OrdinalIgnoreCase) ? "GitHub"
        : "Link";

    /// <summary>The card's title; its Contesto, Dove, Fatto quando… and notes as the body; bug / enhancement from its tags.</summary>
    public static IssueDraft Draft(BoardItem item)
    {
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            body.AppendLine(item.Note.Trim()).AppendLine();
        }

        foreach (var detail in item.Details.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            var colon = detail.IndexOf(':');
            // "Fatto quando: …" → "- **Fatto quando**: …"; a line that only contains a colon (a link…) stays as it is.
            var labelled = colon is > 0 and <= 25 && detail[..colon].All(c => char.IsLetter(c) || c == ' ');
            body.AppendLine(labelled ? $"- **{detail[..colon].Trim()}**:{detail[(colon + 1)..]}" : $"- {detail}");
        }

        if (body.Length > 0)
        {
            body.AppendLine();
        }

        body.Append(item.Id is { } id ? $"_Creata da Buddy dalla voce {id}._" : "_Creata da Buddy._");

        var tags = item.TagList;
        var labels = new List<string>();
        if (tags.Any(t => t.Contains("bug", StringComparison.OrdinalIgnoreCase) || t.Equals("errore", StringComparison.OrdinalIgnoreCase)))
        {
            labels.Add("bug");
        }
        else if (tags.Any(t => t is "feature" or "idea" or "miglioria" or "enhancement"))
        {
            labels.Add("enhancement");
        }

        return new IssueDraft(item.Title.Trim(), body.ToString().Trim(), labels);
    }

    /// <summary>
    /// Creates the issue in the repository at <paramref name="repoPath"/> and returns its link. If GitHub refuses the
    /// labels (not defined in that repository) or the assignee, it is created again without them.
    /// </summary>
    public static async Task<string> CreateAsync(string repoPath, IssueDraft draft, CancellationToken cancellationToken = default)
    {
        const string missing = "Serve la GitHub CLI (gh) installata e collegata con `gh auth login`.";
        List<string> Arguments(bool extras)
        {
            var arguments = new List<string> { "issue", "create", "--title", draft.Title, "--body", draft.Body };
            if (extras)
            {
                foreach (var label in draft.Labels)
                {
                    arguments.AddRange(["--label", label]);
                }

                if (draft.AssignToMe)
                {
                    arguments.AddRange(["--assignee", "@me"]);
                }
            }

            return arguments;
        }

        string output;
        try
        {
            output = await Cli.RunAsync("gh", repoPath, Arguments(true), cancellationToken, missing);
        }
        catch (TrackerException) when (draft.Labels.Count > 0 || draft.AssignToMe)
        {
            output = await Cli.RunAsync("gh", repoPath, Arguments(false), cancellationToken, missing);
        }

        return UrlOf(output) ?? throw new TrackerException($"gh non ha restituito il link dell'issue: {output.Trim()}");
    }

    /// <summary>The issue link gh prints at the end of `gh issue create`.</summary>
    public static string? UrlOf(string output) =>
        IssueUrlRegex().Matches(output).LastOrDefault()?.Value;

    /// <summary>"#12" from the issue link.</summary>
    public static string Number(string url) =>
        Regex.Match(url, @"/issues/(\d+)") is { Success: true } m ? "#" + m.Groups[1].Value : url;

    [GeneratedRegex(@"https://github\.com/[\w.-]+/[\w.-]+/issues/\d+")]
    private static partial Regex IssueUrlRegex();

    /// <summary>A GitHub issue, a Jira card (…/browse/KEY-12) or an Azure Boards work item (…/_workitems/edit/12).</summary>
    [GeneratedRegex(@"https://github\.com/[\w.-]+/[\w.-]+/issues/\d+|https?://[^\s()\[\]<>]+/browse/[A-Za-z][A-Za-z0-9_]*-\d+|https://[^\s()\[\]<>]+/_workitems/edit/\d+")]
    private static partial Regex TrackerUrlRegex();
}
