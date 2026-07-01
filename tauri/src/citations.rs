//! Citation normalization for page references like "S. 502 ff." / "S. 502ff.".
//!
//! The normalization runs in two phases:
//!   1. Detection: scan the input text and count how many page-reference
//!      markers use a space before `ff.`/`f.` vs. no space. The dominant
//!      variant becomes the canonical style for the document.
//!   2. Apply: rewrite the corrected output so every `S. <number> ff.` or
//!      `S. <number> ff.` occurrence matches the chosen canonical style.
//!
//! Non-breaking spaces (U+00A0) are treated as spaces for detection and are
//! written when the canonical style is `WithSpace`, so the result stays
//! compatible with the existing non-breaking-space restorer.

use regex::Regex;
use std::sync::OnceLock;

/// The canonical citation style for a document.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CitationStyle {
    /// `S. 502 ff.` — a (non-breaking) space between the number and `ff.`/`f.`.
    WithSpace,
    /// `S. 502ff.` — no space between the number and `ff.`/`f.`.
    WithoutSpace,
}

/// The user-configurable normalization mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NormalizationMode {
    /// Normalization disabled entirely.
    Off,
    /// Detect the dominant variant in the document and apply it everywhere.
    Auto,
    /// Force `S. 502 ff.` everywhere.
    WithSpace,
    /// Force `S. 502ff.` everywhere.
    WithoutSpace,
}

impl NormalizationMode {
    /// Parse the mode from the settings string. Unknown values fall back to `Auto`.
    pub fn parse(raw: &str) -> Self {
        match raw.trim().to_ascii_lowercase().as_str() {
            "off" => NormalizationMode::Off,
            "auto" => NormalizationMode::Auto,
            "with_space" | "with-space" | "withspace" => NormalizationMode::WithSpace,
            "without_space" | "without-space" | "withoutspace" => {
                NormalizationMode::WithoutSpace
            }
            _ => NormalizationMode::Auto,
        }
    }
}

const NON_BREAKING_SPACE: char = '\u{00A0}';

/// Whitespace characters that count as a separator between the number and the
/// `ff.`/`f.` marker. Includes the regular space and the non-breaking space.
const SEPARATOR_CHARS: &[char] = &[' ', NON_BREAKING_SPACE];

fn detection_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        // Matches `S.` or `s.`, optional whitespace, a number, optional
        // whitespace (the part we classify), and `ff.` or `f.`. The trailing
        // dot is required literally, which avoids false positives such as
        // "S. 12 fundamentals." (the `f` there is followed by `u`, not `.`).
        Regex::new(r"(?i)(?:S|s)\.\s*[\s\u00A0]*\d+[\s\u00A0]*(ff|f)\.")
            .expect("citation detection regex is valid")
    })
}

fn replacement_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        // Capture groups:
        //   1 = prefix (`S.` + whitespace + number)
        //   2 = separator between number and marker (the part we replace)
        //   3 = marker (`ff` or `f`)
        //   4 = trailing dot
        Regex::new(r"(?i)((?:S|s)\.\s*[\s\u00A0]*\d+)([\s\u00A0]*)(ff|f)(\.)")
            .expect("citation replacement regex is valid")
    })
}

/// Detect the canonical citation style for a text by counting how many
/// page-reference markers use a separator vs. no separator. When the counts
/// are equal or zero, `WithSpace` is returned as the default (Duden convention).
pub fn detect_canonical_style(text: &str) -> CitationStyle {
    let re = detection_regex();
    let mut with_space = 0usize;
    let mut without_space = 0usize;

    for caps in re.captures_iter(text) {
        // The detection regex does not capture the separator directly, so we
        // re-scan the full match to decide which variant it is.
        let full = caps.get(0).map(|m| m.as_str()).unwrap_or("");
        if has_separator_before_marker(full) {
            with_space += 1;
        } else {
            without_space += 1;
        }
    }

    if with_space >= without_space {
        CitationStyle::WithSpace
    } else {
        CitationStyle::WithoutSpace
    }
}

