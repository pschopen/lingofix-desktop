using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// The detector decides whether a paragraph is sent to the LLM at all, so the negative
/// cases matter more than the positive ones: a false positive silently leaves real text
/// uncorrected/untranslated, while a false negative only costs a few tokens.
/// </summary>
public class OutlineLabelDetectorTests
{
    // ---- Marginal numbers (no letters at all) -----------------------------------

    [Theory]
    [InlineData("12")]
    [InlineData("(3)")]
    [InlineData("§ 12")]
    [InlineData("§§ 12, 13")]
    [InlineData("2.3.4")]
    [InlineData("– 17 –")]
    [InlineData("[5]")]
    [InlineData("1.000")]
    [InlineData("1.000,00 € 2.500,00 €")]
    public void NumberOnly_IsLabel(string text) =>
        Assert.True(OutlineLabelDetector.IsLabelOnly(text));

    // ---- German legal outline levels --------------------------------------------

    [Theory]
    [InlineData("A.")]           // Gliederungsebene 1
    [InlineData("B.")]
    [InlineData("I.")]           // Ebene 2, römisch
    [InlineData("IV.")]
    [InlineData("XII.")]
    [InlineData("1.")]           // Ebene 3
    [InlineData("a)")]           // Ebene 4
    [InlineData("b)")]
    [InlineData("aa)")]          // Ebene 5, verdoppelt
    [InlineData("bb)")]
    [InlineData("ccc)")]
    [InlineData("(1)")]          // Ebene 6
    [InlineData("(a)")]
    [InlineData("(aa)")]
    [InlineData("i)")]           // kleinrömisch
    [InlineData("iii)")]
    [InlineData("α)")]           // griechisch
    [InlineData("β)")]
    [InlineData("A. I. 1. a) aa) (1)")]  // vollständige Kette in einem Absatz
    [InlineData("A.1")]          // alphanumerisch verbunden
    [InlineData("B.2.a")]
    [InlineData("IV.2")]
    [InlineData("1.a")]
    [InlineData("A.1.")]
    [InlineData("a.")]
    [InlineData("A)")]
    [InlineData("1.\t")]         // Tabulator hinter der Marke
    public void OutlineLabel_IsLabel(string text) =>
        Assert.True(OutlineLabelDetector.IsLabelOnly(text));

    // ---- Real content that must never be swallowed ------------------------------

    [Theory]
    [InlineData("a.a.O.")]              // am angegebenen Ort — muss übersetzt werden
    [InlineData("i.V.m.")]              // in Verbindung mit
    [InlineData("z.B.")]
    [InlineData("u.a.")]
    [InlineData("Rn. 17")]              // Abkürzung + Zahl ist übersetzbarer Inhalt
    [InlineData("Art. 12")]
    [InlineData("Nr. 5")]
    [InlineData("S. 42")]               // Seitenangabe, formgleich mit "A. 1"
    [InlineData("p. 5")]
    [InlineData("f. 3")]
    [InlineData("vgl.")]
    [InlineData("Ebd.")]
    [InlineData("A. Einleitung")]       // Marke + Überschrift
    [InlineData("aa) Der Anspruch ist begründet.")]
    [InlineData("1. Der Kläger hat Recht.")]
    [InlineData("I")]                   // bloßer Buchstabe ohne Trenner
    [InlineData("a")]
    [InlineData("Die")]
    [InlineData("MIX.")]                // sähe ohne Zeichensatzgrenze wie römisch aus
    [InlineData("DIL.")]
    [InlineData("Anspruch")]
    [InlineData("Das Gericht hat entschieden, dass der Anspruch besteht.")]
    public void Content_IsNotLabel(string text) =>
        Assert.False(OutlineLabelDetector.IsLabelOnly(text));

    // ---- Guard rails -------------------------------------------------------------

    [Fact]
    public void LongText_IsNotLabel_EvenIfEveryTokenLooksLabelish()
    {
        // Length backstop: beyond MaxLabelLength nothing counts as a label.
        Assert.False(OutlineLabelDetector.IsLabelOnly("A. I. 1. a) aa) (1) (a) bb) cc) B."));
    }

    [Fact]
    public void DoubledLetterAbbreviation_IsAcceptedAsALabel_KnownLimitation()
    {
        // "ff." collides with the doubled-letter level ("aa)", "bb)") and is classified
        // as a label. Accepted: a paragraph consisting of nothing but "ff." does not
        // occur — the abbreviation always trails a citation in the same paragraph.
        Assert.True(OutlineLabelDetector.IsLabelOnly("ff."));
        Assert.False(OutlineLabelDetector.IsLabelOnly("BGHZ 45, 12 ff."));
    }

    [Fact]
    public void EmptyOrWhitespace_IsNotLabel()
    {
        Assert.False(OutlineLabelDetector.IsLabelOnly(""));
        Assert.False(OutlineLabelDetector.IsLabelOnly("   "));
    }

    [Fact]
    public void NonBreakingSpaceBetweenTokens_IsStillALabel()
    {
        // Word inserts U+00A0 between "§" and the number often enough to matter.
        Assert.True(OutlineLabelDetector.IsLabelOnly("A. I. 1."));
    }

    // ---- TryStripLeadingLabel: the write-back-side counterpart -------------------

    [Theory]
    [InlineData("I. Grundfragen und Terminologie", "I. ", "Grundfragen und Terminologie")]
    [InlineData("1. Einleitung", "1. ", "Einleitung")]
    [InlineData("aa) Zweite Untergliederung", "aa) ", "Zweite Untergliederung")]
    [InlineData("(1) Erster Absatz", "(1) ", "Erster Absatz")]
    [InlineData("A.1. Kombinierte Ebene", "A.1. ", "Kombinierte Ebene")]
    public void TryStripLeadingLabel_HeadingWithLabel_SplitsLabelFromProse(string text, string expectedLabel, string expectedRest)
    {
        var stripped = OutlineLabelDetector.TryStripLeadingLabel(text, out var label, out var rest);

        Assert.True(stripped);
        Assert.Equal(expectedLabel, label);
        Assert.Equal(expectedRest, rest);
    }

    [Fact]
    public void TryStripLeadingLabel_NonBreakingSpaceSeparator_SplitsLabelFromProse()
    {
        var stripped = OutlineLabelDetector.TryStripLeadingLabel("I. Grundfragen und Terminologie", out var label, out var rest);

        Assert.True(stripped);
        Assert.Equal("I. ", label);
        Assert.Equal("Grundfragen und Terminologie", rest);
    }

    [Theory]
    [InlineData("Ein ganz normaler Satz ohne Label.")]
    [InlineData("S. 42 verweist auf die Fundstelle.")] // abbreviation + number, not a label
    [InlineData("a.a.O. verweist auf dieselbe Stelle.")]
    [InlineData("12")] // whole-paragraph label, no separator/prose to split off
    [InlineData("")]
    public void TryStripLeadingLabel_NoLeadingLabel_ReturnsFalse(string text)
    {
        Assert.False(OutlineLabelDetector.TryStripLeadingLabel(text, out _, out _));
    }

    [Fact]
    public void TryStripLeadingLabel_LabelWithNothingAfterIt_ReturnsFalse()
    {
        // "1. " alone (no prose following) — same grammar as IsLabelOnly, nothing to
        // reattach a label to.
        Assert.False(OutlineLabelDetector.TryStripLeadingLabel("1.   ", out _, out _));
    }
}
