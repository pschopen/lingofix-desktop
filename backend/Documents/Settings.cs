using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lingofix.Backend.Documents;

public sealed class Settings
{
    public const double MinTemperature = 0.0;
    public const double MaxTemperature = 2.0;
    public const int DefaultChunkSize = 7_500;
    public const int MinChunkSize = 500;
    public const int MaxChunkSize = 50_000;
    public const int MinBatchMaxChars = 500;
    public const int MaxBatchMaxChars = 50_000;
    public const int MinBatchMaxParagraphs = 1;
    public const int MaxBatchMaxParagraphs = 100;
    public const int MinMaxParallelRequests = 1;
    public const int MaxMaxParallelRequests = 16;

    public string Provider { get; set; } = string.Empty;
    public string ApiBase { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string BatchPrompt { get; set; } = string.Empty;
    public string CompareMode { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public bool EnableReasoning { get; set; }
    public string ReasoningEffort { get; set; } = "low";
    public int ChunkSize { get; set; }
    public bool EnableBatching { get; set; }
    internal HashSet<ProcessorWorkItemKind> BatchingParts { get; set; } =
    [
        ProcessorWorkItemKind.Main,
        ProcessorWorkItemKind.Footnotes,
        ProcessorWorkItemKind.Endnotes,
        ProcessorWorkItemKind.Headers,
        ProcessorWorkItemKind.Footers,
        ProcessorWorkItemKind.Glossary
    ];
    internal HashSet<ProcessorWorkItemKind> CorrectionScopeParts { get; set; } =
    [
        ProcessorWorkItemKind.Main,
        ProcessorWorkItemKind.Footnotes,
        ProcessorWorkItemKind.Endnotes,
        ProcessorWorkItemKind.Headers,
        ProcessorWorkItemKind.Footers,
        ProcessorWorkItemKind.Glossary
    ];
    public int BatchMaxChars { get; set; }
    public int BatchMaxParagraphs { get; set; }
    public bool EnableCache { get; set; }
    public bool EnableParallelization { get; set; }
    public int MaxParallelRequests { get; set; }
    public bool RestoreNonBreakingSpaces { get; set; }
    public CitationNormalizer.NormalizationMode CitationNormalizationMode { get; set; } = CitationNormalizer.NormalizationMode.Auto;
    public CitationNormalizer.CitationStyle? CitationStyle { get; set; }
    public bool? TemperatureSupportedHint { get; set; }
    public bool? ReasoningEffortSupportedHint { get; set; }

    public static string ResolveApiKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        const string prefix = "ENV:";
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var envName = raw.Substring(prefix.Length).Trim();
            return Environment.GetEnvironmentVariable(envName) ?? string.Empty;
        }

        return raw.Trim();
    }

    public static CompareModeKind NormalizeCompareMode(string? raw)
    {
        if (string.Equals(raw, "word-native", StringComparison.OrdinalIgnoreCase))
        {
            return CompareModeKind.Word;
        }

        if (string.Equals(raw, "libreoffice-uno", StringComparison.OrdinalIgnoreCase))
        {
            return CompareModeKind.LibreOffice;
        }

        return CompareModeKind.OpenXml;
    }

