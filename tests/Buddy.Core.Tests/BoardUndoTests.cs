using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class BoardUndoTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buddy-undo-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static readonly string Pattern = new BuddySettings().TaskIdPattern;

    private (BoardColumn Todo, BoardColumn Review, VaultIndex Vault) Board()
    {
        File.WriteAllText(Path.Combine(_root, "To-Do.md"), "# To-Do\n\n## Task\n\n- [ ] T-6 · Titolo\n- [ ] T-7 · Altro\n");
        File.WriteAllText(Path.Combine(_root, "Da controllare.md"), "# Da controllare\n\n## Voci\n\n- [ ] T-1 · Già lì\n");
        var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = _root }));
        var board = ProjectBoard.Load(vault, Pattern);
        return (board.Columns.Single(c => c.Key == "todo"), board.Columns.Single(c => c.Key == "controllare"), vault);
    }

    [Test]
    public void A_move_is_undone_exactly()
    {
        var (todo, review, vault) = Board();
        using var _ = vault;
        var before = (File.ReadAllText(Path.Combine(_root, "To-Do.md")), File.ReadAllText(Path.Combine(_root, "Da controllare.md")));

        var undo = BoardMoves.MoveWithUndo(_root, todo, todo.Items.Single(i => i.Id == "T-7"), review);
        Assert.That(undo.Description, Is.EqualTo("T-7 → Da controllare"));
        Assert.That(File.ReadAllText(Path.Combine(_root, "To-Do.md")), Does.Not.Contain("T-7"));

        Assert.That(undo.Undo(), Is.Null);
        Assert.That((File.ReadAllText(Path.Combine(_root, "To-Do.md")), File.ReadAllText(Path.Combine(_root, "Da controllare.md"))),
            Is.EqualTo(before));
    }

    [Test]
    public void A_note_changed_after_the_move_is_never_overwritten()
    {
        var (todo, review, vault) = Board();
        using var _ = vault;
        var undo = BoardMoves.MoveWithUndo(_root, todo, todo.Items.Single(i => i.Id == "T-6"), review);
        File.AppendAllText(Path.Combine(_root, "Da controllare.md"), "- [ ] T-9 · Scritta da Claude nel frattempo\n");
        var changed = File.ReadAllText(Path.Combine(_root, "Da controllare.md"));

        Assert.That(undo.Undo(), Does.Contain("Da controllare"));
        Assert.That(File.ReadAllText(Path.Combine(_root, "Da controllare.md")), Is.EqualTo(changed));
        Assert.That(File.ReadAllText(Path.Combine(_root, "To-Do.md")), Does.Not.Contain("T-6"), "nothing written at all");
    }
}
