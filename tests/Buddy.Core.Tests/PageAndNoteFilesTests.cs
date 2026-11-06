using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class PageAndNoteFilesTests
{
    private string _vault = "";
    private string _project = "";

    [SetUp]
    public void SetUp()
    {
        _vault = Directory.CreateTempSubdirectory("buddy-pages-").FullName;
        Directory.CreateDirectory(Path.Combine(_vault, ".obsidian"));
        _project = Path.Combine(_vault, "Progetti", "Demo");
        Directory.CreateDirectory(_project);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_vault, recursive: true);

    [Test]
    public void Renaming_a_page_moves_its_folder_and_updates_links_with_the_path()
    {
        CustomPages.CreatePage(_project, "Ricette");
        CustomPages.CreateNote(_project, "Ricette", "Avvio", "testo");
        File.WriteAllText(Path.Combine(_project, "Indice.md"), "vedi [[Pagine/Ricette/Avvio]] e [[Avvio]]");

        Assert.That(CustomPages.RenamePage(_project, "Ricette", "Procedure"), Is.EqualTo("Procedure"));

        Assert.That(CustomPages.List(_project), Is.EqualTo(new[] { "Procedure" }));
        Assert.That(File.Exists(Path.Combine(_project, "Pagine", "Procedure", "Avvio.md")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(_project, "Indice.md")), Is.EqualTo("vedi [[Pagine/Procedure/Avvio]] e [[Avvio]]"));
    }

    [Test]
    public void Renaming_a_page_to_an_existing_one_is_refused()
    {
        CustomPages.CreatePage(_project, "A");
        CustomPages.CreatePage(_project, "B");
        Assert.Throws<IOException>(() => CustomPages.RenamePage(_project, "A", "B"));
    }

    [Test]
    public void A_deleted_page_goes_to_the_vault_trash_and_can_be_recovered()
    {
        CustomPages.CreatePage(_project, "Ricette");
        CustomPages.CreateNote(_project, "Ricette", "Avvio", "testo");

        var trashed = CustomPages.DeletePage(_project, "Ricette");

        Assert.That(CustomPages.List(_project), Is.Empty);
        Assert.That(trashed, Is.EqualTo(Path.Combine(_vault, ".trash", "Ricette")));
        Assert.That(File.Exists(Path.Combine(trashed, "Avvio.md")), Is.True);
    }

    [Test]
    public void Renaming_a_note_changes_file_heading_and_links()
    {
        var note = CustomPages.CreateNote(_project, "Ricette", "Avvio", "testo");
        File.WriteAllText(Path.Combine(_project, "Indice.md"), "vedi [[Avvio|qui]] e [[Avvio#Passi]]");

        var renamed = NoteFiles.Rename(_project, note, "Avvio rapido");

        Assert.That(renamed, Is.EqualTo(Path.Combine("Pagine", "Ricette", "Avvio rapido.md")));
        Assert.That(File.ReadAllText(Path.Combine(_project, renamed)), Does.Contain("# Avvio rapido"));
        Assert.That(File.ReadAllText(Path.Combine(_project, "Indice.md")), Is.EqualTo("vedi [[Avvio rapido|qui]] e [[Avvio rapido#Passi]]"));
    }

    [Test]
    public void Deleting_two_notes_with_the_same_name_keeps_both_in_the_trash()
    {
        var first = CustomPages.CreateNote(_project, "A", "Nota", "1");
        var second = CustomPages.CreateNote(_project, "B", "Nota", "2");

        NoteFiles.Delete(_project, first);
        var trashed = NoteFiles.Delete(_project, second);

        Assert.That(Path.GetFileName(trashed), Is.EqualTo("Nota (2).md"));
        Assert.That(File.Exists(Path.Combine(_vault, ".trash", "Nota.md")), Is.True);
    }

    [Test]
    public void Flow_notes_are_protected()
    {
        Assert.That(NoteFiles.IsFlowNote("To-Do.md"), Is.True);
        Assert.That(NoteFiles.IsFlowNote(Path.Combine("Pagine", "A", "Nota.md")), Is.False);
    }
}
