using DocumentFormat.OpenXml.Wordprocessing;
using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// A batch response whose block count doesn't match the number of paragraphs sent no
/// longer voids the whole batch: paragraphs the response answered well are applied
/// directly, and only the ones it actually missed or garbled go through the (cheap)
/// per-item fallback. Uses <see cref="FakeLlmClient"/> so the fallback calls it makes are
/// directly observable instead of just inferred from the final document.
/// </summary>
public class ParagraphProcessorBatchAlignmentTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Paragraph Text(string text) => new($"<w:p xmlns:w=\"{W}\"><w:r><w:t>{text}</w:t></w:r></w:p>");

    private static Lingofix.Backend.Documents.Settings BuildSettings() => new()
    {
        Provider = "test",
        ApiBase = "https://example.invalid",
        Model = "test-model",
        Prompt = "Correct.",
        SystemPrompt = "",
        BatchPrompt = "",
        Mode = OperationMode.Correction,
        TargetLanguage = "",
        ChunkSize = Lingofix.Backend.Documents.Settings.MaxChunkSize,
        EnableBatching = true,
        BatchMaxChars = 10_000,
        BatchMaxParagraphs = 10,
        EnableCache = false,
        SpeedMode = SpeedMode.Manual,
        MaxParallelRequests = 1
    };

    [Fact]
    public async Task ModelOverSplitsOneAnswer_HallucinatedExtraBlockIsDiscarded_NoFallbackNeeded()
    {
        // 3 paragraphs sent, 4 blocks come back: the 2nd, 3rd and 4th line up cleanly with
        // paragraphs 1-3, and the extra 3rd block shares no vocabulary with any of them (an
        // over-split/hallucinated block). The old dp rejected the whole batch outright
        // whenever block count > paragraph count; this should recover all 3 without a
        // single fallback request.
        var paragraphs = new[]
        {
            Text("Erste Zeile mit Feler."),
            Text("Zweiter Absatz ist gut."),
            Text("Dritter Absatz auch gut.")
        };
        var fake = new FakeLlmClient
        {
            ResponseFactory = call => call.IsBatch
                ? "Erste Zeile mit Fehler.\n\nZweiter Absatz ist gut.\n\nEtwas komplett Fremdes, das nirgendwo hingehört und nichts mit den Originalen zu tun hat.\n\nDritter Absatz auch gut."
                : call.Input
        };

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, BuildSettings(), ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Single(fake.Calls); // only the initial batch call — no fallback triggered
        Assert.Equal("Erste Zeile mit Fehler.", ParagraphTextMapper.ExtractEditableText(paragraphs[0]));
        Assert.Equal("Zweiter Absatz ist gut.", ParagraphTextMapper.ExtractEditableText(paragraphs[1]));
        Assert.Equal("Dritter Absatz auch gut.", ParagraphTextMapper.ExtractEditableText(paragraphs[2]));
    }

    [Fact]
    public async Task ModelSkipsOneParagraph_OnlyThatParagraphGoesThroughFallback()
    {
        // 3 paragraphs sent, but the model's reply only answers 2 of them (skips the
        // middle one). Only the missing paragraph should trigger a single-item fallback
        // request — the two good answers must not be discarded and re-requested too.
        var paragraphs = new[]
        {
            Text("Erste Zeile mit Feler."),
            Text("Zweiter Absatz ist gut."),
            Text("Dritter Absatz auch gut.")
        };
        var fake = new FakeLlmClient
        {
            ResponseFactory = call => call.IsBatch
                ? "Erste Zeile mit Fehler.\n\nDritter Absatz auch gut."
                : call.Input
        };

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, BuildSettings(), ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal(2, fake.Calls.Count);
        var fallbackCall = Assert.Single(fake.Calls, call => !call.IsBatch);
        Assert.Equal("Zweiter Absatz ist gut.", fallbackCall.Input);

        Assert.Equal("Erste Zeile mit Fehler.", ParagraphTextMapper.ExtractEditableText(paragraphs[0]));
        Assert.Equal("Zweiter Absatz ist gut.", ParagraphTextMapper.ExtractEditableText(paragraphs[1]));
        Assert.Equal("Dritter Absatz auch gut.", ParagraphTextMapper.ExtractEditableText(paragraphs[2]));
    }

    [Fact]
    public async Task ModelReturnsUnrelatedGarbage_FallsBackForEveryParagraph()
    {
        // Nothing in the reply resembles either original, so alignment resolves nothing —
        // this must still fall back for every paragraph, same as the old hard failure.
        var paragraphs = new[]
        {
            Text("Erste Zeile mit Feler."),
            Text("Zweiter Absatz ist gut.")
        };
        var fake = new FakeLlmClient
        {
            ResponseFactory = call => call.IsBatch
                ? "Völlig unpassender Text ohne jeden Bezug, ganz andere Wörter überall."
                : call.Input
        };

        await ParagraphProcessor.ProcessAsync(paragraphs, fake, BuildSettings(), ProcessorWorkItemKind.Main, NullRunLogger.Instance);

        Assert.Equal(3, fake.Calls.Count); // 1 batch call + 2 single fallback calls
        Assert.Equal(2, fake.Calls.Count(call => !call.IsBatch));
        Assert.Equal("Erste Zeile mit Feler.", ParagraphTextMapper.ExtractEditableText(paragraphs[0]));
        Assert.Equal("Zweiter Absatz ist gut.", ParagraphTextMapper.ExtractEditableText(paragraphs[1]));
    }
}
