using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;

namespace Lingofix.Backend.Documents;

public sealed class LlmClient
{
    private const string ReasoningUnsupportedErrorCode = "REASONING_UNSUPPORTED";
    private const int TemperatureSupportUnknown = 0;
    private const int TemperatureSupportSupported = 1;
    private const int TemperatureSupportUnsupported = 2;
    private const int ReasoningSupportUnknown = 0;
    private const int ReasoningSupportSupported = 1;
    private const int ReasoningSupportUnsupported = 2;
    private const int MaxRateLimitAttempts = 20;

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly string _endpoint;
    private readonly string _capabilityCacheKey;
    private readonly bool _isOllama;
    private readonly string _model;
    private readonly string _prompt;
    private readonly string _systemPromptOverride;
    private readonly double _temperature;
    private readonly bool _enableReasoning;
    private readonly string _reasoningEffort;
    private readonly IRunLogger? _logger;
    private readonly AdaptiveRateLimiter _rateLimiter = new();
    private string _apiKey = string.Empty;
    private int _temperatureSupport = TemperatureSupportUnknown;
    private int _reasoningSupport = ReasoningSupportUnknown;
    private bool _rateLearningEnabled;
    private int _loggedSuccessHeaders;
    private int _loggedThrottleHeaders;
    private readonly object _latencySync = new();
    private double _latencyEmaMs;
    private bool _hasLatencySample;
    private const double LatencyEmaWeight = 0.3;
    public LlmClient(
        string provider,
        string apiBase,
        string model,
        string prompt,
        string systemPromptOverride,
        double temperature,
        bool enableReasoning,
        string reasoningEffort,
        bool? temperatureSupportedHint,
        bool? reasoningEffortSupportedHint,
        SpeedMode speedMode = SpeedMode.Auto,
        int? manualRequestsPerMinute = null,
        double? rateHintIntervalMs = null,
        IRunLogger? logger = null)
    {
        _isOllama = string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase);
        _endpoint = BuildEditorCompatibleEndpoint(apiBase, _isOllama);
        _capabilityCacheKey = BuildCapabilityCacheKey(provider, apiBase, model);
        _model = model;
        _prompt = prompt;
        _systemPromptOverride = systemPromptOverride ?? string.Empty;
        _temperature = temperature;
        _enableReasoning = enableReasoning;
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? "low" : reasoningEffort.Trim().ToLowerInvariant();
        _temperatureSupport = ToSupportState(temperatureSupportedHint);
        _reasoningSupport = ToSupportState(reasoningEffortSupportedHint);
        _logger = logger;

