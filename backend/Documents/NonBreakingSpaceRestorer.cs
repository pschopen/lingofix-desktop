using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

internal static class NonBreakingSpaceRestorer
{
    private const char NonBreakingSpace = '\u00A0';
    private const char RegularSpace = ' ';

    public static void Restore(string documentPath, IRunLogger? logger)
    {
        using var doc = WordprocessingDocument.Open(documentPath, true);
        var restoredCount = 0;

        foreach (var root in EnumerateRevisionRoots(doc))
        {
            foreach (var paragraph in root.Descendants<Paragraph>().ToList())
            {
                restoredCount += RestoreInParagraph(paragraph);
            }
        }

        if (restoredCount > 0)
        {
            doc.Save();
            logger?.Info($"Non-breaking spaces restored: {restoredCount} occurrence(s).");
        }
    }

    private static int RestoreInParagraph(Paragraph paragraph)
    {
        var restored = 0;
        var current = paragraph.FirstChild;

        while (current is not null)
        {
            var next = current.NextSibling();
            if (next is null)
            {
                break;
            }

            var firstKind = ClassifyChild(current);
            var secondKind = ClassifyChild(next);

            var pair = TryGetNbspRestorationPair(current, firstKind, next, secondKind);
            if (pair is null)
            {
                current = next;
                continue;
            }

            var (deletedElement, insertedElement) = pair.Value;
            var deletedText = ExtractDeletedText(deletedElement);
            var insertedText = ExtractInsertedText(insertedElement);

            if (!IsNbspToSpaceOnly(deletedText, insertedText))
            {
                current = next;
                continue;
            }

            var replacementRun = BuildReplacementRun(deletedElement, deletedText);
            var anchor = current.PreviousSibling();

            deletedElement.Remove();
            insertedElement.Remove();

            InsertReplacement(paragraph, anchor, replacementRun);
            restored++;

            current = replacementRun.NextSibling();
        }

        return restored;
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

    private static (OpenXmlElement Deleted, OpenXmlElement Inserted)? TryGetNbspRestorationPair(
        OpenXmlElement firstElement,
        BlockKind firstKind,
        OpenXmlElement secondElement,
        BlockKind secondKind)
    {
        if (firstKind == BlockKind.Deleted && secondKind == BlockKind.Inserted)
        {
            return (firstElement, secondElement);
        }

        if (firstKind == BlockKind.Inserted && secondKind == BlockKind.Deleted)
        {
            return (secondElement, firstElement);
        }

        return null;
    }

    private static BlockKind ClassifyChild(OpenXmlElement element)
    {
        if (element is DeletedRun)
        {
            return BlockKind.Deleted;
        }

        if (element is InsertedRun)
        {
            return BlockKind.Inserted;
        }

        if (element is Run run)
        {
            if (run.Descendants<DeletedRun>().Any())
            {
                return BlockKind.Deleted;
            }

            if (run.Descendants<InsertedRun>().Any())
            {
                return BlockKind.Inserted;
            }
        }

        return BlockKind.Plain;
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

    private static bool IsNbspToSpaceOnly(string deleted, string inserted)
    {
        if (deleted.Length != inserted.Length || deleted.Length == 0)
        {
            return false;
        }

        var hasNbspToSpace = false;
        for (var i = 0; i < deleted.Length; i++)
        {
            var delChar = deleted[i];
            var insChar = inserted[i];

            if (delChar == insChar)
            {
                continue;
            }

            if (delChar == NonBreakingSpace && insChar == RegularSpace)
            {
                hasNbspToSpace = true;
                continue;
            }

            return false;
        }

        return hasNbspToSpace;
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
        if (deletedElement is Run run && run.RunProperties is not null)
        {
            return run.RunProperties;
        }

        var nestedRun = deletedElement.Descendants<Run>().FirstOrDefault();
        return nestedRun?.RunProperties;
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

    private enum BlockKind
    {
        Plain,
        Deleted,
        Inserted
    }
}
