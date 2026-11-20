using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class NoteDraftsTests
{
    private string _folder = "";
    private NoteDrafts _drafts = null!;
    private string _note = "";

    [SetUp]
    public void SetUp()
    {
        _folder = Directory.CreateTempSubdirectory("buddy-drafts-").FullName;
        _drafts = new NoteDrafts(Path.Combine(_folder, "drafts"));
        _note = Path.Combine(_folder, "vault", "Analisi", "Data Retention.md");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, true);

    [Test]
    public void A_draft_is_saved_and_read_back_until_deleted()
    {
        _drafts.Save(_note, "prima", "prima e dopo");

        var draft = _drafts.Load(_note);
        Assert.That(draft, Is.Not.Null);
        Assert.That((draft!.Original, draft.Text), Is.EqualTo(("prima", "prima e dopo")));

        _drafts.Delete(_note);
        Assert.That(_drafts.Load(_note), Is.Null);
    }

    [Test]
    public void Typing_back_to_the_original_leaves_no_draft()
    {
        _drafts.Save(_note, "prima", "prima e dopo");
        _drafts.Save(_note, "prima", "prima");

        Assert.That(_drafts.Load(_note), Is.Null);
    }

    [Test]
    public void Each_note_has_its_own_draft_and_a_later_save_replaces_the_earlier()
    {
        var other = Path.Combine(_folder, "vault", "Glossario.md");
        _drafts.Save(_note, "a", "a1");
        _drafts.Save(_note, "a", "a2");
        _drafts.Save(other, "b", "b1");

        Assert.That(_drafts.Load(_note)!.Text, Is.EqualTo("a2"));
        Assert.That(_drafts.Load(other)!.Text, Is.EqualTo("b1"));
    }

    [Test]
    public void No_draft_and_a_missing_folder_are_not_errors()
    {
        Assert.That(_drafts.Load(_note), Is.Null);
        Assert.DoesNotThrow(() => _drafts.Delete(_note));
    }
}
