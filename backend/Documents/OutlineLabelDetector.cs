using System.Text.RegularExpressions;

namespace Lingofix.Backend.Documents;

/// <summary>
/// Recognizes paragraphs that carry no correctable/translatable prose: a bare marginal
/// number ("12", "– 17 –") or a manually typed outline label of the kind German legal
/// writing uses ("A.", "I.", "1.", "a)", "aa)", "(1)", "§ 5", "A.1", "IV.2").
/// <see cref="ParagraphProcessor"/> skips those before they ever enter a batch, so they
/// reach the output document byte-identical.
///
/// Word's own list numbering (w:numPr) never appears in run text, so this only ever sees
/// labels an author typed by hand — precisely the ones that used to be sent to the LLM.
///
/// The grammar is deliberately strict, because a false positive silently drops real text
/// from the run while a false negative merely costs a few tokens:
///  * every whitespace-separated token of the paragraph must be a label token,
///  * a single-segment core containing letters must carry a separator ("a)" yes, "a" no),
///  * a dotted multi-segment core must contain a digit ("A.1" yes, "a.a.O." no),
///  * and the whole paragraph must stay under <see cref="MaxLabelLength"/> characters.
/// So "A. Einleitung" keeps going to the LLM, and abbreviations that merely look
/// label-ish but are real content ("a.a.O.", "Rn. 17", "vgl.", "i.V.m.") are not swallowed.
/// </summary>
internal static class OutlineLabelDetector
{
    // A hand-typed outline label is short ("A. I. 1. a) aa) (1)" is 19 chars). This is the
    // backstop that keeps any future grammar hole from swallowing a whole sentence.
    private const int MaxLabelLength = 32;
    private const int MaxTokens = 6;

    // Roman numerals restricted to I/V/X/L, i.e. 1-49 — deeper than any real outline goes.
    // Leaving out C/D/M is what stops words like "MIX." or "DIL." from parsing as numerals.
    private static readonly Regex RomanNumeral = new(
        @"^(XL|L?X{0,3})(IX|IV|V?I{0,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly char[] LeadingBrackets = ['(', '[', '{'];

    private static readonly char[] TrailingSeparators = [')', ']', '}', '.', ',', ':', ';', '-', '–', '—'];

    public static bool IsLabelOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // No letters at all: marginal numbers, "1.000", "2.3.4", "– 17 –", "§ 12".
        // Deliberately uncapped in length — a paragraph of nothing but digits and
        // punctuation has nothing to correct however long it is (e.g. a table row).
        if (!text.Any(char.IsLetter))
        {
            return true;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxLabelLength)
        {
            return false;
        }

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > MaxTokens)
        {
            return false;
        }

