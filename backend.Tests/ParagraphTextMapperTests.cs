using DocumentFormat.OpenXml.Wordprocessing;
using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Regression tests for footnote/paragraph text extraction. These lock in the
/// invariant that no editable <c>w:t</c> text is dropped just because a run also
/// carries a structural sibling (tab, break, symbol, reference mark, drawing).
/// The tab+text case is the exact layout that shipped truncated footnotes to the
/// model in the field.
/// </summary>
public class ParagraphTextMapperTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string V = "urn:schemas-microsoft-com:vml";

    private static Paragraph P(string innerXml) =>
        new($"<w:p xmlns:w=\"{W}\" xmlns:v=\"{V}\">{innerXml}</w:p>");

    // ---- The actual field bug: tab and text share one run --------------------

    [Fact]
    public void TabAndTextInSameRun_KeepsTheText()
    {
        // Mirrors footnote 2 of the reported document: a footnote ref, a space run,
        // then a run that packs <w:tab/> together with the first citation fragment,
        // then a run that continues mid-word ("juridic" + "isation").
        var p = P(
            "<w:r><w:rPr><w:rStyle w:val=\"Funotenzeichen\"/></w:rPr><w:footnoteRef/></w:r>" +
            "<w:r><w:t xml:space=\"preserve\"> </w:t></w:r>" +
            "<w:r><w:tab/><w:t>Darauf wies nun hin: Charles de Miramon, La juridic</w:t></w:r>" +
            "<w:r><w:t>isation de l’Église, Paris 2025.</w:t></w:r>");

        var extracted = ParagraphTextMapper.ExtractEditableText(p);

        Assert.Contains("Darauf wies nun hin: Charles de Miramon", extracted);
        // The mid-word split across two runs must be joined into one word.
        Assert.Contains("juridicisation", extracted);
        Assert.Equal(" Darauf wies nun hin: Charles de Miramon, La juridicisation de l’Église, Paris 2025.", extracted);
    }

    [Fact]
    public void BreakAndTextInSameRun_KeepsTheText()
    {
        var p = P("<w:r><w:br/><w:t>Erster Teil und zweiter Teil.</w:t></w:r>");
        Assert.Equal("Erster Teil und zweiter Teil.", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void SymbolAndTextInSameRun_KeepsTheText()
    {
        var p = P("<w:r><w:sym w:font=\"Symbol\" w:char=\"F0E0\"/><w:t>Text nach Symbol.</w:t></w:r>");
        Assert.Equal("Text nach Symbol.", ParagraphTextMapper.ExtractEditableText(p));
    }

    // ---- Things that must still be excluded ----------------------------------

    [Fact]
    public void ReferenceMarkRun_IsExcluded_ButFollowingTextKept()
    {
        var p = P(
            "<w:r><w:footnoteRef/></w:r>" +
            "<w:r><w:tab/><w:t>Kuttner, Repertorium der Kanonistik.</w:t></w:r>");
        Assert.Equal("Kuttner, Repertorium der Kanonistik.", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void FieldResultText_IsExcluded()
    {
        // "Siehe Fn. [NOTEREF->5] oben." — the computed "5" must not be extracted.
        var p = P(
            "<w:r><w:t xml:space=\"preserve\">Siehe Fn. </w:t></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "<w:r><w:instrText xml:space=\"preserve\"> NOTEREF _Ref1 \\h </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "<w:r><w:t>5</w:t></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            "<w:r><w:t xml:space=\"preserve\"> oben.</w:t></w:r>");

        var extracted = ParagraphTextMapper.ExtractEditableText(p);
        Assert.DoesNotContain("5", extracted);
        Assert.Contains("Siehe Fn.", extracted);
        Assert.Contains("oben.", extracted);
    }

    [Fact]
    public void DeletedRun_IsExcluded()
    {
        var p = P(
            "<w:r><w:t xml:space=\"preserve\">Keep </w:t></w:r>" +
            "<w:del><w:r><w:delText xml:space=\"preserve\">deleted </w:delText></w:r></w:del>" +
            "<w:r><w:t>tail.</w:t></w:r>");
        Assert.Equal("Keep tail.", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void NestedTextboxContent_IsExcludedFromHost()
    {
        var p = P(
            "<w:r><w:t xml:space=\"preserve\">Host text </w:t></w:r>" +
            "<w:r><w:pict><v:shape><v:textbox><w:txbxContent>" +
            "<w:p><w:r><w:t>Inside box</w:t></w:r></w:p>" +
            "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>" +
            "<w:r><w:t>after.</w:t></w:r>");

        var extracted = ParagraphTextMapper.ExtractEditableText(p);
        Assert.DoesNotContain("Inside box", extracted);
        Assert.Equal("Host text after.", extracted);
    }

    // ---- The extraction-coverage invariant -----------------------------------

    [Fact]
    public void HealthyParagraph_KeepsAllVisibleChars()
    {
        var p = P(
            "<w:r><w:footnoteRef/></w:r>" +
            "<w:r><w:tab/><w:t>Vollständiger Fußnotentext ohne Verlust.</w:t></w:r>");

        var extracted = ParagraphTextMapper.ExtractEditableText(p);
        var visible = ParagraphTextMapper.CountVisibleTextChars(p);
        Assert.Equal(visible, extracted.Length);
    }

    [Fact]
    public void TocEntry_FieldOnlyParagraph_ReportsNoExtractionGap()
    {
        // A TOC line ("Abkürzungsverzeichnis" + tab + page number) is entirely field
        // code/result text: the TOC field wraps a hyperlink whose page number comes
        // from a nested PAGEREF field. None of it should ever be rewritten, and
        // CountVisibleTextChars must agree with ExtractEditableText that there is
        // nothing to extract here — otherwise every TOC entry falsely looks like a
        // dropped-text regression.
        var p = P(
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "<w:r><w:instrText xml:space=\"preserve\"> TOC \\o \\h \\z \\u </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "<w:r><w:t>Abkürzungsverzeichnis</w:t></w:r>" +
            "<w:r><w:tab/></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>" +
            "<w:r><w:instrText>PAGEREF _Toc1 \\h</w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" +
            "<w:r><w:t>29</w:t></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>");

        var extracted = ParagraphTextMapper.ExtractEditableText(p);
        var visible = ParagraphTextMapper.CountVisibleTextChars(p);

        Assert.Equal(string.Empty, extracted);
        Assert.Equal(0, visible);
    }

    // ---- Round-trip: correction is applied and the tab survives ---------------

    [Fact]
    public void ApplyCorrection_PreservesTab_AndWritesCorrectedText()
    {
        var p = P(
            "<w:r><w:footnoteRef/></w:r>" +
            "<w:r><w:t xml:space=\"preserve\"> </w:t></w:r>" +
            "<w:r><w:tab/><w:t>Charles de Miramont, La juridic</w:t></w:r>" +
            "<w:r><w:t>isation de l’Église, Paris 2025.</w:t></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        // Correct the surname typo "Miramont" -> "Miramon".
        var corrected = original.Replace("Miramont", "Miramon");
        Assert.NotEqual(original, corrected);

        ParagraphTextMapper.ApplyCorrection(p, original, corrected);

        // Structure preserved: exactly one tab and one footnote ref mark survive.
        Assert.Single(p.Descendants<TabChar>());
        Assert.Single(p.Descendants<FootnoteReferenceMark>());
        // Text now reflects the correction and re-extracts cleanly.
        Assert.Equal(corrected, ParagraphTextMapper.ExtractEditableText(p));
        Assert.Contains("Miramon,", ParagraphTextMapper.ExtractEditableText(p));
    }
}
