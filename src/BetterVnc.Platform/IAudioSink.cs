namespace BetterVnc.Platform;

/// <summary>
/// Plays the plain PCM the QEMU audio extension sends. <see cref="Start"/> and <see cref="Stop"/> come from the UI
/// thread, <see cref="Feed"/> from the connection's receive thread; feeding a stopped sink drops the data.
/// </summary>
public interface IAudioSink : IDisposable
{
    void Start(int sampleRate, int channels, int bits);
    void Feed(byte[] pcm);
    void Stop();
}
