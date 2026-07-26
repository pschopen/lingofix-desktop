using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Phase 3a: prompt assembly with and without a context block. Both CorrectAsync and
/// CorrectBatchAsync funnel through the same BuildSimplePrompt, so exercising it directly
/// covers both the single-request and batch paths (they cannot otherwise be observed
/// without a real HTTP call).
/// </summary>
public class LlmClientPromptTests
{
    [Fact]
    public void WithoutContext_UsesPlainTextLabel()
    {
        var prompt = LlmClient.BuildSimplePrompt("Translate this.", "", "Hallo Welt.");

        Assert.Equal("Translate this.\n\nText:\nHallo Welt.", prompt);
        Assert.DoesNotContain("Kontext", prompt);
    }

    [Fact]
    public void WithContext_IncludesContextBlockBeforeText()
    {
        var prompt = LlmClient.BuildSimplePrompt("Translate this.", "", "Der zweite Absatz.", context: "Der erste Absatz.");

        Assert.Equal(
            "Translate this.\n\n" +
            "Kontext (NUR zum Verständnis — NICHT übersetzen, NICHT in die Antwort aufnehmen):\n" +
            "Der erste Absatz.\n\n" +
            "Zu übersetzender Text:\n" +
            "Der zweite Absatz.",
            prompt);
    }

    [Fact]
    public void WithNullOrWhitespaceContext_FallsBackToPlainTextLabel()
    {
        var prompt = LlmClient.BuildSimplePrompt("Translate this.", "", "Text.", context: "   ");

        Assert.Contains("Text:\nText.", prompt);
        Assert.DoesNotContain("Kontext", prompt);
    }

    [Fact]
    public void SystemPromptAndCustomPrompt_AreJoinedOnOneLine_RegardlessOfContext()
    {
        var withoutContext = LlmClient.BuildSimplePrompt("Custom.", "System.", "Text.");
        var withContext = LlmClient.BuildSimplePrompt("Custom.", "System.", "Text.", context: "Previous.");

        Assert.StartsWith("Custom. System.", withoutContext);
        Assert.StartsWith("Custom. System.", withContext);
    }
}