        // In a real outline chain every rung carries its own separator ("A. I. 1. a)").
        // An abbreviation followed by a bare number does not ("S. 42", "p. 5") — that is
        // what keeps citation shorthand out, since "S." alone is shaped like a label.
        var requireSeparator = tokens.Length > 1;
        return tokens.All(token => IsLabelToken(token, requireSeparator));
    }

    /// <summary>
    /// Splits a leading outline label ("1.", "I.", "aa)", "(1)") off <paramref name="text"/>
    /// when it is followed by real prose, using the same token grammar as
    /// <see cref="IsLabelOnly"/> — just applied to the first whitespace-delimited token
    /// instead of every token in the paragraph. This exists for the write-back side: when
    /// the label's separator is a tab, <see cref="ParagraphTextMapper"/> strips the whole
    /// prefix from the editable stream before the LLM ever sees it (via
    /// <c>StripLeadingLabelPrefix</c>), so the label can never be touched. When the
    /// separator is a plain or non-breaking space instead — or shares a run with the
    /// following text — that pre-emptive exclusion cannot apply (there is no run boundary
    /// to cut at), so the label goes to the model as ordinary text and is occasionally
    /// dropped as perceived list formatting. This method lets the write-back path notice
    /// that and re-attach the exact original label, using the same trusted grammar that
    /// already excludes it in the tab case.
    /// </summary>
    public static bool TryStripLeadingLabel(string text, out string label, out string rest)
    {
        label = string.Empty;
        rest = text;

        var separatorStart = 0;
        while (separatorStart < text.Length && !char.IsWhiteSpace(text[separatorStart]))
        {
            separatorStart++;
        }

        if (separatorStart == 0 || separatorStart >= text.Length)
        {
            return false;
        }

        var token = text[..separatorStart];
        if (!IsLabelToken(token, requireSeparator: true))
        {
            return false;
        }

        // A single-letter-plus-separator token ("S.", "p.") is indistinguishable from a
        // real level-1 outline label ("A.", "I.") by shape alone — the difference IsLabelOnly
        // relies on elsewhere is that a bare page number right after it ("S. 42") has no
        // separator of its own and so fails its own IsLabelToken check as a second token.
        // The same disambiguation applies here: a label is never immediately followed by a
        // bare number, that shape is a citation abbreviation instead ("S. 42", "Rn. 17").
        var nextTokenStart = separatorStart;
        while (nextTokenStart < text.Length && char.IsWhiteSpace(text[nextTokenStart]))
        {
            nextTokenStart++;
        }

        var nextTokenEnd = nextTokenStart;
        while (nextTokenEnd < text.Length && !char.IsWhiteSpace(text[nextTokenEnd]))
        {
            nextTokenEnd++;
        }

        if (text[nextTokenStart..nextTokenEnd] is { Length: > 0 } nextToken && nextToken.All(char.IsDigit))
        {
            return false;
        }

        var contentStart = separatorStart;
        while (contentStart < text.Length && char.IsWhiteSpace(text[contentStart]))
        {
            contentStart++;
        }

        if (contentStart >= text.Length || !text[contentStart..].Any(char.IsLetter))
        {
            return false;
        }

        label = text[..contentStart];
        rest = text[contentStart..];
        return true;
    }

    private static bool IsLabelToken(string token, bool requireSeparator)
    {
        // Pure punctuation or symbols: "§", "§§", "–". No text content either way.
        if (!token.Any(char.IsLetterOrDigit))
        {
            return true;
        }

        var core = StripAffixes(token, out var hadSeparator);
        if (core.Length == 0)
        {
            return false;
        }

        if (requireSeparator && !hadSeparator)
        {
            return false;
        }

        var segments = core.Split('.');
        if (segments.Length == 1)
        {
            // A bare "I" or "a" is far more likely to be prose than an outline label;
            // require the "." or ")" that a real label carries.
            if (!hadSeparator && core.Any(char.IsLetter))
            {
                return false;
            }
        }
        else if (!segments.Any(IsDigits))
        {
            // "A.1" / "IV.2" / "1.a" are labels; "a.a.O." / "z.B." / "i.V.m." are content.
            return false;
        }

        return segments.All(IsLabelSegment);
    }

    private static string StripAffixes(string token, out bool hadSeparator)
    {
        var start = 0;
        var end = token.Length;
        hadSeparator = false;

        while (start < end && LeadingBrackets.Contains(token[start]))
        {
            start++;
            hadSeparator = true;
        }

        while (end > start && TrailingSeparators.Contains(token[end - 1]))
        {
            end--;
            hadSeparator = true;
        }

        return token[start..end];
    }

    private static bool IsLabelSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        // "1", "12"; a single letter of any script ("a", "A", "α"); the doubled letters of
        // the deeper German levels ("aa", "bb", "ccc"); or a roman numeral ("IV", "xii").
        return IsDigits(segment)
            || (segment.Length == 1 && char.IsLetter(segment[0]))
            || IsRepeatedLetter(segment)
            || RomanNumeral.IsMatch(segment);
    }

    private static bool IsDigits(string segment) =>
        segment.Length > 0 && segment.All(char.IsDigit);

    private static bool IsRepeatedLetter(string segment) =>
        segment.Length is 2 or 3
        && char.IsLetter(segment[0])
        && segment.All(c => c == segment[0]);
}
