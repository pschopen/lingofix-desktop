using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Phase 3 migration: old settings.json (no speed_mode) must load as Auto, and both the
/// enable_parallelization-present and -absent shapes must still parse.
/// </summary>
public class SettingsSpeedModeTests
{
    private static string BuildJson(string docxExtra = "")
    {
        var extra = string.IsNullOrEmpty(docxExtra) ? "" : "," + docxExtra;
        return $$"""
        {
          "provider": "mistral",
          "api_url": "https://api.mistral.ai/v1",
          "model": "mistral-large",
          "custom_prompt": "Fix it",
          "system_prompt": "You are careful",
          "reasoning_effort": "low",
          "temperature": 0.2,
          "docx": {
            "compare_mode": "openxml",
            "enable_batching": true,
            "batching_parts": ["main"],
            "correction_scope_parts": ["main"],
            "batch_max_chars": 7500,
            "batch_max_paragraphs": 10,
            "enable_cache": true,
            "max_parallel_requests": 4
            {{extra}}
          }
        }
        """;
    }

    [Fact]
    public void Missing_SpeedMode_Defaults_To_Auto()
    {
        var settings = Settings.FromFrontendJson(BuildJson());
        Assert.Equal(SpeedMode.Auto, settings.SpeedMode);
        Assert.Null(settings.ManualRequestsPerMinute);
    }

    [Fact]
    public void Legacy_EnableParallelization_Still_Parses()
    {
        // Old installs wrote enable_parallelization; it must not break loading, and it no
        // longer forces a mode — the install comes up in Auto.
        var settings = Settings.FromFrontendJson(BuildJson("\"enable_parallelization\": false"));
        Assert.Equal(SpeedMode.Auto, settings.SpeedMode);
    }

    [Fact]
    public void Manual_Mode_With_RequestsPerMinute_Parses()
    {
        var settings = Settings.FromFrontendJson(
            BuildJson("\"speed_mode\": \"manual\", \"manual_requests_per_minute\": 30"));
        Assert.Equal(SpeedMode.Manual, settings.SpeedMode);
        Assert.Equal(30, settings.ManualRequestsPerMinute);
    }

    [Fact]
    public void Manual_Mode_With_Null_Or_Zero_Rpm_Is_Unthrottled()
    {
        var settings = Settings.FromFrontendJson(
            BuildJson("\"speed_mode\": \"manual\", \"manual_requests_per_minute\": 0"));
        Assert.Equal(SpeedMode.Manual, settings.SpeedMode);
        Assert.Null(settings.ManualRequestsPerMinute);
    }
}
