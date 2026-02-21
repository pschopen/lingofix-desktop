using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

internal sealed record ProcessorWorkItem(string Label, int Weight, List<Paragraph> Paragraphs);

internal sealed class DocxCoverageReport
{
    public required List<ProcessorWorkItem> WorkItems { get; init; }
    public required int TotalParagraphs { get; init; }
    public required int CommentCount { get; init; }
    public required int GlossaryParagraphs { get; init; }
    public required int AltChunkCount { get; init; }
    public required SpecialContentAudit SpecialContentAudit { get; init; }
}

internal static class DocxPartScanner
{
    public static DocxCoverageReport Scan(WordprocessingDocument doc)
    {
        var workItems = new List<ProcessorWorkItem>();
        var totalParagraphs = 0;

        if (doc.MainDocumentPart?.Document?.Body is not null)
        {
            var bodyParagraphs = FilterEditableParagraphs(doc.MainDocumentPart.Document.Body.Descendants<Paragraph>());
            if (bodyParagraphs.Count > 0)
            {
                workItems.Add(new ProcessorWorkItem("Main Document", 70, bodyParagraphs));
                totalParagraphs += bodyParagraphs.Count;
            }
        }

        if (doc.MainDocumentPart?.FootnotesPart?.Footnotes is not null)
        {
            var footnoteParagraphs = FilterEditableParagraphs(doc.MainDocumentPart.FootnotesPart.Footnotes.Descendants<Paragraph>());
            if (footnoteParagraphs.Count > 0)
            {
                workItems.Add(new ProcessorWorkItem("Footnotes", 5, footnoteParagraphs));
                totalParagraphs += footnoteParagraphs.Count;
            }
        }

        if (doc.MainDocumentPart?.EndnotesPart?.Endnotes is not null)
        {
            var endnoteParagraphs = FilterEditableParagraphs(doc.MainDocumentPart.EndnotesPart.Endnotes.Descendants<Paragraph>());
            if (endnoteParagraphs.Count > 0)
            {
                workItems.Add(new ProcessorWorkItem("Endnotes", 5, endnoteParagraphs));
                totalParagraphs += endnoteParagraphs.Count;
            }
        }

        if (doc.MainDocumentPart is not null)
        {
            var headerIndex = 0;
            foreach (var header in doc.MainDocumentPart.HeaderParts)
            {
                var headerParagraphs = FilterEditableParagraphs(header.Header?.Descendants<Paragraph>() ?? Enumerable.Empty<Paragraph>());
                if (headerParagraphs.Count > 0)
                {
                    workItems.Add(new ProcessorWorkItem($"Header {headerIndex + 1}", 2, headerParagraphs));
                    totalParagraphs += headerParagraphs.Count;
                }

                headerIndex++;
            }

            var footerIndex = 0;
            foreach (var footer in doc.MainDocumentPart.FooterParts)
            {
                var footerParagraphs = FilterEditableParagraphs(footer.Footer?.Descendants<Paragraph>() ?? Enumerable.Empty<Paragraph>());
                if (footerParagraphs.Count > 0)
                {
                    workItems.Add(new ProcessorWorkItem($"Footer {footerIndex + 1}", 3, footerParagraphs));
                    totalParagraphs += footerParagraphs.Count;
                }

                footerIndex++;
            }
        }

        if (doc.MainDocumentPart?.GlossaryDocumentPart?.GlossaryDocument is not null)
        {
            var glossaryParagraphs = FilterEditableParagraphs(doc.MainDocumentPart.GlossaryDocumentPart.GlossaryDocument.Descendants<Paragraph>());
            if (glossaryParagraphs.Count > 0)
            {
                workItems.Add(new ProcessorWorkItem("Glossary", 2, glossaryParagraphs));
                totalParagraphs += glossaryParagraphs.Count;
            }
        }

        var commentCount = doc.MainDocumentPart?.WordprocessingCommentsPart?.Comments?.Elements<Comment>().Count() ?? 0;
        var glossaryCount = doc.MainDocumentPart?.GlossaryDocumentPart?.GlossaryDocument?.Descendants<Paragraph>().Count() ?? 0;
        var altChunkCount = doc.MainDocumentPart?.Document?.Body?.Descendants<AltChunk>().Count() ?? 0;
        var specialContentAudit = DocxSpecialContentInspector.Inspect(doc);

        return new DocxCoverageReport
        {
            WorkItems = workItems,
            TotalParagraphs = totalParagraphs,
            CommentCount = commentCount,
            GlossaryParagraphs = glossaryCount,
            AltChunkCount = altChunkCount,
            SpecialContentAudit = specialContentAudit
        };
    }

    private static List<Paragraph> FilterEditableParagraphs(IEnumerable<Paragraph> paragraphs)
    {
        return paragraphs.Where(p => !string.IsNullOrWhiteSpace(ExtractText(p))).ToList();
    }

    private static string ExtractText(Paragraph paragraph)
    {
        return string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
    }
}
