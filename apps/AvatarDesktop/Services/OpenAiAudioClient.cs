using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AvatarDesktop.Models;

namespace AvatarDesktop.Services;

public interface IOpenAiAudioClient
{
    Task<AudioTranscriptionResult> TranscribeAsync(ChatGptVoiceConfig config, byte[] wavBytes, CancellationToken cancellationToken = default);
    Task<AudioSpeechResult> SynthesizeSpeechAsync(ChatGptVoiceConfig config, string text, CancellationToken cancellationToken = default);
}

public sealed class OpenAiAudioClient : IOpenAiAudioClient
{
    private readonly HttpClient _httpClient;

    public OpenAiAudioClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<AudioTranscriptionResult> TranscribeAsync(ChatGptVoiceConfig config, byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (wavBytes.Length == 0)
        {
            return new AudioTranscriptionResult
            {
                Success = false,
                Message = "Recorded audio is empty",
            };
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new AudioTranscriptionResult
            {
                Success = false,
                Message = "API key is empty",
            };
        }

        using var request = CreateRequest(config, HttpMethod.Post, "/audio/transcriptions");
        using var form = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        form.Add(fileContent, "file", "mic.wav");

        form.Add(new StringContent(config.TranscriptionModel), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(config.TranscriptionLanguage))
        {
            form.Add(new StringContent(config.TranscriptionLanguage.Trim()), "language");
        }

        request.Content = form;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new AudioTranscriptionResult
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    RawResponse = raw,
                };
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var text = doc.RootElement.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(text))
                {
                    return new AudioTranscriptionResult
                    {
                        Success = false,
                        Message = "Transcription returned empty text",
                        RawResponse = raw,
                    };
                }

                return new AudioTranscriptionResult
                {
                    Success = true,
                    Message = "OK",
                    Text = text.Trim(),
                    RawResponse = raw,
                };
            }
            catch (Exception ex)
            {
                return new AudioTranscriptionResult
                {
                    Success = false,
                    Message = $"Failed to parse transcription response: {ex.Message}",
                    RawResponse = raw,
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AudioTranscriptionResult
            {
                Success = false,
                Message = "Timeout waiting for transcription",
            };
        }
        catch (Exception ex)
        {
            return new AudioTranscriptionResult
            {
                Success = false,
                Message = ex.Message,
            };
        }
    }

    public async Task<AudioSpeechResult> SynthesizeSpeechAsync(ChatGptVoiceConfig config, string text, CancellationToken cancellationToken = default)
    {
        var input = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return new AudioSpeechResult
            {
                Success = false,
                Message = "Empty TTS input",
            };
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new AudioSpeechResult
            {
                Success = false,
                Message = "API key is empty",
            };
        }

        using var request = CreateRequest(config, HttpMethod.Post, "/audio/speech");
        request.Content = JsonContent.Create(new
        {
            model = config.TtsModel,
            voice = config.TtsVoice,
            input,
            response_format = "mp3"
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                return new AudioSpeechResult
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {TrimApiError(raw)}",
                };
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
            return new AudioSpeechResult
            {
                Success = true,
                Message = "OK",
                AudioBytes = bytes,
                ContentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg",
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AudioSpeechResult
            {
                Success = false,
                Message = "Timeout waiting for TTS audio",
            };
        }
        catch (Exception ex)
        {
            return new AudioSpeechResult
            {
                Success = false,
                Message = ex.Message,
            };
        }
    }

    private HttpRequestMessage CreateRequest(ChatGptVoiceConfig config, HttpMethod method, string relativePath)
    {
        var baseUrl = ChatGptVoiceConfigService.NormalizeBaseUrl(config.BaseUrl);
        var request = new HttpRequestMessage(method, new Uri(baseUrl.TrimEnd('/') + relativePath, UriKind.Absolute));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
        return request;
    }

    private static string TrimApiError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var compact = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 220 ? compact : compact[..220] + "...";
    }
}
