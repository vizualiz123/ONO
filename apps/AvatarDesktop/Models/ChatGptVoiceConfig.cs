namespace AvatarDesktop.Models;

public sealed class ChatGptVoiceConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-5.2";
    public string ApiKey { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.4;
    public int MaxTokens { get; set; } = 1024;
    public string TranscriptionModel { get; set; } = "gpt-4o-mini-transcribe";
    public string TranscriptionLanguage { get; set; } = string.Empty;
    public string TtsModel { get; set; } = "gpt-4o-mini-tts";
    public string TtsVoice { get; set; } = "marin";
    public string RealtimeModel { get; set; } = "gpt-realtime";
    public string RealtimeVoice { get; set; } = "marin";
}
