using Buddy.App.Platform;
using Whisper.net;

namespace Buddy.App.Voice;

/// <summary>
/// Push-to-talk dictation fully on this PC: records the default microphone (16 kHz mono, what Whisper expects)
/// and transcribes it with a local Whisper model. Nothing leaves the machine.
/// </summary>
public sealed class VoiceInput : IDisposable
{
    private const int SampleRate = 16_000;

    private readonly string _modelPath;
    private readonly string _language;
    private readonly IAudioCapture _capture;
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private WhisperFactory? _factory;

    public VoiceInput(string modelPath, string language, IAudioCapture capture)
    {
        _modelPath = modelPath;
        _language = string.IsNullOrWhiteSpace(language) ? "it" : language;
        _capture = capture;
    }

    public bool IsModelAvailable => File.Exists(_modelPath);

    public bool IsRecording => _capture.IsRecording;

    public string ModelPath => _modelPath;

    public void StartRecording() => _capture.Start();

    private CancellationTokenSource? _live;
    private Task? _liveLoop;
    private LiveTranscript? _transcript;

    /// <summary>
    /// Starts recording and transcribes while the user speaks: about every second <paramref name="onText"/> gets the
    /// text so far (on a background thread). <see cref="StopLiveAsync"/> ends it and returns the final text.
    /// </summary>
    public void StartLive(Action<string> onText)
    {
        _capture.Start();
        _transcript = new LiveTranscript();
        _live = new CancellationTokenSource();
        var token = _live.Token;
        var transcript = _transcript;
        _liveLoop = Task.Run(async () =>
        {
            // The model loads while the first words are spoken, not at the first pass.
            await _modelLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _factory ??= WhisperFactory.FromPath(_modelPath);
            }
            finally
            {
                _modelLock.Release();
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    var pcm = _capture.Snapshot();
                    var window = pcm.Length - transcript.CommittedBytes;
                    if (window < LiveTranscript.BytesPerSecond * 3 / 2)
                    {
                        continue; // less than a second and a half of new audio: too little to be worth a pass
                    }

                    var segments = await TranscribeAsync(pcm[transcript.CommittedBytes..], transcript.CommittedText, token).ConfigureAwait(false);
                    var text = transcript.Update(segments, TimeSpan.FromSeconds((double)window / LiveTranscript.BytesPerSecond));
                    if (!token.IsCancellationRequested)
                    {
                        onText(text);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    /// <summary>Stops a live dictation: only the audio not committed yet is transcribed again, so the text comes quickly.</summary>
    public async Task<string> StopLiveAsync(CancellationToken cancellationToken = default)
    {
        if (_live is not { } live || _liveLoop is not { } loop || _transcript is not { } transcript)
        {
            return await StopAndTranscribeAsync(cancellationToken).ConfigureAwait(false);
        }

        _live = null;
        _liveLoop = null;
        _transcript = null;
        await live.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false); // a pass in progress finishes (or stops) before the last one
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            live.Dispose();
        }

        var pcm = await _capture.StopAsync().ConfigureAwait(false);
        var rest = pcm.Length > transcript.CommittedBytes ? pcm[transcript.CommittedBytes..] : [];
        var segments = rest.Length < SampleRate * 2 * 0.3
            ? []
            : await TranscribeAsync(rest, transcript.CommittedText, cancellationToken).ConfigureAwait(false);
        return CleanUp(transcript.Finish(segments));
    }

    /// <summary>
    /// One pass of Whisper over a piece of audio. The encoder's window is cut to the audio's length (whisper.cpp's
    /// audio_ctx), which makes a pass on a few seconds about three times faster than the full 30 s window.
    /// </summary>
    private async Task<IReadOnlyList<Segment>> TranscribeAsync(byte[] pcm, string prompt, CancellationToken cancellationToken)
    {
        await using var audio = new MemoryStream(Wav(pcm));
        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _factory ??= WhisperFactory.FromPath(_modelPath);
            var seconds = (double)pcm.Length / (SampleRate * 2);
            var builder = _factory.CreateBuilder().WithLanguage(_language).WithThreads(Math.Max(1, Environment.ProcessorCount - 1));
            if (seconds < 28)
            {
                // 50 encoder frames per second, plus margin; never below ~4 s: with a tinier window the decoder can loop
                // on a second of audio and never finish.
                builder = builder.WithAudioContextSize((int)(Math.Max(seconds, 4) * 50) + 64);
            }

            if (prompt.Length > 0)
            {
                builder = builder.WithPrompt(prompt.Length > 200 ? prompt[^200..] : prompt);
            }

            await using var processor = builder.Build();
            var segments = new List<Segment>();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken).ConfigureAwait(false))
            {
                segments.Add(new Segment(segment.Start, segment.End, segment.Text));
            }

            return segments;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>Stops recording and returns the transcription (empty if nothing intelligible was said).</summary>
    public async Task<string> StopAndTranscribeAsync(CancellationToken cancellationToken = default)
    {
        var pcm = await _capture.StopAsync().ConfigureAwait(false);

        // Less than ~0.3 s of audio: a click, not speech.
        if (pcm.Length < SampleRate * 2 * 0.3)
        {
            return "";
        }

        var segments = await TranscribeAsync(pcm, "", cancellationToken).ConfigureAwait(false);
        return CleanUp(string.Concat(segments.Select(segment => segment.Text)));
    }

    /// <summary>Wraps 16 kHz mono 16-bit PCM in a WAV header.</summary>
    internal static byte[] Wav(byte[] pcm)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2); // bytes per second
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits per sample
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Drops Whisper's silence markers such as "[BLANK_AUDIO]" or "(musica)".</summary>
    internal static string CleanUp(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", "").Trim();

    public void Dispose()
    {
        _capture.Dispose();
        _factory?.Dispose();
        _modelLock.Dispose();
    }
}
