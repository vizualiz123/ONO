using AvatarDesktop.Models;

namespace AvatarDesktop.Services;

public static class DemoAvatarCommandFactory
{
    public static AvatarCommand Create(string userText)
    {
        var text = (userText ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        var mood = "neutral";
        var action = "nod";
        var duration = 900;

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Empty input. Demo mode is active.";
            action = "think";
            duration = 700;
        }
        else if (ContainsAny(lower, "hello", "hi", "hey"))
        {
            mood = "happy";
            action = "wave";
            duration = 1200;
        }
        else if (ContainsAny(lower, "dance"))
        {
            mood = "happy";
            action = "dance_01";
            duration = 1800;
        }
        else if (ContainsAny(lower, "think", "question", "why", "how"))
        {
            mood = "curious";
            action = "think";
            duration = 1100;
        }
        else if (ContainsAny(lower, "sad"))
        {
            mood = "sad";
            action = "shrug";
            duration = 1000;
        }
        else if (ContainsAny(lower, "angry", "mad"))
        {
            mood = "angry";
            action = "nod";
            duration = 900;
        }

        return new AvatarCommand
        {
            Text = $"[DEMO] {BuildReply(text, action)}",
            Mood = mood,
            Action = action,
            DurationMs = duration,
        };
    }

    private static string BuildReply(string userText, string action)
    {
        return action switch
        {
            "wave" => $"Hello! Offline demo mode is running. You wrote: {userText}",
            "dance_01" => "Starting demo dance animation. LM Studio is not required for this test.",
            "think" => $"Thinking about your message: {userText}",
            "shrug" => $"Demo mode received: \"{userText}\"",
            _ => $"Offline demo reply. Message received: {userText}"
        };
    }

    private static bool ContainsAny(string source, params string[] parts)
    {
        foreach (var part in parts)
        {
            if (source.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
