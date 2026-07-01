using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lingofix.Backend.Documents;

/// <summary>
/// Citation normalization for page references like "S. 502 ff." / "S. 502ff.".
/// </summary>
/// <remarks>
/// The normalization runs in two phases:
///   1. Detection: scan the input text and count how many page-reference
///      markers use a space before <c>ff.</c>/<c>f.</c> vs. no space. The
///      dominant variant becomes the canonical style for the document.
///   2. Apply: rewrite the corrected output so every <c>S. &lt;number&gt; ff.</c>
///      or <c>S. &lt;number&gt; f.</c> occurrence matches the chosen style.
/// Non-breaking spaces (U+00A0) are treated as spaces for detection and are
/// written when the canonical style is <see cref="CitationStyle.WithSpace"/>,
/// so the result stays compatible with the non-breaking-space restorer.
/// </remarks>
public static class CitationNormalizer
{
    private const char NonBreakingSpace = '\u00A0';

    private static readonly Regex DetectionPattern = new(
        @"(?:S|s)\.\s*[\s\u00A0]*\d+[\s\u00A0]*(ff|f)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReplacementPattern = new(
        @"((?:S|s)\.\s*[\s\u00A0]*\d+)([\s\u00A0]*)(ff|f)(\.)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The canonical citation style for a document.
    /// </summary>
    public enum CitationStyle
    {
        /// <summary><c>S. 502 ff.</c> — a (non-breaking) space between the number and <c>ff.</c>/<c>f.</c>.</summary>
        WithSpace,
        /// <summary><c>S. 502ff.</c> — no space between the number and <c>ff.</c>/<c>f.</c>.</summary>
        WithoutSpace,
    }

    /// <summary>
    /// The user-configurable normalization mode.
    /// </summary>
    public enum NormalizationMode
    {
        /// <summary>Normalization disabled entirely.</summary>
        Off,
        /// <summary>Detect the dominant variant in the document and apply it everywhere.</summary>
        Auto,
        /// <summary>Force <c>S. 502 ff.</c> everywhere.</summary>
        WithSpace,
        /// <summary>Force <c>S. 502ff.</c> everywhere.</summary>
        WithoutSpace,
    }

    /// <summary>
    /// Parse the mode from the settings string. Unknown values fall back to <see cref="NormalizationMode.Auto"/>.
    /// </summary>
    public static NormalizationMode ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NormalizationMode.Auto;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "off" => NormalizationMode.Off,
            "auto" => NormalizationMode.Auto,
            "with_space" or "with-space" or "withspace" => NormalizationMode.WithSpace,
            "without_space" or "without-space" or "withoutspace" => NormalizationMode.WithoutSpace,
            _ => NormalizationMode.Auto,
        };
    }

    /// <summary>
    /// Detect the canonical citation style for a text by counting how many
    /// page-reference markers use a separator vs. no separator. When the counts
    /// are equal or zero, <see cref="CitationStyle.WithSpace"/> is returned as
    /// the default (Duden convention).
    /// </summary>
    public static CitationStyle DetectCanonicalStyle(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return CitationStyle.WithSpace;
        }

        int withSpace = 0;
        int withoutSpace = 0;
        foreach (Match match in DetectionPattern.Matches(text))
        {
            if (HasSeparatorBeforeMarker(match.Value))
            {
                withSpace++;
            }
            else
            {
                withoutSpace++;
            }
        }

        return withSpace >= withoutSpace ? CitationStyle.WithSpace : CitationStyle.WithoutSpace;
    }

    /// <summary>
    /// Detect the canonical citation style across multiple text fragments
    /// (e.g. all paragraphs of a document part).
    /// </summary>
    public static CitationStyle DetectCanonicalStyle(IEnumerable<string> texts)
    {
        int withSpace = 0;
        int withoutSpace = 0;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (Match match in DetectionPattern.Matches(text))
            {
                if (HasSeparatorBeforeMarker(match.Value))
                {
                    withSpace++;
                }
                else
                {
                    withoutSpace++;
                }
            }
        }

        return withSpace >= withoutSpace ? CitationStyle.WithSpace : CitationStyle.WithoutSpace;
    }

    /// <summary>
    /// Rewrite every page-reference marker in <paramref name="text"/> to match <paramref name="style"/>.
    /// </summary>
    public static string Normalize(string text, CitationStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return ReplacementPattern.Replace(text, match =>
        {
            var prefix = match.Groups[1].Value;
            var marker = match.Groups[3].Value;
            var dot = match.Groups[4].Value;
            return style == CitationStyle.WithSpace
                ? string.Concat(prefix, NonBreakingSpace, marker, dot)
                : string.Concat(prefix, marker, dot);
        });
    }

    /// <summary>
    /// Resolve the effective style for a given input text and mode. Returns
    /// <c>null</c> when normalization is disabled.
    /// </summary>
    public static CitationStyle? ResolveStyle(NormalizationMode mode, string inputText)
    {
        return mode switch
        {
            NormalizationMode.Off => null,
            NormalizationMode.Auto => DetectCanonicalStyle(inputText),
            NormalizationMode.WithSpace => CitationStyle.WithSpace,
            NormalizationMode.WithoutSpace => CitationStyle.WithoutSpace,
            _ => DetectCanonicalStyle(inputText),
        };
    }

    /// <summary>
    /// Resolve the effective style across multiple input texts. Returns
    /// <c>null</c> when normalization is disabled.
    /// </summary>
    public static CitationStyle? ResolveStyle(NormalizationMode mode, IEnumerable<string> inputTexts)
    {
        return mode switch
        {
            NormalizationMode.Off => null,
            NormalizationMode.Auto => DetectCanonicalStyle(inputTexts),
            NormalizationMode.WithSpace => CitationStyle.WithSpace,
            NormalizationMode.WithoutSpace => CitationStyle.WithoutSpace,
            _ => DetectCanonicalStyle(inputTexts),
        };
    }

    private static bool HasSeparatorBeforeMarker(string matchText)
    {
        var span = matchText.AsSpan();
        var lower = matchText.ToLowerInvariant();
        int markerPos = lower.IndexOf("ff.", StringComparison.Ordinal);
        if (markerPos < 0)
        {
            markerPos = lower.IndexOf("f.", StringComparison.Ordinal);
        }

        if (markerPos <= 0)
        {
            return false;
        }

        char before = span[markerPos - 1];
        return before == ' ' || before == NonBreakingSpace;
    }
}
