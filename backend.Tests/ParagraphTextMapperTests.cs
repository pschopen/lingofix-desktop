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
        // The letter-free spacer run before the tab is a label prefix and stays out of
        // the editable stream (so the LLM never sees it and write-back never touches it).
        Assert.Equal("Darauf wies nun hin: Charles de Miramon, La juridicisation de l’Église, Paris 2025.", extracted);
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

    // ---- Label prefix before a tab: outline numbers, footnote spacers ---------

    [Fact]
    public void HeadingOutlineNumber_BeforeTab_IsExcludedFromEditableText()
    {
        // Mirrors the field bug: "1." + tab + heading text. The tab is invisible in
        // the extracted stream, so the LLM used to see "1.Bestimmung …", drop the
        // number, and the write-back deleted the "1." run.
        var p = P(
            "<w:r><w:t>1.</w:t></w:r>" +
            "<w:r><w:tab/><w:t>Bestimmung des Gegenstands</w:t></w:r>");

        Assert.Equal("Bestimmung des Gegenstands", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyCorrection_NeverTouchesOutlineNumberRun()
    {
        var p = P(
            "<w:r><w:t>1.</w:t></w:r>" +
            "<w:r><w:tab/><w:t>Bestimung des Gegenstands</w:t></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        ParagraphTextMapper.ApplyCorrection(p, original, "Bestimmung des Gegenstands");

        Assert.Single(p.Descendants<TabChar>());
        Assert.Equal("1.", p.Descendants<Run>().First().InnerText);
        Assert.Equal("Bestimmung des Gegenstands", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_KeepsOutlineNumberAndTab_TextGoesAfterTheTab()
    {
        var p = P(
            "<w:r><w:t>1.</w:t></w:r>" +
            "<w:r><w:tab/><w:t>Bestimmung des Gegenstands</w:t></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        ParagraphTextMapper.ApplyTranslation(p, original, "Definition of the subject");

        // The outline number run is untouched and the tab survives; the translated
        // text lives in the run after the tab.
        var runs = p.Descendants<Run>().ToList();
        Assert.Equal("1.", runs[0].InnerText);
        Assert.Single(p.Descendants<TabChar>());
        Assert.Equal("Definition of the subject", ParagraphTextMapper.ExtractEditableText(p));
        // Inside the tab run, the text node still sits after the tab element.
        var tabRun = runs[1];
        var children = tabRun.ChildElements.ToList();
        Assert.True(children.FindIndex(c => c is TabChar) < children.FindIndex(c => c is Text));
    }

    [Fact]
    public void ApplyTranslation_FootnoteSpacerAndTab_TranslationGoesAfterTheTab()
    {
        // Footnote layout: ref mark, " " spacer, tab + text. The translation must not
        // land in the spacer run before the tab (which used to visually delete the tab).
        var p = P(
            "<w:r><w:footnoteRef/></w:r>" +
            "<w:r><w:t xml:space=\"preserve\"> </w:t></w:r>" +
            "<w:r><w:tab/><w:t>So die Maxime des kanonischen Rechts.</w:t></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        ParagraphTextMapper.ApplyTranslation(p, original, "Thus the maxim of canon law.");

        var runs = p.Descendants<Run>().ToList();
        Assert.Equal(" ", runs[1].InnerText);
        Assert.Single(p.Descendants<TabChar>());
        Assert.Equal("Thus the maxim of canon law.", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void LettersBeforeTab_AreRealContent_NotALabel()
    {
        // "Siehe" + tab + "Kapitel 3": letters before the tab mean it is content, not
        // a label — everything stays editable.
        var p = P(
            "<w:r><w:t>Siehe</w:t></w:r>" +
            "<w:r><w:tab/><w:t>Kapitel 3 der Einleitung</w:t></w:r>");

        Assert.Equal("SieheKapitel 3 der Einleitung", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void TextBeforeTabInsideSameRun_DisablesPrefixStripping()
    {
        // The label boundary would cut through the run ("1." and the tab share a run
        // with the number before the tab): nothing is stripped.
        var p = P("<w:r><w:t>1.</w:t><w:tab/><w:t>Bestimmung des Gegenstands</w:t></w:r>");

        Assert.Equal("1.Bestimmung des Gegenstands", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void CountVisibleTextChars_MirrorsLabelPrefixExclusion()
    {
        var p = P(
            "<w:r><w:t>1.</w:t></w:r>" +
            "<w:r><w:tab/><w:t>Bestimmung des Gegenstands</w:t></w:r>");

        Assert.Equal(
            ParagraphTextMapper.ExtractEditableText(p).Length,
            ParagraphTextMapper.CountVisibleTextChars(p));
    }

    // ---- ApplyTranslation: marker-free write-back (Phase 2) -------------------

    [Fact]
    public void ApplyTranslation_SingleRun_ReplacesFully()
    {
        var p = P("<w:r><w:t>Hallo Welt.</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);

        ParagraphTextMapper.ApplyTranslation(p, original, "Hello world.");

        Assert.Equal("Hello world.", ParagraphTextMapper.ExtractEditableText(p));
        Assert.Single(p.Descendants<Run>());
    }

    [Fact]
    public void ApplyTranslation_BoldWordInMiddle_TextInFirstNode_DominantNonBoldFormatting()
    {
        var p = P(
            "<w:r><w:rPr><w:i/></w:rPr><w:t xml:space=\"preserve\">Der </w:t></w:r>" +
            "<w:r><w:rPr><w:b/></w:rPr><w:t>wichtige</w:t></w:r>" +
            "<w:r><w:t xml:space=\"preserve\"> Satz endet hier.</w:t></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        ParagraphTextMapper.ApplyTranslation(p, original, "The important sentence ends here.");

        Assert.Equal("The important sentence ends here.", ParagraphTextMapper.ExtractEditableText(p));
        var runs = p.Descendants<Run>().ToList();
        Assert.Equal(3, runs.Count);
        // All text lives in the first run's text node; the rest are emptied.
        Assert.Equal("The important sentence ends here.", runs[0].Descendants<Text>().First().Text);
        Assert.All(runs.Skip(1).SelectMany(r => r.Descendants<Text>()), t => Assert.Equal(string.Empty, t.Text));
        // Dominant run (the longest original share, " Satz endet hier.") had no formatting,
        // so the first run's italic must have been overwritten away.
        Assert.Null(runs[0].RunProperties);
    }

    [Fact]
    public void ApplyTranslation_PredominantlyItalicParagraph_StaysItalic()
    {
        var p = P(
            "<w:r><w:rPr><w:i/></w:rPr><w:t xml:space=\"preserve\">Dies ist ein überwiegend kursiver Satz, </w:t></w:r>" +
            "<w:r><w:t>kurz.</w:t></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        ParagraphTextMapper.ApplyTranslation(p, original, "This is a mostly italic sentence, short.");

        var runs = p.Descendants<Run>().ToList();
        Assert.NotNull(runs[0].RunProperties?.Italic);
    }

    [Fact]
    public void ApplyTranslation_FootnoteReferenceMidParagraph_SplitsIntoTwoSegmentsAtWordBoundary()
    {
        var p = P(
            "<w:r><w:t>AAAAAAAAAA</w:t></w:r>" +
            "<w:r><w:footnoteReference w:id=\"1\"/></w:r>" +
            "<w:r><w:t>BBBBBBBBBBBBBBBBBBBB</w:t></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        Assert.Equal(30, original.Length);

        var translated = "one two three four five six seven eight nine ten";
        ParagraphTextMapper.ApplyTranslation(p, original, translated);

        Assert.Single(p.Descendants<FootnoteReference>());
        var texts = p.Descendants<Text>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal(translated, string.Concat(texts));

        // The cut point must fall on a word boundary, not mid-word.
        var boundary = texts[0].Length;
        if (boundary > 0 && boundary < translated.Length)
        {
            Assert.False(char.IsLetterOrDigit(translated[boundary - 1]) && char.IsLetterOrDigit(translated[boundary]));
        }
    }

    [Fact]
    public void ApplyTranslation_FootnoteReferenceAtEnd_PutsAllTextBeforeAnchor()
    {
        var p = P(
            "<w:r><w:t>Text davor.</w:t></w:r>" +
            "<w:r><w:footnoteReference w:id=\"1\"/></w:r>");

        var original = ParagraphTextMapper.ExtractEditableText(p);
        ParagraphTextMapper.ApplyTranslation(p, original, "Text before it.");

        Assert.Single(p.Descendants<FootnoteReference>());
        var texts = p.Descendants<Text>().Select(t => t.Text).ToList();
        Assert.Equal("Text before it.", texts[0]);
    }

    [Fact]
    public void ApplyTranslation_ShortHeading_AllowsExpansionBeyondRatioGuard()
    {
        var p = P("<w:r><w:t>TOC</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        var translated = "Inhaltsverzeichnis"; // 6x expansion: would fail the 4.0x ratio guard.

        ParagraphTextMapper.ApplyTranslation(p, original, translated);

        Assert.Equal(translated, ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_EmptyResponse_LeavesParagraphUnchanged()
    {
        var p = P("<w:r><w:t>Unverändert.</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);

        ParagraphTextMapper.ApplyTranslation(p, original, "   ");

        Assert.Equal(original, ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_LongOriginal_ExcessiveExpansion_IsRejected()
    {
        var p = P($"<w:r><w:t>{new string('A', 100)}</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        var tooLong = new string('B', 500); // 5x expansion, exceeds the 4.0x ratio guard.

        ParagraphTextMapper.ApplyTranslation(p, original, tooLong);

        Assert.Equal(original, ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_ShortOriginal_ExceedsAbsoluteCap_IsRejected()
    {
        var p = P("<w:r><w:t>Kurz</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        // "Kurz" is 4 chars; cap is max(4 * 6.0, 60) = 60. 70 exceeds it.
        var tooLong = new string('x', 70);

        ParagraphTextMapper.ApplyTranslation(p, original, tooLong);

        Assert.Equal(original, ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_ShortHeading_TightenedCap_RejectsBlobThatOldFlatCapWouldHaveAllowed()
    {
        // A 26-char heading "translated" into a 200-char single-block invention (no
        // newline, so Point 1's multi-paragraph guard doesn't apply here — this is
        // Point 2's ratio-based cap doing the work). The old flat 400-char cap for short
        // originals let this straight through; the new cap (max(6x original, 60) = 156
        // here) catches it.
        var p = P("<w:r><w:t>Bestimmung des Gegenstands</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        Assert.Equal(26, original.Length);
        var hallucinated = new string('あ', 200);

        ParagraphTextMapper.ApplyTranslation(p, original, hallucinated);

        Assert.Equal(original, ParagraphTextMapper.ExtractEditableText(p));
    }

    // ---- Multi-paragraph results: the model answered with more than one paragraph ---

    [Fact]
    public void ApplyTranslation_ModelHallucinatesContinuation_UsesOnlyTheFirstBlock()
    {
        // Mirrors the field bug: a short heading gets a correct short translation as the
        // first paragraph, followed by a hallucinated, unrelated continuation after a
        // blank line. A single extracted paragraph can never legitimately contain a
        // literal newline, so only the first block is ever eligible for write-back.
        var p = P("<w:r><w:t>Bestimmung des Gegenstands</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        var modelReply = "対象の定義\n\n" + new string('あ', 200) + "\n\n" + new string('い', 200);

        var applied = ParagraphTextMapper.ApplyTranslation(p, original, modelReply);

        Assert.True(applied);
        Assert.Equal("対象の定義", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_ModelHallucinatesContinuation_FirstBlockTooLong_Discarded()
    {
        // Same shape, but this time even the leading block alone fails the length-safety
        // check: nothing is written back, and the paragraph stays untouched.
        var p = P("<w:r><w:t>Kurz</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        var modelReply = new string('あ', 200) + "\n\n" + new string('い', 200);

        var applied = ParagraphTextMapper.ApplyTranslation(p, original, modelReply);

        Assert.False(applied);
        Assert.Equal(original, ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyCorrection_ModelHallucinatesContinuation_UsesOnlyTheFirstBlock()
    {
        var p = P("<w:r><w:t>Ein kurzer Satz mit einem Feler.</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        var modelReply = "Ein kurzer Satz mit einem Fehler.\n\nUnd hier erfindet das Modell einen ganz neuen, thematisch fremden Absatz frei dazu, der niemals im Original stand.";

        var applied = ParagraphTextMapper.ApplyCorrection(p, original, modelReply);

        Assert.True(applied);
        Assert.Equal("Ein kurzer Satz mit einem Fehler.", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_SingleNewlineWithoutBlankLine_SplitsOnTheFirstNewline()
    {
        var p = P("<w:r><w:t>Kurzer Titel</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        var modelReply = "Short Title\nSome unexpected second line.";

        var applied = ParagraphTextMapper.ApplyTranslation(p, original, modelReply);

        Assert.True(applied);
        Assert.Equal("Short Title", ParagraphTextMapper.ExtractEditableText(p));
    }

    [Fact]
    public void ApplyTranslation_MultiParagraphReply_EmptyFirstBlock_IsEntirelyDiscarded()
    {
        var p = P("<w:r><w:t>Unverändert lassen.</w:t></w:r>");
        var original = ParagraphTextMapper.ExtractEditableText(p);
        var modelReply = "\n\nDoch etwas Text, aber erst nach einer leeren ersten Zeile.";

        var applied = ParagraphTextMapper.ApplyTranslation(p, original, modelReply);

        Assert.False(applied);
        Assert.Equal(original, ParagraphTextMapper.ExtractEditableText(p));
    }
}
