using System.Text.RegularExpressions;
using Buddy.Core.Tracker;
using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class IssueFromCardTests
{
    private static BoardItem Item(string text) => ProjectBoard.ParseItems(text, new Regex(@"^(?:T-\d+|[A-Z]{1,10}-\d+)$")).Single();

    [Test]
    public void A_card_becomes_an_issue_with_its_details_and_a_label_from_its_tags()
    {
        var item = Item("""
            - [ ] T-014 · 2026-11-06 · Export CSV vuoto [priorita:: P1] [stima:: 1h] [tag:: bug, ui]
              - Contesto: il file scaricato non ha righe
              - Fatto quando: il CSV contiene tutti i voti
            """);

        var draft = IssueFromCard.Draft(item);

        Assert.That(draft.Title, Is.EqualTo("Export CSV vuoto"));
        Assert.That(draft.Body, Does.Contain("- **Contesto**: il file scaricato non ha righe"));
        Assert.That(draft.Body, Does.Contain("- **Fatto quando**: il CSV contiene tutti i voti"));
        Assert.That(draft.Body, Does.EndWith("_Creata da Buddy dalla voce T-014._"));
        Assert.That(draft.Labels, Is.EqualTo(new[] { "bug" }));
        Assert.That(draft.AssignToMe, Is.True);
        Assert.That(IssueFromCard.Draft(Item("- [ ] T-015 · Tema scuro [tag:: feature]")).Labels, Is.EqualTo(new[] { "enhancement" }));
    }

    [Test]
    public void The_link_comes_from_the_issue_field_or_from_an_imported_card()
    {
        Assert.That(IssueFromCard.LinkOf(Item("- [ ] T-014 · Export [issue:: https://github.com/o/r/issues/7]")), Is.EqualTo("https://github.com/o/r/issues/7"));
        Assert.That(IssueFromCard.LinkOf(Item("- [ ] GH-12 · Crash\n  - Contesto: [GH-12](https://github.com/o/r/issues/12) — si chiude")),
            Is.EqualTo("https://github.com/o/r/issues/12"));
        Assert.That(IssueFromCard.LinkOf(Item("- [ ] T-016 · Senza issue")), Is.Null);
        Assert.That(IssueFromCard.Number("https://github.com/o/r/issues/7"), Is.EqualTo("#7"));
    }

    [Test]
    public void The_link_is_read_from_what_gh_prints()
    {
        Assert.That(IssueFromCard.UrlOf("Creating issue in o/r\n\nhttps://github.com/o/r/issues/31\n"), Is.EqualTo("https://github.com/o/r/issues/31"));
        Assert.That(IssueFromCard.UrlOf("nothing"), Is.Null);
    }

    [Test]
    public void An_issue_created_from_a_card_is_not_imported_back()
    {
        var item = Item("- [ ] T-014 · Export [issue:: https://github.com/o/r/issues/7]");
        var board = new ProjectBoard([new BoardColumn("todo", "Da fare", "", "To-Do.md", [item])], [], []);
        TrackerIssue Issue(int n) => new($"GH-{n}", "x", "", null, null, "open", null, null, $"https://github.com/o/r/issues/{n}");

        Assert.That(TrackerImport.New([Issue(7), Issue(8)], board).Select(i => i.Key), Is.EqualTo(new[] { "GH-8" }));
    }
}
