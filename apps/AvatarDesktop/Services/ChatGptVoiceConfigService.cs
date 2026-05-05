using System.IO;
using System.Text.Json;
using AvatarDesktop.Models;

namespace AvatarDesktop.Services;

public sealed class ChatGptVoiceConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string _path;

    public ChatGptVoiceConfigService(string path)
    {
        _path = path;
    }

    public ChatGptVoiceConfig Load(out string? warning)
    {
        warning = null;

        if (!File.Exists(_path))
        {
            var cfg = new ChatGptVoiceConfig();
            Save(cfg);
            warning = $"Voice config not found. Created default: {_path}";
            return cfg;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<ChatGptVoiceConfig>(json, JsonOptions) ?? new ChatGptVoiceConfig();

            cfg.BaseUrl = NormalizeBaseUrl(cfg.BaseUrl);
            cfg.Model = string.IsNullOrWhiteSpace(cfg.Model) ? "gpt-5.2" : cfg.Model.Trim();
            cfg.MaxTokens = cfg.MaxTokens <= 0 ? 1024 : cfg.MaxTokens;
            cfg.TranscriptionModel = string.IsNullOrWhiteSpace(cfg.TranscriptionModel) ? "gpt-4o-mini-transcribe" : cfg.TranscriptionModel.Trim();
            cfg.TtsModel = string.IsNullOrWhiteSpace(cfg.TtsModel) ? "gpt-4o-mini-tts" : cfg.TtsModel.Trim();
            cfg.TtsVoice = string.IsNullOrWhiteSpace(cfg.TtsVoice) ? "marin" : cfg.TtsVoice.Trim();
            cfg.RealtimeModel = string.IsNullOrWhiteSpace(cfg.RealtimeModel) ? "gpt-realtime" : cfg.RealtimeModel.Trim();
            cfg.RealtimeVoice = string.IsNullOrWhiteSpace(cfg.RealtimeVoice) ? "marin" : cfg.RealtimeVoice.Trim();
            cfg.TranscriptionLanguage = (cfg.TranscriptionLanguage ?? string.Empty).Trim();
            return cfg;
        }
        catch (Exception ex)
        {
            warning = $"Voice config load error: {ex.Message}. Using defaults.";
            return new ChatGptVoiceConfig();
        }
    }

    public void Save(ChatGptVoiceConfig config)
    {
        config.BaseUrl = NormalizeBaseUrl(config.BaseUrl);
        config.MaxTokens = Math.Clamp(config.MaxTokens, 1, 4096);
        config.Model = string.IsNullOrWhiteSpace(config.Model) ? "gpt-5.2" : config.Model.Trim();
        config.TranscriptionModel = string.IsNullOrWhiteSpace(config.TranscriptionModel) ? "gpt-4o-mini-transcribe" : config.TranscriptionModel.Trim();
        config.TtsModel = string.IsNullOrWhiteSpace(config.TtsModel) ? "gpt-4o-mini-tts" : config.TtsModel.Trim();
        config.TtsVoice = string.IsNullOrWhiteSpace(config.TtsVoice) ? "marin" : config.TtsVoice.Trim();
        config.RealtimeModel = string.IsNullOrWhiteSpace(config.RealtimeModel) ? "gpt-realtime" : config.RealtimeModel.Trim();
        config.RealtimeVoice = string.IsNullOrWhiteSpace(config.RealtimeVoice) ? "marin" : config.RealtimeVoice.Trim();
        config.TranscriptionLanguage = (config.TranscriptionLanguage ?? string.Empty).Trim();
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_path, json);
    }

    public static string NormalizeBaseUrl(string? raw)
    {
        var baseUrl = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.openai.com/v1";
        }

        baseUrl = baseUrl.TrimEnd('/');
        if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl += "/v1";
        }

        return baseUrl;
    }
}
