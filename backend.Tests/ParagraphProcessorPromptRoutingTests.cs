using DocumentFormat.OpenXml.Wordprocessing;
using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Translation mode routes footnotes/endnotes to a separate prompt; correction mode must
/// be byte-for-byte unaffected. Uses <see cref="FakeLlmClient"/> instead of real HTTP calls.
/// </summary>
public class ParagraphProcessorPromptRoutingTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Paragraph P(string innerXml) => new($"<w:p xmlns:w=\"{W}\">{innerXml}</w:p>");

    private static Paragraph Text(string text) => P($"<w:r><w:t>{text}</w:t></w:r>");

    private static Lingofix.Backend.Documents.Settings BuildSettings(
        OperationMode mode,
        bool enableBatching,
        bool enableCache = false,
        string prompt = "Translate.",
        string footnotePrompt = "",
        int batchMaxParagraphs = 10,
        int batchMaxChars = 10_000)
    {
        return new Lingofix.Backend.Documents.Settings
        {
            Provider = "test",
            ApiBase = "https://example.invalid",
            Model = "test-model",
            Prompt = prompt,
            SystemPrompt = "",
            BatchPrompt = "",
            Mode = mode,
            TargetLanguage = mode == OperationMode.Translation ? "en" : "",
            FootnotePrompt = footnotePrompt,
            ChunkSize = Lingofix.Backend.Documents.Settings.MaxChunkSize,
            EnableBatching = enableBatching,
            BatchMaxChars = batchMaxChars,
            BatchMaxParagraphs = batchMaxParagraphs,
            EnableCache = enableCache,
            SpeedMode = SpeedMode.Manual,
            MaxParallelRequests = 1
        };
    }

    // ---- Prompt routing ---------------------------------------------------------

    [Fact]
    public async Task Translation_FootnotesPart_UsesFootnotePrompt()
    {
        var paragraphs = new[] { Text("Fußnotentext.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false, prompt: "Main prompt.", footnotePrompt: "Footnote prompt.");

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Footnotes, NullRunLogger.Instance);

        Assert.Equal("Footnote prompt.", Assert.Single(fake.Calls).PromptOverride);
    }

    [Fact]
    public async Task Translation_EndnotesPart_UsesFootnotePrompt()
    {
        var paragraphs = new[] { Text("Endnotentext.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false, prompt: "Main prompt.", footnotePrompt: "Footnote prompt.");

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Endnotes, NullRunLogger.Instance);

        Assert.Equal("Footnote prompt.", Assert.Single(fake.Calls).PromptOverride);
    }

    [Fact]
    public async Task Translation_MainPart_UsesMainPrompt_NotFootnotePrompt()
    {
        var paragraphs = new[] { Text("Haupttext.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false, prompt: "Main prompt.", footnotePrompt: "Footnote prompt.");

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal("Main prompt.", Assert.Single(fake.Calls).PromptOverride);
    }

    [Fact]
    public async Task Translation_FootnotesPart_EmptyFootnotePrompt_FallsBackToMainPrompt()
    {
        var paragraphs = new[] { Text("Fußnotentext.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false, prompt: "Main prompt.", footnotePrompt: "");

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Footnotes, NullRunLogger.Instance);

        Assert.Equal("Main prompt.", Assert.Single(fake.Calls).PromptOverride);
    }

    [Fact]
    public async Task Correction_FootnotesPart_IgnoresFootnotePrompt()
    {
        var paragraphs = new[] { Text("Text.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false, prompt: "Correct this.", footnotePrompt: "Should never be used.");

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Footnotes, NullRunLogger.Instance);

        Assert.Equal("Correct this.", Assert.Single(fake.Calls).PromptOverride);
    }

    // ---- Cache key composition --------------------------------------------------

    [Fact]
    public async Task Translation_DuplicateText_IsCachedRegardlessOfPrecedingParagraph()
    {
        var paragraphs = new[] { Text("Alpha."), Text("Repeat."), Text("Beta."), Text("Repeat.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false, enableCache: true);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Single(fake.Calls, c => c.Input == "Repeat.");
    }

    [Fact]
    public async Task Correction_DuplicateText_IsCachedRegardlessOfPrecedingParagraph()
    {
        var paragraphs = new[] { Text("Alpha."), Text("Repeat."), Text("Beta."), Text("Repeat.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false, enableCache: true);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Single(fake.Calls, c => c.Input == "Repeat.");
    }

    // ---- Number-only paragraphs ("Randnummern") ----------------------------------

    [Theory]
    [InlineData("12")]
    [InlineData("(3)")]
    [InlineData("§ 12")]
    [InlineData("2.3.4")]
    [InlineData("– 17 –")]
    [InlineData("[5]")]
    public async Task NumberOnlyParagraph_IsNeverSentToLlm(string numberText)
    {
        var paragraphs = new[] { Text(numberText) };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task NumberOnlyParagraph_TextIsUnchangedInDocument()
    {
        var paragraph = Text("12");
        var fake = new FakeLlmClient { ResponseFactory = _ => "SHOULD NEVER APPEAR" };
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false);

        await ParagraphProcessor.ProcessAsync([paragraph], fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal("12", paragraph.InnerText);
    }

    [Fact]
    public async Task MixedBatch_NumberOnlyParagraphIsSkipped_TextParagraphIsStillProcessed()
    {
        var paragraphs = new[] { Text("12"), Text("Ein normaler Satz.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: true);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.DoesNotContain(fake.Calls, c => c.Input.Contains("12", StringComparison.Ordinal));
        Assert.Contains(fake.Calls, c => c.Input.Contains("Ein normaler Satz.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RomanNumeralParagraph_IsStillSentToLlm()
    {
        // Roman numerals contain letters, so the number-only skip does not apply to
        // them; documented as an accepted gap (see IsNumberOnly's remarks).
        var paragraphs = new[] { Text("IV.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Single(fake.Calls);
    }
}
