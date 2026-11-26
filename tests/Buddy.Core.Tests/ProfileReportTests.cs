using Buddy.Core.Mentor;

namespace Buddy.Core.Tests;

public class ProfileReportTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "buddy-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private const string Answer = """
        Ecco il resoconto:
        {"sintesi": "Sei curioso e preciso; con chi è vago ti spazientisci.",
         "voti": [{"aspetto": "leadership", "voto": 6, "motivo": "Hai guidato la riunione del rilascio.",
                   "indizi": ["episodio 2026-11-16 · Rilascio", "diario 2026-11-18", "Episodio 2026-11-16 · rilascio", "retro 2026-W47 (sfida)"]},
                  {"aspetto": "Maturità", "voto": 14, "motivo": "Troppo alto: tagliato.", "indizi": ["diario 2026-11-20", "diario 2026-11-21", "diario 2026-11-22"]},
                  {"aspetto": "Autonomia", "voto": 9, "motivo": "Su un solo fatto.", "indizi": ["diario 2026-11-20"]},
                  {"aspetto": "Comunicazione", "voto": null, "motivo": "Servono più episodi."},
                  {"aspetto": "Simpatia", "voto": 9, "motivo": "Non è un aspetto valutato."}],
         "punti_di_forza": ["Chiedi il perché prima di insistere", "Chiedi il perché prima di insistere", "Scrivi i test prima"],
         "da_migliorare": ["Alzi la voce sotto pressione — prova a fermarti un attimo"],
         "interventi_positivi": [{"quando": "2026-11-16", "cosa": "Hai proposto il rilascio a fasi", "perche": "Ha sbloccato la riunione"}],
         "interventi_negativi": [{"quando": "ieri", "cosa": "Hai risposto secco a Chicca", "perché": "Potevi chiedere cosa le serviva"}],
         "prossimi_passi": ["Prima della riunione scrivi i tre punti da dire", "Chiedi un feedback a Tony", "Un passo", "Troppi"],
         "rispetto_a_prima": "Più calmo nelle riunioni."}
        """;

    [Test]
    public void The_answer_is_checked_aspects_marks_and_lists()
    {
        var report = ProfileReports.Parse(Answer, new DateOnly(2026, 11, 26))!;

        Assert.That(report.Scores.Select(s => s.Aspect), Is.EqualTo(ProfileReports.Aspects), "every aspect, in order, unknown ones dropped");
        Assert.That(report.Scores[0].Score, Is.EqualTo(6));
        Assert.That(report.Scores[1].Score, Is.EqualTo(10), "kept within 1-10");
        Assert.That(report.Scores[2].Score, Is.Null);
        Assert.That(report.Scores.Single(s => s.Aspect == "Affidabilità").Score, Is.Null, "not given: no mark");
        Assert.That(report.Scores[0].Evidence, Is.EqualTo(new[] { "episodio 2026-11-16 · Rilascio", "diario 2026-11-18", "retro 2026-W47 [sfida]" }),
            "each fact once, without the note's separators");

        // A mark on fewer than three facts is kept but not shown: out of the average and of the marks shown.
        var autonomy = report.Scores.Single(s => s.Aspect == "Autonomia");
        Assert.That((autonomy.Score, autonomy.Shown, autonomy.Forming), Is.EqualTo((9, (int?)null, true)));
        Assert.That(report.Forming.Select(s => s.Aspect), Is.EqualTo(new[] { "Autonomia" }));
        Assert.That(report.Rated.Select(s => s.Aspect), Is.EqualTo(new[] { "Leadership", "Maturità" }));
        Assert.That(report.Strengths, Is.EqualTo(new[] { "Chiedi il perché prima di insistere", "Scrivi i test prima" }));
        Assert.That(report.Improvements.Single(), Does.Not.Contain(" — "), "the dash would split the line when read back");
        Assert.That(report.Positive.Single().When, Is.EqualTo("2026-11-16"));
        Assert.That(report.Negative.Single(), Is.EqualTo(new Intervention("", "Hai risposto secco a Chicca", "Potevi chiedere cosa le serviva")));
        Assert.That(report.Average, Is.EqualTo(8));
        Assert.That(report.NextSteps, Has.Count.EqualTo(ProfileReports.MaxSteps));
        Assert.That(report.Since, Is.EqualTo("Più calmo nelle riunioni."));
    }

    [Test]
    public void Andamento_is_read_by_its_column_headings()
    {
        var weeks = ProfileTrends.ParseAndamento("""
            # Andamento

            | Settimana | Umore medio | Ore attive | Focus | Ripassi ✓/totali | Abitudine scelta | Mantenuta? |
            |---|---|---|---|---|---|---|
            | 2026-W46 | 3,5 | 31h | 4 | 2/3 | test prima | in parte |
            | 2026-W47 | 4 / 5 | 28.5 | 5 | 3/3 | pause | sì |
            | 2026-W48 | — | | | | | |
            | 2026-W49 | n.d. (nei check-out nessun voto 1-5) | 34,8 | 0 | 0/0 | — | — |
            """);

        Assert.That(weeks.Select(w => ProfileTrends.ShortWeek(w.Week)), Is.EqualTo(new[] { "W46", "W47", "W48", "W49" }));
        Assert.That(weeks.Select(w => w.Mood), Is.EqualTo(new double?[] { 3.5, 4, null, null }),
            "not the hours («umore» contains «ore»), nor a number only mentioned");
        Assert.That(weeks.Select(w => w.Hours), Is.EqualTo(new double?[] { 31, 28.5, null, 34.8 }));
        Assert.That(weeks.Select(w => w.KeptKind), Is.EqualTo(new[] { "in parte", "sì", "", "" }));
    }

    [Test]
    public void Episodes_are_counted_month_by_month_calm_or_tense()
    {
        Buddy.Core.Vault.Episode Episode(string date, string facts) => new(DateOnly.Parse(date), "x", [], facts, "", "", "", []);
        var months = ProfileTrends.EpisodesByMonth(
            [Episode("2026-11-20", "Discussione tesa sul rilascio"), Episode("2026-11-02", "Abbiamo chiuso il rilascio"), Episode("2026-09-10", "Tutto liscio"),
             Episode("2026-01-10", "troppo vecchio")],
            new DateOnly(2026, 11, 26), months: 3);

        Assert.That(months.Select(m => (m.Month.Month, m.Calm, m.Tense)), Is.EqualTo(new[] { (9, 1, 0), (10, 0, 0), (11, 1, 1) }));
    }

    [Test]
    public void No_json_no_report()
    {
        Assert.That(ProfileReports.Parse("Non ho trovato il diario.", DateOnly.MinValue), Is.Null);
        Assert.That(ProfileReports.Parse("{}", DateOnly.MinValue), Is.Null);
    }

    [Test]
    public void The_note_keeps_the_reports_newest_first_one_per_day()
    {
        var path = Path.Combine(_root, ProfileReports.FileName);
        var first = ProfileReports.Parse(Answer, new DateOnly(2026, 11, 12))!;
        ProfileReports.Add(path, first);
        var second = first with
        {
            Date = new DateOnly(2026, 11, 26),
            Scores = first.Scores.Select(s => s.Aspect == "Leadership" ? s with { Score = 8 } : s).ToList(),
        };
        ProfileReports.Add(path, second);
        ProfileReports.Add(path, second with { Summary = "Rifatto oggi." }); // same day: replaced

        var reports = ProfileReports.Read(path);
        Assert.That(reports.Select(r => r.Date), Is.EqualTo(new[] { new DateOnly(2026, 11, 26), new DateOnly(2026, 11, 12) }));
        Assert.That(File.ReadAllText(path), Does.StartWith("# Resoconto"));

        var latest = reports[0];
        Assert.That(latest.Summary, Is.EqualTo("Rifatto oggi."));
        Assert.That(latest.Scores, Is.EqualTo(second.Scores), "marks, n.d. and reasons read back");
        Assert.That(latest.Strengths, Is.EqualTo(second.Strengths));
        Assert.That(latest.Improvements, Is.EqualTo(second.Improvements));
        Assert.That(latest.Positive, Is.EqualTo(second.Positive));
        Assert.That(latest.Negative, Is.EqualTo(second.Negative));
        Assert.That(latest.Scores.Single(s => s.Aspect == "Autonomia").Forming, Is.True, "the evidence is read back with the mark");
        Assert.That(latest.Scores.Single(s => s.Aspect == "Comunicazione").Evidence, Is.Empty, "«nessuno» is counted, not unknown");
        Assert.That(latest.NextSteps, Is.EqualTo(second.NextSteps));
        Assert.That(latest.Since, Is.EqualTo(second.Since));
        Assert.That(ProfileReports.Change(latest, reports[1], "Leadership"), Is.EqualTo(2));
        Assert.That(ProfileReports.Change(latest, reports[1], "Comunicazione"), Is.Null);
    }

    [Test]
    public void The_prompt_asks_for_every_aspect_and_recalls_the_previous_marks()
    {
        var previous = ProfileReports.Parse(Answer, new DateOnly(2026, 11, 12));
        var prompt = ProfileReports.Prompt("Sei il mentore.", new DateOnly(2026, 11, 26), previous);

        Assert.That(prompt, Does.Contain("Leadership, Maturità"));
        Assert.That(prompt, Does.Contain("Resoconto precedente (2026-11-12)"));
        Assert.That(prompt, Does.Contain("- Leadership: 6/10 (3 indizi)"));
        Assert.That(prompt, Does.Contain("- Autonomia: 9/10 (1 indizio, in formazione)"));
        Assert.That(prompt, Does.Contain("\"indizi\""));
        Assert.That(prompt, Does.Contain("- Comunicazione: n.d."));
    }

    [Test]
    public void The_chat_message_shows_how_the_marks_moved()
    {
        var before = ProfileReports.Parse(Answer, new DateOnly(2026, 11, 12))!;
        var now = before with { Scores = before.Scores.Select(s => s.Aspect == "Leadership" ? s with { Score = 7 } : s).ToList() };

        var message = MentorService.ReportMessage(now, before);
        Assert.That(message, Does.Contain("Leadership **7** ↑1"));
        Assert.That(message, Does.Not.Contain("Autonomia **"), "a mark on one fact is not given as a number");
        Assert.That(message, Does.Contain("In formazione, con meno di 3 indizi: Autonomia"));
        Assert.That(message, Does.Contain("💪 Chiedi il perché"));
        Assert.That(message, Does.Contain("Il mio profilo"));
    }
}
