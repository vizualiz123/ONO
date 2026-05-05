namespace AvatarDesktop.Models;

public sealed class HealthCheckResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? RawResponse { get; init; }
}

public sealed class ChatRequestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string RawModelContent { get; init; } = string.Empty;
    public AvatarCommand Command { get; init; } = new();
    public bool UsedFallback { get; init; }
}
