using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AvatarDesktop.Models;
using AvatarDesktop.Services;

namespace AvatarDesktop;

public partial class VoiceChatWindow : Window
{
    private const string BakedRealtimeModel = "gpt-realtime";
    private const string DefaultRealtimeVoice = "marin";
    private const string DefaultTtsVoice = "marin";

    // ChatGPT product voice names (UI labels) mapped to available Realtime API voice IDs.
    // These are UI aliases so users can choose familiar ChatGPT names while the app still
    // sends valid API voice IDs.
    private static readonly (string ChatGptName, string ApiVoiceId)[] ChatGptRealtimeVoiceAliases =
    {
        ("Arbor", "marin"),
        ("Breeze", "alloy"),
        ("Cove", "cedar"),
        ("Ember", "coral"),
        ("Juniper", "shimmer"),
        ("Maple", "verse"),
        ("Sol", "sage"),
        ("Spruce", "ash"),
        ("Vale", "ballad")
    };

    // OpenAI Realtime built-in voices (text+audio realtime).
    private static readonly string[] RealtimeVoiceOptions =
    {
        "alloy",
        "ash",
        "ballad",
        "coral",
        "echo",
        "sage",
        "shimmer",
        "verse",
        "marin",
        "cedar"
    };

    // OpenAI TTS built-in voices (gpt-4o-mini-tts / gpt-4o-tts).
    private static readonly string[] TtsVoiceOptions =
    {
        "alloy",
        "ash",
        "ballad",
        "coral",
        "echo",
        "fable",
        "nova",
        "onyx",
        "sage",
        "shimmer",
        "verse",
        "marin",
        "cedar"
    };

    private readonly ObservableCollection<string> _logs = new();
    private readonly ChatGptVoiceConfigService _configService;
    private readonly IOpenAiChatClient _openAiClient;
    private readonly IOpenAiAudioClient _openAiAudioClient;
    private readonly OpenAiRealtimeVoiceClient _realtimeClient;
    private readonly MicrophoneRecorderService _microphoneRecorder;
    private readonly WaveAudioPlaybackService _audioPlayback;
    private readonly Func<ChatGptVoiceConfig, string, CancellationToken, Task<ChatRequestResult>> _sendPromptAsync;
    private readonly Func<ChatRequestResult, CancellationToken, Task> _applyAvatarResultAsync;
    private readonly Action<string> _mainLog;
    private readonly Action<AvatarState> _avatarStateHint;
    private readonly Action<string> _assistantTextSink;

    private CancellationTokenSource? _turnCts;
    private bool _isProcessingTurn;
    private bool _isSyncingVoicePickers;
    private bool _isApplyingLiveVoiceChange;

    public VoiceChatWindow(
        IOpenAiChatClient openAiClient,
        Func<ChatGptVoiceConfig, string, CancellationToken, Task<ChatRequestResult>> sendPromptAsync,
        Func<ChatRequestResult, CancellationToken, Task> applyAvatarResultAsync,
        Action<string> mainLog,
        Action<AvatarState> avatarStateHint,
        Action<string> assistantTextSink)
    {
        InitializeComponent();

        _openAiClient = openAiClient;
        _openAiAudioClient = new OpenAiAudioClient();
        _realtimeClient = new OpenAiRealtimeVoiceClient();
        _microphoneRecorder = new MicrophoneRecorderService();
        _audioPlayback = new WaveAudioPlaybackService();
        _sendPromptAsync = sendPromptAsync;
        _applyAvatarResultAsync = applyAvatarResultAsync;
        _mainLog = mainLog;
        _avatarStateHint = avatarStateHint;
        _assistantTextSink = assistantTextSink;

        VoiceLogsListBox.ItemsSource = _logs;
        _configService = new ChatGptVoiceConfigService(Path.Combine(AppContext.BaseDirectory, "chatgpt_voice_settings.json"));

        _microphoneRecorder.Log += AddLog;
        _audioPlayback.Log += AddLog;
        _realtimeClient.Log += AddLog;
        _realtimeClient.ConnectionStatusChanged += RealtimeClient_ConnectionStatusChanged;
        _realtimeClient.UserSpeakingChanged += RealtimeClient_UserSpeakingChanged;
        _realtimeClient.AssistantSpeakingChanged += RealtimeClient_AssistantSpeakingChanged;
        _realtimeClient.AssistantTranscriptChanged += RealtimeClient_AssistantTranscriptChanged;
        _realtimeClient.AssistantTranscriptFinalized += RealtimeClient_AssistantTranscriptFinalized;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeVoicePickers();
        WireUnifiedVoiceSync();
        var config = _configService.Load(out var warning);
        ApplyConfigToUi(config);
        SyncTtsVoiceFromRealtimePicker();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            AddLog($"[Config] {warning}");
        }

