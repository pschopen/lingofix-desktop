using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Lingofix.Backend.Documents;

public sealed class LlmClient
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _prompt;
    private readonly string _systemPromptOverride;
    private readonly double _temperature;
    private readonly IRunLogger? _logger;
    private string _apiKey = string.Empty;

    public LlmClient(string apiBase, string model, string prompt, string systemPromptOverride, double temperature, IRunLogger? logger = null)
    {
        _endpoint = ApiEndpointBuilder.Build(apiBase);
        _model = model;
        _prompt = prompt;
        _systemPromptOverride = systemPromptOverride ?? string.Empty;
        _temperature = temperature;
        _logger = logger;
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public async Task<string> CorrectAsync(string input, CancellationToken cancellationToken = default)
    {
        var baseRequest = new ChatCompletionsRequest
        {
            Model = _model,
            Messages =
            [
                new ChatMessage("user", BuildUserPrompt(_prompt, _systemPromptOverride, input))
            ],
            Temperature = _temperature
        };

        return await SendWithTemperatureFallbackAsync(baseRequest, cancellationToken);
    }

    public void ApplyAuth(string apiKey)
    {
        _apiKey = apiKey?.Trim() ?? string.Empty;
    }

    private static string BuildUserPrompt(string customPrompt, string systemPrompt, string input)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            parts.Add(customPrompt.Trim());
        }
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            parts.Add(systemPrompt.Trim());
        }
        parts.Add(input);
        return string.Join("\n\n", parts);
    }

    private static string SanitizeCorrection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
        text = StripMarkdown(text);
        if (text.Length >= 2 &&
            ((text.StartsWith("\"") && text.EndsWith("\"")) || (text.StartsWith("'") && text.EndsWith("'"))))
        {
            text = text.Substring(1, text.Length - 2).Trim();
        }

        text = StripLeadingNote(text);
        return text.Trim();
    }

    private static string StripMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // Remove fenced code blocks, keep inner content.
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "```[\\s\\S]*?```",
            m =>
            {
                var inner = m.Value;
                inner = inner.Trim('`', '\r', '\n');
                var firstNewline = inner.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    inner = inner.Substring(firstNewline + 1);
                }
                return inner;
            });

        // Remove inline code backticks.
        text = System.Text.RegularExpressions.Regex.Replace(text, "`([^`]*)`", "$1");

        // Convert markdown links to just their text.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[(?<t>[^\]]+)\]\([^)]+\)", "${t}");

        // Remove bold/italic markers.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\*\*|__)(.*?)\1", "$2");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\*|_)(.*?)\1", "$2");

        // Strip common markdown prefixes at line starts.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^[ \t]*([#>*-]|\d+\.)[ \t]+", string.Empty);

        return text.Trim();
    }

    private static string StripLeadingNote(string text)
    {
        var trimmed = text.TrimStart();
        var hintIndex = trimmed.IndexOf("Note:", StringComparison.OrdinalIgnoreCase);
        if (hintIndex < 0 || hintIndex > 200)
        {
            return text;
        }

        var end = trimmed.IndexOf(')', hintIndex);
        if (end < 0)
        {
            end = trimmed.IndexOf('\n', hintIndex);
        }

        if (end < 0)
        {
            return text;
        }

        var remainder = trimmed.Substring(end + 1).TrimStart('.', ' ', '\t', '\r', '\n', '*');
        return remainder.Length == 0 ? text : remainder;
    }

    private static int? GetRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta.HasValue)
        {
            var seconds = (int)Math.Ceiling(retryAfter.Delta.Value.TotalSeconds);
            return Math.Max(1, seconds);
        }

        if (retryAfter.Date.HasValue)
        {
            var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            var seconds = (int)Math.Ceiling(delta.TotalSeconds);
            return Math.Max(1, seconds);
        }

        return null;
    }

    private static int GetRetryDelay(int attempt, int? retryAfterSeconds)
    {
        var baseDelay = 500 * attempt;
        if (retryAfterSeconds.HasValue)
        {
            return Math.Max(baseDelay, retryAfterSeconds.Value * 1000);
        }

        return baseDelay;
    }

    private async Task<string> SendWithTemperatureFallbackAsync(ChatCompletionsRequest request, CancellationToken cancellationToken)
    {
        var allowTemperatureFallback = true;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var payload = JsonSerializer.Serialize(request, JsonOptions.Default);
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            using var response = await SharedHttpClient.SendAsync(message, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var parsed = JsonSerializer.Deserialize<ChatCompletionsResponse>(responseBody, JsonOptions.Default);
                var result = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
                return SanitizeCorrection(result);
            }

            var retryAfterSeconds = GetRetryAfterSeconds(response);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                if (attempt == 3)
                {
                    throw new LlmRateLimitException(retryAfterSeconds, responseBody);
                }

                await Task.Delay(GetRetryDelay(attempt, retryAfterSeconds), cancellationToken);
                continue;
            }

            if (allowTemperatureFallback && IsTemperatureUnsupported(responseBody))
            {
                _logger?.Info("Note: temperature not accepted by the model. Retrying without temperature.");
                request.Temperature = null;
                allowTemperatureFallback = false;
                attempt = 0;
                continue;
            }

            if (attempt == 3)
            {
                throw new InvalidOperationException($"LLM error: {response.StatusCode} - {responseBody}");
            }

            await Task.Delay(GetRetryDelay(attempt, retryAfterSeconds), cancellationToken);
        }

        throw new InvalidOperationException("LLM request failed after retries.");
    }

    private static bool IsTemperatureUnsupported(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        var text = responseBody.ToLowerInvariant();
        return text.Contains("temperature") &&
               (text.Contains("unsupported") || text.Contains("not supported") || text.Contains("not allowed") || text.Contains("invalid"));
    }
}

public sealed class LlmRateLimitException : Exception
{
    public int? RetryAfterSeconds { get; }

    public LlmRateLimitException(int? retryAfterSeconds, string message)
        : base($"Rate limit reached. Retry-After: {(retryAfterSeconds?.ToString() ?? "n/a")}. {message}".Trim())
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}
