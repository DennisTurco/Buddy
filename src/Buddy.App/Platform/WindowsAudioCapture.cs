#if WINDOWS
using NAudio.Wave;

namespace Buddy.App.Platform;

/// <summary>Records the default microphone through WinMM (NAudio), 16 kHz mono 16-bit.</summary>
public sealed class WindowsAudioCapture : IAudioCapture
{
    private WaveInEvent? _recorder;
    private MemoryStream? _audio;
    private readonly object _gate = new();

    public bool IsRecording => _recorder is not null;

    public void Start()
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
        _recorder = new WaveInEvent { WaveFormat = new WaveFormat(16_000, 16, 1), BufferMilliseconds = 100 };
        _recorder.DataAvailable += (_, e) =>
        {
            lock (_gate)
            {
                _audio?.Write(e.Buffer, 0, e.BytesRecorded);
            }
        };
        try
        {
            _recorder.StartRecording();
        }
        catch (NAudio.MmException ex)
        {
            _recorder.Dispose();
            _recorder = null;
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public async Task<byte[]> StopAsync()
    {
        if (_recorder is not { } recorder || _audio is not { } audio)
        {
            return [];
        }

        var stopped = new TaskCompletionSource();
        recorder.RecordingStopped += (_, _) => stopped.TrySetResult();
        recorder.StopRecording();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        recorder.Dispose();
        _recorder = null;
        lock (_gate)
        {
            _audio = null;
            return audio.ToArray();
        }
    }

    public byte[] Snapshot()
    {
        lock (_gate)
        {
            return _audio?.ToArray() ?? [];
        }
    }

    public void Dispose()
    {
        _recorder?.Dispose();
        _audio?.Dispose();
    }
}
#endif
