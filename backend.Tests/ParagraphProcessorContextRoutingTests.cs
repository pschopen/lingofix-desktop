using DocumentFormat.OpenXml.Wordprocessing;
using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Phase 3: translation mode feeds each request the previous source paragraph as
/// context and routes footnotes/endnotes to a separate prompt; correction mode must be
/// byte-for-byte unaffected. Uses <see cref="FakeLlmClient"/> instead of real HTTP calls.
/// </summary>
public class ParagraphProcessorContextRoutingTests
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

    // ---- Context propagation (single-request path) ---------------------------

    [Fact]
    public async Task Translation_SingleRequests_FirstParagraphHasNoContext_LaterOnesGetPreviousOriginal()
    {
        var paragraphs = new[] { Text("First paragraph."), Text("Second paragraph."), Text("Third paragraph.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal(3, fake.Calls.Count);
        Assert.Null(fake.Calls[0].Context);
        Assert.Equal("First paragraph.", fake.Calls[1].Context);
        Assert.Equal("Second paragraph.", fake.Calls[2].Context);
    }

    [Fact]
    public async Task Correction_SingleRequests_NeverGetsContext()
    {
        var paragraphs = new[] { Text("First."), Text("Second.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.All(fake.Calls, call => Assert.Null(call.Context));
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
    public async Task Translation_DuplicateText_DifferentPrecedingContext_BothCallTheLlm()
    {
        var paragraphs = new[] { Text("Alpha."), Text("Repeat."), Text("Beta."), Text("Repeat.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false, enableCache: true);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal(2, fake.Calls.Count(c => c.Input == "Repeat."));
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

    // ---- Batch path: explicit context is only the first item's ------------------

    [Fact]
    public async Task Translation_Batch_SecondBatchContextIsLastParagraphOfPreviousBatch()
    {
        var paragraphs = new[] { Text("Intro paragraph."), Text("Second paragraph."), Text("Third paragraph.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: true, batchMaxParagraphs: 2);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        var batchCalls = fake.Calls.Where(c => c.IsBatch).ToList();
        Assert.Equal(2, batchCalls.Count);
        Assert.Null(batchCalls[0].Context);
        Assert.Equal("Second paragraph.", batchCalls[1].Context);
    }
}
