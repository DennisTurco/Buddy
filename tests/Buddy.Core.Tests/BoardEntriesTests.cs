using System.Text.RegularExpressions;
using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class BoardEntriesTests
{
    private static readonly DateOnly Day = new(2026, 11, 6);
    private static readonly Regex Ids = new(@"^(?:T-\d+)$", RegexOptions.IgnoreCase);

    private static BoardItem Parse(string text) => ProjectBoard.ParseItems(text, Ids).Single();

    [Test]
    public void An_appunto_has_title_tags_and_details_as_nested_lines()
    {
        var text = BoardEntries.Appunto(new NewEntry { Title = "Export CSV", Details = "per il PO\n\ncon le date", Tags = ["feature", "ui"] }, Day);
        Assert.That(text, Is.EqualTo("- 2026-11-06 · Export CSV [tag:: feature, ui]\n  - per il PO\n  - con le date"));

        var item = Parse(text);
        Assert.That(item.Title, Is.EqualTo("Export CSV"));
        Assert.That(item.TagList, Is.EqualTo(new[] { "feature", "ui" }));
        Assert.That(item.Details, Is.EqualTo(new[] { "per il PO", "con le date" }));
    }

    [Test]
    public void A_task_has_the_fields_of_the_template_and_skips_the_empty_ones()
    {
        var text = BoardEntries.Task(new NewEntry
        {
            Title = "Login con SSO", Priority = "P1", Estimate = "4h", Tags = ["feature"],
            DoneWhen = "si entra con l'account aziendale", Where = "`Auth/Login.cs`",
        }, "T-014", Day);
        Assert.That(text, Is.EqualTo(
            "- [ ] T-014 · 2026-11-06 · Login con SSO [priorita:: P1] [stima:: 4h] [tag:: feature]\n"
            + "  - Dove: `Auth/Login.cs`\n  - Fatto quando: si entra con l'account aziendale"));

        var item = Parse(text);
        Assert.That(item.Id, Is.EqualTo("T-014"));
        Assert.That(item.Field("priorita"), Is.EqualTo("P1"));
        Assert.That(item.Waiting, Is.False);
    }

    [Test]
    public void A_waiting_task_says_what_it_waits_for_or_until_when()
    {
        var waiting = BoardEntries.WaitingTask(new NewEntry { Title = "Report", WaitingFor = "rilascio 2.0", Priority = "P3", Until = Day }, "T-015");
        Assert.That(waiting, Is.EqualTo("- [ ] T-015 · ⏸ Report — In attesa di: rilascio 2.0 [priorita:: P3] [stima:: 1h]"));
        Assert.That(Parse(waiting).Waiting, Is.True);

        var until = BoardEntries.WaitingTask(new NewEntry { Title = "Report", Until = Day }, "T-016");
        Assert.That(Parse(until).Due, Is.EqualTo(Day));
    }

    [Test]
    public void An_item_to_review_has_what_changed_and_how_to_test()
    {
        var text = BoardEntries.ToReview(new NewEntry { Title = "Fix login", Changed = "riga\nseconda", HowToTest = "1. apri", Commit = "fix: login" }, "T-003");
        Assert.That(text, Is.EqualTo("- [ ] T-003 · Fix login\n  - Cambiato: riga seconda\n  - Come testare: 1. apri\n  - Commit: `fix: login`"));
    }

    [Test]
    public void The_next_id_comes_from_prossimo_id_but_skips_ids_already_used()
    {
        const string todo = "---\ntitle: To-Do\nprossimo_id: T-014\n---\n";
        Assert.That(BoardEntries.NextId(todo, ["T-003", null, "ABC-9"]), Is.EqualTo("T-014"));
        Assert.That(BoardEntries.NextId(todo, ["T-020"]), Is.EqualTo("T-021"));
        Assert.That(BoardEntries.NextId(null, []), Is.EqualTo("T-001"));
    }

    [Test]
    public void Using_an_id_moves_prossimo_id_past_it()
    {
        const string todo = "---\ntitle: To-Do\nprossimo_id: T-014\n---\n\n## Task\n";
        Assert.That(BoardEntries.AdvanceNextId(todo, "T-014"), Does.Contain("prossimo_id: T-015"));
        Assert.That(BoardEntries.AdvanceNextId(todo, "T-002"), Is.EqualTo(todo), "an older id does not move it back");
        Assert.That(BoardEntries.AdvanceNextId(todo, "ABC-1"), Is.EqualTo(todo), "tracker keys do not use the counter");
        Assert.That(BoardEntries.AdvanceNextId("---\ntitle: To-Do\n---\n", "T-001"), Is.EqualTo("---\ntitle: To-Do\nprossimo_id: T-002\n---\n"));
    }

    [Test]
    public void Tags_typed_in_a_box_are_cleaned()
    {
        Assert.That(BoardEntries.ParseTags("#bug, ui  ui; [x]"), Is.EqualTo(new[] { "bug", "ui", "x" }));
    }
}
