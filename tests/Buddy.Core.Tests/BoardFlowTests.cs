using System.Text.RegularExpressions;
using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

/// <summary>In corso (▶), closing by drag (/chiudi, /rifiuta) and editing a card through its form.</summary>
public class BoardFlowTests
{
    private static readonly Regex Ids = new(@"^(?:T-\d+)$", RegexOptions.IgnoreCase);

    private static BoardItem Parse(string text) => ProjectBoard.ParseItems(text, Ids).Single();

    [Test]
    public void A_task_marked_with_a_play_sign_is_in_progress()
    {
        var item = Parse("- [ ] T-7 · ▶ 2026-11-06 · Login [priorita:: P1]");
        Assert.That(item.InProgress, Is.True);
        Assert.That(item.Waiting, Is.False);
        Assert.That(item.Date, Is.EqualTo(new DateOnly(2026, 11, 6)));
        Assert.That(item.Title, Is.EqualTo("Login"));
    }

    [Test]
    public void The_state_marker_replaces_the_other_one()
    {
        Assert.That(BoardMoves.SetState("- [ ] T-7 · ⏸ Login", BoardMoves.Working, "T-7"), Is.EqualTo("- [ ] T-7 · ▶ Login"));
        Assert.That(BoardMoves.SetState("- [ ] T-7 · ▶ Login", BoardMoves.Waiting, "T-7"), Is.EqualTo("- [ ] T-7 · ⏸ Login"));
        Assert.That(BoardMoves.SetState("- [ ] T-7 · ▶️ Login", null, "T-7"), Is.EqualTo("- [ ] T-7 · Login"));
    }

    [Test]
    public void Dropping_on_the_archive_goes_through_the_skills()
    {
        Assert.That(BoardMoves.ClosingSkill("todo", "fatti"), Is.EqualTo("chiudi"));
        Assert.That(BoardMoves.ClosingSkill("corso", "fatti"), Is.EqualTo("chiudi"));
        Assert.That(BoardMoves.ClosingSkill("valutare", "fatti"), Is.Null, "an idea is not closed, it is approved or refused");
        Assert.That(BoardMoves.ClosingSkill("valutare", "rifiutati"), Is.EqualTo("rifiuta"));
        Assert.That(BoardMoves.ClosingSkill("appunti", "rifiutati"), Is.Null);
        Assert.That(BoardMoves.ClosingSkill("todo", "controllare"), Is.Null);
    }

    [Test]
    public void Editing_a_task_rewrites_the_form_fields_and_keeps_the_rest()
    {
        const string raw = "- [ ] T-4 · ▶ 2026-11-01 · Login con SSO · PR #4 · 🔁1 [priorita:: P2] [stima:: 2h] [tag:: feature] [promemoria:: 2026-11-20]\n"
                           + "  - Contesto: [[Auth]]\n"
                           + "  - Accordi: 2026-11-02 con Persona A — niente MFA\n"
                           + "  - Fatto quando: si entra";
        var item = Parse(raw);
        var before = BoardEdits.ToEntry(item, "corso");
        Assert.That(before.Context, Is.EqualTo("[[Auth]]"));
        Assert.That(before.DoneWhen, Is.EqualTo("si entra"));
        Assert.That(before.Priority, Is.EqualTo("P2"));

        var after = before with { Title = "Login SSO", Priority = "P1", DoneWhen = "si entra con l'account aziendale", Where = "`Auth.cs`" };
        var text = BoardEdits.Apply(item, "corso", before, after);
        Assert.That(text, Is.EqualTo(
            "- [ ] T-4 · ▶ 2026-11-01 · Login SSO · PR #4 · 🔁1 [priorita:: P1] [stima:: 2h] [tag:: feature] [promemoria:: 2026-11-20]\n"
            + "  - Contesto: [[Auth]]\n"
            + "  - Accordi: 2026-11-02 con Persona A — niente MFA\n"
            + "  - Fatto quando: si entra con l'account aziendale\n"
            + "  - Dove: `Auth.cs`"));
    }

    [Test]
    public void Nothing_changed_gives_back_the_same_item()
    {
        const string raw = "- [ ] T-9 · Review · PR #9 [tag:: bug]\n  - Cambiato: il login\n  - Come testare:\n    1. apri\n    2. entra\n  - Costo: 3%";
        var item = Parse(raw);
        var before = BoardEdits.ToEntry(item, "controllare");
        Assert.That(before.HowToTest, Is.EqualTo("1. apri\n2. entra"));
        Assert.That(BoardEdits.Apply(item, "controllare", before, before), Is.EqualTo(raw));

        var edited = BoardEdits.Apply(item, "controllare", before, before with { HowToTest = "1. apri\n2. esci" });
        Assert.That(edited, Does.Contain("  - Come testare:\n    1. apri\n    2. esci\n  - Costo: 3%"));
    }

    [Test]
    public void Putting_a_task_on_hold_from_the_form_writes_what_it_waits_for()
    {
        var item = Parse("- [ ] T-3 · 2026-11-01 · Report [priorita:: P3]");
        var before = BoardEdits.ToEntry(item, "todo");
        var text = BoardEdits.Apply(item, "todo", before, before with { Waiting = true, WaitingFor = "rilascio 2.0" });
        Assert.That(text, Is.EqualTo("- [ ] T-3 · ⏸ 2026-11-01 · Report — In attesa di: rilascio 2.0 [priorita:: P3]"));

        var waiting = Parse(text);
        Assert.That(waiting.Waiting, Is.True);
        var back = BoardEdits.ToEntry(waiting, "attesa");
        Assert.That(back.WaitingFor, Is.EqualTo("rilascio 2.0"));
        Assert.That(BoardEdits.Apply(waiting, "attesa", back, back with { Waiting = false }), Is.EqualTo("- [ ] T-3 · 2026-11-01 · Report [priorita:: P3]"));
    }

    [Test]
    public void An_idea_keeps_its_doubt_and_review_date()
    {
        var item = Parse("- [ ] T-5 · 2026-10-15 · Dark mode — Dubbio: serve davvero? [rivedere:: 2026-10-29]");
        var before = BoardEdits.ToEntry(item, "valutare");
        Assert.That(before.Note, Is.EqualTo("Dubbio: serve davvero?"));
        Assert.That(BoardEdits.Apply(item, "valutare", before, before with { Title = "Tema scuro" }),
            Is.EqualTo("- [ ] T-5 · 2026-10-15 · Tema scuro — Dubbio: serve davvero? [rivedere:: 2026-10-29]"));
    }

    [Test]
    public void An_appunto_rewrites_its_free_text_only_when_changed()
    {
        var item = Parse("- 2026-11-06 · Export CSV [tag:: feature]\n  - per il PO\n    - con le date");
        var before = BoardEdits.ToEntry(item, "appunti");
        Assert.That(BoardEdits.Apply(item, "appunti", before, before with { Title = "Export" }),
            Is.EqualTo("- 2026-11-06 · Export [tag:: feature]\n  - per il PO\n    - con le date"));
        Assert.That(BoardEdits.Apply(item, "appunti", before, before with { Details = "solo questo" }),
            Is.EqualTo("- 2026-11-06 · Export CSV [tag:: feature]\n  - solo questo"));
    }
}
