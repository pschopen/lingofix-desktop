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

    // ---- Label-only paragraphs (Randnummern, Gliederungsmarken) ------------------

    [Theory]
    [InlineData("12")]
    [InlineData("(3)")]
    [InlineData("§ 12")]
    [InlineData("2.3.4")]
    [InlineData("– 17 –")]
    [InlineData("[5]")]
    [InlineData("A.")]
    [InlineData("IV.")]
    [InlineData("a)")]
    [InlineData("aa)")]
    [InlineData("(1)")]
    [InlineData("A.1")]
    public async Task LabelOnlyParagraph_IsNeverSentToLlm(string labelText)
    {
        var paragraphs = new[] { Text(labelText) };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task LabelOnlyParagraph_TextIsUnchangedInDocument()
    {
        var paragraph = Text("aa)");
        var fake = new FakeLlmClient { ResponseFactory = _ => "SHOULD NEVER APPEAR" };
        var settings = BuildSettings(OperationMode.Correction, enableBatching: false);

        await ParagraphProcessor.ProcessAsync([paragraph], fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal("aa)", paragraph.InnerText);
    }

    [Fact]
    public async Task MixedBatch_LabelParagraphsSkipped_TextParagraphsStillProcessed()
    {
        var paragraphs = new[] { Text("A."), Text("Ein normaler Satz."), Text("aa)"), Text("Noch ein Satz.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Correction, enableBatching: true);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        var batch = Assert.Single(fake.Calls);
        Assert.Equal("Ein normaler Satz.\n\nNoch ein Satz.", batch.Input);
    }

    [Fact]
    public async Task LabelWithHeading_IsStillSentToLlm()
    {
        // "A. Einleitung" carries translatable text; only the bare label is skipped.
        var paragraphs = new[] { Text("A. Einleitung") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal("A. Einleitung", Assert.Single(fake.Calls).Input);
    }

    [Fact]
    public async Task LabelAndHeadingInOneParagraph_LabelIsStrippedFromThePayload()
    {
        // The Word layout for a manually numbered legal heading is label + <w:tab/> +
        // heading in ONE paragraph. ParagraphTextMapper strips the label prefix, so the
        // LLM sees only the heading — and the label run is never written back to.
        var paragraph = P(
            "<w:r><w:t>aa)</w:t></w:r>" +
            "<w:r><w:tab/><w:t>Einleitung</w:t></w:r>");
        var fake = new FakeLlmClient { ResponseFactory = _ => "Introduction" };
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false);

        await ParagraphProcessor.ProcessAsync([paragraph], fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal("Einleitung", Assert.Single(fake.Calls).Input);
        Assert.Equal("aa)", paragraph.Descendants<Run>().First().InnerText);
    }

    [Fact]
    public async Task HeadingWithWordListNumbering_HasNoLabelInItsText()
    {
        // Styles + a Word list keep the label in w:numPr, never in run text — so it was
        // never part of the payload, with or without the label filter.
        var paragraph = P(
            "<w:pPr><w:pStyle w:val=\"berschrift1\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"3\"/></w:numPr></w:pPr>" +
            "<w:r><w:t>Einleitung</w:t></w:r>");
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false);

        await ParagraphProcessor.ProcessAsync([paragraph], fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal("Einleitung", Assert.Single(fake.Calls).Input);
    }

    [Fact]
    public async Task LabelInOwnParagraph_HeadingReachesLlmWithoutTheLabel()
    {
        // Separate-paragraph layout: the label paragraph is skipped, so the heading is
        // translated on its own. The label itself survives untouched in the document.
        var label = Text("A.");
        var heading = Text("Einleitung");
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false);

        await ParagraphProcessor.ProcessAsync([label, heading], fake, settings, ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal("Einleitung", Assert.Single(fake.Calls).Input);
        Assert.Equal("A.", label.InnerText);
    }

    [Fact]
    public async Task LegalAbbreviationParagraph_IsStillSentToLlm()
    {
        // "a.a.O." must be translated ("ibid."), so it must not parse as a label.
        var paragraphs = new[] { Text("a.a.O.") };
        var fake = new FakeLlmClient();
        var settings = BuildSettings(OperationMode.Translation, enableBatching: false);

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, settings, ProcessorWorkItemKind.Footnotes, NullRunLogger.Instance);

        Assert.Single(fake.Calls);
    }
}
