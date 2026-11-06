using Buddy.Core.Chat;

namespace Buddy.Core.Tests;

public class QuickRepliesTests
{
    [Test]
    public void The_hidden_line_becomes_replies_and_leaves_the_text()
    {
        var (text, replies) = QuickReplies.Extract("Quale funzione proveresti?\n\n<!-- risposte: Lo sconto | «Il login» | Non saprei -->");
        Assert.That(text, Is.EqualTo("Quale funzione proveresti?"));
        Assert.That(replies, Is.EqualTo(new[] { "Lo sconto", "Il login", "Non saprei" }));
    }

    [Test]
    public void Sources_after_the_line_stay()
    {
        var (text, replies) = QuickReplies.Extract("Risposta.\n<!-- risposte: Sì | No -->\n\nFonti: [To-Do](x)");
        Assert.That(text, Is.EqualTo("Risposta.\n\nFonti: [To-Do](x)"));
        Assert.That(replies, Has.Count.EqualTo(2));
    }

    [Test]
    public void At_most_three_short_distinct_replies()
    {
        var (_, replies) = QuickReplies.Extract("x <!-- risposte: a | A | b | c | d | " + new string('z', 80) + " -->");
        Assert.That(replies, Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Without_the_line_nothing_changes()
    {
        Assert.That(QuickReplies.Extract("Solo testo <!-- altro commento -->"), Is.EqualTo(("Solo testo <!-- altro commento -->", (IReadOnlyList<string>)[])).Using<(string, IReadOnlyList<string>)>((a, b) => a.Item1 == b.Item1 && a.Item2.Count == b.Item2.Count));
    }

    [TestCase("Ciao\n<!-- risp", "Ciao")]
    [TestCase("Ciao\n<!-- risposte: Sì | N", "Ciao")]
    [TestCase("Ciao\n<!", "Ciao\n<!")]
    [TestCase("Ciao <!-- altro --> fine", "Ciao <!-- altro --> fine")]
    [TestCase("Ciao\n<!-- risposte: Sì -->", "Ciao")]
    public void While_streaming_a_half_written_line_is_hidden(string text, string shown)
    {
        Assert.That(QuickReplies.Hide(text), Is.EqualTo(shown));
    }
}