    public static Settings FromFrontendJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw InvalidSettings("settings payload is empty");
        }

        var payload = JsonSerializer.Deserialize<FrontendSettingsPayload>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw InvalidSettings("settings payload could not be parsed");

        var docx = payload.Docx ?? throw InvalidSettings("docx settings are missing");
        var batchingParts = ParseBatchingParts(docx.BatchingParts);
        var correctionScopeParts = ParseCorrectionScopeParts(docx.CorrectionScopeParts);

        var normalized = new Settings
        {
            Provider = RequireString(payload.Provider, "provider"),
            ApiBase = RequireString(payload.ApiUrl, "api_url"),
            ApiKey = payload.ApiKey?.Trim() ?? string.Empty,
            Model = RequireString(payload.Model, "model"),
            Prompt = RequireString(payload.CustomPrompt, "custom_prompt"),
            SystemPrompt = RequireString(payload.SystemPrompt, "system_prompt"),
            BatchPrompt = payload.BatchPrompt?.Trim() ?? string.Empty,
            Temperature = payload.Temperature,
            EnableReasoning = payload.EnableReasoning,
            ReasoningEffort = ParseReasoningEffort(payload.ReasoningEffort),
            CompareMode = RequireString(docx.CompareMode, "docx.compare_mode"),
            ChunkSize = docx.ChunkSize ?? DefaultChunkSize,
            EnableBatching = docx.EnableBatching,
            BatchingParts = batchingParts,
            CorrectionScopeParts = correctionScopeParts,
            BatchMaxChars = docx.BatchMaxChars,
            BatchMaxParagraphs = docx.BatchMaxParagraphs,
            EnableCache = docx.EnableCache,
            EnableParallelization = docx.EnableParallelization,
            MaxParallelRequests = docx.MaxParallelRequests,
            RestoreNonBreakingSpaces = docx.RestoreNonBreakingSpaces,
            CitationNormalizationMode = CitationNormalizer.ParseMode(payload.CitationNormalization),
            TemperatureSupportedHint = payload.LlmCapabilityHint?.TemperatureSupported,
            ReasoningEffortSupportedHint = payload.LlmCapabilityHint?.ReasoningEffortSupported
        };

        if (double.IsNaN(normalized.Temperature) || double.IsInfinity(normalized.Temperature))
        {
            normalized.Temperature = 0.0;
        }

        normalized.Temperature = Math.Clamp(normalized.Temperature, MinTemperature, MaxTemperature);
        normalized.ChunkSize = Math.Clamp(normalized.ChunkSize, MinChunkSize, MaxChunkSize);
        normalized.BatchMaxChars = Math.Clamp(normalized.BatchMaxChars, MinBatchMaxChars, MaxBatchMaxChars);
        normalized.BatchMaxParagraphs = Math.Clamp(normalized.BatchMaxParagraphs, MinBatchMaxParagraphs, MaxBatchMaxParagraphs);
        normalized.MaxParallelRequests = Math.Clamp(normalized.MaxParallelRequests, MinMaxParallelRequests, MaxMaxParallelRequests);
        return normalized;
    }

    private static string RequireString(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidSettings($"missing or empty field '{field}'");
        }

        return value.Trim();
    }

    private static InvalidOperationException InvalidSettings(string reason)
    {
        return new InvalidOperationException(
            $"Invalid settings: {reason}. Open Settings > Advanced and use 'Reset app'.");
    }

    private static HashSet<ProcessorWorkItemKind> ParseBatchingParts(List<string>? rawParts)
    {
        if (rawParts is null || rawParts.Count == 0)
        {
            throw InvalidSettings("missing or empty field 'docx.batching_parts'");
        }

        var result = new HashSet<ProcessorWorkItemKind>();
        foreach (var raw in rawParts)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw InvalidSettings("docx.batching_parts contains empty values");
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "main":
                    result.Add(ProcessorWorkItemKind.Main);
                    break;
                case "footnotes":
                    result.Add(ProcessorWorkItemKind.Footnotes);
                    break;
                case "endnotes":
                    result.Add(ProcessorWorkItemKind.Endnotes);
                    break;
                case "headers":
                    result.Add(ProcessorWorkItemKind.Headers);
                    break;
                case "footers":
                    result.Add(ProcessorWorkItemKind.Footers);
                    break;
                case "glossary":
                    result.Add(ProcessorWorkItemKind.Glossary);
                    break;
                default:
                    throw InvalidSettings($"docx.batching_parts contains unknown value '{raw}'");
            }
        }

        return result;
    }

    private static HashSet<ProcessorWorkItemKind> ParseCorrectionScopeParts(List<string>? rawParts)
    {
        if (rawParts is null || rawParts.Count == 0)
        {
            throw InvalidSettings("missing or empty field 'docx.correction_scope_parts'");
        }

        var result = new HashSet<ProcessorWorkItemKind>();
        foreach (var raw in rawParts)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw InvalidSettings("docx.correction_scope_parts contains empty values");
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "main":
                    result.Add(ProcessorWorkItemKind.Main);
                    break;
                case "footnotes":
                    result.Add(ProcessorWorkItemKind.Footnotes);
                    break;
                case "endnotes":
                    result.Add(ProcessorWorkItemKind.Endnotes);
                    break;
                case "headers":
                    result.Add(ProcessorWorkItemKind.Headers);
                    break;
                case "footers":
                    result.Add(ProcessorWorkItemKind.Footers);
                    break;
                case "glossary":
                    result.Add(ProcessorWorkItemKind.Glossary);
                    break;
                default:
                    throw InvalidSettings($"docx.correction_scope_parts contains unknown value '{raw}'");
            }
        }

        if (result.Count == 0)
        {
            throw InvalidSettings("missing or empty field 'docx.correction_scope_parts'");
        }

        return result;
    }

    private static string ParseReasoningEffort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw InvalidSettings("missing or empty field 'reasoning_effort'");
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => throw InvalidSettings($"reasoning_effort contains unknown value '{raw}'")
        };
    }
}

internal sealed class FrontendSettingsPayload
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("api_url")]
    public string? ApiUrl { get; set; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("custom_prompt")]
    public string? CustomPrompt { get; set; }

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("batch_prompt")]
    public string? BatchPrompt { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("enable_reasoning")]
    public bool EnableReasoning { get; set; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("citation_normalization")]
    public string? CitationNormalization { get; set; }

    [JsonPropertyName("docx")]
    public FrontendDocxSettingsPayload? Docx { get; set; }

    [JsonPropertyName("llm_capability_hint")]
    public FrontendLlmCapabilityHintPayload? LlmCapabilityHint { get; set; }
}

internal sealed class FrontendLlmCapabilityHintPayload
{
    [JsonPropertyName("temperature_supported")]
    public bool? TemperatureSupported { get; set; }

    [JsonPropertyName("reasoning_effort_supported")]
    public bool? ReasoningEffortSupported { get; set; }
}

internal sealed class FrontendDocxSettingsPayload
{
    [JsonPropertyName("compare_mode")]
    public string? CompareMode { get; set; }

    [JsonPropertyName("chunk_size")]
    public int? ChunkSize { get; set; }

    [JsonPropertyName("enable_batching")]
    public bool EnableBatching { get; set; }

    [JsonPropertyName("batch_max_chars")]
    public int BatchMaxChars { get; set; }

    [JsonPropertyName("batch_max_paragraphs")]
    public int BatchMaxParagraphs { get; set; }

    [JsonPropertyName("batching_parts")]
    public List<string>? BatchingParts { get; set; }

    [JsonPropertyName("correction_scope_parts")]
    public List<string>? CorrectionScopeParts { get; set; }

    [JsonPropertyName("enable_cache")]
    public bool EnableCache { get; set; }

    [JsonPropertyName("enable_parallelization")]
    public bool EnableParallelization { get; set; }

    [JsonPropertyName("max_parallel_requests")]
    public int MaxParallelRequests { get; set; }

    [JsonPropertyName("restore_non_breaking_spaces")]
    public bool RestoreNonBreakingSpaces { get; set; }
}
