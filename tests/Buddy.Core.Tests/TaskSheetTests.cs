using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class TaskSheetTests
{
    private string _folder = "";

    [SetUp]
    public void SetUp() => _folder = Directory.CreateTempSubdirectory("buddy-sheet-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, true);

    [Test]
    public void The_sheet_of_a_task_is_one_note_per_id_in_the_Task_folder() =>
        Assert.That(TaskSheet.RelativePath("NUSR-2670").Replace('\\', '/'), Is.EqualTo("Task/NUSR-2670.md"));

    [Test]
    public void A_new_sheet_starts_with_the_id_and_the_section()
    {
        var text = TaskSheet.WithSection(null, "NUSR-2670", "Import fatture CleanEnergy", TaskSheet.Missing, "Bloccante\n- WS MUBI reali");

        Assert.That(text, Does.StartWith("# NUSR-2670 · Import fatture CleanEnergy\n"));
        Assert.That(TaskSheet.Section(text, TaskSheet.Missing), Is.EqualTo("Bloccante\n- WS MUBI reali"));
        Assert.That(TaskSheet.Section(text, TaskSheet.HowDone), Is.Empty);
    }

    [Test]
    public void Writing_one_section_keeps_the_other_and_the_rest_of_the_note()
    {
        var note = "# T-1 · Titolo\n\nUna riga mia.\n\n## Come è stato fatto\n\n1. Passo uno\n\n## Link\n\n- [[Analisi]]\n";

        var text = TaskSheet.WithSection(note, "T-1", "Titolo", TaskSheet.Missing, "- niente");

        Assert.That(text, Does.Contain("Una riga mia."));
        Assert.That(text.IndexOf("## Cosa manca", StringComparison.Ordinal), Is.LessThan(text.IndexOf("## Come è stato fatto", StringComparison.Ordinal)));
        Assert.That(TaskSheet.Section(text, TaskSheet.HowDone), Is.EqualTo("1. Passo uno"));
        Assert.That(TaskSheet.Section(text, "Link"), Is.EqualTo("- [[Analisi]]"));

        var again = TaskSheet.WithSection(text, "T-1", "Titolo", TaskSheet.HowDone, "1. Passo uno\n2. Passo due");
        Assert.That(TaskSheet.Section(again, TaskSheet.HowDone), Is.EqualTo("1. Passo uno\n2. Passo due"));
        Assert.That(TaskSheet.Section(again, TaskSheet.Missing), Is.EqualTo("- niente"));
    }

    [Test]
    public void A_heading_written_twice_is_read_and_saved_as_one()
    {
        var note = "# T-4 · Titolo\n\n## Cosa manca\n\n## Cosa manca\n\n**Bloccante**\n- WS reali\n\n## Come è stato fatto\n\n1. Passo\n";

        Assert.That(TaskSheet.Section(note, TaskSheet.Missing), Is.EqualTo("**Bloccante**\n- WS reali"));

        var text = TaskSheet.WithSection(note, "T-4", "Titolo", TaskSheet.Missing, "- WS reali (sollecitato)");
        Assert.That(text.Split("## Cosa manca").Length - 1, Is.EqualTo(1));
        Assert.That(TaskSheet.Section(text, TaskSheet.Missing), Is.EqualTo("- WS reali (sollecitato)"));
        Assert.That(TaskSheet.Section(text, TaskSheet.HowDone), Is.EqualTo("1. Passo"));
    }

    [Test]
    public void Write_creates_the_note_and_keeps_its_line_endings()
    {
        var path = Path.Combine(_folder, TaskSheet.RelativePath("T-2"));

        TaskSheet.Write(path, "T-2", "Titolo", TaskSheet.Missing, "", "- primo");
        Assert.That(TaskSheet.Read(path, TaskSheet.Missing), Is.EqualTo("- primo"));

        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));
        TaskSheet.Write(path, "T-2", "Titolo", TaskSheet.Missing, "- primo", "- secondo");
        Assert.That(TaskSheet.Read(path, TaskSheet.Missing), Is.EqualTo("- secondo"));
        Assert.That(File.ReadAllText(path), Does.Contain("\r\n").And.Not.Match("[^\r]\n"));
    }

    [Test]
    public void A_section_changed_meanwhile_is_not_overwritten()
    {
        var path = Path.Combine(_folder, TaskSheet.RelativePath("T-3"));
        TaskSheet.Write(path, "T-3", "Titolo", TaskSheet.Missing, "", "- scritto da Claude");

        Assert.Throws<VaultConflictException>(() =>
            TaskSheet.Write(path, "T-3", "Titolo", TaskSheet.Missing, "- quello che avevo aperto", "- mio"));
        Assert.That(TaskSheet.Read(path, TaskSheet.Missing), Is.EqualTo("- scritto da Claude"));
    }

    [Test]
    public void The_request_to_Claude_names_the_note_and_keeps_what_the_user_wrote()
    {
        Assert.That(TaskSheet.Request("NUSR-2670", TaskSheet.Missing),
            Does.Contain("Task/NUSR-2670.md").And.Contain("## Cosa manca").And.Contain("tienilo"));
        Assert.That(TaskSheet.Request("NUSR-2670", TaskSheet.HowDone),
            Does.Contain("## Come è stato fatto").And.Contain("procedura"));
    }
}
