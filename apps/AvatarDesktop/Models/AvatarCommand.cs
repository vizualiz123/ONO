using System.Collections.Generic;

namespace AvatarDesktop.Models;

public sealed class AvatarCommand
{
    public string Text { get; set; } = string.Empty;
    public string Mood { get; set; } = "neutral";
    public string Action { get; set; } = "idle";
    public int DurationMs { get; set; } = 500;

    public static readonly HashSet<string> AllowedMoods = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "neutral", "happy", "sad", "angry", "curious"
    };

    public static readonly HashSet<string> AllowedActions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "idle", "wave", "dance_01", "think", "nod", "shrug"
    };
}