        UpdateRecordButtons();
        UpdateRealtimeButtons();
    }

    private void WireUnifiedVoiceSync()
    {
        RealtimeVoiceTextBox.SelectionChanged -= RealtimeVoiceTextBox_SelectionChanged;
        RealtimeVoiceTextBox.SelectionChanged += RealtimeVoiceTextBox_SelectionChanged;

        RealtimeVoiceTextBox.DropDownClosed -= RealtimeVoiceTextBox_DropDownClosed;
        RealtimeVoiceTextBox.DropDownClosed += RealtimeVoiceTextBox_DropDownClosed;

        RealtimeVoiceTextBox.LostKeyboardFocus -= RealtimeVoiceTextBox_LostKeyboardFocus;
        RealtimeVoiceTextBox.LostKeyboardFocus += RealtimeVoiceTextBox_LostKeyboardFocus;
    }

    private void RealtimeVoiceTextBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SyncTtsVoiceFromRealtimePicker();
        if (!RealtimeVoiceTextBox.IsDropDownOpen)
        {
            TryApplyLiveVoiceChangeAsync();
        }
    }

    private void RealtimeVoiceTextBox_DropDownClosed(object? sender, EventArgs e)
    {
        SyncTtsVoiceFromRealtimePicker();
        TryApplyLiveVoiceChangeAsync();
    }

    private void RealtimeVoiceTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SyncTtsVoiceFromRealtimePicker();
        TryApplyLiveVoiceChangeAsync();
    }

    private void SyncTtsVoiceFromRealtimePicker()
    {
        if (_isSyncingVoicePickers)
        {
            return;
        }

        _isSyncingVoicePickers = true;
        try
        {
            var unifiedVoice = ResolveRealtimeApiVoiceFromUi(RealtimeVoiceTextBox.Text);
            var uiLabel = GetRealtimeVoiceUiLabel(unifiedVoice);
            if (!string.Equals(RealtimeVoiceTextBox.Text, uiLabel, StringComparison.Ordinal))
            {
                RealtimeVoiceTextBox.Text = uiLabel;
            }

            if (!string.Equals(TtsVoiceTextBox.Text, unifiedVoice, StringComparison.Ordinal))
            {
                TtsVoiceTextBox.Text = uiLabel;
            }
        }
        finally
        {
            _isSyncingVoicePickers = false;
        }
    }

    private void InitializeVoicePickers()
    {
        var uiLabels = GetRealtimeVoiceUiLabels().ToArray();
        PopulateVoicePicker(RealtimeVoiceTextBox, uiLabels);
        PopulateVoicePicker(TtsVoiceTextBox, uiLabels);
    }

    private static void PopulateVoicePicker(System.Windows.Controls.ComboBox picker, IEnumerable<string> voices)
    {
        if (picker.Items.Count > 0)
        {
            return;
        }

        foreach (var voice in voices)
        {
            picker.Items.Add(voice);
        }
    }

    private async void TryApplyLiveVoiceChangeAsync()
    {
        if (_isSyncingVoicePickers || _isApplyingLiveVoiceChange || _isProcessingTurn || !_realtimeClient.IsConnected)
        {
            return;
        }

        _isApplyingLiveVoiceChange = true;

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            _isApplyingLiveVoiceChange = false;
            AddLog($"[Config] Voice change ignored: {error}");
            return;
        }

        config = ApplyBakedRealtimeSettings(config);
        RealtimeConnectButton.IsEnabled = false;
        UpdateRealtimeStatus(true, $"Realtime: switching voice to {GetRealtimeVoiceUiLabel(config.RealtimeVoice)}...");

        try
        {
            _realtimeClient.StopMicrophoneStreaming();
            await _realtimeClient.DisconnectAsync();
            await _realtimeClient.ConnectAsync(config);
            _realtimeClient.StartMicrophoneStreaming();
            SaveConfigSafe(config);
            UpdateRealtimeStatus(true, $"Realtime: voice changed to {GetRealtimeVoiceUiLabel(config.RealtimeVoice)}");
            AddLog($"[Realtime] Voice switched to {GetRealtimeVoiceUiLabel(config.RealtimeVoice)} ({config.RealtimeVoice}).");
        }
        catch (Exception ex)
        {
            AddLog($"[Realtime] Voice switch failed: {ex.Message}");
            UpdateRealtimeStatus(false, $"Realtime: voice switch error ({ex.Message})");
        }
        finally
        {
            _isApplyingLiveVoiceChange = false;
            UpdateRealtimeButtons();
        }
    }

    private static ChatGptVoiceConfig ApplyBakedRealtimeSettings(ChatGptVoiceConfig config)
    {
        config.RealtimeModel = BakedRealtimeModel;
        config.RealtimeVoice = NormalizeVoice(config.RealtimeVoice, RealtimeVoiceOptions, DefaultRealtimeVoice);
        // Keep one unified "ChatGPT voice" across realtime and fallback TTS paths.
        config.TtsVoice = config.RealtimeVoice;
        if (string.IsNullOrWhiteSpace(config.TranscriptionModel))
        {
            config.TranscriptionModel = "gpt-4o-mini-transcribe";
        }

        return config;
    }

    private static string NormalizeVoice(string? rawVoice, IEnumerable<string> allowedVoices, string fallback)
    {
        var candidate = (rawVoice ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        foreach (var voice in allowedVoices)
        {
            if (voice.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return voice;
            }
        }

        return fallback;
    }

    private static IEnumerable<string> GetRealtimeVoiceUiLabels()
    {
        foreach (var alias in ChatGptRealtimeVoiceAliases)
        {
            yield return alias.ChatGptName;
        }

        // Keep direct API IDs available as fallback/advanced options.
        foreach (var apiVoice in RealtimeVoiceOptions)
        {
            if (IsRealtimeAliasDefinedForApiVoice(apiVoice))
            {
                continue;
            }

            yield return apiVoice;
        }
    }

    private static bool IsRealtimeAliasDefinedForApiVoice(string apiVoice)
    {
        foreach (var alias in ChatGptRealtimeVoiceAliases)
        {
            if (alias.ApiVoiceId.Equals(apiVoice, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveRealtimeApiVoiceFromUi(string? rawVoiceSelection)
    {
        var candidate = (rawVoiceSelection ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return DefaultRealtimeVoice;
        }

        foreach (var alias in ChatGptRealtimeVoiceAliases)
        {
            if (alias.ChatGptName.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return alias.ApiVoiceId;
            }
        }

        return NormalizeVoice(candidate, RealtimeVoiceOptions, DefaultRealtimeVoice);
    }

    private static string GetRealtimeVoiceUiLabel(string? apiVoiceId)
    {
        var normalized = NormalizeVoice(apiVoiceId, RealtimeVoiceOptions, DefaultRealtimeVoice);
        foreach (var alias in ChatGptRealtimeVoiceAliases)
        {
            if (alias.ApiVoiceId.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return alias.ChatGptName;
            }
        }

        return normalized;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _microphoneRecorder.Log -= AddLog;
            _audioPlayback.Log -= AddLog;
            _realtimeClient.Log -= AddLog;
            _realtimeClient.ConnectionStatusChanged -= RealtimeClient_ConnectionStatusChanged;
            _realtimeClient.UserSpeakingChanged -= RealtimeClient_UserSpeakingChanged;
            _realtimeClient.AssistantSpeakingChanged -= RealtimeClient_AssistantSpeakingChanged;
            _realtimeClient.AssistantTranscriptChanged -= RealtimeClient_AssistantTranscriptChanged;
            _realtimeClient.AssistantTranscriptFinalized -= RealtimeClient_AssistantTranscriptFinalized;

            _turnCts?.Cancel();
            _microphoneRecorder.CancelRecording();
            _audioPlayback.StopPlayback();
            _realtimeClient.StopMicrophoneStreaming();
        }
        catch
        {
            // Ignore shutdown races.
        }

        _turnCts?.Dispose();
        _microphoneRecorder.Dispose();
        _audioPlayback.Dispose();
        _realtimeClient.Dispose();
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingTurn)
        {
            return;
        }

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            AddLog($"[Config] {error}");
            UpdateStatus(false, $"Status: {error}");
            return;
        }

        SaveConfigSafe(config);
        SetControlsEnabled(false);
        AddLog("[API] Checking /models...");
        try
        {
            var result = await _openAiClient.CheckHealthAsync(config);
            UpdateStatus(result.Success, result.Success ? "Status: OpenAI API connected" : $"Status: {result.Message}");
            AddLog(result.Success ? "[API] Connected." : $"[API] Failed: {result.Message}");
        }
        finally
        {
            SetControlsEnabled(true);
            UpdateRecordButtons();
        }
    }

    private async void RealtimeConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingTurn)
        {
            return;
        }

        if (_realtimeClient.IsConnected)
        {
            RealtimeConnectButton.IsEnabled = false;
            try
            {
                _realtimeClient.StopMicrophoneStreaming();
                await _realtimeClient.DisconnectAsync();
                _avatarStateHint(AvatarState.Idle);
                UpdateRealtimeStatus(false, "Realtime: stopped");
                AddLog("[Realtime] Live chat stopped.");
            }
            catch (Exception ex)
            {
                AddLog($"[Realtime] Stop failed: {ex.Message}");
                UpdateRealtimeStatus(false, $"Realtime: stop error ({ex.Message})");
            }
            finally
            {
                UpdateRealtimeButtons();
            }

            return;
        }

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            AddLog($"[Config] {error}");
            UpdateStatus(false, $"Status: {error}");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            AddLog("[Config] API key is empty.");
            UpdateRealtimeStatus(false, "Realtime: API key is empty");
            return;
        }

        config = ApplyBakedRealtimeSettings(config);
        SaveConfigSafe(config);
        RealtimeConnectButton.IsEnabled = false;
        try
        {
            await _realtimeClient.ConnectAsync(config);
            _realtimeClient.StartMicrophoneStreaming();
            UpdateRealtimeStatus(true, "Realtime: live chat started");
            AddLog($"[Realtime] Live chat started (voice={config.RealtimeVoice}, model={config.RealtimeModel}).");
        }
        catch (Exception ex)
        {
            AddLog($"[Realtime] Start failed: {ex.Message}");
            if (_realtimeClient.IsConnected)
            {
                try
                {
                    await _realtimeClient.DisconnectAsync();
                }
                catch
                {
                    // Ignore cleanup failure after start error.
                }
            }
            UpdateRealtimeStatus(false, $"Realtime: start error ({ex.Message})");
        }
        finally
        {
            UpdateRealtimeButtons();
        }
    }

    private async void RealtimeDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _realtimeClient.DisconnectAsync();
            _avatarStateHint(AvatarState.Idle);
            UpdateRealtimeStatus(false, "Realtime: disconnected");
        }
        catch (Exception ex)
        {
            AddLog($"[Realtime] Disconnect failed: {ex.Message}");
        }
        finally
        {
            UpdateRealtimeButtons();
        }
    }

    private void RealtimeMicOnButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _realtimeClient.StartMicrophoneStreaming();
            UpdateRealtimeStatus(true, "Realtime: live dialog mic on");
        }
        catch (Exception ex)
        {
            AddLog($"[Realtime] Mic on failed: {ex.Message}");
            UpdateRealtimeStatus(false, $"Realtime: mic error ({ex.Message})");
        }
        finally
        {
            UpdateRealtimeButtons();
        }
    }

    private void RealtimeMicOffButton_Click(object sender, RoutedEventArgs e)
    {
        _realtimeClient.StopMicrophoneStreaming();
        _avatarStateHint(AvatarState.Idle);
        UpdateRealtimeStatus(_realtimeClient.IsConnected, _realtimeClient.IsConnected ? "Realtime: connected (mic off)" : "Realtime: disconnected");
        UpdateRealtimeButtons();
    }

    private void StartRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingTurn || _microphoneRecorder.IsRecording)
        {
            return;
        }

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            AddLog($"[Config] {error}");
            UpdateStatus(false, $"Status: {error}");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            AddLog("[Config] API key is empty.");
            UpdateStatus(false, "Status: API key is empty");
            return;
        }

        SaveConfigSafe(config);

        try
        {
            _audioPlayback.StopPlayback();
            _microphoneRecorder.StartRecording();
            UpdateStatus(true, "Status: recording microphone...");
            _mainLog("[VoiceChat] Microphone recording started.");
        }
        catch (Exception ex)
        {
            AddLog($"[Mic] Start failed: {ex.Message}");
            UpdateStatus(false, $"Status: mic error ({ex.Message})");
        }
        finally
        {
            UpdateRecordButtons();
        }
    }

    private async void StopAndSendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessingTurn || !_microphoneRecorder.IsRecording)
        {
            return;
        }

        byte[] wavBytes;
        try
        {
            UpdateStatus(true, "Status: stopping recording...");
            UpdateRecordButtons();
            wavBytes = await _microphoneRecorder.StopAndGetWavAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("[Mic] Recording canceled.");
            UpdateStatus(true, "Status: recording canceled");
            UpdateRecordButtons();
            return;
        }
        catch (Exception ex)
        {
            AddLog($"[Mic] Stop failed: {ex.Message}");
            UpdateStatus(false, $"Status: mic error ({ex.Message})");
            UpdateRecordButtons();
            return;
        }

        UpdateRecordButtons();
        await ProcessRecordedAudioAsync(wavBytes);
    }

    private void CancelRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_microphoneRecorder.IsRecording)
        {
            return;
        }

        _microphoneRecorder.CancelRecording();
        UpdateStatus(true, "Status: recording canceled");
        UpdateRecordButtons();
    }

    private void StopAudioButton_Click(object sender, RoutedEventArgs e)
    {
        _audioPlayback.StopPlayback();
        AddLog("[Audio] Playback stopped.");
    }

    private async void SendManualButton_Click(object sender, RoutedEventArgs e)
    {
        await SubmitManualPromptAsync();
    }

    private async void ManualPromptTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SubmitManualPromptAsync();
        }
    }

    private async Task SubmitManualPromptAsync()
    {
        var text = ManualPromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            AddLog("[UI] Manual prompt is empty.");
            return;
        }

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            AddLog($"[Config] {error}");
            UpdateStatus(false, $"Status: {error}");
            return;
        }

        SaveConfigSafe(config);
        await RunChatTurnAsync(config, text, "manual");
    }

    private async Task ProcessRecordedAudioAsync(byte[] wavBytes)
    {
        if (_isProcessingTurn)
        {
            return;
        }

        if (wavBytes.Length == 0)
        {
            AddLog("[Mic] Empty recording.");
            UpdateStatus(false, "Status: empty recording");
            return;
        }

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            AddLog($"[Config] {error}");
            UpdateStatus(false, $"Status: {error}");
            return;
        }

        SaveConfigSafe(config);
        SetControlsEnabled(false);
        UpdateRecordButtons();
        _isProcessingTurn = true;
        _turnCts?.Cancel();
        _turnCts?.Dispose();
        _turnCts = new CancellationTokenSource();
        var ct = _turnCts.Token;

        try
        {
            UpdateStatus(true, "Status: transcribing audio...");
            AddLog("[STT] Sending audio to OpenAI /audio/transcriptions...");

            var stt = await _openAiAudioClient.TranscribeAsync(config, wavBytes, ct);
            if (!stt.Success)
            {
                AddLog($"[STT] Error: {stt.Message}");
                if (!string.IsNullOrWhiteSpace(stt.RawResponse))
                {
                    AddLog($"[STT Raw] {TrimForLog(stt.RawResponse)}");
                }
                UpdateStatus(false, $"Status: STT error ({stt.Message})");
                return;
            }

            AddLog($"[STT] {stt.Text}");
            _mainLog($"[VoiceInput] {stt.Text}");
            await RunChatTurnCoreAsync(config, stt.Text, "mic", ct);
        }
        catch (OperationCanceledException)
        {
            AddLog("[Voice] Turn cancelled.");
            UpdateStatus(true, "Status: cancelled");
        }
        catch (Exception ex)
        {
            AddLog($"[Voice] Unexpected error: {ex.Message}");
            UpdateStatus(false, $"Status: error ({ex.Message})");
        }
        finally
        {
            _isProcessingTurn = false;
            SetControlsEnabled(true);
            UpdateRecordButtons();
        }
    }

    private async Task RunChatTurnAsync(ChatGptVoiceConfig config, string userText, string source)
    {
        if (_isProcessingTurn)
        {
            return;
        }

        _isProcessingTurn = true;
        _turnCts?.Cancel();
        _turnCts?.Dispose();
        _turnCts = new CancellationTokenSource();
        var ct = _turnCts.Token;
        SetControlsEnabled(false);
        UpdateRecordButtons();

        try
        {
            await RunChatTurnCoreAsync(config, userText, source, ct);
        }
        catch (OperationCanceledException)
        {
            AddLog("[Voice] Turn cancelled.");
            UpdateStatus(true, "Status: cancelled");
        }
        catch (Exception ex)
        {
            AddLog($"[Voice] Unexpected error: {ex.Message}");
            UpdateStatus(false, $"Status: error ({ex.Message})");
        }
        finally
        {
            _isProcessingTurn = false;
            SetControlsEnabled(true);
            UpdateRecordButtons();
        }
    }

    private async Task RunChatTurnCoreAsync(ChatGptVoiceConfig config, string userText, string source, CancellationToken ct)
    {
        AddLog($"[User:{source}] {userText}");
        UpdateStatus(true, "Status: requesting ChatGPT...");

        var result = await _sendPromptAsync(config, userText, ct);
        if (!result.Success)
        {
            UpdateStatus(false, $"Status: API error ({result.Message})");
            AddLog($"[Chat] Error: {result.Message}");
        }
        else
        {
            UpdateStatus(true, "Status: generating OpenAI voice...");
            if (result.UsedFallback)
            {
                AddLog("[Parser] Fallback command applied (non-strict JSON received).");
            }
        }

        LastTurnTextBox.Text = $"User: {userText}{Environment.NewLine}Assistant: {result.Command.Text}";

        if (!result.Success)
        {
            await _applyAvatarResultAsync(result, ct);
            return;
        }

        var ttsTask = _openAiAudioClient.SynthesizeSpeechAsync(config, result.Command.Text, ct);
        var avatarTask = _applyAvatarResultAsync(result, ct);
        var ttsResult = await ttsTask;
        await avatarTask;

        if (!ttsResult.Success)
        {
            AddLog($"[TTS] Error: {ttsResult.Message}");
            UpdateStatus(false, $"Status: TTS error ({ttsResult.Message})");
            return;
        }

        AddLog($"[TTS] Received {ttsResult.AudioBytes.Length / 1024.0:0.0} KB ({ttsResult.ContentType}).");
        UpdateStatus(true, "Status: playing ChatGPT voice...");
        await _audioPlayback.PlayAudioAsync(ttsResult.AudioBytes, ttsResult.ContentType, ct);
        UpdateStatus(true, "Status: ready");
    }

    private void ApplyConfigToUi(ChatGptVoiceConfig config)
    {
        config = ApplyBakedRealtimeSettings(config);
        ApiBaseUrlTextBox.Text = config.BaseUrl;
        ApiModelTextBox.Text = config.Model;
        ApiKeyPasswordBox.Password = config.ApiKey;
        ApiTemperatureTextBox.Text = config.Temperature.ToString("0.###", CultureInfo.InvariantCulture);
        ApiMaxTokensTextBox.Text = config.MaxTokens.ToString(CultureInfo.InvariantCulture);
        TranscriptionModelTextBox.Text = config.TranscriptionModel;
        TranscriptionLanguageTextBox.Text = config.TranscriptionLanguage;
        TtsModelTextBox.Text = config.TtsModel;
        TtsVoiceTextBox.Text = GetRealtimeVoiceUiLabel(config.TtsVoice);
        RealtimeModelTextBox.Text = BakedRealtimeModel;
        RealtimeVoiceTextBox.Text = GetRealtimeVoiceUiLabel(config.RealtimeVoice);
        SyncTtsVoiceFromRealtimePicker();
    }

    private bool TryReadConfigFromUi(out ChatGptVoiceConfig config, out string error)
    {
        config = new ChatGptVoiceConfig();
        error = string.Empty;

        if (!double.TryParse(ApiTemperatureTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            error = "Temperature must be a number";
            return false;
        }

        if (!int.TryParse(ApiMaxTokensTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens) || maxTokens <= 0)
        {
            error = "Max Tokens must be a positive integer";
            return false;
        }

        var baseUrl = ChatGptVoiceConfigService.NormalizeBaseUrl(ApiBaseUrlTextBox.Text);
        var model = string.IsNullOrWhiteSpace(ApiModelTextBox.Text) ? "gpt-5.2" : ApiModelTextBox.Text.Trim();
        var apiKey = ApiKeyPasswordBox.Password.Trim();
        var sttModel = string.IsNullOrWhiteSpace(TranscriptionModelTextBox.Text) ? "gpt-4o-mini-transcribe" : TranscriptionModelTextBox.Text.Trim();
        var sttLanguage = (TranscriptionLanguageTextBox.Text ?? string.Empty).Trim();
        var ttsModel = string.IsNullOrWhiteSpace(TtsModelTextBox.Text) ? "gpt-4o-mini-tts" : TtsModelTextBox.Text.Trim();
        var unifiedVoice = ResolveRealtimeApiVoiceFromUi(RealtimeVoiceTextBox.Text);
        var ttsVoice = unifiedVoice;
        var realtimeModel = BakedRealtimeModel;
        var realtimeVoice = unifiedVoice;

        config = new ChatGptVoiceConfig
        {
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = apiKey,
            Temperature = Math.Clamp(temperature, 0, 2),
            MaxTokens = Math.Clamp(maxTokens, 1, 4096),
            TranscriptionModel = sttModel,
            TranscriptionLanguage = sttLanguage,
            TtsModel = ttsModel,
            TtsVoice = ttsVoice,
            RealtimeModel = realtimeModel,
            RealtimeVoice = realtimeVoice,
        };

        config = ApplyBakedRealtimeSettings(config);
        TtsVoiceTextBox.Text = GetRealtimeVoiceUiLabel(config.TtsVoice);
        RealtimeVoiceTextBox.Text = GetRealtimeVoiceUiLabel(config.RealtimeVoice);

        return true;
    }

    private void SaveConfigSafe(ChatGptVoiceConfig config)
    {
        try
        {
            _configService.Save(config);
        }
        catch (Exception ex)
        {
            AddLog($"[Config] Save warning: {ex.Message}");
        }
    }

    private void UpdateStatus(bool ok, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => UpdateStatus(ok, text)));
            return;
        }

        ApiStatusTextBlock.Text = text;
        ApiStatusBadge.Background = ok
            ? new SolidColorBrush(Color.FromRgb(66, 184, 131))
            : new SolidColorBrush(Color.FromRgb(154, 160, 166));
    }

    private void UpdateRealtimeStatus(bool ok, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => UpdateRealtimeStatus(ok, text)));
            return;
        }

        RealtimeStatusTextBlock.Text = text;
        RealtimeStatusBadge.Background = ok
            ? new SolidColorBrush(Color.FromRgb(66, 184, 131))
            : new SolidColorBrush(Color.FromRgb(154, 160, 166));
    }

    private void SetControlsEnabled(bool enabled)
    {
        TestConnectionButton.IsEnabled = enabled;
        SendManualButton.IsEnabled = enabled;
        ManualPromptTextBox.IsEnabled = enabled;

        ApiBaseUrlTextBox.IsEnabled = enabled;
        ApiModelTextBox.IsEnabled = enabled;
        ApiKeyPasswordBox.IsEnabled = enabled;
        ApiTemperatureTextBox.IsEnabled = enabled;
        ApiMaxTokensTextBox.IsEnabled = enabled;
        TranscriptionModelTextBox.IsEnabled = enabled;
        TranscriptionLanguageTextBox.IsEnabled = enabled;
        TtsModelTextBox.IsEnabled = enabled;
        TtsVoiceTextBox.IsEnabled = enabled;
        RealtimeModelTextBox.IsEnabled = enabled && !_realtimeClient.IsConnected;
        RealtimeVoiceTextBox.IsEnabled = enabled && !_realtimeClient.IsConnected;
        UpdateRealtimeButtons();
    }

    private void UpdateRecordButtons()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateRecordButtons));
            return;
        }

        var canRecord = !_isProcessingTurn;
        StartRecordButton.IsEnabled = canRecord && !_microphoneRecorder.IsRecording;
        StopAndSendButton.IsEnabled = canRecord && _microphoneRecorder.IsRecording;
        CancelRecordButton.IsEnabled = canRecord && _microphoneRecorder.IsRecording;
        StopAudioButton.IsEnabled = true;
    }

    private void UpdateRealtimeButtons()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(UpdateRealtimeButtons));
            return;
        }

        var connected = _realtimeClient.IsConnected;
        var micOn = _realtimeClient.IsMicEnabled;
        var busy = _isProcessingTurn || _isApplyingLiveVoiceChange;

        RealtimeConnectButton.IsEnabled = !busy;
        RealtimeConnectButton.Content = connected ? "Stop Live Chat" : "Start Live Chat";
        RealtimeDisconnectButton.IsEnabled = connected;
        RealtimeMicOnButton.IsEnabled = connected && !micOn;
        RealtimeMicOffButton.IsEnabled = connected && micOn;
    }

    private void RealtimeClient_ConnectionStatusChanged(bool connected, string message)
    {
        UpdateRealtimeStatus(connected, $"Realtime: {message}");
        UpdateRealtimeButtons();
    }

    private void RealtimeClient_UserSpeakingChanged(bool isSpeaking)
    {
        if (isSpeaking)
        {
            _avatarStateHint(AvatarState.Listening);
        }
        else if (_realtimeClient.IsConnected)
        {
            _avatarStateHint(AvatarState.Thinking);
        }
    }

    private void RealtimeClient_AssistantSpeakingChanged(bool isSpeaking)
    {
        if (isSpeaking)
        {
            _avatarStateHint(AvatarState.Speaking);
            UpdateRealtimeStatus(true, "Realtime: assistant speaking");
        }
        else if (_realtimeClient.IsConnected)
        {
            _avatarStateHint(_realtimeClient.IsMicEnabled ? AvatarState.Listening : AvatarState.Idle);
            UpdateRealtimeStatus(true, _realtimeClient.IsMicEnabled ? "Realtime: listening" : "Realtime: connected");
        }
    }

    private void RealtimeClient_AssistantTranscriptChanged(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => RealtimeClient_AssistantTranscriptChanged(text)));
            return;
        }

        var current = LastTurnTextBox.Text;
        var userLine = current.StartsWith("User:", StringComparison.Ordinal) ? current.Split(Environment.NewLine)[0] : "User: (live)";
        LastTurnTextBox.Text = $"{userLine}{Environment.NewLine}Assistant: {text}";
        _assistantTextSink(text);
    }

    private void RealtimeClient_AssistantTranscriptFinalized(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _mainLog($"[RealtimeVoice] {text}");
    }

    private void AddLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => AddLog(message)));
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        _logs.Add(line);

        while (_logs.Count > 500)
        {
            _logs.RemoveAt(0);
        }

        if (_logs.Count > 0)
        {
            VoiceLogsListBox.ScrollIntoView(_logs[^1]);
        }
    }

    private static string TrimForLog(string input)
    {
        const int max = 420;
        if (string.IsNullOrWhiteSpace(input))
        {
            return "(empty)";
        }

        var compact = input.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= max ? compact : compact[..max] + "...";
    }
}
