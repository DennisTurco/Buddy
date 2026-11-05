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

    /// <summary>Stops recording and returns the transcription (empty if nothing intelligible was said).</summary>
    public async Task<string> StopAndTranscribeAsync(CancellationToken cancellationToken = default)
    {
        var pcm = await _capture.StopAsync().ConfigureAwait(false);

        // Less than ~0.3 s of audio: a click, not speech.
        if (pcm.Length < SampleRate * 2 * 0.3)
        {
            return "";
        }

        await using var audio = new MemoryStream(Wav(pcm));
        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _factory ??= WhisperFactory.FromPath(_modelPath);
            await using var processor = _factory.CreateBuilder().WithLanguage(_language).Build();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            return CleanUp(text.ToString());
        }
        finally
        {
            _modelLock.Release();
        }
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
