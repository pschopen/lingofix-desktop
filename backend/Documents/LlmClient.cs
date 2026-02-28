using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;

namespace Lingofix.Backend.Documents;

public sealed class LlmClient
{
    private const int TemperatureSupportUnknown = 0;
    private const int TemperatureSupportSupported = 1;
    private const int TemperatureSupportUnsupported = 2;
    private const int ReasoningSupportUnknown = 0;
    private const int ReasoningSupportSupported = 1;
    private const int ReasoningSupportUnsupported = 2;

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly string _endpoint;
    private readonly string _capabilityCacheKey;
    private readonly bool _isOllama;
    private readonly string _model;
    private readonly string _prompt;
    private readonly string _systemPromptOverride;
    private readonly double _temperature;
    private readonly IRunLogger? _logger;
    private string _apiKey = string.Empty;
    private int _temperatureSupport = TemperatureSupportUnknown;
    private int _reasoningSupport = ReasoningSupportUnknown;
    public LlmClient(
        string provider,
        string apiBase,
        string model,
        string prompt,
        string systemPromptOverride,
        double temperature,
        bool? temperatureSupportedHint,
        bool? reasoningEffortSupportedHint,
        IRunLogger? logger = null)
    {
        _isOllama = string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase);
        _endpoint = BuildEditorCompatibleEndpoint(apiBase, _isOllama);
        _capabilityCacheKey = BuildCapabilityCacheKey(provider, apiBase, model);
        _model = model;
        _prompt = prompt;
        _systemPromptOverride = systemPromptOverride ?? string.Empty;
        _temperature = temperature;
        _temperatureSupport = ToSupportState(temperatureSupportedHint);
        _reasoningSupport = ToSupportState(reasoningEffortSupportedHint);
        _logger = logger;
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    private static string BuildEditorCompatibleEndpoint(string apiBase, bool isOllama)
    {
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            throw new InvalidOperationException(
                "Invalid settings: api_url is missing. Open Settings > Advanced and use 'Reset app'.");
        }

