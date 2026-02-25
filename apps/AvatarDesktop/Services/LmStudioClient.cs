using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AvatarDesktop.Models;

namespace AvatarDesktop.Services;

public interface ILmStudioClient
{
    Task<HealthCheckResult> CheckHealthAsync(AppConfig config, CancellationToken cancellationToken = default);
    Task<ChatRequestResult> SendChatAsync(AppConfig config, string userText, CancellationToken cancellationToken = default);
}

public sealed class LmStudioClient : ILmStudioClient
{
    private readonly HttpClient _httpClient;

    public LmStudioClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<HealthCheckResult> CheckHealthAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var endpoint = $"{config.BaseUrl.TrimEnd('/')}/models";
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, timeoutCts.Token);
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new HealthCheckResult
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    RawResponse = raw,
                };
            }

            var message = "Connected";
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                {
                    var first = dataEl.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object &&
                        first.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.String)
                    {
                        message = $"Connected (model: {idEl.GetString()})";
                    }
                }
            }
            catch
            {
                // Keep generic message if /models payload differs.
            }

            return new HealthCheckResult
            {
                Success = true,
                Message = message,
                RawResponse = raw,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult
            {
                Success = false,
                Message = "Timeout connecting to LM Studio /models",
            };
        }
        catch (Exception ex)
        {
            return new HealthCheckResult
            {
                Success = false,
                Message = ex.Message,
            };
        }
    }

    public async Task<ChatRequestResult> SendChatAsync(AppConfig config, string userText, CancellationToken cancellationToken = default)
    {
        var endpoint = $"{config.BaseUrl.TrimEnd('/')}/chat/completions";
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        var payload = new
        {
            model = config.Model,
            temperature = config.Temperature,
            max_tokens = config.MaxTokens,
            stream = false,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildSystemPrompt(),
                },
                new
                {
                    role = "user",
                    content = userText,
                }
            }
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, timeoutCts.Token);
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new ChatRequestResult
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    RawModelContent = raw,
                    Command = new AvatarCommand
                    {
                        Text = $"LM Studio error: HTTP {(int)response.StatusCode}",
                        Mood = "neutral",
                        Action = "think",
                        DurationMs = 800,
                    }
                };
            }

            var content = ExtractAssistantContent(raw);
            var command = AvatarResponseParser.ParseOrFallback(content, out var usedFallback);

            return new ChatRequestResult
            {
                Success = true,
                Message = usedFallback ? "Response parsed with fallback" : "OK",
                RawModelContent = content,
                Command = command,
                UsedFallback = usedFallback,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChatRequestResult
            {
                Success = false,
                Message = "Timeout waiting for LM Studio response",
                Command = new AvatarCommand
                {
                    Text = "Timeout waiting for LM Studio response",
                    Mood = "neutral",
                    Action = "think",
                    DurationMs = 800,
                }
            };
        }
        catch (Exception ex)
        {
            return new ChatRequestResult
            {
                Success = false,
                Message = ex.Message,
                Command = new AvatarCommand
                {
                    Text = $"LM Studio request error: {ex.Message}",
                    Mood = "neutral",
                    Action = "think",
                    DurationMs = 800,
                }
            };
        }
    }

    private static string BuildSystemPrompt()
    {
        return """
               You control a desktop avatar. Respond with ONLY a JSON object.
               No markdown, no code fences, no explanations.
               Required schema:
               {
                 "text": "short reply to user",
                 "mood": "neutral|happy|sad|angry|curious",
                 "action": "idle|wave|dance_01|think|nod|shrug",
                 "duration_ms": 500
               }
               Keep text concise. duration_ms must be an integer between 100 and 5000.
               """;
    }

    private static string ExtractAssistantContent(string completionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(completionJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return completionJson;
            }

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message))
            {
                return completionJson;
            }

            if (!message.TryGetProperty("content", out var contentElement))
            {
                return completionJson;
            }

            if (contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString() ?? string.Empty;
            }

            if (contentElement.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in contentElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object &&
                        part.TryGetProperty("text", out var textEl) &&
                        textEl.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(textEl.GetString());
                    }
                    else
                    {
                        sb.Append(part.ToString());
                    }
                }
                return sb.ToString();
            }

            return contentElement.ToString();
        }
        catch
        {
            return completionJson;
        }
    }
}
