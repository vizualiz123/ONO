using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
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

    private void WidgetModeButton_Click(object sender, RoutedEventArgs e)
    {
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
        var avatarUsdPath = ResolveAvatarUsdPath();
        _currentAvatarUsdPath = avatarUsdPath;

        _avatarRenderer.LoadUsd(avatarUsdPath);
        UpdateRendererInfoText(avatarUsdPath);

        var usdKind = DetectUsdContainerKind(avatarUsdPath);
        AddLog($"[Renderer] {logSource}: selected avatar USD '{avatarUsdPath}' ({usdKind}).");

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
            _currentAvatarUsdPath = ResolveAvatarUsdPath();
            UpdateRendererInfoText(_currentAvatarUsdPath);
        }

        return _currentAvatarUsdPath;
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

    private static string ResolveAvatarUsdPath()
    {
        var assetsDirectory = ResolveAssetsDirectory();
        if (!string.IsNullOrWhiteSpace(assetsDirectory))
        {
            foreach (var explicitName in new[]
                     {
                         "avatar.usdz", "avatar.usdc", "avatar.usda", "avatar.usd",
                         "character.usdz", "character.usdc", "character.usda", "character.usd"
                     })
            {
                var explicitCandidate = Path.Combine(assetsDirectory, explicitName);
                if (File.Exists(explicitCandidate))
                {
                    return explicitCandidate;
                }
            }

            var candidates = new[] { "*.usd", "*.usda", "*.usdc", "*.usdz" }
                .SelectMany(pattern => Directory.EnumerateFiles(assetsDirectory, pattern, SearchOption.AllDirectories))
                .Select(path => new FileInfo(path))
                .Where(static file => file.Exists)
                .Select(file => new
                {
                    File = file,
                    Score = ScoreAvatarUsdCandidate(assetsDirectory, file)
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.File.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates[0].File.FullName;
            }

            return Path.Combine(assetsDirectory, "avatar_placeholder.usd");
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "avatar_placeholder.usd"));
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

        if (fileNameWithoutExt.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            score -= 10000;
        }

        if (relativeParts.Length == 1)
        {
            score += 250;
        }

        if (relativeParts.Length == 2 && relativeParts[0].Equals("Props", StringComparison.OrdinalIgnoreCase))
        {
            score += 500; // Prefer top-level avatar/assembly files under assets/Props
        }
        else if (relativeParts.Length > 0 && relativeParts[0].Equals("Props", StringComparison.OrdinalIgnoreCase))
        {
            score += 140;
        }

        foreach (var part in relativeParts)
        {
            if (part.Equals("Materials", StringComparison.OrdinalIgnoreCase) || part.Equals("Textures", StringComparison.OrdinalIgnoreCase))
            {
                score -= 900;
            }

            if (part.Equals("Meshes", StringComparison.OrdinalIgnoreCase) || part.Equals("Bones", StringComparison.OrdinalIgnoreCase))
            {
                score -= 450;
            }

            if (part.Equals("Motions", StringComparison.OrdinalIgnoreCase) || part.Equals("Animations", StringComparison.OrdinalIgnoreCase))
            {
                score -= 700;
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