/// Returns true when the match text has a whitespace separator between the
/// trailing number and the `ff`/`f` marker.
fn has_separator_before_marker(match_text: &str) -> bool {
    // Walk from the `ff`/`f` marker backwards: find the digits, then check the
    // char immediately before the marker.
    let lower = match_text.to_ascii_lowercase();
    let marker_pos = lower
        .find("ff.")
        .or_else(|| lower.find("f."))
        .unwrap_or(0);
    if marker_pos == 0 {
        return false;
    }
    let before = match_text.as_bytes()[marker_pos - 1] as char;
    SEPARATOR_CHARS.contains(&before)
}

/// Rewrite every page-reference marker in `text` to match `style`.
pub fn normalize_citations(text: &str, style: CitationStyle) -> String {
    let re = replacement_regex();
    re.replace_all(text, |caps: &regex::Captures| {
        let prefix = caps.get(1).map(|m| m.as_str()).unwrap_or("");
        let marker = caps.get(3).map(|m| m.as_str()).unwrap_or("");
        let dot = caps.get(4).map(|m| m.as_str()).unwrap_or("");
        match style {
            CitationStyle::WithSpace => {
                format!("{}{}{}{}", prefix, NON_BREAKING_SPACE, marker, dot)
            }
            CitationStyle::WithoutSpace => format!("{}{}{}", prefix, marker, dot),
        }
    })
    .into_owned()
}

