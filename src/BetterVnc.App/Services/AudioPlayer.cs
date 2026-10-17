using NAudio.Wave;

namespace BetterVnc.App.Services;

/// <summary>Plays the raw PCM stream delivered by the QEMU audio extension.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private BufferedWaveProvider? _buffer;
    private readonly object _lock = new();

    public void Start(int sampleRate, int channels, int bits)
    {
        lock (_lock)
        {
            Stop();
            _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, bits, channels))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _out = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 4 };
            _out.Init(_buffer);
            _out.Play();
        }
    }

    public void Feed(byte[] pcm)
    {
        lock (_lock)
        {
            _buffer?.AddSamples(pcm, 0, pcm.Length);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _out?.Stop();
            _out?.Dispose();
            _out = null;
            _buffer = null;
        }
    }

    public void Dispose() => Stop();
}
