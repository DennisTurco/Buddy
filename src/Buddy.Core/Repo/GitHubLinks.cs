using System.Text.RegularExpressions;

namespace Buddy.Core.Repo;

/// <summary>A repository on GitHub, e.g. DennisTurco/Buddy.</summary>
public sealed record GitHubRepo(string Owner, string Name)
{
    public string Url => $"https://github.com/{Owner}/{Name}";

    public override string ToString() => $"{Owner}/{Name}";
}

public enum IssueKind
{
    Bug,
    Feature,
}

/// <summary>
/// Links for feedback: a new GitHub issue (bug or feature) already filled in with a template, on Buddy's own
/// repository or on the project's, and the donation page. Everything opens in the browser: no token, the user
/// reviews the text and submits it with their own GitHub account.
/// </summary>
public static partial class GitHubLinks
{
    /// <summary>Buddy's own repository: issues about the app.</summary>
    public static readonly GitHubRepo Buddy = new("DennisTurco", "Buddy");

    /// <summary>The PayPal donation page for Buddy.</summary>
    public const string DonationUrl = "https://www.paypal.com/donate/?hosted_button_id=M7CJXS929334U";

    /// <summary>The GitHub repository of a git remote (https or ssh form); null when it is not on GitHub.</summary>
    public static GitHubRepo? FromRemote(string? remote)
    {
        var match = RemoteRegex().Match(remote?.Trim() ?? "");
        return match.Success ? new GitHubRepo(match.Groups["owner"].Value, match.Groups["name"].Value) : null;
    }

    /// <summary>
    /// The "new issue" page with title and body filled in. For Buddy's own repository the body also carries the app
    /// version and the operating system, which help to reproduce a bug.
    /// </summary>
    public static string NewIssueUrl(GitHubRepo repo, IssueKind kind, string? appVersion = null, string? os = null)
    {
        var body = kind == IssueKind.Bug
            ? "## Cosa succede\n\n\n## Come riprodurlo\n1. \n2. \n3. \n\n## Cosa ti aspettavi\n\n"
            : "## Cosa vorresti\n\n\n## A cosa ti serve\n\n\n## Come te lo immagini\n\n";
        if (appVersion is not null || os is not null)
        {
            body += "\n## Ambiente\n" + (appVersion is null ? "" : $"- Buddy {appVersion}\n") + (os is null ? "" : $"- {os}\n");
        }

        var title = kind == IssueKind.Bug ? "[Bug] " : "[Funzionalità] ";
        var label = kind == IssueKind.Bug ? "bug" : "enhancement";
        return $"{repo.Url}/issues/new?labels={label}&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

    [GeneratedRegex(@"github\.com[:/](?<owner>[A-Za-z0-9_.-]+)/(?<name>[A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteRegex();
}
