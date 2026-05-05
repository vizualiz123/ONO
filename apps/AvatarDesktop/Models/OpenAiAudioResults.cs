namespace AvatarDesktop.Models;

public sealed class AudioTranscriptionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? RawResponse { get; init; }
}

public sealed class AudioSpeechResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public byte[] AudioBytes { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = string.Empty;
}