        var trimmed = apiBase.Trim().TrimEnd('/');
        return isOllama ? $"{trimmed}/api/chat" : $"{trimmed}/chat/completions";
    }

    private static string BuildCapabilityCacheKey(string provider, string apiBase, string model)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedApiBase = (apiBase ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        var normalizedModel = (model ?? string.Empty).Trim().ToLowerInvariant();
        return $"{normalizedProvider}|{normalizedApiBase}|{normalizedModel}";
    }

    private static int ToSupportState(bool? supported)
    {
        if (!supported.HasValue)
        {
            return TemperatureSupportUnknown;
        }

        return supported.Value ? TemperatureSupportSupported : TemperatureSupportUnsupported;
    }

    public async Task<string> CorrectAsync(string input, CancellationToken cancellationToken = default)
    {
        var prompt = BuildSimplePrompt(_prompt, _systemPromptOverride, input);
        var baseRequest = new ChatCompletionsRequest
        {
            Model = _model,
            Messages =
            [
                new ChatMessage("user", prompt)
            ],
            Temperature = _temperature,
            Stream = false
        };

        return await SendWithTemperatureFallbackAsync(
            baseRequest,
            sanitizeOutput: true,
            cancellationToken: cancellationToken);
    }

    public async Task<string> CorrectBatchAsync(string input, string _batchPrompt, CancellationToken cancellationToken = default)
    {
        var prompt = BuildSimplePrompt(_prompt, _systemPromptOverride, input);
        var baseRequest = new ChatCompletionsRequest
        {
            Model = _model,
            Messages =
            [
                new ChatMessage("user", prompt)
            ],
            Temperature = _temperature,
            Stream = false
        };

        return await SendWithTemperatureFallbackAsync(
            baseRequest,
            sanitizeOutput: false,
            cancellationToken: cancellationToken,
            maxAttempts: 1,
            allowTemperatureFallback: true,
            allowReasoningFallback: true);
    }

    public void ApplyAuth(string apiKey)
    {
        _apiKey = apiKey?.Trim() ?? string.Empty;
    }

    private static string BuildSimplePrompt(string customPrompt, string systemPrompt, string text, string? extraPrompt = null)
    {
        var parts = new List<string>();
        var promptLineParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            promptLineParts.Add(customPrompt.Trim());
        }
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            promptLineParts.Add(systemPrompt.Trim());
        }

        if (promptLineParts.Count > 0)
        {
            parts.Add(string.Join(" ", promptLineParts));
        }

        if (!string.IsNullOrWhiteSpace(extraPrompt))
        {
            parts.Add(extraPrompt.Trim());
        }

        parts.Add($"Text:\n{text}");
        return string.Join("\n\n", parts);
    }

    internal static string SanitizeCorrection(string? value)
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
        text = XmlTextSanitizer.StripInvalidXmlChars(text, out _);
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

    private async Task<string> SendWithTemperatureFallbackAsync(
        ChatCompletionsRequest request,
        bool sanitizeOutput,
        CancellationToken cancellationToken = default,
        int maxAttempts = 3,
        bool allowTemperatureFallback = true,
        bool allowReasoningFallback = true,
        bool trimOutputWhenNotSanitized = true)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        var includeTemperature = request.Temperature.HasValue && Volatile.Read(ref _temperatureSupport) != TemperatureSupportUnsupported;
        var includeReasoningEffort = !_isOllama && Volatile.Read(ref _reasoningSupport) != ReasoningSupportUnsupported;

        var allowTemperatureFallbackRetry = allowTemperatureFallback && includeTemperature;
        var allowReasoningFallbackRetry = allowReasoningFallback && includeReasoningEffort;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var effectiveRequest = new ChatCompletionsRequest
            {
                Model = request.Model,
                Messages = request.Messages,
                Stream = request.Stream,
                Temperature = includeTemperature ? request.Temperature : null,
                ReasoningEffort = includeReasoningEffort ? "none" : null
            };

            LogRequestPayload(effectiveRequest, attempt);
            var payload = JsonSerializer.Serialize(effectiveRequest, JsonOptions.Default);
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
                if (includeTemperature)
                {
                    Volatile.Write(ref _temperatureSupport, TemperatureSupportSupported);
                    EmitCapabilityLog("temperature", true);
                }

                if (includeReasoningEffort)
                {
                    Volatile.Write(ref _reasoningSupport, ReasoningSupportSupported);
                    EmitCapabilityLog("reasoning_effort", true);
                }

                var result = ExtractCompletionText(responseBody);
                LogResponsePayload(result, attempt);

                var finalResult = sanitizeOutput
                    ? SanitizeCorrection(result)
                    : (trimOutputWhenNotSanitized ? result.Trim() : result);
                if (!string.Equals(result, finalResult, StringComparison.Ordinal))
                {
                    _logger?.Info($"LLM response after post-processing (attempt {attempt}):\n{finalResult}");
                }

                return finalResult;
            }

            var retryAfterSeconds = GetRetryAfterSeconds(response);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                if (attempt == maxAttempts)
                {
                    throw new LlmRateLimitException(retryAfterSeconds, responseBody);
                }

                await Task.Delay(GetRetryDelay(attempt, retryAfterSeconds), cancellationToken);
                continue;
            }

            if (allowTemperatureFallbackRetry && IsTemperatureUnsupported(responseBody))
            {
                _logger?.Info("Note: temperature not accepted by the model. Retrying without temperature.");
                includeTemperature = false;
                allowTemperatureFallbackRetry = false;
                Volatile.Write(ref _temperatureSupport, TemperatureSupportUnsupported);
                EmitCapabilityLog("temperature", false);
                attempt = 0;
                continue;
            }

            if (allowReasoningFallbackRetry && IsReasoningOrThinkingError(responseBody))
            {
                _logger?.Info("Note: reasoning_effort not accepted by the model. Retrying without reasoning_effort.");
                includeReasoningEffort = false;
                allowReasoningFallbackRetry = false;
                Volatile.Write(ref _reasoningSupport, ReasoningSupportUnsupported);
                EmitCapabilityLog("reasoning_effort", false);
                attempt = 0;
                continue;
            }

            if (attempt == maxAttempts)
            {
                throw new InvalidOperationException($"LLM error: {response.StatusCode} - {responseBody}");
            }

            await Task.Delay(GetRetryDelay(attempt, retryAfterSeconds), cancellationToken);
        }

        throw new InvalidOperationException("LLM request failed after retries.");
    }

    private void LogRequestPayload(ChatCompletionsRequest request, int attempt)
    {
        if (_logger is null || request.Messages.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append($"LLM request payload (attempt {attempt}):\n");
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            builder.Append(message.Content);
            if (i < request.Messages.Count - 1)
            {
                builder.Append("\n\n");
            }
        }

        _logger.Info(builder.ToString());
    }

    private void LogResponsePayload(string responseText, int attempt)
    {
        _logger?.Info($"LLM response payload (attempt {attempt}):\n{responseText}");
    }

    private void EmitCapabilityLog(string capability, bool supported)
    {
        _logger?.Info($"LLM capability update: key={_capabilityCacheKey}; capability={capability}; supported={supported.ToString().ToLowerInvariant()}");
    }

    private static bool IsTemperatureUnsupported(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        var text = responseBody.ToLowerInvariant();
        if (text.Contains("unsupported_parameter") || text.Contains("param\":\"temperature"))
        {
            return true;
        }

        if (TryGetErrorText(responseBody, out var errorText))
        {
            text = errorText.ToLowerInvariant();
        }

        return text.Contains("temperature") &&
               (text.Contains("unsupported") ||
                text.Contains("not supported") ||
                text.Contains("does not support") ||
                text.Contains("not allowed") ||
                text.Contains("invalid"));
    }

    private static bool IsReasoningOrThinkingError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        var text = responseBody.ToLowerInvariant();
        if (TryGetErrorText(responseBody, out var errorText))
        {
            text = errorText.ToLowerInvariant();
        }

        return text.Contains("reasoning") || text.Contains("thinking");
    }

    private static bool TryGetErrorText(string responseBody, out string errorText)
    {
        errorText = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                var parts = new List<string>();
                if (errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
                {
                    parts.Add(messageElement.GetString() ?? string.Empty);
                }

                if (errorElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                {
                    parts.Add(typeElement.GetString() ?? string.Empty);
                }

                if (errorElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                {
                    parts.Add(codeElement.GetString() ?? string.Empty);
                }

                if (errorElement.TryGetProperty("param", out var paramElement) && paramElement.ValueKind == JsonValueKind.String)
                {
                    parts.Add(paramElement.GetString() ?? string.Empty);
                }

                errorText = string.Join(" ", parts.Where(static p => !string.IsNullOrWhiteSpace(p)));
            }
            else
            {
                errorText = responseBody;
            }

            return !string.IsNullOrWhiteSpace(errorText);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractCompletionText(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (TryExtractTextFromChoices(root, out var textFromChoices))
            {
                return textFromChoices;
            }

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content) &&
                TryReadContentElement(content, out var messageText))
            {
                return messageText;
            }

            if (TryExtractTextFromOutput(root, out var textFromOutput))
            {
                return textFromOutput;
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private static bool TryExtractTextFromChoices(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var firstChoice = choices.EnumerateArray().FirstOrDefault();
        if (firstChoice.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (firstChoice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (message.TryGetProperty("content", out var content))
            {
                if (TryReadContentElement(content, out text))
                {
                    return true;
                }
            }
        }

        if (firstChoice.TryGetProperty("text", out var legacyText) && legacyText.ValueKind == JsonValueKind.String)
        {
            text = legacyText.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool TryExtractTextFromOutput(JsonElement root, out string text)
    {
        text = string.Empty;

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            text = outputText.GetString() ?? string.Empty;
            return true;
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (TryReadContentElement(contentItem, out var segment) && !string.IsNullOrEmpty(segment))
                {
                    parts.Add(segment);
                }
            }
        }

        text = string.Concat(parts);
        return parts.Count > 0;
    }

    private static bool TryReadContentElement(JsonElement content, out string text)
    {
        text = string.Empty;

        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? string.Empty;
            return true;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (TryReadContentElement(item, out var segment) && !string.IsNullOrEmpty(segment))
                {
                    parts.Add(segment);
                }
            }

            text = string.Concat(parts);
            return parts.Count > 0;
        }

        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (content.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
        {
            text = directText.GetString() ?? string.Empty;
            return true;
        }

        if (content.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString() ?? string.Empty;
            return true;
        }

        if (content.TryGetProperty("content", out var nestedContent) && TryReadContentElement(nestedContent, out var nestedText))
        {
            text = nestedText;
            return true;
        }

        return false;
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
