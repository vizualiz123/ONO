using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AvatarDesktop.Models;
using NAudio.Wave;

namespace AvatarDesktop.Services;

public sealed class OpenAiRealtimeVoiceClient : IDisposable
{
    private const int AudioChunkMicCooldownMs = 250;
    private const int ResponseDoneMicCooldownMs = 500;
    private const int PlaybackTailPaddingMs = 220;
    private const int BargeInPostInterruptMicCooldownMs = 120;
    private const int BargeInRetryCooldownMs = 600;
    private static readonly TimeSpan PlaybackTailThreshold = TimeSpan.FromMilliseconds(120);
    private const double BargeInPeakThreshold = 0.28;
    private const double BargeInRmsThreshold = 0.06;

    private enum SessionUpdateMode
    {
        Modern,
        LegacyFallback
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveLoopTask;
    private WaveInEvent? _mic;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;
    private bool _disposed;
    private bool _assistantSpeaking;
    private bool _userSpeaking;
    private bool _micEnabled;
    private bool _responseInFlight;
    private bool _turnHasEligibleUserSpeech;
    private bool _ignoredEchoSpeechLogged;
    private string _assistantTranscript = string.Empty;
    private ChatGptVoiceConfig? _currentConfig;
    private SessionUpdateMode _sessionUpdateMode = SessionUpdateMode.Modern;
    private bool _sessionConfigured;
    private DateTime _suppressMicUntilUtc = DateTime.MinValue;
    private DateTime _lastResponseCreateUtc = DateTime.MinValue;
    private bool _echoGuardLogged;
    private bool _dropAssistantAudioUntilResponseDone;
    private bool _bargeInCancelRequested;
    private DateTime _bargeInRetryAllowedUtc = DateTime.MinValue;
    private bool _bargeInLogged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public bool IsMicEnabled => _micEnabled;

    public event Action<string>? Log;
    public event Action<bool, string>? ConnectionStatusChanged;
    public event Action<bool>? UserSpeakingChanged;
    public event Action<bool>? AssistantSpeakingChanged;
    public event Action<string>? AssistantTranscriptChanged;
    public event Action<string>? AssistantTranscriptFinalized;

    public async Task ConnectAsync(ChatGptVoiceConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsConnected)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("API key is empty.");
        }

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _assistantTranscript = string.Empty;
        _currentConfig = config;
        _sessionUpdateMode = SessionUpdateMode.Modern;
        _sessionConfigured = false;
        _responseInFlight = false;
        _turnHasEligibleUserSpeech = false;
        _ignoredEchoSpeechLogged = false;
        _lastResponseCreateUtc = DateTime.MinValue;
        _suppressMicUntilUtc = DateTime.MinValue;
        _dropAssistantAudioUntilResponseDone = false;
        _bargeInCancelRequested = false;
        _bargeInRetryAllowedUtc = DateTime.MinValue;
        _bargeInLogged = false;
        SetAssistantSpeaking(false);
        SetUserSpeaking(false);

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {config.ApiKey.Trim()}");

        var uri = BuildRealtimeUri(config);
        Log?.Invoke($"[Realtime] Connecting to {uri} ...");
        await ws.ConnectAsync(uri, _sessionCts.Token);
        _ws = ws;

