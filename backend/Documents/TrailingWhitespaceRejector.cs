using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

internal static class TrailingWhitespaceRejector
{
    private const char Space = ' ';

    public static void Reject(string documentPath, IRunLogger? logger)
    {
        using var doc = WordprocessingDocument.Open(documentPath, true);
        var rejectedCount = 0;

        foreach (var root in EnumerateRevisionRoots(doc))
        {
            foreach (var paragraph in root.Descendants<Paragraph>().ToList())
            {
                rejectedCount += RejectInParagraph(paragraph);
            }
        }

        if (rejectedCount > 0)
        {
            doc.Save();
            logger?.Info($"Trailing paragraph whitespace rejections: {rejectedCount} occurrence(s).");
        }
    }

    private static int RejectInParagraph(Paragraph paragraph)
    {
        var rejected = 0;
        var current = paragraph.LastChild;

        while (current is not null)
        {
            if (current is ParagraphProperties)
            {
                break;
            }

            var kind = ClassifyChild(current, out var text);

            switch (kind)
            {
                case TrailingKind.DeletedSpace:
                    var replacementRun = BuildReplacementRun(current, text);
                    var anchor = current.PreviousSibling();
                    current.Remove();
                    InsertReplacement(paragraph, anchor, replacementRun);
                    rejected++;
                    current = anchor;
                    break;

                case TrailingKind.InsertedSpace:
                    var prev = current.PreviousSibling();
                    current.Remove();
                    rejected++;
                    current = prev;
                    break;

                case TrailingKind.PlainSpace:
                    current = current.PreviousSibling();
                    break;

                default:
                    return rejected;
            }
        }

        return rejected;
    }

    private static TrailingKind ClassifyChild(OpenXmlElement element, out string text)
    {
        text = string.Empty;

        if (element is DeletedRun deletedRun)
        {
            text = ExtractDeletedText(deletedRun);
            return IsSpaceOnly(text) ? TrailingKind.DeletedSpace : TrailingKind.Other;
        }

        if (element is InsertedRun insertedRun)
        {
            text = ExtractInsertedText(insertedRun);
            return IsSpaceOnly(text) ? TrailingKind.InsertedSpace : TrailingKind.Other;
        }

        if (element is Run run)
        {
            if (run.Descendants<DeletedRun>().FirstOrDefault() is { } nestedDel)
            {
                text = ExtractDeletedText(nestedDel);
                return IsSpaceOnly(text) ? TrailingKind.DeletedSpace : TrailingKind.Other;
            }

            if (run.Descendants<InsertedRun>().FirstOrDefault() is { } nestedIns)
            {
                text = ExtractInsertedText(nestedIns);
                return IsSpaceOnly(text) ? TrailingKind.InsertedSpace : TrailingKind.Other;
            }

            text = ExtractRunText(run);
            return IsSpaceOnly(text) ? TrailingKind.PlainSpace : TrailingKind.Other;
        }

        return TrailingKind.Other;
    }

    private static bool IsSpaceOnly(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch != Space)
            {
                return false;
            }
        }

        return true;
    }

    private static string ExtractDeletedText(OpenXmlElement element)
    {
        var builder = new StringBuilder();
        foreach (var delText in element.Descendants<DeletedText>())
        {
            builder.Append(delText.Text);
        }

        return builder.ToString();
    }

    private static string ExtractInsertedText(OpenXmlElement element)
    {
        var builder = new StringBuilder();
        foreach (var text in element.Descendants<Text>())
        {
            builder.Append(text.Text);
        }

        return builder.ToString();
    }

    private static string ExtractRunText(Run run)
    {
        var builder = new StringBuilder();
        foreach (var text in run.Descendants<Text>())
        {
            if (text.Parent is DeletedRun or InsertedRun)
            {
                continue;
            }

            builder.Append(text.Text);
        }

        return builder.ToString();
    }

    private static Run BuildReplacementRun(OpenXmlElement deletedElement, string text)
    {
        var runProps = ResolveRunProperties(deletedElement);
        var textElement = new Text(text) { Space = SpaceProcessingModeValues.Preserve };
        var run = new Run();
        if (runProps is not null)
        {
            run.AppendChild((RunProperties)runProps.CloneNode(true));
        }

        run.AppendChild(textElement);
        return run;
    }

    private static RunProperties? ResolveRunProperties(OpenXmlElement deletedElement)
    {
        if (deletedElement is DeletedRun del)
        {
            return del.Elements<RunProperties>().FirstOrDefault();
        }

        if (deletedElement is Run run && run.RunProperties is not null)
        {
            return run.RunProperties;
        }

        var nestedRun = deletedElement.Descendants<Run>().FirstOrDefault();
        return nestedRun?.RunProperties;
    }

    private static void InsertReplacement(Paragraph paragraph, OpenXmlElement? anchor, Run replacementRun)
    {
        if (anchor is not null && anchor.Parent is not null)
        {
            anchor.InsertAfterSelf(replacementRun);
            return;
        }

        var paragraphProperties = paragraph.Elements<ParagraphProperties>().FirstOrDefault();
        if (paragraphProperties is not null && paragraphProperties.Parent is not null)
        {
            paragraphProperties.InsertAfterSelf(replacementRun);
            return;
        }

        paragraph.PrependChild(replacementRun);
    }

    private static IEnumerable<OpenXmlElement> EnumerateRevisionRoots(WordprocessingDocument doc)
    {
        if (doc.MainDocumentPart?.Document?.Body is not null)
        {
            yield return doc.MainDocumentPart.Document.Body;
        }

        if (doc.MainDocumentPart?.FootnotesPart?.Footnotes is not null)
        {
            yield return doc.MainDocumentPart.FootnotesPart.Footnotes;
        }

        if (doc.MainDocumentPart?.EndnotesPart?.Endnotes is not null)
        {
            yield return doc.MainDocumentPart.EndnotesPart.Endnotes;
        }

        if (doc.MainDocumentPart is null)
        {
            yield break;
        }

        foreach (var header in doc.MainDocumentPart.HeaderParts)
        {
            if (header.Header is not null)
            {
                yield return header.Header;
            }
        }

        foreach (var footer in doc.MainDocumentPart.FooterParts)
        {
            if (footer.Footer is not null)
            {
                yield return footer.Footer;
            }
        }

        if (doc.MainDocumentPart.GlossaryDocumentPart?.GlossaryDocument is not null)
        {
            yield return doc.MainDocumentPart.GlossaryDocumentPart.GlossaryDocument;
        }
    }

    private enum TrailingKind
    {
        Other,
        DeletedSpace,
        InsertedSpace,
        PlainSpace
    }
}
