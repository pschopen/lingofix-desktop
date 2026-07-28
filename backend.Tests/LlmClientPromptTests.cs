using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

public class LlmClientPromptTests
{
    [Fact]
    public void UsesPlainTextLabel()
    {
        var prompt = LlmClient.BuildSimplePrompt("Translate this.", "", "Hallo Welt.");

        Assert.Equal("Translate this.\n\nText:\nHallo Welt.", prompt);
    }

    [Fact]
    public void SystemPromptAndCustomPrompt_AreJoinedOnOneLine()
    {
        var prompt = LlmClient.BuildSimplePrompt("Custom.", "System.", "Text.");

        Assert.StartsWith("Custom. System.", prompt);
    }

    // ---- SanitizeCorrection: origin-aware leading-list-number stripping -------

    [Fact]
    public void SanitizeCorrection_StripsMarkdownNumbering_WhenOriginalHadNone()
    {
        // The model echoed instruction-style list formatting that was never in the
        // source paragraph — a genuine markdown artifact, safe to remove.
        var original = "Ein Satz ohne jede Nummerierung am Anfang.";
        var result = LlmClient.SanitizeCorrection("1. Ein Satz ohne jede Nummerierung am Anfang.", original);

        Assert.Equal("Ein Satz ohne jede Nummerierung am Anfang.", result);
    }

    [Fact]
    public void SanitizeCorrection_KeepsLeadingNumber_WhenOriginalHasTheSameNumbering()
    {
        // Regression: a footnote/bibliography-list entry like "3. Jean Gaudemet, ..."
        // must survive a correction verbatim — the leading "3." is real citation
        // numbering, not a markdown list artifact, because the original already had it.
        var original = "3. Jean Gaudemet, L’Église dans l’Empire romain, Paris 1958.";
        var corrected = "3. Jean Gaudemet, L'Église dans l'Empire romain, Paris 1958.";

        var result = LlmClient.SanitizeCorrection(corrected, original);

        Assert.Equal(corrected, result);
    }

    [Fact]
    public void SanitizeCorrection_WithoutOriginal_FallsBackToStrippingNumbering()
    {
        // No original supplied (e.g. a call site that doesn't have per-item context):
        // preserves the previous behavior rather than becoming stricter by default.
        var result = LlmClient.SanitizeCorrection("1. Some corrected text.");

        Assert.Equal("Some corrected text.", result);
    }

    [Fact]
    public void SanitizeCorrection_MultiLine_ChecksEachLineAgainstItsOwnOriginalLine()
    {
        var original = "3. Erste Zeile mit echter Nummer.\nZweite Zeile ohne Nummer.";
        var corrected = "3. Erste Zeile mit echter Nummer.\n2. Zweite Zeile ohne Nummer.";

        var result = LlmClient.SanitizeCorrection(corrected, original);

        // Line 0 keeps its number (original line 0 had one); line 1's "2." is a
        // markdown artifact (original line 1 had none) and is stripped.
        Assert.Equal("3. Erste Zeile mit echter Nummer.\nZweite Zeile ohne Nummer.", result);
    }

    [Fact]
    public void SanitizeCorrection_MismatchedLineCount_DoesNotStripUnmatchedLines()
    {
        // The model answered with more lines than the original had (e.g. a hallucinated
        // continuation). Without a same-index original line to compare against, the
        // unmatched line's "2." is left alone rather than risk deleting real content.
        var original = "Nur eine Zeile.";
        var corrected = "Nur eine Zeile.\n2. Erfundene Fortsetzung.";

        var result = LlmClient.SanitizeCorrection(corrected, original);

        Assert.Equal("Nur eine Zeile.\n2. Erfundene Fortsetzung.", result);
    }
}
