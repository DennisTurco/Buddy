namespace Buddy.App.Voice;

/// <summary>A piece of transcription, with its times inside the audio that was transcribed.</summary>
public readonly record struct Segment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// The text of a dictation while it is being recorded. Each pass transcribes only the audio after the "committed"
/// part; segments that are well behind the end of the recording are stable, so they are committed and never
/// transcribed again. That keeps each pass short (about a second) however long the dictation is, and leaves only the
/// last few seconds to transcribe when recording stops.
/// </summary>
public sealed class LiveTranscript
{
    public const int BytesPerSecond = 16_000 * 2;

    /// <summary>A segment that ends this long before the end of the audio will not change any more.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(1.5);

    /// <summary>Below this length the window is cheap to redo: nothing is committed yet (more context, better text).</summary>
    private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(6);

    /// <summary>Above this the window is cut even inside a single long segment, so passes stay short.</summary>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(20);

    private readonly List<string> _committed = [];

    /// <summary>Where the audio still to transcribe starts, in bytes of 16 kHz mono 16-bit PCM.</summary>
    public int CommittedBytes { get; private set; }

    /// <summary>The committed text, for the next pass's prompt (it keeps names and spelling consistent).</summary>
    public string CommittedText => string.Join(' ', _committed);

    /// <summary>
    /// Takes the segments of a pass over the audio from <see cref="CommittedBytes"/> on (<paramref name="window"/> long),
    /// commits the stable ones and returns the whole text so far: committed, then the still-changing tail.
    /// </summary>
    public string Update(IReadOnlyList<Segment> segments, TimeSpan window)
    {
        var tail = new List<string>();
        var commitUntil = TimeSpan.Zero;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var settled = window >= MinWindow && segment.End <= window - Settled && i < segments.Count - 1;
            var tooLong = window >= MaxWindow && segment.End > TimeSpan.Zero && segment.End <= window - Settled;
            if (tail.Count == 0 && (settled || tooLong))
            {
                Add(_committed, segment.Text);
                commitUntil = segment.End;
            }
            else
            {
                Add(tail, segment.Text);
            }
        }

        // Whole samples only: an odd offset would read every following sample shifted by a byte.
        CommittedBytes += (int)(commitUntil.TotalSeconds * BytesPerSecond) / 2 * 2;
        return Join(tail);
    }

    /// <summary>The final text: the committed part plus the last pass over what was left.</summary>
    public string Finish(IReadOnlyList<Segment> lastSegments)
    {
        foreach (var segment in lastSegments)
        {
            Add(_committed, segment.Text);
        }

        return CommittedText;
    }

    private string Join(List<string> tail) => string.Join(' ', _committed.Concat(tail));

    private static void Add(List<string> parts, string text)
    {
        if (VoiceInput.CleanUp(text) is { Length: > 0 } clean)
        {
            parts.Add(clean);
        }
    }
}
