using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AvatarDesktop.Models;

namespace AvatarDesktop.Services;

public interface IOpenAiChatClient
{
    Task<HealthCheckResult> CheckHealthAsync(ChatGptVoiceConfig config, CancellationToken cancellationToken = default);
    Task<ChatRequestResult> SendAvatarCommandAsync(ChatGptVoiceConfig config, string userText, CancellationToken cancellationToken = default);
}

public sealed class OpenAiChatClient : IOpenAiChatClient
{
    private readonly HttpClient _httpClient;

    public OpenAiChatClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<HealthCheckResult> CheckHealthAsync(ChatGptVoiceConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new HealthCheckResult
            {
                Success = false,
                Message = "API key is empty",
            };
        }

        using var request = CreateRequest(config, HttpMethod.Get, "/models", null);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
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

            return new HealthCheckResult
            {
                Success = true,
                Message = "Connected",
                RawResponse = raw,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult { Success = false, Message = "Timeout connecting to ChatGPT API" };
        }
        catch (Exception ex)
        {
            return new HealthCheckResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<ChatRequestResult> SendAvatarCommandAsync(ChatGptVoiceConfig config, string userText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new ChatRequestResult
            {
                Success = false,
                Message = "API key is empty",
                Command = new AvatarCommand
                {
                    Text = "API key is empty",
                    Mood = "neutral",
                    Action = "think",
                    DurationMs = 800,
                }
            };
        }

        var payload = new
        {
            model = config.Model,
            temperature = config.Temperature,
            max_tokens = config.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt() },
                new { role = "user", content = userText }
            }
        };

        using var request = CreateRequest(config, HttpMethod.Post, "/chat/completions", JsonSerializer.Serialize(payload));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var rawJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new ChatRequestResult
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    RawModelContent = rawJson,
                    Command = new AvatarCommand
                    {
                        Text = $"ChatGPT API error: HTTP {(int)response.StatusCode}",
                        Mood = "neutral",
                        Action = "think",
                        DurationMs = 800,
                    }
                };
            }

            var content = ExtractAssistantContent(rawJson);
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
                Message = "Timeout waiting for ChatGPT API response",
                Command = new AvatarCommand
                {
                    Text = "Timeout waiting for ChatGPT API response",
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
                    Text = $"ChatGPT API request error: {ex.Message}",
                    Mood = "neutral",
                    Action = "think",
                    DurationMs = 800,
                }
            };
        }
    }

    private HttpRequestMessage CreateRequest(ChatGptVoiceConfig config, HttpMethod method, string relativePath, string? jsonBody)
    {
        var baseUrl = ChatGptVoiceConfigService.NormalizeBaseUrl(config.BaseUrl);
        var uri = new Uri(baseUrl.TrimEnd('/') + relativePath, UriKind.Absolute);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string BuildSystemPrompt()
    {
        return """
               You control a desktop avatar. Respond with ONLY a JSON object.
               No markdown, no code fences, no explanations.
               Required schema:
               {
                 "text": "natural reply to user",
                 "mood": "neutral|happy|sad|angry|curious",
                 "action": "idle|wave|dance_01|think|nod|shrug",
                 "duration_ms": 500
               }
               Use complete, natural sentences. It's okay to be longer when needed.
               duration_ms must be an integer between 100 and 5000.
               """;
    }

    private static string ExtractAssistantContent(string completionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(completionJson);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return completionJson;
            }

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                return completionJson;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object &&
                        part.TryGetProperty("text", out var textEl) &&
                        textEl.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(textEl.GetString());
                    }
                }
                return sb.ToString();
            }

            return content.ToString();
        }
        catch
        {
            return completionJson;
        }
    }
}
