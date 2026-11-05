using System.IO;
using NAudio.Wave;
using Whisper.net;

namespace Buddy.App.Voice;

/// <summary>
/// Push-to-talk dictation fully on this PC: records the default microphone (16 kHz mono, what Whisper expects)
/// and transcribes it with a local Whisper model. Nothing leaves the machine.
/// </summary>
public sealed class VoiceInput : IDisposable
{
    private readonly string _modelPath;
    private readonly string _language;
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private WhisperFactory? _factory;
    private WaveInEvent? _recorder;
    private MemoryStream? _audio;
    private WaveFileWriter? _writer;

    public VoiceInput(string modelPath, string language)
    {
        _modelPath = modelPath;
        _language = string.IsNullOrWhiteSpace(language) ? "it" : language;
    }

    public bool IsModelAvailable => File.Exists(_modelPath);

    public bool IsRecording => _recorder is not null;

    public string ModelPath => _modelPath;

    public void StartRecording()
    {
        if (IsRecording)
        {
            return;
        }

        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException("Nessun microfono trovato.");
        }

        _audio = new MemoryStream();
        var format = new WaveFormat(16_000, 16, 1);
        _writer = new WaveFileWriter(new NonClosingStream(_audio), format);
        _recorder = new WaveInEvent { WaveFormat = format, BufferMilliseconds = 100 };
        _recorder.DataAvailable += (_, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _recorder.StartRecording();
    }

    /// <summary>Stops recording and returns the transcription (empty if nothing intelligible was said).</summary>
    public async Task<string> StopAndTranscribeAsync(CancellationToken cancellationToken = default)
    {
        if (_recorder is not { } recorder || _writer is not { } writer || _audio is not { } audio)
        {
            return "";
        }

        var stopped = new TaskCompletionSource();
        recorder.RecordingStopped += (_, _) => stopped.TrySetResult();
        recorder.StopRecording();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        recorder.Dispose();
        writer.Dispose(); // writes the WAV header lengths; the underlying stream stays open
        _recorder = null;
        _writer = null;
        _audio = null;

        // Less than ~0.3 s of audio: a click, not speech.
        if (audio.Length < 16_000 * 2 * 0.3)
        {
            return "";
        }

        audio.Position = 0;
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
            await audio.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Drops Whisper's silence markers such as "[BLANK_AUDIO]" or "(musica)".</summary>
    internal static string CleanUp(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", "").Trim();

    public void Dispose()
    {
        _recorder?.Dispose();
        _writer?.Dispose();
        _audio?.Dispose();
        _factory?.Dispose();
        _modelLock.Dispose();
    }

    /// <summary>Lets WaveFileWriter finalize the header without closing our MemoryStream.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) => Flush();
    }
}
