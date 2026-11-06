using System.ComponentModel;
using System.Diagnostics;

namespace Buddy.App.Platform;

/// <summary>
/// Records the default microphone with the desktop's own tools, so no native audio library is needed:
/// pw-record (PipeWire) or, if missing, arecord (ALSA, routed to PipeWire/PulseAudio on modern distros).
/// Both write raw PCM to stdout; stopping just kills the recorder, raw audio needs no trailer.
/// </summary>
public sealed class LinuxAudioCapture : IAudioCapture
{
    private readonly object _gate = new();
    private Process? _recorder;
    private Task<byte[]>? _reading;
    private MemoryStream? _buffer;

    public bool IsRecording => _recorder is not null;

    public void Start()
    {
        if (IsRecording)
        {
            return;
        }

        var (tool, arguments) = Recorder()
            ?? throw new InvalidOperationException("Per dettare serve pw-record (PipeWire) o arecord (alsa-utils).");

        var startInfo = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            _recorder = Process.Start(startInfo) ?? throw new InvalidOperationException($"{tool} non si avvia.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"{tool} non si avvia: {ex.Message}", ex);
        }

        var stdout = _recorder.StandardOutput.BaseStream;
        _ = _recorder.StandardError.ReadToEndAsync();
        var buffer = _buffer = new MemoryStream();
        _reading = Task.Run(async () =>
        {
            // Chunk by chunk, so Snapshot can read what arrived so far.
            var chunk = new byte[8192];
            int read;
            while ((read = await stdout.ReadAsync(chunk).ConfigureAwait(false)) > 0)
            {
                lock (_gate)
                {
                    buffer.Write(chunk, 0, read);
                }
            }

            lock (_gate)
            {
                return buffer.ToArray();
            }
        });
    }

    public async Task<byte[]> StopAsync()
    {
        if (_recorder is not { } recorder || _reading is not { } reading)
        {
            return [];
        }

        _recorder = null;
        _reading = null;
        _buffer = null;
        try
        {
            if (!recorder.HasExited)
            {
                recorder.Kill();
            }

            var pcm = await reading.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            // An odd byte count would shift every following sample: drop the last half sample if any.
            return pcm.Length % 2 == 0 ? pcm : pcm[..^1];
        }
        finally
        {
            recorder.Dispose();
        }
    }

    public byte[] Snapshot()
    {
        lock (_gate)
        {
            var pcm = _buffer?.ToArray() ?? [];
            return pcm.Length % 2 == 0 ? pcm : pcm[..^1];
        }
    }

    private static (string Tool, string[] Arguments)? Recorder()
    {
        if (ProcessRunner.Exists("pw-record"))
        {
            return ("pw-record", ["--rate", "16000", "--channels", "1", "--format", "s16", "--raw", "-"]);
        }

        if (ProcessRunner.Exists("arecord"))
        {
            return ("arecord", ["-q", "-f", "S16_LE", "-r", "16000", "-c", "1", "-t", "raw"]);
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            _recorder?.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _recorder?.Dispose();
    }
}