/// Resolve the effective style for a given input text and mode. Returns `None`
/// when normalization is disabled.
pub fn resolve_style(mode: NormalizationMode, input_text: &str) -> Option<CitationStyle> {
    match mode {
        NormalizationMode::Off => None,
        NormalizationMode::Auto => Some(detect_canonical_style(input_text)),
        NormalizationMode::WithSpace => Some(CitationStyle::WithSpace),
        NormalizationMode::WithoutSpace => Some(CitationStyle::WithoutSpace),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn style_of(text: &str) -> CitationStyle {
        detect_canonical_style(text)
    }

    #[test]
    fn detects_dominant_with_space() {
        let text = "Siehe S. 502 ff. und S. 503 ff., aber S. 504ff.";
        assert_eq!(style_of(text), CitationStyle::WithSpace);
    }

    #[test]
    fn detects_dominant_without_space() {
        let text = "Siehe S. 502ff. und S. 503ff., aber S. 504 ff.";
        assert_eq!(style_of(text), CitationStyle::WithoutSpace);
    }

    #[test]
    fn defaults_to_with_space_on_tie() {
        let text = "S. 502 ff. und S. 502ff.";
        assert_eq!(style_of(text), CitationStyle::WithSpace);
    }

    #[test]
    fn defaults_to_with_space_when_no_citations() {
        let text = "Ein ganz normaler Text ohne Querverweise.";
        assert_eq!(style_of(text), CitationStyle::WithSpace);
    }

    #[test]
    fn handles_single_page_marker() {
        assert_eq!(style_of("S. 12 f."), CitationStyle::WithSpace);
        assert_eq!(style_of("S. 12f."), CitationStyle::WithoutSpace);
    }

    #[test]
    fn normalizes_to_with_space() {
        let text = "Siehe S. 502ff. sowie S. 503ff. und S. 12f.";
        let out = normalize_citations(text, CitationStyle::WithSpace);
        assert!(out.contains("S. 502\u{00A0}ff."));
        assert!(out.contains("S. 503\u{00A0}ff."));
        assert!(out.contains("S. 12\u{00A0}f."));
        assert!(!out.contains("502ff"));
        assert!(!out.contains("12f."));
    }

    #[test]
    fn normalizes_to_without_space() {
        let text = "Siehe S. 502 ff. sowie S. 503 ff. und S. 12 f.";
        let out = normalize_citations(text, CitationStyle::WithoutSpace);
        assert!(out.contains("S. 502ff."));
        assert!(out.contains("S. 503ff."));
        assert!(out.contains("S. 12f."));
        assert!(!out.contains("502 ff"));
        assert!(!out.contains("12 f."));
    }

    #[test]
    fn preserves_case_of_s_marker() {
        let lower = normalize_citations("siehe s. 502ff.", CitationStyle::WithSpace);
        assert!(lower.starts_with("siehe s. 502\u{00A0}ff."));
        let upper = normalize_citations("Siehe S. 502ff.", CitationStyle::WithSpace);
        assert!(upper.starts_with("Siehe S. 502\u{00A0}ff."));
    }

    #[test]
    fn idempotent_when_already_canonical() {
        let with = format!("S. 502{}ff. und S. 503{}ff.", NON_BREAKING_SPACE, NON_BREAKING_SPACE);
        assert_eq!(normalize_citations(&with, CitationStyle::WithSpace), with);
        let without = "S. 502ff. und S. 503ff.";
        assert_eq!(
            normalize_citations(without, CitationStyle::WithoutSpace),
            without
        );
    }

    #[test]
    fn avoids_false_positive_in_words() {
        let text = "The 12 fundamentals of programming are fun.";
        assert_eq!(style_of(text), CitationStyle::WithSpace);
        let out = normalize_citations(text, CitationStyle::WithSpace);
        assert_eq!(out, text);
    }

    #[test]
    fn treats_non_breaking_space_as_separator() {
        let text = format!("S. 502{}ff. und S. 503ff.", NON_BREAKING_SPACE);
        assert_eq!(style_of(&text), CitationStyle::WithSpace);
    }

    #[test]
    fn writes_non_breaking_space_for_with_space_style() {
        let out = normalize_citations("S. 502ff.", CitationStyle::WithSpace);
        assert!(out.contains(&format!("502{}ff", NON_BREAKING_SPACE)));
    }

    #[test]
    fn handles_multiple_citations_in_one_line() {
        let text = "Vgl. S. 12ff., S. 13ff. und S. 14 f.";
        let out = normalize_citations(text, CitationStyle::WithSpace);
        assert!(out.contains("S. 12\u{00A0}ff."));
        assert!(out.contains("S. 13\u{00A0}ff."));
        assert!(out.contains("S. 14\u{00A0}f."));
    }

    #[test]
    fn parse_mode_recognizes_known_values() {
        assert_eq!(NormalizationMode::parse("off"), NormalizationMode::Off);
        assert_eq!(NormalizationMode::parse("auto"), NormalizationMode::Auto);
        assert_eq!(
            NormalizationMode::parse("with_space"),
            NormalizationMode::WithSpace
        );
        assert_eq!(
            NormalizationMode::parse("without_space"),
            NormalizationMode::WithoutSpace
        );
        assert_eq!(NormalizationMode::parse("AUTO"), NormalizationMode::Auto);
        assert_eq!(NormalizationMode::parse(""), NormalizationMode::Auto);
        assert_eq!(
            NormalizationMode::parse("nonsense"),
            NormalizationMode::Auto
        );
    }

    #[test]
    fn resolve_style_respects_mode() {
        let text = "S. 502ff.";
        assert_eq!(resolve_style(NormalizationMode::Off, text), None);
        assert_eq!(
            resolve_style(NormalizationMode::Auto, text),
            Some(CitationStyle::WithoutSpace)
        );
        assert_eq!(
            resolve_style(NormalizationMode::WithSpace, text),
            Some(CitationStyle::WithSpace)
        );
        assert_eq!(
            resolve_style(NormalizationMode::WithoutSpace, text),
            Some(CitationStyle::WithoutSpace)
        );
    }

    #[test]
    fn handles_citation_at_end_of_string() {
        let text = "Siehe S. 502ff.";
        let out = normalize_citations(text, CitationStyle::WithSpace);
        assert_eq!(out, format!("Siehe S. 502{}ff.", NON_BREAKING_SPACE));
    }

    #[test]
    fn handles_citation_followed_by_punctuation() {
        let text = "Siehe S. 502ff., S. 503ff.";
        let out = normalize_citations(text, CitationStyle::WithSpace);
        assert!(out.contains("502\u{00A0}ff.,"));
        assert!(out.contains("503\u{00A0}ff."));
    }

    #[test]
    fn empty_input_is_safe() {
        assert_eq!(normalize_citations("", CitationStyle::WithSpace), "");
        assert_eq!(style_of(""), CitationStyle::WithSpace);
    }
}
