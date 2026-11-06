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

    /// <summary>The issue the card is already tied to: its [issue:: …] field, or a GitHub issue link anywhere in it (an imported card).</summary>
    public static string? LinkOf(BoardItem item) =>
        item.Field(Field) is { } field ? field : IssueUrlRegex().Match(item.Raw) is { Success: true } m ? m.Value : null;

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
}
