using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lingofix.Backend.Documents;

public sealed class Settings
{
    public string Provider { get; set; } = "openai";
    public string ApiBase { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";
    public string ApiKey { get; set; } = "ENV:OPENAI_API_KEY";
    public string Prompt { get; set; } = "Correct the following text while maintaining the style and tone.";
    public string SystemPrompt { get; set; } =
        "Important: Respond with the corrected text only. No explanations, no notes, no extra sentences.";
    public string BatchPrompt { get; set; } =
        "Correct only the text inside the tags. Return the response with the exact same tags and IDs.\nNo extra lines outside the tags.";
    public string CompareMode { get; set; } = "diff-engine";
    public double Temperature { get; set; } = 0.0;
    public bool EnableBatching { get; set; } = true;
    public int BatchMaxChars { get; set; } = 50000;
    public int BatchMaxParagraphs { get; set; } = 100;
    public bool EnableCache { get; set; } = true;
    public bool EnableParallelization { get; set; } = true;
    public int MaxParallelRequests { get; set; } = 2;

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
        if (string.Equals(raw, "word", StringComparison.OrdinalIgnoreCase))
        {
            return CompareModeKind.Word;
        }

        if (string.Equals(raw, "libreoffice", StringComparison.OrdinalIgnoreCase))
        {
            return CompareModeKind.LibreOffice;
        }

        return CompareModeKind.DiffEngine;
    }

    public static Settings FromFrontendJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Settings();
        }

        var payload = JsonSerializer.Deserialize<FrontendSettingsPayload>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (payload is null)
        {
            return new Settings();
        }

        var normalized = new Settings
        {
            Provider = string.IsNullOrWhiteSpace(payload.Provider) ? "openai" : payload.Provider,
            ApiBase = string.IsNullOrWhiteSpace(payload.ApiUrl) ? "https://api.openai.com/v1" : payload.ApiUrl,
            ApiKey = payload.ApiKey ?? string.Empty,
            Model = string.IsNullOrWhiteSpace(payload.Model) ? "gpt-4" : payload.Model,
            Prompt = string.IsNullOrWhiteSpace(payload.CustomPrompt) ? "Correct the following text while maintaining the style and tone." : payload.CustomPrompt,
            SystemPrompt = string.IsNullOrWhiteSpace(payload.SystemPrompt)
                ? "Important: Respond with the corrected text only. No explanations, no notes, no extra sentences."
                : payload.SystemPrompt,
            BatchPrompt = string.IsNullOrWhiteSpace(payload.BatchPrompt)
                ? "Correct only the text inside the tags. Return the response with the exact same tags and IDs.\nNo extra lines outside the tags."
                : payload.BatchPrompt,
            Temperature = payload.Temperature,
            CompareMode = string.IsNullOrWhiteSpace(payload.Docx.CompareMode) ? "diff-engine" : payload.Docx.CompareMode,
            EnableBatching = payload.Docx.EnableBatching,
            BatchMaxChars = payload.Docx.BatchMaxChars,
            BatchMaxParagraphs = payload.Docx.BatchMaxParagraphs,
            EnableCache = payload.Docx.EnableCache,
            EnableParallelization = payload.Docx.EnableParallelization,
            MaxParallelRequests = payload.Docx.MaxParallelRequests
        };

        if (double.IsNaN(normalized.Temperature) || double.IsInfinity(normalized.Temperature))
        {
            normalized.Temperature = 0.0;
        }

        normalized.Temperature = Math.Clamp(normalized.Temperature, 0.0, 2.0);
        normalized.BatchMaxChars = Math.Clamp(normalized.BatchMaxChars, 500, 50_000);
        normalized.BatchMaxParagraphs = Math.Clamp(normalized.BatchMaxParagraphs, 1, 100);
        normalized.MaxParallelRequests = Math.Clamp(normalized.MaxParallelRequests, 1, 32);
        return normalized;
    }
}

internal sealed class FrontendSettingsPayload
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "openai";

    [JsonPropertyName("api_url")]
    public string ApiUrl { get; set; } = "https://api.openai.com/v1";

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4";

    [JsonPropertyName("custom_prompt")]
    public string CustomPrompt { get; set; } = "Correct the following text while maintaining the style and tone.";

    [JsonPropertyName("system_prompt")]
    public string SystemPrompt { get; set; } = "Important: Respond with the corrected text only. No explanations, no notes, no extra sentences.";

    [JsonPropertyName("batch_prompt")]
    public string BatchPrompt { get; set; } = "Correct only the text inside the tags. Return the response with the exact same tags and IDs.\nNo extra lines outside the tags.";

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("docx")]
    public FrontendDocxSettingsPayload Docx { get; set; } = new();
}

internal sealed class FrontendDocxSettingsPayload
{
    [JsonPropertyName("compare_mode")]
    public string CompareMode { get; set; } = "diff-engine";

    [JsonPropertyName("enable_batching")]
    public bool EnableBatching { get; set; } = true;

    [JsonPropertyName("batch_max_chars")]
    public int BatchMaxChars { get; set; } = 50000;

    [JsonPropertyName("batch_max_paragraphs")]
    public int BatchMaxParagraphs { get; set; } = 100;

    [JsonPropertyName("enable_cache")]
    public bool EnableCache { get; set; } = true;

    [JsonPropertyName("enable_parallelization")]
    public bool EnableParallelization { get; set; } = true;

    [JsonPropertyName("max_parallel_requests")]
    public int MaxParallelRequests { get; set; } = 2;
}

public static class SettingsStore
{
    public static Settings LoadOrCreate(string path, out bool created)
    {
        if (!File.Exists(path))
        {
            created = true;
            var settings = new Settings();
            Save(path, settings);
            return settings;
        }

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                created = true;
                var settings = new Settings();
                Save(path, settings);
                return settings;
            }

            var parsed = JsonSerializer.Deserialize<Settings>(text, JsonOptions.Default);
            if (parsed is null)
            {
                created = true;
                var settings = new Settings();
                Save(path, settings);
                return settings;
            }

            created = false;
            return parsed;
        }
        catch (JsonException)
        {
            created = true;
            var settings = new Settings();
            Save(path, settings);
            return settings;
        }
    }

    public static void Save(string path, Settings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions.Default);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(path, json, Encoding.UTF8);
    }
}
