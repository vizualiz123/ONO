using System.IO;
using NAudio.Wave;

namespace AvatarDesktop.Services;

public sealed class WaveAudioPlaybackService : IDisposable
{
    private readonly SemaphoreSlim _playLock = new(1, 1);
    private WaveOutEvent? _waveOut;
    private WaveStream? _currentReader;
    private bool _disposed;

    public event Action<string>? Log;

    public Task PlayWavAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        return PlayAudioAsync(wavBytes, "audio/wav", cancellationToken);
    }

    public async Task PlayAudioAsync(byte[] audioBytes, string? contentType, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        if (audioBytes.Length == 0)
        {
            return;
        }

        await _playLock.WaitAsync(cancellationToken);
        try
        {
            StopPlaybackInternal();

            var reader = CreateReader(audioBytes, contentType);
            var waveOut = new WaveOutEvent();
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler<StoppedEventArgs>? handler = null;
            CancellationTokenRegistration ctr = default;

            handler = (_, e) =>
            {
                waveOut.PlaybackStopped -= handler;
                ctr.Dispose();

                if (e.Exception is not null)
                {
                    tcs.TrySetException(e.Exception);
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                tcs.TrySetResult(null);
            };

            _waveOut = waveOut;
            _currentReader = reader;

            waveOut.PlaybackStopped += handler;
            waveOut.Init(reader);

            ctr = cancellationToken.Register(() =>
            {
                try
                {
                    waveOut.Stop();
                }
                catch
                {
                    // Ignore cancellation races.
                }
            });

            Log?.Invoke($"[Audio] Playing OpenAI TTS ({DescribeFormat(contentType, audioBytes)}; {reader.TotalTime.TotalSeconds:0.0}s).");
            waveOut.Play();
            await tcs.Task;
        }
        finally
        {
            StopPlaybackInternal();
            _playLock.Release();
        }
    }

    public void StopPlayback()
    {
        if (_disposed)
        {
            return;
        }

        StopPlaybackInternal();
    }

    private static WaveStream CreateReader(byte[] audioBytes, string? contentType)
    {
        var formatHint = (contentType ?? string.Empty).ToLowerInvariant();
        var isWav = formatHint.Contains("wav") || LooksLikeWav(audioBytes);
        var isMp3 = formatHint.Contains("mpeg") || formatHint.Contains("mp3") || LooksLikeMp3(audioBytes);

        if (isWav)
        {
            try
            {
                return new WaveFileReader(new MemoryStream(audioBytes, writable: false));
            }
            catch when (isMp3)
            {
                // Fallback below.
            }
        }

        if (isMp3 || !isWav)
        {
            return new Mp3FileReader(new MemoryStream(audioBytes, writable: false));
        }

        return new WaveFileReader(new MemoryStream(audioBytes, writable: false));
    }

    private static bool LooksLikeWav(byte[] bytes)
    {
        return bytes.Length > 12 &&
               bytes[0] == (byte)'R' &&
               bytes[1] == (byte)'I' &&
               bytes[2] == (byte)'F' &&
               bytes[3] == (byte)'F' &&
               bytes[8] == (byte)'W' &&
               bytes[9] == (byte)'A' &&
               bytes[10] == (byte)'V' &&
               bytes[11] == (byte)'E';
    }

    private static bool LooksLikeMp3(byte[] bytes)
    {
        if (bytes.Length < 3)
        {
            return false;
        }

        if (bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3')
        {
            return true;
        }

        return bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
    }

    private static string DescribeFormat(string? contentType, byte[] bytes)
    {
        if (LooksLikeWav(bytes))
        {
            return "wav";
        }

        if (LooksLikeMp3(bytes))
        {
            return "mp3";
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            return contentType;
        }

        return "unknown";
    }

    private void StopPlaybackInternal()
    {
        try
        {
            _waveOut?.Stop();
        }
        catch
        {
            // Ignore stop races.
        }

        _waveOut?.Dispose();
        _waveOut = null;

        _currentReader?.Dispose();
        _currentReader = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPlaybackInternal();
        _playLock.Dispose();
    }
}