        if (_isOllama)
        {
            // Local server, no server-side rate limit: never pace.
            _rateLimiter.Configure(pacingEnabled: false, learnFloor: false, hardMinIntervalMs: 0);
        }
        else if (speedMode == SpeedMode.Manual)
        {
            var hardMin = manualRequestsPerMinute is > 0 ? 60_000.0 / manualRequestsPerMinute.Value : 0;
            _rateLimiter.Configure(pacingEnabled: true, learnFloor: false, hardMinIntervalMs: hardMin);
        }
        else
        {
            _rateLimiter.Configure(pacingEnabled: true, learnFloor: true, hardMinIntervalMs: 0);
            _rateLearningEnabled = true;

            // Session memory: seed the limiter with the interval learned earlier this
            // session (fed back in by the Tauri host) so the run skips re-calibration.
            if (rateHintIntervalMs is > 0)
            {
                _rateLimiter.Seed(rateHintIntervalMs.Value);
            }
        }
    }

    /// <summary>Current shared pacing interval in ms (0 = unthrottled).</summary>
    public double CurrentPacingIntervalMs => _rateLimiter.CurrentIntervalMs;

    /// <summary>
    /// Moving average of observed HTTP round-trip latency in ms. Used (in Auto mode) to
    /// derive how many workers are needed to keep the pacing interval saturated.
    /// </summary>
    public double AverageLatencyMs
    {
        get { lock (_latencySync) { return _latencyEmaMs; } }
    }

    /// <summary>
    /// Emits the interval the limiter converged on this run so the host can keep it as
    /// in-memory session memory and seed the next run of the same provider/model. No-op
    /// when nothing was learned (interval still zero, i.e. never throttled).
    /// </summary>
    public void EmitRateUpdateLog()
    {
        // Only Auto mode learns a rate worth remembering. Manual's interval is the user's
        // fixed value (plus transient backoff), so we do not feed it back as session memory.
        if (!_rateLearningEnabled)
        {
            return;
        }

        var interval = _rateLimiter.CurrentIntervalMs;
        if (interval <= 0)
        {
            return;
        }

        _logger?.Info($"LLM rate update: key={_capabilityCacheKey}; interval_ms={(long)Math.Round(interval)}");
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

    public async Task<string> CorrectAsync(string input, IConcurrencyGate? gate = null, CancellationToken cancellationToken = default)
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
            gate: gate,
            cancellationToken: cancellationToken);
    }

    public async Task<string> CorrectBatchAsync(string input, string _batchPrompt, IConcurrencyGate? gate = null, CancellationToken cancellationToken = default)
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
            gate: gate,
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

    private static int? GetRetryAfterSeconds(HttpResponseMessage response, string? responseBody = null)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is not null)
        {
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
        }

        // Gemini returns no Retry-After header; the wait lives in the JSON body as a
        // RetryInfo.retryDelay (e.g. "6s"). Honor it as a hard floor like a real header.
        return TryParseGeminiRetryDelaySeconds(responseBody);
    }

    /// <summary>
    /// Pulls the Google RetryInfo <c>retryDelay</c> (e.g. "6s", "1.5s", "600ms") out of a
    /// Gemini-style error body and returns it in whole seconds (rounded up, min 1).
    /// </summary>
    internal static int? TryParseGeminiRetryDelaySeconds(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("details", out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object &&
                    detail.TryGetProperty("retryDelay", out var retryDelay) &&
                    retryDelay.ValueKind == JsonValueKind.String)
                {
                    return ParseDurationToSeconds(retryDelay.GetString());
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static int? ParseDurationToSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        double value;
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(text[..^2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return null;
            }

            return Math.Max(1, (int)Math.Ceiling(value / 1000.0));
        }

        if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1].Trim();
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        return Math.Max(1, (int)Math.Ceiling(value));
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
        IConcurrencyGate? gate = null,
        CancellationToken cancellationToken = default,
        int maxAttempts = 3,
        bool allowTemperatureFallback = true,
        bool allowReasoningFallback = true,
        bool trimOutputWhenNotSanitized = true)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        var includeTemperature = request.Temperature.HasValue && Volatile.Read(ref _temperatureSupport) != TemperatureSupportUnsupported;
        var includeReasoningEffort = _enableReasoning && !_isOllama && Volatile.Read(ref _reasoningSupport) != ReasoningSupportUnsupported;

        var allowTemperatureFallbackRetry = allowTemperatureFallback && includeTemperature;
        var allowReasoningFallbackRetry = allowReasoningFallback && includeReasoningEffort;
        var rateLimitAttempts = 0;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await _rateLimiter.WaitAsync(cancellationToken);

            var effectiveRequest = new ChatCompletionsRequest
            {
                Model = request.Model,
                Messages = request.Messages,
                Stream = request.Stream,
                Temperature = includeTemperature ? request.Temperature : null,
                ReasoningEffort = includeReasoningEffort ? _reasoningEffort : null
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

            // The concurrency slot wraps only the in-flight HTTP send, not the pacing
            // WaitAsync above, so a slow pace never ties up a slot and starves throughput.
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken);
            }

            HttpResponseMessage response;
            string responseBody;
            var sendStopwatch = Stopwatch.StartNew();
            try
            {
                response = await SharedHttpClient.SendAsync(message, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            finally
            {
                gate?.Release();
            }
            sendStopwatch.Stop();

            using (response)
            {
            // Step 0 for provider rate signals: capture the response headers once for a 2xx
            // and once for a throttle, so a real run reveals which vendor headers exist
            // (x-ratelimit-*, retry hints, ...) before committing to parsing any of them.
            LogResponseHeadersOnce(response);

            // If the provider states a hard request/minute ceiling in its headers (Mistral
            // does, on every response), pace directly to it instead of discovering it via
            // repeated 429s. Applied for successes and throttles alike, so the floor is set
            // as early as possible.
            if (TryGetRequestsPerMinuteLimit(response, out var advertisedReqPerMinute))
            {
                _rateLimiter.ApplyAuthoritativeLimit(advertisedReqPerMinute);
            }

            if (response.IsSuccessStatusCode)
            {
                RecordLatency(sendStopwatch.Elapsed.TotalMilliseconds);
                _rateLimiter.OnSuccess();

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

            var retryAfterSeconds = GetRetryAfterSeconds(response, responseBody);
            if (IsThrottleStatus(response.StatusCode))
            {
                rateLimitAttempts++;

                // Slow the shared pacing down. The limiter also pushes the next global
                // slot past any Retry-After, so the WaitAsync at the top of the loop is
                // the single place that waits before we retry (no separate Task.Delay).
                // 503/529 (overload) share this path with 429: a common counter is a
                // deliberate simplification — overload is throttled the same as a hard limit.
                _rateLimiter.OnRateLimited(retryAfterSeconds);

                if (rateLimitAttempts > MaxRateLimitAttempts)
                {
                    throw new LlmRateLimitException(retryAfterSeconds, responseBody);
                }

                _logger?.Info($"Rate limit/overload hit (status {(int)response.StatusCode}, attempt {rateLimitAttempts}/{MaxRateLimitAttempts}); interval_ms={(long)Math.Round(_rateLimiter.CurrentIntervalMs)}. Slowing down and retrying.");

                // Rate-limit retries are unbounded relative to maxAttempts; only the
                // dedicated rate-limit counter above governs when to give up.
                attempt = 0;
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
                Volatile.Write(ref _reasoningSupport, ReasoningSupportUnsupported);
                EmitCapabilityLog("reasoning_effort", false);

                if (_enableReasoning)
                {
                    throw new InvalidOperationException(
                        $"{ReasoningUnsupportedErrorCode}: reasoning_effort is not supported by this model/provider. {responseBody}");
                }

                _logger?.Info("Note: reasoning_effort not accepted by the model. Retrying without reasoning_effort.");
                includeReasoningEffort = false;
                allowReasoningFallbackRetry = false;
                attempt = 0;
                continue;
            }

            if (attempt == maxAttempts)
            {
                throw new InvalidOperationException($"LLM error: {response.StatusCode} - {responseBody}");
            }

            await Task.Delay(GetRetryDelay(attempt, retryAfterSeconds), cancellationToken);
            }
        }

        throw new InvalidOperationException("LLM request failed after retries.");
    }

    /// <summary>
    /// Reads a provider's advertised per-minute request ceiling from the response headers,
    /// when one is present. Mistral returns <c>x-ratelimit-limit-req-minute</c> on every
    /// response; Gemini and Ollama send no such header, so those keep the purely reactive
    /// (429-driven) pacing.
    /// </summary>
    private static bool TryGetRequestsPerMinuteLimit(HttpResponseMessage response, out double requestsPerMinute)
    {
        requestsPerMinute = 0;
        if (!response.Headers.TryGetValues("x-ratelimit-limit-req-minute", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                requestsPerMinute = parsed;
                return true;
            }
        }

        return false;
    }

    private void LogResponseHeadersOnce(HttpResponseMessage response)
    {
        if (_logger is null)
        {
            return;
        }

        var isThrottle = IsThrottleStatus(response.StatusCode);
        var isSuccess = response.IsSuccessStatusCode;
        if (!isThrottle && !isSuccess)
        {
            return;
        }

        // Only the first sample of each kind, and only once per client instance.
        if (isThrottle)
        {
            if (Interlocked.Exchange(ref _loggedThrottleHeaders, 1) != 0)
            {
                return;
            }
        }
        else if (Interlocked.Exchange(ref _loggedSuccessHeaders, 1) != 0)
        {
            return;
        }

        var headers = response.Headers.Concat(response.Content.Headers);
        var parts = new List<string>();
        foreach (var header in headers)
        {
            var name = header.Key;
            // Never echo credential-bearing response headers.
            if (name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{name}=<redacted>");
                continue;
            }

            var value = string.Join(",", header.Value);
            if (value.Length > 120)
            {
                value = value[..120] + "…";
            }

            parts.Add($"{name}={value}");
        }

        var line = string.Join("; ", parts);
        if (line.Length > 1600)
        {
            line = line[..1600] + "…";
        }

        _logger.Info($"Response headers ({(int)response.StatusCode}, {(isThrottle ? "throttle" : "success")}): {line}");
    }

    private void RecordLatency(double sampleMs)
    {
        if (sampleMs <= 0)
        {
            return;
        }

        lock (_latencySync)
        {
            _latencyEmaMs = _hasLatencySample
                ? (LatencyEmaWeight * sampleMs) + ((1 - LatencyEmaWeight) * _latencyEmaMs)
                : sampleMs;
            _hasLatencySample = true;
        }
    }

    private static bool IsThrottleStatus(HttpStatusCode status)
    {
        var code = (int)status;
        // 429 Too Many Requests, 503 Service Unavailable, 529 Overloaded (Anthropic/others).
        return code == 429 || code == 503 || code == 529;
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

/// <summary>
/// Bounds the number of concurrently in-flight HTTP requests. Kept separate from the
/// rate limiter (which governs *when* a request may start) so a slot is only held for
/// the actual send, never across the pacing wait — otherwise throughput would collapse
/// to maxParallel/latency, below the provider's real rate limit.
/// </summary>
public interface IConcurrencyGate
{
    Task WaitAsync(CancellationToken cancellationToken);
    void Release();
}

/// <summary>
/// Paces requests to a shared minimum interval so all parallel workers of one run
/// converge on a provider's actual rate limit (e.g. Mistral Large's ~0.6 req/s)
/// instead of hammering it on every retry.
///
/// Control law is AIMD (multiplicative increase on 429, additive decrease after a
/// streak of successes). Two additions keep it converging to a *plateau* instead of
/// oscillating back to "unthrottled":
///
///  * A learned floor — the smallest interval that has survived a full success streak.
///    Decay stops just below it (at <see cref="FloorProbeRatio"/> of the floor) so the
///    limiter gently probes for a relaxed limit but never crashes back to zero and runs
///    straight into the limit again. A 429 raises the floor to the interval that just
///    failed.
///  * Jitter on slot spacing so parallel workers desynchronise instead of bursting at
///    exact slot boundaries.
///
/// Growth is debounced to once per penalty window so a burst of simultaneous 429s from a
/// single overload event only slows us down once, not once per concurrent request.
/// Retry-After is honored as a hard floor for the next global slot even when it exceeds
/// the pacing cap, so we never retry before the server said we may.
///
/// The clock, jitter and delay are injectable so convergence is deterministically
/// testable without real time.
/// </summary>
internal sealed class AdaptiveRateLimiter
{
    private const double MaxIntervalMs = 15_000;
    private const double InitialPenaltyMs = 250;
    private const double IncreaseFactor = 2.0;
    private const double DecreaseStepMs = 150;
    private const int SuccessesBeforeDecay = 20;
    private const double FloorProbeRatio = 0.9;
    private const double JitterFraction = 0.25;

    // Auto mode opens with this gentle non-zero spacing instead of interval 0, so the very
    // first wave of workers is paced apart rather than fired as a simultaneous burst that
    // blows a small per-minute budget (e.g. Mistral's 15/min) before any header is seen.
    private const double AutoStartIntervalMs = 500;

    // When a provider advertises a hard request/minute ceiling in its headers we pace to
    // 60000/limit, but leave a margin below the raw limit: slot jitter (±JitterFraction)
    // and the server's rolling-minute window would otherwise let occasional bursts cross
    // the limit and trip needless 429s. Targeting ~91% of the ceiling absorbs that.
    private const double HeaderReqSafetyFactor = 1.1;

    private readonly object _sync = new();
    private readonly Func<TimeSpan> _now;
    private readonly Func<double> _nextJitter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private double _intervalMs;
    // Smallest interval known to survive a full success streak; 0 = not yet learned.
    private double _learnedFloorMs;
    // Authoritative lower bound derived from a provider-advertised req/minute ceiling
    // (see ApplyAuthoritativeLimit); 0 = provider sends no such header. The interval is
    // never paced below this, so we stop probing past a limit the server stated outright.
    private double _authoritativeFloorMs;
    private TimeSpan _nextSlot = TimeSpan.Zero;
    private TimeSpan _growthLockedUntil = TimeSpan.MinValue;
    private int _consecutiveSuccesses;

    // Mode configuration (see Configure). Defaults are the Auto profile.
    private bool _pacingEnabled = true;
    private bool _learnFloor = true;
    private double _hardMinIntervalMs;

    public AdaptiveRateLimiter()
        : this(CreateStopwatchClock(), static () => Random.Shared.NextDouble(), static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal AdaptiveRateLimiter(
        Func<TimeSpan> now,
        Func<double> nextJitter,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _now = now;
        _nextJitter = nextJitter;
        _delay = delay;
    }

    private static Func<TimeSpan> CreateStopwatchClock()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }

    internal double CurrentIntervalMs
    {
        get { lock (_sync) { return _intervalMs; } }
    }

    internal double LearnedFloorMs
    {
        get { lock (_sync) { return _learnedFloorMs; } }
    }

    internal double AuthoritativeFloorMs
    {
        get { lock (_sync) { return _authoritativeFloorMs; } }
    }

    /// <summary>
    /// Selects the control profile:
    ///  * Auto:   pacingEnabled, learnFloor, hardMin 0 — learns the limit and plateaus.
    ///  * Manual: pacingEnabled, !learnFloor, hardMin = 60000/rpm (or 0 = unthrottled) —
    ///            holds the fixed interval; a 429 only causes a temporary safety backoff
    ///            that decays straight back to hardMin, never learning a new plateau.
    ///  * Ollama: pacing disabled entirely (local server, no server-side limit).
    /// </summary>
    public void Configure(bool pacingEnabled, bool learnFloor, double hardMinIntervalMs)
    {
        lock (_sync)
        {
            _pacingEnabled = pacingEnabled;
            _learnFloor = learnFloor;
            _hardMinIntervalMs = Math.Max(0, hardMinIntervalMs);
            if (_hardMinIntervalMs > 0)
            {
                _intervalMs = Math.Min(_hardMinIntervalMs, MaxIntervalMs);
                _learnedFloorMs = _intervalMs;
            }
            else if (pacingEnabled && learnFloor)
            {
                // Auto profile: start gently paced rather than unthrottled so the opening
                // burst can't exhaust a small budget before the first rate-limit header
                // arrives. The floor stays 0 (unlearned) so a genuinely unlimited provider
                // still decays back toward zero.
                _intervalMs = AutoStartIntervalMs;
            }
        }
    }

    /// <summary>
    /// Seeds the limiter from a previously learned interval (session memory). Sets both
    /// the starting interval and a provisional floor so the run skips re-calibration.
    /// </summary>
    public void Seed(double intervalMs)
    {
        if (double.IsNaN(intervalMs) || intervalMs <= 0)
        {
            return;
        }

        lock (_sync)
        {
            _intervalMs = Math.Min(intervalMs, MaxIntervalMs);
            _learnedFloorMs = _intervalMs;
            _consecutiveSuccesses = 0;
        }
    }

    /// <summary>
    /// Applies a provider-advertised hard ceiling of <paramref name="requestsPerMinute"/>
    /// (from a header such as Mistral's x-ratelimit-limit-req-minute). Instead of
    /// discovering the limit by crashing into it and overshooting on AIMD growth, we pace
    /// directly to 60000/limit (with a small safety margin) and never probe below it.
    /// Auto profile only — Manual keeps the user's fixed interval, Ollama is unpaced.
    /// Idempotent: repeated identical calls do not perturb ongoing decay.
    /// </summary>
    public void ApplyAuthoritativeLimit(double requestsPerMinute)
    {
        if (double.IsNaN(requestsPerMinute) || requestsPerMinute <= 0)
        {
            return;
        }

        lock (_sync)
        {
            // Manual pacing is the user's explicit choice; Ollama/local is unpaced. Only
            // Auto (which learns a floor) defers to the server-stated ceiling.
            if (!_pacingEnabled || !_learnFloor)
            {
                return;
            }

            var target = Math.Min(60_000.0 / requestsPerMinute * HeaderReqSafetyFactor, MaxIntervalMs);
            _authoritativeFloorMs = target;

            // Only raise (never lower) the live interval here; a higher interval grown by a
            // recent 429 is left to decay back down to the floor on its own. Resetting the
            // streak solely when we actually raise keeps repeated calls from stalling decay.
            if (_intervalMs < target)
            {
                _intervalMs = target;
                _consecutiveSuccesses = 0;
            }
        }
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_sync)
        {
            if (!_pacingEnabled)
            {
                return;
            }

            var now = _now();
            var slot = _nextSlot > now ? _nextSlot : now;

            var increment = _intervalMs;
            if (increment > 0)
            {
                // ±JitterFraction around the interval; average spacing is unchanged, but
                // parallel workers no longer line up on identical slot boundaries.
                var jitter = (_nextJitter() * 2.0 - 1.0) * JitterFraction;
                increment = Math.Max(0, increment * (1.0 + jitter));
            }

            _nextSlot = slot + TimeSpan.FromMilliseconds(increment);
            delay = slot - now;
        }

        if (delay > TimeSpan.Zero)
        {
            await _delay(delay, cancellationToken);
        }
    }

    public void OnRateLimited(int? retryAfterSeconds)
    {
        lock (_sync)
        {
            if (!_pacingEnabled)
            {
                return;
            }

            var now = _now();
            var retryAfterMs = retryAfterSeconds.HasValue ? retryAfterSeconds.Value * 1000.0 : 0;

            // Debounce: concurrent 429s from the same overload burst arrive before the
            // penalty window opened by the first one elapses, so they don't stack growth.
            if (now >= _growthLockedUntil)
            {
                var grown = _intervalMs <= 0 ? InitialPenaltyMs : _intervalMs * IncreaseFactor;
                _intervalMs = Math.Min(grown, MaxIntervalMs);
                _consecutiveSuccesses = 0;
                _growthLockedUntil = now + TimeSpan.FromMilliseconds(_intervalMs);

                // The interval that just got a 429 did not hold; raise the safe floor to
                // the grown interval so decay never probes back down into it. In Manual
                // mode we do not learn a floor — the backoff is temporary and must decay
                // all the way back to the configured hard minimum.
                if (_learnFloor && _intervalMs > _learnedFloorMs)
                {
                    _learnedFloorMs = _intervalMs;
                }
            }

            // Push the next global slot past both the pacing interval and any Retry-After.
            // This is what actually delays the retry (via WaitAsync), and it honors a
            // Retry-After larger than our interval cap.
            var resumeAt = now + TimeSpan.FromMilliseconds(Math.Max(_intervalMs, retryAfterMs));
            if (resumeAt > _nextSlot)
            {
                _nextSlot = resumeAt;
            }
        }
    }

    public void OnSuccess()
    {
        lock (_sync)
        {
            if (!_pacingEnabled || _intervalMs <= 0)
            {
                _consecutiveSuccesses = 0;
                return;
            }

            if (_intervalMs <= _hardMinIntervalMs && !_learnFloor)
            {
                // Manual mode already sitting at the configured interval; nothing to decay.
                _consecutiveSuccesses = 0;
                return;
            }

            if (++_consecutiveSuccesses < SuccessesBeforeDecay)
            {
                return;
            }

            _consecutiveSuccesses = 0;

            if (_authoritativeFloorMs > 0)
            {
                // The provider stated its ceiling outright, so the sustainable rate is
                // already known — no gradual probing needed. Any interval that a transient
                // 429 inflated above the floor returns straight to it once a success streak
                // confirms recovery, instead of crawling down 150 ms at a time for hours.
                _intervalMs = Math.Max(_authoritativeFloorMs, _hardMinIntervalMs);
                return;
            }

            double decayFloor;
            if (_learnFloor)
            {
                // The current interval survived a full streak → record it as the new safe
                // floor if it is the smallest one seen so far, then probe just below it.
                if (_learnedFloorMs <= 0 || _intervalMs < _learnedFloorMs)
                {
                    _learnedFloorMs = _intervalMs;
                }

                decayFloor = _learnedFloorMs * FloorProbeRatio;
            }
            else
            {
                // Manual: never probe below the configured hard minimum.
                decayFloor = 0;
            }

            var minInterval = Math.Max(decayFloor, _hardMinIntervalMs);
            _intervalMs = Math.Max(_intervalMs - DecreaseStepMs, minInterval);
        }
    }
}
