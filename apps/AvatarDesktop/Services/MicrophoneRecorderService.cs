using System.IO;
using NAudio.Wave;

namespace AvatarDesktop.Services;

public sealed class MicrophoneRecorderService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _tempFilePath;
    private TaskCompletionSource<byte[]>? _stopTcs;
    private StopMode _stopMode = StopMode.None;
    private bool _disposed;

    public bool IsRecording { get; private set; }

    public event Action<string>? Log;

    public void StartRecording()
    {
        ThrowIfDisposed();
        if (IsRecording)
        {
            return;
        }

        _tempFilePath = Path.Combine(Path.GetTempPath(), $"avatardesk-mic-{Guid.NewGuid():N}.wav");
        _stopTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopMode = StopMode.None;

        try
        {
            _waveIn = CreateWaveIn(prefer16k: true);
        }
        catch
        {
            _waveIn = CreateWaveIn(prefer16k: false);
        }

        _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);
        _waveIn.DataAvailable += WaveIn_DataAvailable;
        _waveIn.RecordingStopped += WaveIn_RecordingStopped;
        _waveIn.StartRecording();

        IsRecording = true;
        Log?.Invoke($"[Mic] Recording started ({_waveIn.WaveFormat.SampleRate}Hz, {_waveIn.WaveFormat.Channels}ch).");
    }

    public Task<byte[]> StopAndGetWavAsync()
    {
        if (!IsRecording || _waveIn is null || _stopTcs is null)
        {
            return Task.FromResult(Array.Empty<byte>());
        }

        _stopMode = StopMode.ReturnAudio;
        _waveIn.StopRecording();
        return _stopTcs.Task;
    }

    public void CancelRecording()
    {
        if (!IsRecording || _waveIn is null)
        {
            return;
        }

        _stopMode = StopMode.Cancel;
        _waveIn.StopRecording();
    }

    private static WaveInEvent CreateWaveIn(bool prefer16k)
    {
        var waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            BufferMilliseconds = 120,
            NumberOfBuffers = 3,
            WaveFormat = prefer16k
                ? new WaveFormat(16000, 16, 1)
                : new WaveFormat(44100, 16, 1)
        };

        return waveIn;
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_writer is null || _disposed)
        {
            return;
        }

        _writer.Write(e.Buffer, 0, e.BytesRecorded);
        _writer.Flush();
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        var writer = _writer;
        var waveIn = _waveIn;
        var temp = _tempFilePath;
        var tcs = _stopTcs;
        var stopMode = _stopMode;

        _writer = null;
        _waveIn = null;
        _tempFilePath = null;
        _stopTcs = null;
        _stopMode = StopMode.None;
        IsRecording = false;

        try
        {
            if (waveIn is not null)
            {
                waveIn.DataAvailable -= WaveIn_DataAvailable;
                waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                waveIn.Dispose();
            }
        }
        catch
        {
            // Ignore disposal exceptions.
        }

        try
        {
            writer?.Dispose();
        }
        catch
        {
            // Ignore disposal exceptions.
        }

        if (e.Exception is not null)
        {
            Log?.Invoke($"[Mic] Recording error: {e.Exception.Message}");
            tcs?.TrySetException(e.Exception);
            CleanupTemp(temp);
            return;
        }

        if (stopMode == StopMode.Cancel)
        {
            Log?.Invoke("[Mic] Recording cancelled.");
            tcs?.TrySetCanceled();
            CleanupTemp(temp);
            return;
        }

        try
        {
            var bytes = temp is not null && File.Exists(temp) ? File.ReadAllBytes(temp) : Array.Empty<byte>();
            Log?.Invoke($"[Mic] Recording stopped. Captured {bytes.Length / 1024.0:0.0} KB.");
            tcs?.TrySetResult(bytes);
        }
        catch (Exception ex)
        {
            tcs?.TrySetException(ex);
        }
        finally
        {
            CleanupTemp(temp);
        }
    }

    private static void CleanupTemp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            CancelRecording();
        }
        catch
        {
            // Ignore on shutdown.
        }

        _writer?.Dispose();
        _writer = null;
        _waveIn?.Dispose();
        _waveIn = null;
    }

    private enum StopMode
    {
        None,
        ReturnAudio,
        Cancel
    }
}
