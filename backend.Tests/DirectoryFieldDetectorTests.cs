using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Locks in that generated directory content (table of contents, index, table of figures)
/// stays out of the model payload. The decisive case is the *middle* of a TOC: the field's
/// begin/instrText/separate markers sit in the first entry paragraph and the matching end
/// in the last, so every entry in between carries no field marker of its own and reads as
/// ordinary body text unless field state is carried across paragraph boundaries.
/// </summary>
public class DirectoryFieldDetectorTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Body B(string innerXml) => new($"<w:body xmlns:w=\"{W}\">{innerXml}</w:body>");

    private static readonly IReadOnlySet<string> NoStyles = new HashSet<string>();

    private static string Text(Paragraph p) => string.Concat(p.Descendants<Text>().Select(t => t.Text));

    /// <summary>A three-entry TOC field exactly as Word writes it.</summary>
    private const string TocFieldXml =
        "<w:p><w:pPr><w:pStyle w:val=\"berschrift1\"/></w:pPr><w:r><w:t>Inhaltsverzeichnis</w:t></w:r></w:p>" +
        "<w:p>" +
        "  <w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
        "  <w:r><w:instrText xml:space=\"preserve\"> TOC \\o \"1-3\" \\h \\z \\u </w:instrText></w:r>" +
        "  <w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
        "  <w:hyperlink><w:r><w:t>1 Einleitung</w:t></w:r><w:r><w:tab/><w:t>1</w:t></w:r></w:hyperlink>" +
        "</w:p>" +
        "<w:p><w:hyperlink><w:r><w:t>2 Hauptteil</w:t></w:r><w:r><w:tab/><w:t>7</w:t></w:r></w:hyperlink></w:p>" +
        "<w:p><w:hyperlink><w:r><w:t>3 Schluss</w:t></w:r><w:r><w:tab/><w:t>42</w:t></w:r></w:hyperlink></w:p>" +
        "<w:p><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>";

    [Fact]
    public void TocEntriesWithoutOwnFieldMarker_AreDetected()
    {
        var body = B(TocFieldXml);

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);

        var detected = body.Descendants<Paragraph>().Where(directory.Contains).Select(Text).ToList();
        Assert.Contains("1 Einleitung1", detected);
        Assert.Contains("2 Hauptteil7", detected);
        Assert.Contains("3 Schluss42", detected);
    }

    [Fact]
    public void DirectoryTitleBeforeTheField_StaysEditable()
    {
        // The "Inhaltsverzeichnis" heading is the user's own text, not field output — in
        // translation mode it must still be translated.
        var body = B(TocFieldXml);

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);

        var title = body.Descendants<Paragraph>().First();
        Assert.False(directory.Contains(title));
    }

    [Fact]
    public void OrdinaryParagraphWithPageRefField_StaysEditable()
    {
        var body = B(
            "<w:p>" +
            "  <w:r><w:t xml:space=\"preserve\">Siehe dazu oben S. </w:t></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "  <w:r><w:instrText> PAGEREF _Ref123 \\h </w:instrText></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "  <w:r><w:t>17</w:t></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            "</w:p>");

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);

        Assert.Empty(directory);
    }

    [Fact]
    public void NestedPageRefInsideToc_DoesNotCloseTheTocRange()
    {
        // Word nests a PAGEREF field per entry. Its end marker must pop only the inner
        // field, or every entry after the first would fall out of the TOC range.
        var body = B(
            "<w:p>" +
            "  <w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "  <w:r><w:instrText> TOC \\o \"1-3\" </w:instrText></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "  <w:r><w:t>1 Einleitung</w:t></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "  <w:r><w:instrText> PAGEREF _Toc1 \\h </w:instrText></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "  <w:r><w:t>1</w:t></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            "</w:p>" +
            "<w:p><w:r><w:t>2 Hauptteil</w:t></w:r></w:p>" +
            "<w:p><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>" +
            "<w:p><w:r><w:t>Erster Satz des Fließtextes.</w:t></w:r></w:p>");

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);

        var paragraphs = body.Descendants<Paragraph>().ToList();
        Assert.True(directory.Contains(paragraphs[1]), "entry after the nested PAGEREF");
        Assert.False(directory.Contains(paragraphs[3]), "body text after the TOC field ended");
    }

    [Fact]
    public void InstructionSplitAcrossRuns_IsStillRecognized()
    {
        var body = B(
            "<w:p>" +
            "  <w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "  <w:r><w:instrText xml:space=\"preserve\"> TO</w:instrText></w:r>" +
            "  <w:r><w:instrText xml:space=\"preserve\">C \\o \"1-3\" </w:instrText></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "</w:p>" +
            "<w:p><w:r><w:t>1 Einleitung</w:t></w:r></w:p>" +
            "<w:p><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>");

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);

        Assert.Contains("1 Einleitung", body.Descendants<Paragraph>().Where(directory.Contains).Select(Text));
    }

    [Fact]
    public void UnbalancedFieldMarkers_DoNotSwallowTheDocument()
    {
        // A begin without a matching end must not mark the rest of the body as directory
        // content — that would silently drop it from correction.
        var body = B(
            "<w:p>" +
            "  <w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "  <w:r><w:instrText> TOC \\o \"1-3\" </w:instrText></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "</w:p>" +
            "<w:p><w:r><w:t>Erster Satz des Fließtextes.</w:t></w:r></w:p>");

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);

        Assert.Empty(directory);
    }

    [Fact]
    public void OrphanedTocStyle_IsDetectedById()
    {
        // No field left (dissolved TOC), but the entry style still identifies it. The
        // built-in id spelling drops the space of the built-in name ("toc 1" -> "TOC1").
        var body = B(
            "<w:p><w:pPr><w:pStyle w:val=\"TOC1\"/></w:pPr><w:r><w:t>1 Einleitung</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:pStyle w:val=\"Standard\"/></w:pPr><w:r><w:t>Fließtext.</w:t></w:r></w:p>");

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);

        var paragraphs = body.Descendants<Paragraph>().ToList();
        Assert.True(directory.Contains(paragraphs[0]));
        Assert.False(directory.Contains(paragraphs[1]));
    }

    [Fact]
    public void LocalizedStyleId_IsResolvedThroughItsBuiltinName()
    {
        // A German Word writes the style id "Verzeichnis1"; only its built-in w:name
        // ("toc 1") is locale-independent.
        using var stream = new MemoryStream();
        using var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new Style(new StyleName { Val = "toc 1" }) { StyleId = "Verzeichnis1", Type = StyleValues.Paragraph },
            new Style(new StyleName { Val = "heading 1" }) { StyleId = "berschrift1", Type = StyleValues.Paragraph });

        var styleIds = DirectoryFieldDetector.ResolveDirectoryStyleIds(doc);

        Assert.Contains("Verzeichnis1", styleIds);
        Assert.DoesNotContain("berschrift1", styleIds);
    }

    [Fact]
    public void TocHeadingStyle_StaysEditable()
    {
        using var stream = new MemoryStream();
        using var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new Style(new StyleName { Val = "TOC Heading" }) { StyleId = "Inhaltsverzeichnisberschrift", Type = StyleValues.Paragraph });

        var styleIds = DirectoryFieldDetector.ResolveDirectoryStyleIds(doc);

        Assert.Empty(styleIds);
    }

    // ---- Refresh-on-open flag -----------------------------------------------

    [Fact]
    public void MarkDirty_FlagsTheTocFieldOnly()
    {
        var body = B(TocFieldXml +
            "<w:p>" +
            "  <w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "  <w:r><w:instrText> DATE \\@ \"dd.MM.yyyy\" </w:instrText></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "  <w:r><w:t>01.01.2020</w:t></w:r>" +
            "  <w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            "</w:p>");

        var marked = DirectoryFieldDetector.MarkDirectoryFieldsDirty(body);

        Assert.Equal(1, marked);
        var dirty = body.Descendants<FieldChar>().Where(f => f.Dirty?.Value == true).ToList();
        var flagged = Assert.Single(dirty);
        Assert.Equal(FieldCharValues.Begin, flagged.FieldCharType?.Value);
        // The DATE field keeps its stale result: refreshing it would silently rewrite
        // document content to today's date.
        Assert.Equal("01.01.2020", Text(body.Descendants<Paragraph>().Last()));
    }

    [Fact]
    public void MarkDirty_LeavesFieldResultParagraphsUntouched()
    {
        var body = B(TocFieldXml);
        var before = body.Descendants<Paragraph>().Count();
        var textBefore = body.InnerText;

        DirectoryFieldDetector.MarkDirectoryFieldsDirty(body);

        Assert.Equal(before, body.Descendants<Paragraph>().Count());
        Assert.Equal(textBefore, body.InnerText);
    }

    [Fact]
    public void Scanner_KeepsDirectoryParagraphsOutOfTheWorkItems()
    {
        using var stream = new MemoryStream();
        using var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(B(TocFieldXml + "<w:p><w:r><w:t>Erster Satz des Fließtextes.</w:t></w:r></w:p>"));

        var coverage = DocxPartScanner.Scan(doc);

        // Three entries; the empty paragraph holding only the field end carries no text
        // and was already filtered out as blank.
        Assert.Equal(3, coverage.DirectoryParagraphs);
        var mainItem = coverage.WorkItems.Single(i => i.Kind == ProcessorWorkItemKind.Main);
        Assert.Equal(
            ["Inhaltsverzeichnis", "Erster Satz des Fließtextes."],
            mainItem.Paragraphs.Select(Text));
    }

    [Fact]
    public void SimpleFieldToc_IsDetectedAndFlagged()
    {
        var body = B(
            "<w:p><w:fldSimple w:instr=\" TOC \\o &quot;1-3&quot; \">" +
            "<w:r><w:t>1 Einleitung</w:t></w:r></w:fldSimple></w:p>");

        var directory = DirectoryFieldDetector.FindDirectoryParagraphs(body, NoStyles);
        var marked = DirectoryFieldDetector.MarkDirectoryFieldsDirty(body);

        Assert.Single(directory);
        Assert.Equal(1, marked);
        Assert.True(body.Descendants<SimpleField>().Single().Dirty?.Value);
    }
}