        EnsurePlayback();
        EnsureMicrophone();

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token));
        await SendSessionUpdateAsync(config, _sessionCts.Token);

        ConnectionStatusChanged?.Invoke(true, "connected");
        Log?.Invoke("[Realtime] Connected.");
    }

    public async Task DisconnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        _micEnabled = false;
        StopMicrophoneCapture();
        StopPlayback();

        var ws = _ws;
        _ws = null;

        try
        {
            _sessionCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation races.
        }

        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", CancellationToken.None);
                }
            }
            catch
            {
                // Ignore close exceptions.
            }

            ws.Dispose();
        }

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch
            {
                // Ignore loop exceptions during shutdown.
            }
            _receiveLoopTask = null;
        }

        _sessionCts?.Dispose();
        _sessionCts = null;

        SetAssistantSpeaking(false);
        SetUserSpeaking(false);
        ConnectionStatusChanged?.Invoke(false, "disconnected");
        Log?.Invoke("[Realtime] Disconnected.");
    }

    public void StartMicrophoneStreaming()
    {
        ThrowIfDisposed();
        if (!IsConnected)
        {
            throw new InvalidOperationException("Realtime is not connected.");
        }

        EnsureMicrophone();
        if (_micEnabled)
        {
            return;
        }

        _micEnabled = true;
        _mic?.StartRecording();
        Log?.Invoke("[Realtime] Microphone streaming started.");
    }

    public void StopMicrophoneStreaming()
    {
        if (_disposed)
        {
            return;
        }

        if (!_micEnabled)
        {
            return;
        }

        _micEnabled = false;
        StopMicrophoneCapture();
        Log?.Invoke("[Realtime] Microphone streaming stopped.");
        SetUserSpeaking(false);
    }

    private static Uri BuildRealtimeUri(ChatGptVoiceConfig config)
    {
        var baseUrl = ChatGptVoiceConfigService.NormalizeBaseUrl(config.BaseUrl);
        var realtimeBase = baseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
                                  .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);
        return new Uri($"{realtimeBase}/realtime?model={Uri.EscapeDataString(config.RealtimeModel)}", UriKind.Absolute);
    }

    private async Task SendSessionUpdateAsync(ChatGptVoiceConfig config, CancellationToken ct)
    {
        object sessionUpdate = _sessionUpdateMode switch
        {
            SessionUpdateMode.Modern => new
            {
                type = "session.update",
                session = new
                {
                    type = "realtime",
                    model = config.RealtimeModel,
                    instructions = "You are a desktop avatar voice assistant. Speak naturally with complete sentences. Do not over-shorten replies; answer fully and clearly.",
                    voice = config.RealtimeVoice,
                    output_modalities = new[] { "audio", "text" },
                    audio = new
                    {
                        input = new
                        {
                            format = new { type = "audio/pcm", rate = 24000 },
                            turn_detection = new
                            {
                                type = "semantic_vad",
                                create_response = false,
                                interrupt_response = true
                            }
                        },
                        output = new
                        {
                            format = new { type = "audio/pcm", rate = 24000 },
                            voice = config.RealtimeVoice
                        }
                    },
                    input_audio_transcription = new
                    {
                        model = config.TranscriptionModel
                    }
                }
            },
            _ => new
            {
                type = "session.update",
                session = new
                {
                    instructions = "You are a desktop avatar voice assistant. Speak naturally with complete sentences. Do not over-shorten replies; answer fully and clearly.",
                    voice = config.RealtimeVoice,
                    modalities = new[] { "audio", "text" },
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
                    turn_detection = new
                    {
                        type = "server_vad",
                        create_response = false,
                        interrupt_response = true
                    },
                    input_audio_transcription = new
                    {
                        model = config.TranscriptionModel
                    }
                }
            }
        };

        await SendJsonAsync(sessionUpdate, ct);
        Log?.Invoke($"[Realtime] session.update sent ({_sessionUpdateMode}, model={config.RealtimeModel}, voice={config.RealtimeVoice}).");
    }

    private void EnsureMicrophone()
    {
        if (_mic is not null)
        {
            return;
        }

        _mic = new WaveInEvent
        {
            DeviceNumber = 0,
            BufferMilliseconds = 80,
            NumberOfBuffers = 4,
            WaveFormat = new WaveFormat(24000, 16, 1)
        };
        _mic.DataAvailable += Mic_DataAvailable;
        _mic.RecordingStopped += Mic_RecordingStopped;
    }

    private void StopMicrophoneCapture()
    {
        if (_mic is null)
        {
            return;
        }

        try
        {
            _mic.StopRecording();
        }
        catch
        {
            // Ignore stop races.
        }
    }

    private void EnsurePlayback()
    {
        if (_waveOut is not null && _playbackBuffer is not null)
        {
            return;
        }

        _playbackBuffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(8),
            DiscardOnBufferOverflow = true,
            // Keep the output device alive between chunks; otherwise WaveOut can stop
            // before the first assistant audio delta arrives.
            ReadFully = true
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();
        Log?.Invoke("[Realtime] Audio playback initialized (PCM16 24kHz).");
    }

    private void StopPlayback()
    {
        _playbackBuffer?.ClearBuffer();
    }

    private async void Mic_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || !_micEnabled || !IsConnected || e.BytesRecorded <= 0)
        {
            return;
        }

        var hasPlaybackTail = HasAudiblePlaybackTail(out var playbackTail);
        var inCooldown = DateTime.UtcNow < _suppressMicUntilUtc;
        var suppressForAssistantAudio = _assistantSpeaking || hasPlaybackTail;

        if (suppressForAssistantAudio &&
            DateTime.UtcNow >= _bargeInRetryAllowedUtc &&
            LooksLikeUserBargeInVoice(e.Buffer, e.BytesRecorded, out var peak, out var rms))
        {
            TriggerBargeInInterrupt(peak, rms);
            return;
        }

        // Simple echo guard: without AEC the assistant voice can be picked up by the
        // microphone and fed back into the model, causing self-replies/repetition.
        if (suppressForAssistantAudio || inCooldown)
        {
            // Important: do not extend cooldown on every suppressed mic chunk,
            // otherwise the cooldown can perpetually self-extend and "freeze" live dialog.
            if (suppressForAssistantAudio)
            {
                ExtendMicSuppressionFromPlaybackTail(AudioChunkMicCooldownMs);
            }
            if (!_echoGuardLogged)
            {
                _echoGuardLogged = true;
                if (hasPlaybackTail)
                {
                    Log?.Invoke($"[Realtime] Echo guard active: suppressing mic while playback tail drains ({playbackTail.TotalMilliseconds:0}ms buffered).");
                }
                else if (_assistantSpeaking)
                {
                    Log?.Invoke("[Realtime] Echo guard active: mic audio is temporarily suppressed while assistant is speaking.");
                }
                else
                {
                    Log?.Invoke("[Realtime] Echo guard cooldown active: waiting briefly before re-enabling mic capture.");
                }
            }
            return;
        }
        _echoGuardLogged = false;
        _bargeInLogged = false;

        try
        {
            var chunk = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
            await SendJsonAsync(new
            {
                type = "input_audio_buffer.append",
                audio = chunk
            }, _sessionCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Realtime] Mic send error: {ex.Message}");
        }
    }

    private void Mic_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log?.Invoke($"[Realtime] Microphone error: {e.Exception.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws is not null && _ws.State == WebSocketState.Open)
            {
                var message = await ReceiveTextMessageAsync(_ws, ct);
                if (message is null)
                {
                    break;
                }

                HandleRealtimeEvent(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on disconnect.
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Realtime] Receive loop error: {ex.Message}");
            ConnectionStatusChanged?.Invoke(false, $"Realtime error: {ex.Message}");
        }
    }

    private void HandleRealtimeEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            switch (type)
            {
                case "session.created":
                    Log?.Invoke($"[Realtime] {type}");
                    break;

                case "session.updated":
                    _sessionConfigured = true;
                    Log?.Invoke($"[Realtime] {type}{DescribeEffectiveSessionVoice(root)}");
                    break;

                case "input_audio_buffer.speech_started":
                    var hasPlaybackTail = HasAudiblePlaybackTail(out var tail);
                    var inCooldown = DateTime.UtcNow < _suppressMicUntilUtc;
                    var suppressForAssistantAudio = _assistantSpeaking || hasPlaybackTail;
                    if ((_assistantSpeaking || _responseInFlight) && !_bargeInCancelRequested)
                    {
                        TriggerBargeInInterrupt();
                    }
                    if (suppressForAssistantAudio || inCooldown)
                    {
                        if (suppressForAssistantAudio)
                        {
                            ExtendMicSuppressionFromPlaybackTail(AudioChunkMicCooldownMs);
                        }
                        _turnHasEligibleUserSpeech = false;
                        if (!_ignoredEchoSpeechLogged)
                        {
                            _ignoredEchoSpeechLogged = true;
                            if (hasPlaybackTail)
                            {
                                Log?.Invoke($"[Realtime] Ignoring speech_started during playback tail ({tail.TotalMilliseconds:0}ms buffered; likely echo/noise).");
                            }
                            else
                            {
                                Log?.Invoke(inCooldown
                                    ? "[Realtime] Ignoring speech_started during short echo guard cooldown."
                                    : "[Realtime] Ignoring speech_started during assistant audio (likely echo/noise).");
                            }
                        }
                        break;
                    }

                    _ignoredEchoSpeechLogged = false;
                    _turnHasEligibleUserSpeech = true;
                    SetUserSpeaking(true);
                    SetAssistantSpeaking(false);
                    StopPlayback();
                    Log?.Invoke("[Realtime] User speech started.");
                    break;

                case "input_audio_buffer.speech_stopped":
                    SetUserSpeaking(false);
                    Log?.Invoke("[Realtime] User speech stopped.");
                    break;

                case "input_audio_buffer.committed":
                    Log?.Invoke("[Realtime] Audio buffer committed.");
                    if (_turnHasEligibleUserSpeech)
                    {
                        _turnHasEligibleUserSpeech = false;
                        TryCreateResponseForCommittedAudio();
                    }
                    else
                    {
                        _ = ClearInputAudioBufferAsync();
                    }
                    break;

                case "response.created":
                    _responseInFlight = true;
                    _dropAssistantAudioUntilResponseDone = false;
                    _bargeInCancelRequested = false;
                    _bargeInLogged = false;
                    _assistantTranscript = string.Empty;
                    AssistantTranscriptChanged?.Invoke(_assistantTranscript);
                    Log?.Invoke("[Realtime] Response created.");
                    break;

                case "response.output_audio.delta":
                case "response.audio.delta":
                    if (_dropAssistantAudioUntilResponseDone)
                    {
                        break;
                    }
                    if (root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String)
                    {
                        var b64 = deltaEl.GetString();
                        if (!string.IsNullOrWhiteSpace(b64))
                        {
                            var bytes = Convert.FromBase64String(b64);
                            if (_playbackBuffer is not null)
                            {
                                _playbackBuffer.AddSamples(bytes, 0, bytes.Length);
                            }
                            ExtendMicSuppressionFromPlaybackTail(AudioChunkMicCooldownMs);
                            if (_waveOut is not null && _waveOut.PlaybackState != PlaybackState.Playing)
                            {
                                _waveOut.Play();
                                Log?.Invoke("[Realtime] Audio playback resumed.");
                            }
                            SetAssistantSpeaking(true);
                        }
                    }
                    break;

                case "response.output_audio.done":
                case "response.audio.done":
                    ExtendMicSuppressionFromPlaybackTail(ResponseDoneMicCooldownMs);
                    SetAssistantSpeaking(false);
                    Log?.Invoke("[Realtime] Assistant audio done.");
                    break;

                case "response.output_audio_transcript.delta":
                    if (root.TryGetProperty("delta", out var tDeltaEl) && tDeltaEl.ValueKind == JsonValueKind.String)
                    {
                        _assistantTranscript += tDeltaEl.GetString() ?? string.Empty;
                        AssistantTranscriptChanged?.Invoke(_assistantTranscript);
                    }
                    break;

                case "response.output_audio_transcript.done":
                    if (root.TryGetProperty("transcript", out var transcriptEl) && transcriptEl.ValueKind == JsonValueKind.String)
                    {
                        _assistantTranscript = transcriptEl.GetString() ?? _assistantTranscript;
                    }
                    AssistantTranscriptChanged?.Invoke(_assistantTranscript);
                    AssistantTranscriptFinalized?.Invoke(_assistantTranscript);
                    Log?.Invoke($"[Realtime] Assistant transcript done: {TrimForLog(_assistantTranscript)}");
                    break;

                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var txtDeltaEl) && txtDeltaEl.ValueKind == JsonValueKind.String)
                    {
                        _assistantTranscript += txtDeltaEl.GetString() ?? string.Empty;
                        AssistantTranscriptChanged?.Invoke(_assistantTranscript);
                    }
                    break;

                case "response.done":
                    _responseInFlight = false;
                    _dropAssistantAudioUntilResponseDone = false;
                    _bargeInCancelRequested = false;
                    ExtendMicSuppressionFromPlaybackTail(ResponseDoneMicCooldownMs);
                    SetAssistantSpeaking(false);
                    Log?.Invoke("[Realtime] Response done.");
                    _ = ClearInputAudioBufferAsync();
                    break;

                case "error":
                    HandleRealtimeError(root);
                    break;

                default:
                    // Noisy but useful for protocol debugging:
                    // Log?.Invoke($"[Realtime] Event: {type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Realtime] Event parse error: {ex.Message}");
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void TryCreateResponseForCommittedAudio()
    {
        if (_disposed || !IsConnected || !_sessionConfigured)
        {
            return;
        }

        if (_assistantSpeaking)
        {
            Log?.Invoke("[Realtime] Skipping response.create: assistant is still speaking.");
            return;
        }

        if (HasAudiblePlaybackTail(out var playbackTail))
        {
            ExtendMicSuppressionFromPlaybackTail(ResponseDoneMicCooldownMs);
            Log?.Invoke($"[Realtime] Skipping response.create: playback tail still audible ({playbackTail.TotalMilliseconds:0}ms buffered).");
            _ = ClearInputAudioBufferAsync();
            return;
        }

        if (_responseInFlight)
        {
            Log?.Invoke("[Realtime] Skipping response.create: previous response still in flight.");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _suppressMicUntilUtc)
        {
            Log?.Invoke("[Realtime] Skipping response.create: within echo guard cooldown.");
            _ = ClearInputAudioBufferAsync();
            return;
        }

        if ((nowUtc - _lastResponseCreateUtc) < TimeSpan.FromMilliseconds(700))
        {
            Log?.Invoke("[Realtime] Skipping response.create: debounce window.");
            _ = ClearInputAudioBufferAsync();
            return;
        }

        _responseInFlight = true; // optimistic guard until response.created arrives
        _lastResponseCreateUtc = nowUtc;

        var ct = _sessionCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await SendJsonAsync(new { type = "response.create" }, ct);
                Log?.Invoke("[Realtime] response.create sent.");
            }
            catch (OperationCanceledException)
            {
                _responseInFlight = false;
            }
            catch (Exception ex)
            {
                _responseInFlight = false;
                Log?.Invoke($"[Realtime] response.create failed: {ex.Message}");
            }
        });
    }

    private Task ClearInputAudioBufferAsync()
    {
        if (_disposed || !IsConnected)
        {
            return Task.CompletedTask;
        }

        var ct = _sessionCts?.Token ?? CancellationToken.None;
        return Task.Run(async () =>
        {
            try
            {
                await SendJsonAsync(new { type = "input_audio_buffer.clear" }, ct);
            }
            catch
            {
                // Some API variants may reject this; safe to ignore.
            }
        });
    }

    private bool LooksLikeUserBargeInVoice(byte[] buffer, int bytesRecorded, out double peakNorm, out double rmsNorm)
    {
        peakNorm = 0;
        rmsNorm = 0;

        if (bytesRecorded < 4)
        {
            return false;
        }

        long sumSquares = 0;
        var sampleCount = 0;
        var maxAbs = 0;

        for (var i = 0; i + 1 < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var abs = Math.Abs((int)sample);
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }

            sumSquares += (long)sample * sample;
            sampleCount++;
        }

        if (sampleCount == 0)
        {
            return false;
        }

        peakNorm = maxAbs / 32768.0;
        rmsNorm = Math.Sqrt(sumSquares / (double)sampleCount) / 32768.0;
        return peakNorm >= BargeInPeakThreshold || rmsNorm >= BargeInRmsThreshold;
    }

    private void TriggerBargeInInterrupt(double? peakNorm = null, double? rmsNorm = null)
    {
        if (_disposed || !IsConnected)
        {
            return;
        }

        StopPlayback();
        SetAssistantSpeaking(false);
        _dropAssistantAudioUntilResponseDone = true;
        _bargeInCancelRequested = true;
        _bargeInRetryAllowedUtc = DateTime.UtcNow.AddMilliseconds(BargeInRetryCooldownMs);
        _suppressMicUntilUtc = DateTime.UtcNow.AddMilliseconds(BargeInPostInterruptMicCooldownMs);
        _echoGuardLogged = false;

        if (!_bargeInLogged)
        {
            _bargeInLogged = true;
            if (peakNorm.HasValue && rmsNorm.HasValue)
            {
                Log?.Invoke($"[Realtime] Barge-in detected (peak={peakNorm.Value:0.00}, rms={rmsNorm.Value:0.00}) -> stopping assistant speech.");
            }
            else
            {
                Log?.Invoke("[Realtime] Barge-in detected -> stopping assistant speech.");
            }
        }

        var ct = _sessionCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await SendJsonAsync(new { type = "response.cancel" }, ct);
            }
            catch (OperationCanceledException)
            {
                // Ignore shutdown races.
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[Realtime] response.cancel failed: {ex.Message}");
            }

            try
            {
                await SendJsonAsync(new { type = "input_audio_buffer.clear" }, ct);
            }
            catch
            {
                // Some API variants may reject this; safe to ignore.
            }
        });
    }

    private bool HasAudiblePlaybackTail(out TimeSpan bufferedDuration)
    {
        bufferedDuration = TimeSpan.Zero;
        var playbackBuffer = _playbackBuffer;
        if (playbackBuffer is null)
        {
            return false;
        }

        bufferedDuration = playbackBuffer.BufferedDuration;
        return bufferedDuration > PlaybackTailThreshold;
    }

    private void ExtendMicSuppressionFromPlaybackTail(int minimumCooldownMs)
    {
        var nowUtc = DateTime.UtcNow;
        var targetUtc = nowUtc.AddMilliseconds(Math.Max(0, minimumCooldownMs));

        if (HasAudiblePlaybackTail(out var playbackTail))
        {
            var playbackTargetUtc = nowUtc.Add(playbackTail).AddMilliseconds(PlaybackTailPaddingMs);
            if (playbackTargetUtc > targetUtc)
            {
                targetUtc = playbackTargetUtc;
            }
        }

        if (targetUtc > _suppressMicUntilUtc)
        {
            _suppressMicUntilUtc = targetUtc;
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void SetUserSpeaking(bool speaking)
    {
        if (_userSpeaking == speaking)
        {
            return;
        }

        _userSpeaking = speaking;
        UserSpeakingChanged?.Invoke(speaking);
    }

    private void SetAssistantSpeaking(bool speaking)
    {
        if (_assistantSpeaking == speaking)
        {
            return;
        }

        _assistantSpeaking = speaking;
        AssistantSpeakingChanged?.Invoke(speaking);
    }

    private static string ExtractErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            if (err.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                return msgEl.GetString() ?? "Unknown error";
            }
            return err.ToString();
        }

        return root.ToString();
    }

    private static string? ExtractErrorParam(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            if (err.TryGetProperty("param", out var paramEl) && paramEl.ValueKind == JsonValueKind.String)
            {
                return paramEl.GetString();
            }
        }

        return null;
    }

    private static string DescribeEffectiveSessionVoice(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (session.TryGetProperty("audio", out var audio) &&
                audio.ValueKind == JsonValueKind.Object &&
                audio.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("voice", out var nestedVoice) &&
                nestedVoice.ValueKind == JsonValueKind.String)
            {
                var voice = nestedVoice.GetString();
                if (!string.IsNullOrWhiteSpace(voice))
                {
                    return $" (voice={voice})";
                }
            }

            if (session.TryGetProperty("voice", out var voiceEl) && voiceEl.ValueKind == JsonValueKind.String)
            {
                var voice = voiceEl.GetString();
                if (!string.IsNullOrWhiteSpace(voice))
                {
                    return $" (voice={voice})";
                }
            }
        }
        catch
        {
            // Ignore parsing issues in debug helper.
        }

        return string.Empty;
    }

    private void HandleRealtimeError(JsonElement root)
    {
        var errMessage = ExtractErrorMessage(root);
        var errParam = ExtractErrorParam(root);
        Log?.Invoke($"[Realtime] Error: {errMessage}");

        if (ShouldSwitchToLegacySessionUpdate(errParam, errMessage))
        {
            if (_sessionUpdateMode != SessionUpdateMode.LegacyFallback)
            {
                _sessionUpdateMode = SessionUpdateMode.LegacyFallback;
                _sessionConfigured = false;
                Log?.Invoke("[Realtime] Switching session.update to legacy compatibility mode and retrying...");

                var cfg = _currentConfig;
                var ct = _sessionCts?.Token ?? CancellationToken.None;
                if (cfg is not null && !ct.IsCancellationRequested)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SendSessionUpdateAsync(cfg, ct);
                        }
                        catch (Exception ex)
                        {
                            Log?.Invoke($"[Realtime] Legacy session.update retry failed: {ex.Message}");
                        }
                    });
                }

                return;
            }
        }

        ConnectionStatusChanged?.Invoke(false, $"error: {errMessage}");
    }

    private static bool ShouldSwitchToLegacySessionUpdate(string? param, string message)
    {
        if (!string.IsNullOrWhiteSpace(param))
        {
            if (param.Equals("session.type", StringComparison.OrdinalIgnoreCase) ||
                param.Equals("session.output_modalities", StringComparison.OrdinalIgnoreCase) ||
                param.Equals("session.audio", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return message.Contains("Unknown parameter: 'session.type'", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unknown parameter: 'session.output_modalities'", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unknown parameter: 'session.audio'", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimForLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var compact = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 220 ? compact : compact[..220] + "...";
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
        _micEnabled = false;
        _assistantSpeaking = false;
        _userSpeaking = false;

        var ws = _ws;
        _ws = null;
        var sessionCts = _sessionCts;
        _sessionCts = null;
        _receiveLoopTask = null;

        try
        {
            StopMicrophoneCapture();
        }
        catch
        {
            // Ignore shutdown exceptions.
        }

        try
        {
            StopPlayback();
        }
        catch
        {
            // Ignore shutdown exceptions.
        }

        try
        {
            sessionCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation exceptions.
        }

        try
        {
            ws?.Abort();
        }
        catch
        {
            // Ignore websocket abort exceptions.
        }

        try
        {
            ws?.Dispose();
        }
        catch
        {
            // Ignore socket disposal exceptions.
        }

        try
        {
            sessionCts?.Dispose();
        }
        catch
        {
            // Ignore cancellation source disposal exceptions.
        }

        if (_mic is not null)
        {
            _mic.DataAvailable -= Mic_DataAvailable;
            _mic.RecordingStopped -= Mic_RecordingStopped;
            _mic.Dispose();
            _mic = null;
        }

        _waveOut?.Dispose();
        _waveOut = null;
        _playbackBuffer = null;

        _sendLock.Dispose();
    }
}
