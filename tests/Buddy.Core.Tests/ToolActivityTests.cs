using Buddy.Core;
using Buddy.Core.Chat;

namespace Buddy.Core.Tests;

public class ToolActivityTests
{
    [Test]
    public void Consecutive_steps_show_as_one_line_with_the_latest()
    {
        var activity = new ToolActivityEntry("Legge Conversation.cs");
        Assert.That(activity.Title, Is.EqualTo("🔧 Legge Conversation.cs"));
        Assert.That(activity.HasMany, Is.False);
        Assert.That(activity.Latest, Is.Empty);

        activity.Add("Esegue dotnet build");
        activity.Add("Modifica ChatEntry.cs");

        Assert.That(activity.Title, Is.EqualTo("🔧 3 azioni"));
        Assert.That(activity.Latest, Is.EqualTo("· Modifica ChatEntry.cs"));
        Assert.That(activity.Text, Is.EqualTo("🔧 Legge Conversation.cs\n🔧 Esegue dotnet build\n🔧 Modifica ChatEntry.cs"));

        activity.IsExpanded = true;
        Assert.That(activity.Latest, Is.Empty, "the open list already shows it");
        Assert.That(activity.Steps, Has.Count.EqualTo(3));
    }

    [Test]
    public void Only_lines_of_tool_calls_are_read_back_as_steps()
    {
        Assert.That(ToolActivityEntry.StepsOf(Speaker.System, "🔧 a\n🔧 b"), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(ToolActivityEntry.StepsOf(Speaker.System, "🔧 a\nConversazione riaperta."), Is.Null);
        Assert.That(ToolActivityEntry.StepsOf(Speaker.Claude, "🔧 a"), Is.Null);
    }

    [Test]
    public async Task A_saved_conversation_comes_back_with_its_tool_calls_grouped()
    {
        var root = Directory.CreateTempSubdirectory("buddy-tools-").FullName;
        try
        {
            var settings = new BuddySettings { ProjectPath = Path.Combine(root, "vault"), Ollama = { BaseUrl = "http://127.0.0.1:1" } };
            var conversation = ModelCommandTests.Create(settings);
            conversation.Archive = new ConversationArchive(Path.Combine(root, "conversations"));
            // Saved before the grouping: one line per call.
            conversation.Archive.Save("x",
            [
                new ChatEntry(Speaker.User, "sistema il bug"),
                new ChatEntry(Speaker.System, "🔧 Legge a.cs"),
                new ChatEntry(Speaker.System, "🔧 Legge b.cs"),
                new ChatEntry(Speaker.Claude, "Ecco il problema."),
                new ChatEntry(Speaker.System, "🔧 Modifica a.cs"),
            ], null);

            Assert.That(await conversation.OpenSavedAsync(conversation.Archive.List()[0]), Is.True);

            var groups = conversation.Entries.OfType<ToolActivityEntry>().ToList();
            Assert.That(groups.Select(g => g.Steps.Count), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(groups[0].Steps, Is.EqualTo(new[] { "Legge a.cs", "Legge b.cs" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class ChatGroupingTests
{
    [Test]
    public void Consecutive_messages_of_one_speaker_form_a_group()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<ChatEntry>();
        ChatGrouping.Track(entries);

        entries.Add(new ChatEntry(Speaker.User, "a"));
        entries.Add(new ChatEntry(Speaker.User, "b"));
        entries.Add(new ChatEntry(Speaker.Claude, "c"));
        entries.Add(new ChatEntry(Speaker.System, "🔧 x"));
        entries.Add(new ChatEntry(Speaker.System, "🔧 y"));
        entries.Add(new ChatEntry(Speaker.Claude, "d"));
        entries.Add(new ChatEntry(Speaker.Claude, "e"));

        Assert.That(entries.Select(e => (e.JoinsPrevious, e.JoinsNext)), Is.EqualTo(new[]
        {
            (false, true), (true, false), (false, false), (false, false), (false, false), (false, true), (true, false),
        }), "system lines never group");

        entries.RemoveAt(1);
        Assert.That(entries[0].JoinsNext, Is.False, "alone again after its neighbour is gone");

        entries.Insert(1, new ChatEntry(Speaker.Claude, "f"));
        Assert.That((entries[1].JoinsNext, entries[2].JoinsPrevious), Is.EqualTo((true, true)));
    }
}
