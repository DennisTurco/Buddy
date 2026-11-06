using Buddy.Core.Repo;

namespace Buddy.Core.Tracker;

/// <summary>
/// What the current project is connected to: the trackers its cards can be imported from (Jira when configured,
/// GitHub issues when the repository is on GitHub, Azure Boards when a project and a token are known) and where its
/// pull requests and their CI are read from (GitHub with gh, Azure Repos with the token).
/// </summary>
public static class ProjectTrackers
{
    public static async Task<IReadOnlyList<ITracker>> AvailableAsync(BuddySettings settings, string secretsFolder, HttpClient http, GitRepo repo,
        CancellationToken cancellationToken = default)
    {
        var trackers = new List<ITracker>();
        if (settings.Jira.IsConfigured && JiraToken.Read(secretsFolder) is { } jiraToken)
        {
            trackers.Add(new JiraTracker(new JiraClient(http), settings.Jira, jiraToken, settings.JiraProject));
        }

        var remote = repo.IsAvailable ? await repo.GetRemoteUrlAsync(cancellationToken) : null;
        if (GitHubLinks.FromRemote(remote) is not null)
        {
            trackers.Add(new GitHubIssues(repo.RootPath));
        }

        if ((AzureProject.FromSettings(settings.AzureDevOps, settings.AzureProject) ?? AzureProject.FromRemote(remote)) is { } azure
            && AzureToken.Read(secretsFolder) is { } azureToken)
        {
            trackers.Add(new AzureBoardsTracker(new AzureDevOpsClient(http, azureToken), azure));
        }

        return trackers;
    }

    /// <summary>My open pull requests with their CI, from GitHub or Azure Repos depending on the remote; a problem to show otherwise.</summary>
    public static async Task<(IReadOnlyList<PullRequestInfo> List, string? Problem)> PullRequestsAsync(BuddySettings settings, string secretsFolder,
        HttpClient http, GitRepo repo, CancellationToken cancellationToken = default)
    {
        var remote = repo.IsAvailable ? await repo.GetRemoteUrlAsync(cancellationToken) : null;
        if (AzureProject.FromRemote(remote) is { } azure)
        {
            if (AzureToken.Read(secretsFolder) is not { } token)
            {
                return ([], "Il repository è su Azure Repos: aggiungi un token (PAT) in Impostazioni → Assistente → Azure DevOps per vedere le PR.");
            }

            try
            {
                return (await new AzureDevOpsClient(http, token).PullRequestsAsync(azure, settings.TaskIdPattern, cancellationToken), null);
            }
            catch (Exception ex) when (ex is TrackerException or HttpRequestException or TaskCanceledException)
            {
                return ([], ex.Message);
            }
        }

        return await PullRequests.ListAsync(repo.RootPath, settings.TaskIdPattern, cancellationToken);
    }
}
