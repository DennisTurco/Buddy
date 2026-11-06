using Buddy.App.Voice;
using NUnit.Framework;

namespace Buddy.App.Tests;

public class LiveTranscriptTests
{
    private static Segment S(double start, double end, string text) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    [Test]
    public void Short_dictations_are_redone_whole_and_nothing_is_committed()
    {
        var live = new LiveTranscript();
        Assert.That(live.Update([S(0, 2, " Ciao"), S(2, 3.5, " come va")], TimeSpan.FromSeconds(4)), Is.EqualTo("Ciao come va"));
        Assert.That(live.CommittedBytes, Is.Zero);
    }

    [Test]
    public void Settled_segments_are_committed_and_only_the_rest_is_transcribed_again()
    {
        var live = new LiveTranscript();
        var text = live.Update([S(0, 3, " Prima frase."), S(3, 6.5, " Seconda"), S(6.5, 7.8, " in corso")], TimeSpan.FromSeconds(8));

        Assert.That(text, Is.EqualTo("Prima frase. Seconda in corso"));
        Assert.That(live.CommittedText, Is.EqualTo("Prima frase. Seconda"), "the last segment and the recent one stay open");
        Assert.That(live.CommittedBytes, Is.EqualTo((int)(6.5 * LiveTranscript.BytesPerSecond)));

        // The next pass starts after "Seconda": its times are relative to that point.
        Assert.That(live.Update([S(0, 2, " in corso di scrittura")], TimeSpan.FromSeconds(3)), Is.EqualTo("Prima frase. Seconda in corso di scrittura"));
        Assert.That(live.Finish([S(0, 2.5, " in corso di scrittura."), S(2.5, 3, " [BLANK_AUDIO]")]), Is.EqualTo("Prima frase. Seconda in corso di scrittura."));
    }

    [Test]
    public void A_very_long_single_segment_is_cut_so_passes_stay_short()
    {
        var live = new LiveTranscript();
        live.Update([S(0, 19, " un discorso senza pause")], TimeSpan.FromSeconds(21));
        Assert.That(live.CommittedText, Is.EqualTo("un discorso senza pause"));
        Assert.That(live.CommittedBytes % 2, Is.Zero, "whole samples");
    }
}
