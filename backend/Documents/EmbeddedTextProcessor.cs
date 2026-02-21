using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace Lingofix.Backend.Documents;

internal sealed class EmbeddedTextProcessingResult
{
    public required int ChartTextNodesVisited { get; init; }
    public required int SmartArtTextNodesVisited { get; init; }
    public required int TextNodesChanged { get; init; }
}

internal static class EmbeddedTextProcessor
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public static async Task<EmbeddedTextProcessingResult> ProcessAsync(
        WordprocessingDocument doc,
        LlmClient llmClient,
        IRunLogger? logger,
        CancellationToken cancellationToken)
    {
        var chartVisited = 0;
        var smartArtVisited = 0;
        var changed = 0;
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in DocumentPartUtils.EnumerateParts(doc.MainDocumentPart))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isChart = part.ContentType.Contains("drawingml.chart", StringComparison.OrdinalIgnoreCase);
            var isSmartArt = part.ContentType.Contains("diagramData+xml", StringComparison.OrdinalIgnoreCase);
            if (!isChart && !isSmartArt)
            {
                continue;
            }

            var xml = DocumentPartUtils.TryReadXml(part);
            if (xml is null)
            {
                continue;
            }

            var textNodes = xml.Descendants(A + "t")
                .Where(e => !string.IsNullOrWhiteSpace(e.Value))
                .ToList();

            if (isChart)
            {
                chartVisited += textNodes.Count;
            }

            if (isSmartArt)
            {
                smartArtVisited += textNodes.Count;
            }

            var partChanged = false;
            foreach (var node in textNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = node.Value;
                if (!cache.TryGetValue(original, out var corrected))
                {
                    corrected = await llmClient.CorrectAsync(original, cancellationToken);
                    if (string.IsNullOrWhiteSpace(corrected))
                    {
                        corrected = original;
                    }

                    cache[original] = corrected;
                }

                if (string.Equals(original, corrected, StringComparison.Ordinal))
                {
                    continue;
                }

                node.Value = corrected;
                changed++;
                partChanged = true;
            }

            if (partChanged)
            {
                SaveXml(part, xml);
            }
        }

        if (changed > 0)
        {
            logger?.Info($"Embedded text updated: {changed} text nodes (charts/smartart).");
        }

        return new EmbeddedTextProcessingResult
        {
            ChartTextNodesVisited = chartVisited,
            SmartArtTextNodesVisited = smartArtVisited,
            TextNodesChanged = changed
        };
    }
    private static void SaveXml(OpenXmlPart part, XDocument xml)
    {
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        xml.Save(stream, SaveOptions.DisableFormatting);
    }
}
