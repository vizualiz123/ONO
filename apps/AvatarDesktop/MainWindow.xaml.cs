using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvatarDesktop.Models;
using AvatarDesktop.Rendering;
using AvatarDesktop.Services;
using AvatarDesktop.Tts;

namespace AvatarDesktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _logs = new();
    private readonly ILmStudioClient _lmStudioClient;
    private readonly IOpenAiChatClient _openAiChatClient;
    private readonly FanoutAvatarRenderer _avatarRenderer;
    private readonly AnimationController _animationController;
    private readonly ITextToSpeech _tts;
    private readonly DispatcherTimer _renderTimer;
    private readonly ConfigService _configService;

    private CancellationTokenSource? _requestCts;
    private WidgetWindow? _widgetWindow;
    private VoiceChatWindow? _voiceChatWindow;
    private IAvatarRenderer? _widgetRenderer;
    private DateTime _lastRenderTickUtc;
    private bool _isBusy;
    private string _currentAvatarUsdPath = string.Empty;
    private bool _rendererPlaceholderNoticeLogged;
    private static readonly bool WidgetMirrorEnabled = false;

    public MainWindow()
    {
        InitializeComponent();

        LogsListBox.ItemsSource = _logs;

        _lmStudioClient = new LmStudioClient();
        _openAiChatClient = new OpenAiChatClient();
        _avatarRenderer = new FanoutAvatarRenderer(new CubeAvatarRenderer());
        _animationController = new AnimationController(_avatarRenderer, AddLog);
        _animationController.StateChanged += AnimationController_StateChanged;
        _tts = new LoggingTextToSpeech(AddLog);

        AvatarViewportHost.Content = _avatarRenderer.View;

        _configService = new ConfigService(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 30.0)
        };
        _renderTimer.Tick += RenderTimer_Tick;
        _lastRenderTickUtc = DateTime.UtcNow;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var config = _configService.Load(out var warning);
        ApplyConfigToUi(config);

        if (!string.IsNullOrWhiteSpace(warning))
        {
            AddLog($"[Config] {warning}");
        }

        ReloadAvatarUsdSelection(logSource: "Startup");

        _animationController.ResetToIdle();
        if (IsOfflineDemoModeEnabled)
        {
            UpdateConnectionStatus(true, "Mode: Offline Demo (LM Studio bypassed)");
            AddLog("[Mode] Offline Demo Mode enabled. Network calls to LM Studio are bypassed.");
        }
        else
        {
            UpdateConnectionStatus(false, "LM Studio status: not checked");
        }

        _renderTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _renderTimer.Stop();
        _requestCts?.Cancel();
        _requestCts?.Dispose();
        CloseVoiceChatWindow();
        CloseWidgetWindow();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCurrentPromptAsync();
    }

    private async void HealthCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (IsOfflineDemoModeEnabled)
        {
            UpdateConnectionStatus(true, "Mode: Offline Demo (health check skipped)");
            AddLog("[Health] Offline Demo Mode: /models check skipped.");
            return;
        }

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            AddLog($"[Config] {error}");
            UpdateConnectionStatus(false, $"LM Studio status: invalid config ({error})");
            return;
        }

        SetBusy(true);
        AddLog("[Health] Checking LM Studio /models...");

        try
        {
            var result = await _lmStudioClient.CheckHealthAsync(config);
            UpdateConnectionStatus(result.Success, $"LM Studio status: {result.Message}");
            AddLog(result.Success ? $"[Health] OK - {result.Message}" : $"[Health] FAIL - {result.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadUsdHookButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadAvatarUsdSelection(logSource: "Manual reload");
    }

    private void AvatarPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ReloadAvatarUsdSelection(logSource: "Preset changed");
    }

    private void WidgetModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!WidgetMirrorEnabled)
        {
            AddLog("[Widget] Disabled: single-avatar mode keeps only the main face visible.");
            CloseWidgetWindow();
            return;
        }

        if (_widgetWindow is not null)
        {
            CloseWidgetWindow();
            return;
        }

        OpenWidgetWindow();
    }

    private void ChatGptVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceChatWindow is not null)
        {
            _voiceChatWindow.Activate();
            return;
        }

        OpenVoiceChatWindow();
    }

    private async void UserInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendCurrentPromptAsync();
        }
    }

    private async Task SendCurrentPromptAsync()
    {
        if (_isBusy)
        {
            return;
        }

        var userText = UserInputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(userText))
        {
            AddLog("[UI] Empty prompt ignored.");
            return;
        }

        if (IsOfflineDemoModeEnabled)
        {
            await RunOfflineDemoAsync(userText);
            return;
        }

        if (!TryReadConfigFromUi(out var config, out var error))
        {
            AddLog($"[Config] {error}");
            UpdateConnectionStatus(false, $"LM Studio status: invalid config ({error})");
            return;
        }

        TrySaveConfig(config);

        _requestCts?.Cancel();
        _requestCts?.Dispose();
        _requestCts = new CancellationTokenSource();
        var ct = _requestCts.Token;

        SetBusy(true);
        ModelTextOutputTextBox.Text = string.Empty;
        AddLog($"[User] {userText}");

        try
        {
            _animationController.SetState(AvatarState.Listening);
            await Task.Delay(100, ct);
            _animationController.SetState(AvatarState.Thinking);

            var result = await _lmStudioClient.SendChatAsync(config, userText, ct);

            if (!result.Success)
            {
                UpdateConnectionStatus(false, $"LM Studio status: {result.Message}");
                AddLog($"[LM Studio] Error: {result.Message}");
                if (!string.IsNullOrWhiteSpace(result.RawModelContent))
                {
                    AddLog($"[LM Studio Raw] {TrimForLog(result.RawModelContent)}");
                }

                ModelTextOutputTextBox.Text = result.Command.Text;
                await _animationController.ApplyCommandAsync(result.Command, ct);
                await _tts.SpeakAsync(result.Command.Text, ct);
                return;
            }

            UpdateConnectionStatus(true, "LM Studio status: connected");
            if (result.UsedFallback)
            {
                AddLog("[Parser] Model returned non-strict JSON. Fallback command applied.");
            }

            AddLog($"[LM Studio Raw] {TrimForLog(result.RawModelContent)}");
            AddLog($"[AvatarCmd] mood={result.Command.Mood}, action={result.Command.Action}, duration_ms={result.Command.DurationMs}");

            ModelTextOutputTextBox.Text = result.Command.Text;

            await _tts.SpeakAsync(result.Command.Text, ct);
            await _animationController.ApplyCommandAsync(result.Command, ct);
        }
        catch (OperationCanceledException)
        {
            AddLog("[Request] Cancelled.");
            _animationController.ResetToIdle();
        }
        catch (Exception ex)
        {
            AddLog($"[Request] Unexpected error: {ex.Message}");
            UpdateConnectionStatus(false, $"LM Studio status: error ({ex.Message})");
            _animationController.ResetToIdle();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = now - _lastRenderTickUtc;
        _lastRenderTickUtc = now;
        _avatarRenderer.Update(dt);
    }

    private void AnimationController_StateChanged(AvatarState previous, AvatarState next)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => AnimationController_StateChanged(previous, next)));
            return;
        }

        AvatarStateTextBlock.Text = next.ToString();
    }

    private void ApplyConfigToUi(AppConfig config)
    {
        BaseUrlTextBox.Text = config.BaseUrl;
        ModelTextBox.Text = config.Model;
        TemperatureTextBox.Text = config.Temperature.ToString("0.###", CultureInfo.InvariantCulture);
        MaxTokensTextBox.Text = config.MaxTokens.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryReadConfigFromUi(out AppConfig config, out string error)
    {
        config = new AppConfig();
        error = string.Empty;

        var baseUrl = BaseUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            error = "Base URL is empty";
            return false;
        }

        if (!double.TryParse(TemperatureTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            error = "Temperature must be a number (example: 0.3)";
            return false;
        }

        if (!int.TryParse(MaxTokensTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens) || maxTokens <= 0)
        {
            error = "Max Tokens must be a positive integer";
            return false;
        }

        config = new AppConfig
        {
            BaseUrl = baseUrl,
            Model = string.IsNullOrWhiteSpace(ModelTextBox.Text) ? "local-model" : ModelTextBox.Text.Trim(),
            Temperature = Math.Clamp(temperature, 0, 2),
            MaxTokens = Math.Clamp(maxTokens, 1, 4096),
        };

        return true;
    }

    private void TrySaveConfig(AppConfig config)
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

    private void UpdateConnectionStatus(bool connected, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => UpdateConnectionStatus(connected, message)));
            return;
        }

        ConnectionStatusTextBlock.Text = message;
        ConnectionStatusBadge.Background = connected
            ? new SolidColorBrush(Color.FromRgb(66, 184, 131))
            : new SolidColorBrush(Color.FromRgb(154, 160, 166));
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
            LogsListBox.ScrollIntoView(_logs[^1]);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        SendButton.IsEnabled = !isBusy;
        HealthCheckButton.IsEnabled = !isBusy;
        WidgetModeButton.IsEnabled = !isBusy;
        ChatGptVoiceButton.IsEnabled = !isBusy;
        LoadUsdHookButton.IsEnabled = !isBusy;
        AvatarPresetComboBox.IsEnabled = !isBusy;
        OfflineDemoModeCheckBox.IsEnabled = !isBusy;
    }

    private static string TrimForLog(string input)
    {
        const int max = 400;
        if (string.IsNullOrWhiteSpace(input))
        {
            return "(empty)";
        }

        var normalized = input.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= max)
        {
            return normalized;
        }

        return normalized[..max] + "...";
    }

    private void OpenWidgetWindow()
    {
        if (!WidgetMirrorEnabled)
        {
            return;
        }

        if (_widgetWindow is not null)
        {
            _widgetWindow.Activate();
            return;
        }

        var widgetRenderer = new CubeAvatarRenderer(showGroundPlane: false);
        var widgetWindow = new WidgetWindow(widgetRenderer);
        widgetWindow.Owner = this;
        widgetWindow.Closed += WidgetWindow_Closed;

        _widgetRenderer = widgetRenderer;
        _avatarRenderer.AddRenderer(widgetRenderer);

        var avatarUsdPath = GetOrResolveAvatarUsdPath();
        widgetRenderer.LoadUsd(avatarUsdPath);
        widgetRenderer.SetAnimation(GetAnimationNameForState(_animationController.State));

        _widgetWindow = widgetWindow;
        widgetWindow.Show();

        WidgetModeButton.Content = "Stop Widget";
        AddLog("[Widget] Transparent overlay widget started.");
    }

    private void OpenVoiceChatWindow()
    {
        if (_voiceChatWindow is not null)
        {
            _voiceChatWindow.Activate();
            return;
        }

        var window = new VoiceChatWindow(
            _openAiChatClient,
            SendChatGptVoicePromptAsync,
            ApplyVoiceChatResultAsync,
            AddLog,
            SetAvatarStateHint,
            SetAssistantTextHint);
        window.Owner = this;
        window.Closed += VoiceChatWindow_Closed;
        _voiceChatWindow = window;
        window.Show();
        AddLog("[VoiceChat] ChatGPT Voice window opened.");
    }

    private void CloseVoiceChatWindow()
    {
        if (_voiceChatWindow is null)
        {
            return;
        }

        var window = _voiceChatWindow;
        _voiceChatWindow = null;
        window.Closed -= VoiceChatWindow_Closed;
        if (window.IsVisible)
        {
            window.Close();
        }
    }

    private void VoiceChatWindow_Closed(object? sender, EventArgs e)
    {
        _voiceChatWindow = null;
        AddLog("[VoiceChat] ChatGPT Voice window closed.");
    }

    private void CloseWidgetWindow()
    {
        if (_widgetWindow is null)
        {
            return;
        }

        var window = _widgetWindow;
        _widgetWindow = null;

        window.Closed -= WidgetWindow_Closed;

        if (_widgetRenderer is not null)
        {
            _avatarRenderer.RemoveRenderer(_widgetRenderer);
            _widgetRenderer = null;
        }

        if (window.IsVisible)
        {
            window.Close();
        }

        WidgetModeButton.Content = "Start Widget";
        AddLog("[Widget] Widget stopped.");
    }

    private void WidgetWindow_Closed(object? sender, EventArgs e)
    {
        if (_widgetRenderer is not null)
        {
            _avatarRenderer.RemoveRenderer(_widgetRenderer);
            _widgetRenderer = null;
        }

        _widgetWindow = null;
        WidgetModeButton.Content = "Start Widget";
        AddLog("[Widget] Widget window closed.");
    }

    private static string GetAnimationNameForState(AvatarState state)
    {
        return state switch
        {
            AvatarState.Idle => "idle",
            AvatarState.Listening => "listening",
            AvatarState.Thinking => "think",
            AvatarState.Speaking => "speaking",
            AvatarState.Acting => "speaking",
            _ => "idle"
        };
    }

    private async Task<ChatRequestResult> SendChatGptVoicePromptAsync(ChatGptVoiceConfig config, string userText, CancellationToken cancellationToken)
    {
        AddLog($"[ChatGPT Voice] Sending prompt to API model={config.Model}");
        var result = await _openAiChatClient.SendAvatarCommandAsync(config, userText, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.RawModelContent))
        {
            AddLog($"[ChatGPT Voice Raw] {TrimForLog(result.RawModelContent)}");
        }

        if (!result.Success)
        {
            AddLog($"[ChatGPT Voice] Error: {result.Message}");
        }
        else
        {
            AddLog($"[ChatGPT Voice Cmd] mood={result.Command.Mood}, action={result.Command.Action}, duration_ms={result.Command.DurationMs}");
        }

        return result;
    }

    private async Task ApplyVoiceChatResultAsync(ChatRequestResult result, CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => ApplyVoiceChatResultAsync(result, cancellationToken)).Task.Unwrap();
            return;
        }

        ModelTextOutputTextBox.Text = result.Command.Text;
        await _animationController.ApplyCommandAsync(result.Command, cancellationToken);
    }

    private void SetAvatarStateHint(AvatarState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => SetAvatarStateHint(state)));
            return;
        }

        _animationController.SetState(state);
    }

    private void SetAssistantTextHint(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => SetAssistantTextHint(text)));
            return;
        }

        ModelTextOutputTextBox.Text = text ?? string.Empty;
    }

    private bool IsOfflineDemoModeEnabled => OfflineDemoModeCheckBox.IsChecked == true;

    private void OfflineDemoModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (IsOfflineDemoModeEnabled)
        {
            UpdateConnectionStatus(true, "Mode: Offline Demo (LM Studio bypassed)");
            AddLog("[Mode] Switched to Offline Demo Mode.");
        }
        else
        {
            UpdateConnectionStatus(false, "LM Studio status: not checked");
            AddLog("[Mode] Switched to LM Studio mode.");
        }
    }

    private async Task RunOfflineDemoAsync(string userText)
    {
        _requestCts?.Cancel();
        _requestCts?.Dispose();
        _requestCts = new CancellationTokenSource();
        var ct = _requestCts.Token;

        SetBusy(true);
        ModelTextOutputTextBox.Text = string.Empty;
        UpdateConnectionStatus(true, "Mode: Offline Demo (LM Studio bypassed)");
        AddLog($"[User] {userText}");

        try
        {
            _animationController.SetState(AvatarState.Listening);
            await Task.Delay(120, ct);
            _animationController.SetState(AvatarState.Thinking);
            await Task.Delay(320, ct);

            var command = DemoAvatarCommandFactory.Create(userText);
            AddLog($"[DemoCmd] mood={command.Mood}, action={command.Action}, duration_ms={command.DurationMs}");
            ModelTextOutputTextBox.Text = command.Text;

            await _tts.SpeakAsync(command.Text, ct);
            await _animationController.ApplyCommandAsync(command, ct);
        }
        catch (OperationCanceledException)
        {
            AddLog("[Demo] Cancelled.");
            _animationController.ResetToIdle();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ReloadAvatarUsdSelection(string logSource)
    {
        var avatarUsdPath = ResolveSelectedAvatarUsdPath();
        _currentAvatarUsdPath = avatarUsdPath;

        _avatarRenderer.LoadUsd(avatarUsdPath);
        if (_widgetRenderer is not null)
        {
            _widgetRenderer.LoadUsd(avatarUsdPath);
            _widgetRenderer.SetAnimation(GetAnimationNameForState(_animationController.State));
        }

        UpdateRendererInfoText(avatarUsdPath);

        var usdKind = DetectUsdContainerKind(avatarUsdPath);
        AddLog($"[Renderer] {logSource}: {GetSelectedAvatarPresetLabel()} -> '{avatarUsdPath}' ({usdKind}).");

        if (!_rendererPlaceholderNoticeLogged)
        {
            AddLog("[Renderer] Note: WPF renderer now attempts static USD mesh rendering via USD.NET (materials/skinning/blendshapes are limited). Cube is used only as fallback if USD load fails.");
            _rendererPlaceholderNoticeLogged = true;
        }
    }

    private string GetOrResolveAvatarUsdPath()
    {
        if (string.IsNullOrWhiteSpace(_currentAvatarUsdPath))
        {
            _currentAvatarUsdPath = ResolveSelectedAvatarUsdPath();
            UpdateRendererInfoText(_currentAvatarUsdPath);
        }

        return _currentAvatarUsdPath;
    }

    private string ResolveSelectedAvatarUsdPath()
    {
        return ResolveAvatarUsdPathForPreset(GetSelectedAvatarPresetIndex()) ?? ResolveAvatarUsdPath();
    }

    private int GetSelectedAvatarPresetIndex()
    {
        // UI is locked to a single face-only preset.
        return 0;
    }

    private string GetSelectedAvatarPresetLabel()
    {
        if (AvatarPresetComboBox?.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? "Avatar";
        }

        return "Avatar: Auto (smart)";
    }

    private void UpdateRendererInfoText(string usdPath)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => UpdateRendererInfoText(usdPath)));
            return;
        }

        var fileName = string.IsNullOrWhiteSpace(usdPath) ? "(not found)" : Path.GetFileName(usdPath);
        var usdKind = string.IsNullOrWhiteSpace(usdPath) ? "USD" : DetectUsdContainerKind(usdPath);
        RendererInfoTextBlock.Text = $"WPF USD mesh view (cube fallback) | USD: {fileName} ({usdKind})";
        RendererInfoTextBlock.ToolTip = usdPath;
    }

    private static string? ResolveAvatarUsdPathForPreset(int presetIndex)
    {
        return presetIndex switch
        {
            0 => ResolveFaceOnlyStageUsdPath() ?? ResolveAvatarPresetByRelativeCandidates(
                Path.Combine("Audio2Face_Preset_Examples", "Debra_A2F_CC_GameBase", "Debra_Mark_fitted_mesh.usd"),
                Path.Combine("Audio2Face_Preset_Examples", "Debra_A2F_CC_GameBase", "Debra_Mark_transferred.usd"),
                Path.Combine("Audio2Face_Preset_Examples", "Debra_A2F_CC_GameBase", "Debra_Mark_start.usd"),
                Path.Combine("Debra", "Props", "Debra.usd"),
                Path.Combine("Debra", "Debra.usd")),
            2 => ResolveAvatarPresetByRelativeCandidates(
                Path.Combine("Debra", "Props", "Debra.usd"),
                Path.Combine("Debra", "Debra.usd")),
            3 => ResolveAvatarPresetByRelativeCandidates(
                Path.Combine("Worker", "Props", "Worker.usd"),
                Path.Combine("Worker", "Worker.usd")),
            4 => ResolveAvatarPresetByRelativeCandidates(
                Path.Combine("Orc", "Props", "Orc.usd"),
                Path.Combine("Orc", "Orc.usd")),
            _ => null // Auto mode handled by ResolveAvatarUsdPath()
        };
    }

    private static string? ResolveFaceOnlyStageUsdPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "usd", "stages", "main_faceonly_v2.usda");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? ResolveAvatarPresetByRelativeCandidates(params string[] relativeCandidates)
    {
        if (relativeCandidates is null || relativeCandidates.Length == 0)
        {
            return null;
        }

        var searchRoots = ResolveAvatarSearchRoots();
        foreach (var root in searchRoots)
        {
            foreach (var relativePath in relativeCandidates)
            {
                var candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string ResolveAvatarUsdPath()
    {
        if (TryResolveAvatarUsdOverride(out var overrideUsdPath))
        {
            return overrideUsdPath;
        }

        var searchRoots = ResolveAvatarSearchRoots();
        var explicitTopLevelUsd = ResolveExplicitTopLevelAvatarUsd(searchRoots);
        if (!string.IsNullOrWhiteSpace(explicitTopLevelUsd))
        {
            return explicitTopLevelUsd;
        }

        var curatedFaceUsd = ResolveCuratedFaceDefaultUsd(searchRoots);
        if (!string.IsNullOrWhiteSpace(curatedFaceUsd))
        {
            return curatedFaceUsd;
        }

        var candidates = searchRoots
            .SelectMany(root => EnumerateUsdFilesSafe(root).Select(path => new
            {
                Root = root,
                File = new FileInfo(path)
            }))
            .Where(static x => x.File.Exists)
            .Select(x => new
            {
                x.File,
                Score = ScoreAvatarUsdCandidate(x.Root, x.File)
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.File.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count > 0)
        {
            return candidates[0].File.FullName;
        }

        var assetsDirectory = ResolveAssetsDirectory();
        if (!string.IsNullOrWhiteSpace(assetsDirectory))
        {
            return Path.Combine(assetsDirectory, "avatar_placeholder.usd");
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "avatar_placeholder.usd"));
    }

    private static bool TryResolveAvatarUsdOverride(out string usdPath)
    {
        usdPath = string.Empty;

        var raw = Environment.GetEnvironmentVariable("AVATAR_USD_PATH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Trim().Trim('"');
        if (File.Exists(candidate))
        {
            usdPath = Path.GetFullPath(candidate);
            return true;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var combined = Path.GetFullPath(Path.Combine(current.FullName, candidate));
            if (File.Exists(combined))
            {
                usdPath = combined;
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static IReadOnlyList<string> ResolveAvatarSearchRoots()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            AddRoot(Path.Combine(current.FullName, "Characters_NVD@10012", "Assets", "Characters", "Reallusion"));
            AddRoot(Path.Combine(current.FullName, "assets"));

            current = current.Parent;
        }

        return roots;

        void AddRoot(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
            {
                roots.Add(fullPath);
            }
        }
    }

    private static string? ResolveExplicitTopLevelAvatarUsd(IReadOnlyList<string> searchRoots)
    {
        var explicitNames = new[]
        {
            "avatar.usdz", "avatar.usdc", "avatar.usda", "avatar.usd",
            "character.usdz", "character.usdc", "character.usda", "character.usd"
        };

        // Prefer local simplified assets/ folder for explicit "avatar.usd" drop-ins.
        foreach (var root in searchRoots
                     .OrderBy(root => Path.GetFileName(root).Equals("assets", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(root => root, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var explicitName in explicitNames)
            {
                var candidate = Path.Combine(root, explicitName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? ResolveCuratedFaceDefaultUsd(IReadOnlyList<string> searchRoots)
    {
        var reallusionRoots = searchRoots
            .Where(root => Path.GetFileName(root).Equals("Reallusion", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (reallusionRoots.Count == 0)
        {
            return null;
        }

        // Reliable defaults for this project: full character Debra first (works with portrait framing),
        // then A2F example meshes for tighter face experiments.
        var preferredRelativePaths = new[]
        {
            Path.Combine("Debra", "Props", "Debra.usd"),
            Path.Combine("Debra", "Debra.usd"),
            Path.Combine("Audio2Face_Preset_Examples", "Debra_A2F_CC_GameBase", "Debra_Mark_fitted_mesh.usd"),
            Path.Combine("Audio2Face_Preset_Examples", "Debra_A2F_CC_GameBase", "Debra_Mark_transferred.usd"),
            Path.Combine("Worker", "Props", "Worker.usd"),
            Path.Combine("Orc", "Props", "Orc.usd")
        };

        foreach (var root in reallusionRoots)
        {
            foreach (var relativePath in preferredRelativePaths)
            {
                var candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateUsdFilesSafe(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        foreach (var pattern in new[] { "*.usd", "*.usda", "*.usdc", "*.usdz" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static string? ResolveAssetsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "assets");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static int ScoreAvatarUsdCandidate(string assetsDirectory, FileInfo file)
    {
        var score = 0;
        var relativePath = Path.GetRelativePath(assetsDirectory, file.FullName);
        var relativeParts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file.Name);
        var normalizedRelativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var rootName = Path.GetFileName(assetsDirectory);

        if (fileNameWithoutExt.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            score -= 10000;
        }

        if (relativeParts.Length == 1)
        {
            score += 250;
        }

        var propsIndex = Array.FindIndex(relativeParts, static part => part.Equals("Props", StringComparison.OrdinalIgnoreCase));
        if (propsIndex >= 0)
        {
            score += 220;

            if (relativeParts.Length == propsIndex + 2)
            {
                score += 480; // Prefer avatar assembly files directly under Props
            }
            else if (relativeParts.Length == propsIndex + 3)
            {
                score += 160;
            }

            if (propsIndex > 0 && fileNameWithoutExt.Equals(relativeParts[propsIndex - 1], StringComparison.OrdinalIgnoreCase))
            {
                score += 420; // Debra/Props/Debra.usd, Worker/Props/Worker.usd, etc.
            }
        }

        foreach (var part in relativeParts)
        {
            if (part.Equals("Materials", StringComparison.OrdinalIgnoreCase) || part.Equals("Textures", StringComparison.OrdinalIgnoreCase))
            {
                score -= 2500;
            }

            if (part.Equals("Meshes", StringComparison.OrdinalIgnoreCase) || part.Equals("Bones", StringComparison.OrdinalIgnoreCase))
            {
                score -= 1200;
            }

            if (part.Equals("Motions", StringComparison.OrdinalIgnoreCase) || part.Equals("Animations", StringComparison.OrdinalIgnoreCase))
            {
                score -= 1600;
            }

            if (part.Equals(".thumbs", StringComparison.OrdinalIgnoreCase) || part.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                score -= 4000;
            }
        }

        score -= fileNameWithoutExt.Count(ch => ch == '.') * 90;

        if (fileNameWithoutExt.Contains("idle", StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExt.Contains("walk", StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExt.Contains("run", StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExt.Contains("pose", StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExt.Contains("talk", StringComparison.OrdinalIgnoreCase))
        {
            score -= 220;
        }

        if (fileNameWithoutExt.Equals("debra", StringComparison.OrdinalIgnoreCase))
        {
            score += 650;
        }
        else if (fileNameWithoutExt.Equals("worker", StringComparison.OrdinalIgnoreCase))
        {
            score += 220;
        }

        if (fileNameWithoutExt.Contains("debra", StringComparison.OrdinalIgnoreCase))
        {
            score += 260;
        }

        if (fileNameWithoutExt.Contains("head", StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExt.Contains("face", StringComparison.OrdinalIgnoreCase))
        {
            score += 180;
        }

        if (fileNameWithoutExt.Contains("fitted", StringComparison.OrdinalIgnoreCase)
            || fileNameWithoutExt.Contains("transferred", StringComparison.OrdinalIgnoreCase))
        {
            score += 140;
        }

        if (normalizedRelativePath.Contains($"{Path.DirectorySeparatorChar}Audio2Face_Preset_Examples{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            score += 120;
        }

        if (normalizedRelativePath.Contains($"{Path.DirectorySeparatorChar}Debra{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            score += 220;
        }

        if (string.Equals(rootName, "Reallusion", StringComparison.OrdinalIgnoreCase))
        {
            score += 150; // Prefer richer character pack over minimal fallback assets when both exist.
        }

        if (file.Length < 1024)
        {
            score -= 150;
        }
        else if (file.Length <= 32 * 1024)
        {
            score += 80;
        }

        return score;
    }

    private static string DetectUsdContainerKind(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[16];
            var read = stream.Read(header, 0, header.Length);
            if (read > 0)
            {
                var ascii = Encoding.ASCII.GetString(header, 0, read);
                if (ascii.StartsWith("PXR-USDC", StringComparison.Ordinal))
                {
                    return "USDC";
                }

                if (ascii.StartsWith("#usda", StringComparison.OrdinalIgnoreCase))
                {
                    return "USDA";
                }
            }
        }
        catch
        {
            // Best-effort detection only.
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "USD";
        }

        return extension.TrimStart('.').ToUpperInvariant();
    }
}
