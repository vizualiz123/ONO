using System.Text.Json;
using AvatarDesktop.Models;

namespace AvatarDesktop.Services;

public static class AvatarResponseParser
{
    public static AvatarCommand ParseOrFallback(string? raw, out bool usedFallback)
    {
        usedFallback = false;
        raw ??= string.Empty;

        if (TryParseCommand(raw, out var parsed))
        {
            return parsed;
        }

        var trimmed = raw.Trim();
        usedFallback = true;
        return new AvatarCommand
        {
            Text = string.IsNullOrWhiteSpace(trimmed) ? "(empty model response)" : trimmed,
            Mood = "neutral",
            Action = "think",
            DurationMs = 800,
        };
    }

    private static bool TryParseCommand(string raw, out AvatarCommand command)
    {
        if (TryParseJsonObject(raw, out command))
        {
            return true;
        }

        var stripped = StripCodeFences(raw);
        if (!ReferenceEquals(stripped, raw) && TryParseJsonObject(stripped, out command))
        {
            return true;
        }

        command = new AvatarCommand();
        return false;
    }

    private static bool TryParseJsonObject(string raw, out AvatarCommand command)
    {
        command = new AvatarCommand();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!root.TryGetProperty("mood", out var moodEl) || moodEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!root.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!root.TryGetProperty("duration_ms", out var durEl) || !durEl.TryGetInt32(out var duration))
            {
                return false;
            }

            var text = textEl.GetString() ?? string.Empty;
            var mood = moodEl.GetString() ?? string.Empty;
            var action = actionEl.GetString() ?? string.Empty;

            if (!AvatarCommand.AllowedMoods.Contains(mood))
            {
                return false;
            }

            if (!AvatarCommand.AllowedActions.Contains(action))
            {
                return false;
            }

            mood = mood.ToLowerInvariant();
            action = action.ToLowerInvariant();
            duration = Math.Clamp(duration, 100, 10000);

            command = new AvatarCommand
            {
                Text = text,
                Mood = mood,
                Action = action,
                DurationMs = duration,
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string StripCodeFences(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return raw;
        }

        var lines = trimmed.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        if (lines.Count < 2)
        {
            return raw;
        }

        if (lines[^1].Trim() == "```")
        {
            lines.RemoveAt(lines.Count - 1);
        }

        lines.RemoveAt(0);
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
