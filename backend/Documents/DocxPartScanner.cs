using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

public enum ProcessorWorkItemKind
{
    Main,
    Footnotes,
    Endnotes,
    Headers,
    Footers,
    Glossary
}

internal sealed record ProcessorWorkItem(ProcessorWorkItemKind Kind, string Label, int Weight, List<Paragraph> Paragraphs);

internal sealed class DocxCoverageReport
{
    public required List<ProcessorWorkItem> WorkItems { get; init; }
    public required int TotalParagraphs { get; init; }
    public required int CommentCount { get; init; }
    public required int GlossaryParagraphs { get; init; }
    public required int AltChunkCount { get; init; }
    public required int DirectoryParagraphs { get; init; }
    public required SpecialContentAudit SpecialContentAudit { get; init; }
}

internal static class DocxPartScanner
{
    public static DocxCoverageReport Scan(WordprocessingDocument doc, IRunLogger? logger = null)
    {
        var workItems = new List<ProcessorWorkItem>();
        var totalParagraphs = 0;
        var directoryStyleIds = DirectoryFieldDetector.ResolveDirectoryStyleIds(doc);
        var directoryParagraphs = 0;

        // Directory detection runs on the main body only: TOC/index fields live there, and
        // scanning notes, headers and footers for them would only add false-positive risk
        // (a STYLEREF in a running header mirrors heading text but is not a directory).
        if (doc.MainDocumentPart?.Document?.Body is not null)
        {
            var body = doc.MainDocumentPart.Document.Body;
            var bodyParagraphs = FilterEditableParagraphs(
                body.Descendants<Paragraph>(),
                DirectoryFieldDetector.FindDirectoryParagraphs(body, directoryStyleIds, logger),
                ref directoryParagraphs);
            if (bodyParagraphs.Count > 0)
            {
                workItems.Add(new ProcessorWorkItem(ProcessorWorkItemKind.Main, "Main Document", 70, bodyParagraphs));
                totalParagraphs += bodyParagraphs.Count;
            }
        }

        if (doc.MainDocumentPart?.FootnotesPart?.Footnotes is not null)
        {
            var footnoteParagraphs = FilterEditableParagraphs(doc.MainDocumentPart.FootnotesPart.Footnotes.Descendants<Paragraph>());
            if (footnoteParagraphs.Count > 0)
            {
                workItems.Add(new ProcessorWorkItem(ProcessorWorkItemKind.Footnotes, "Footnotes", 5, footnoteParagraphs));
                totalParagraphs += footnoteParagraphs.Count;
            }
        }

        if (doc.MainDocumentPart?.EndnotesPart?.Endnotes is not null)
        {
            var endnoteParagraphs = FilterEditableParagraphs(doc.MainDocumentPart.EndnotesPart.Endnotes.Descendants<Paragraph>());
            if (endnoteParagraphs.Count > 0)
            {
                workItems.Add(new ProcessorWorkItem(ProcessorWorkItemKind.Endnotes, "Endnotes", 5, endnoteParagraphs));
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
                    workItems.Add(new ProcessorWorkItem(ProcessorWorkItemKind.Headers, $"Header {headerIndex + 1}", 2, headerParagraphs));
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
                    workItems.Add(new ProcessorWorkItem(ProcessorWorkItemKind.Footers, $"Footer {footerIndex + 1}", 3, footerParagraphs));
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
                workItems.Add(new ProcessorWorkItem(ProcessorWorkItemKind.Glossary, "Glossary", 2, glossaryParagraphs));
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
            DirectoryParagraphs = directoryParagraphs,
            SpecialContentAudit = specialContentAudit
        };
    }

    private static List<Paragraph> FilterEditableParagraphs(IEnumerable<Paragraph> paragraphs)
    {
        return paragraphs.Where(p => !string.IsNullOrWhiteSpace(ExtractText(p))).ToList();
    }

    /// <summary>
    /// Like <see cref="FilterEditableParagraphs(IEnumerable{Paragraph})"/>, but also drops
    /// generated directory paragraphs (see <see cref="DirectoryFieldDetector"/>) and adds
    /// their number to <paramref name="directoryParagraphs"/>. Only paragraphs that would
    /// otherwise have been processed are counted, so the reported number matches the work
    /// actually saved.
    /// </summary>
    private static List<Paragraph> FilterEditableParagraphs(
        IEnumerable<Paragraph> paragraphs,
        IReadOnlySet<Paragraph> directory,
        ref int directoryParagraphs)
    {
        var editable = FilterEditableParagraphs(paragraphs);
        if (directory.Count == 0)
        {
            return editable;
        }

        var kept = editable.Where(p => !directory.Contains(p)).ToList();
        directoryParagraphs += editable.Count - kept.Count;
        return kept;
    }

    private static string ExtractText(Paragraph paragraph)
    {
        // Textbox content is a descendant of its host paragraph but belongs to its own
        // textbox paragraph. Exclude it so the host is judged (and processed) on its own
        // text only; the textbox paragraph is collected and corrected in its own right.
        return string.Concat(paragraph.Descendants<Text>()
            .Where(t => !DocumentPartUtils.IsInsideNestedTextBox(t, paragraph))
            .Select(t => t.Text));
    }
}
