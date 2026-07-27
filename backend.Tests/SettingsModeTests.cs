using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Migration + validation for the translation-mode fields: old settings.json (no "mode")
/// must load as Correction, translation mode requires a target language, and the
/// footnote prompt is passed through untouched.
/// </summary>
public class SettingsModeTests
{
    private static string BuildJson(string? modeExtra = null, string? translationBlock = null)
    {
        var modeLine = modeExtra is null ? "" : $",\n          \"mode\": \"{modeExtra}\"";
        var translationLine = translationBlock is null ? "" : $",\n          \"translation\": {translationBlock}";
        return $$"""
        {
          "provider": "mistral",
          "api_url": "https://api.mistral.ai/v1",
          "model": "mistral-large",
          "custom_prompt": "Fix it",
          "system_prompt": "You are careful",
          "reasoning_effort": "low",
          "temperature": 0.2{{modeLine}}{{translationLine}},
          "docx": {
            "compare_mode": "openxml",
            "enable_batching": true,
            "batching_parts": ["main"],
            "correction_scope_parts": ["main"],
            "batch_max_chars": 7500,
            "batch_max_paragraphs": 10,
            "enable_cache": true,
            "max_parallel_requests": 4
          }
        }
        """;
    }

    [Fact]
    public void Missing_Mode_Defaults_To_Correction()
    {
        var settings = Settings.FromFrontendJson(BuildJson());
        Assert.Equal(OperationMode.Correction, settings.Mode);
        Assert.Equal(string.Empty, settings.TargetLanguage);
        Assert.Equal(string.Empty, settings.FootnotePrompt);
    }

    [Fact]
    public void Translation_Mode_With_Target_Language_Parses()
    {
        var settings = Settings.FromFrontendJson(
            BuildJson("translation", """{ "target_language": "en", "footnote_prompt": "Translate footnotes", "system_prompt": "Translate carefully" }"""));
        Assert.Equal(OperationMode.Translation, settings.Mode);
        Assert.Equal("en", settings.TargetLanguage);
        Assert.Equal("Translate footnotes", settings.FootnotePrompt);
        Assert.Equal("Translate carefully", settings.SystemPrompt);
    }

    [Fact]
    public void Translation_Mode_Without_Target_Language_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Settings.FromFrontendJson(BuildJson("translation", """{ "system_prompt": "Translate carefully" }""")));
        Assert.Contains("target_language", ex.Message);
    }

    [Fact]
    public void Translation_Mode_Without_System_Prompt_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Settings.FromFrontendJson(BuildJson("translation", """{ "target_language": "en" }""")));
        Assert.Contains("translation.system_prompt", ex.Message);
    }

    [Fact]
    public void Correction_Mode_Ignores_Translation_Fields_Without_Error()
    {
        var settings = Settings.FromFrontendJson(
            BuildJson("correction", """{ "target_language": "", "footnote_prompt": "unused" }"""));
        Assert.Equal(OperationMode.Correction, settings.Mode);
        Assert.Equal("You are careful", settings.SystemPrompt);
    }

    [Fact]
    public void Unknown_Mode_Value_Defaults_To_Correction()
    {
        var settings = Settings.FromFrontendJson(BuildJson("something-else"));
        Assert.Equal(OperationMode.Correction, settings.Mode);
    }
}
