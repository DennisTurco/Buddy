using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class ProjectChecksTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    private const string Log = """
        # Controlli

        ## Registro

        - 2026-09-01 · /security-audit · 3 problemi, 2 corretti
        - 2026-10-20 · /security-audit · nessun problema
        - 2026-08-01 · /security-audit · più vecchio, scritto dopo
        - `2026-10-02 · /manutenzione` · To-Do Fatti archiviato
        - una riga qualsiasi · /lavora
        """;

    [Test]
    public void Last_run_is_the_most_recent_date_with_its_outcome()
    {
        var runs = ProjectChecks.LastRuns(Log);

        Assert.That(runs["security-audit"], Is.EqualTo(new CheckRun(new DateOnly(2026, 10, 20), "nessun problema")));
        Assert.That(runs["manutenzione"], Is.EqualTo(new CheckRun(new DateOnly(2026, 10, 2), "To-Do Fatti archiviato")));
        Assert.That(runs, Has.Count.EqualTo(2));
    }

    [Test]
    public void A_check_is_due_when_never_run_or_older_than_its_period()
    {
        var installed = new HashSet<string> { "security-audit", "manutenzione" };

        var recent = ProjectChecks.Status(Log, installed, Today);
        Assert.That(recent.Single(s => s.Check.Skill == "security-audit").IsDue, Is.False, "28 giorni fa: non ancora");
        Assert.That(recent.Single(s => s.Check.Skill == "manutenzione").IsDue, Is.True, "46 giorni fa");

        var never = ProjectChecks.Status(null, installed, Today);
        Assert.That(never.All(s => s.IsDue && s.Last is null), Is.True);
    }

    [Test]
    public void A_check_without_its_skill_is_never_due()
    {
        var status = ProjectChecks.Status(null, new HashSet<string>(), Today);

        Assert.That(status.Any(s => s.IsDue || s.Installed), Is.False);
    }

    [Test]
    public void Request_starts_with_the_command_and_asks_for_the_log_line()
    {
        var check = ProjectChecks.All.Single(c => c.Skill == "manutenzione");

        var request = ProjectChecks.Request(check, Today);

        Assert.That(request, Does.StartWith("/manutenzione\n"));
        Assert.That(request, Does.Contain("«Controlli»").And.Contain("`- 2026-11-17 · /manutenzione · <esito>`"));
        Assert.That(ProjectChecks.LastRuns("- 2026-11-17 · /manutenzione · fatto")["manutenzione"].Date, Is.EqualTo(Today),
            "the line it asks for is read back");
    }
}
