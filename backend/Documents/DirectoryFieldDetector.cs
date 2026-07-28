using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

/// <summary>
/// Result of one directory-field scan over a document part.
/// </summary>
/// <param name="Paragraphs">
/// Paragraphs that belong to a generated directory and must not be sent to the model.
/// </param>
/// <param name="FieldBegins">
/// The <c>w:fldChar</c> begin markers (and <c>w:fldSimple</c> elements) of the directory
/// fields found, so callers can flag them as out of date.
/// </param>
/// <param name="Unbalanced">
/// True when field begin/end markers did not pair up. The paragraph list is then empty:
/// an unterminated field would otherwise swallow the rest of the document.
/// </param>
internal sealed record DirectoryFieldScan(
    IReadOnlyList<Paragraph> Paragraphs,
    IReadOnlyList<OpenXmlElement> FieldBegins,
    bool Unbalanced);

/// <summary>
/// Finds paragraphs produced by a directory field — table of contents, table of figures,
/// index, table of authorities. Their text is a computed field result: Word rebuilds it
/// from the headings on the next field update, so correcting or translating it burns
/// tokens on text that is about to be overwritten, and risks entries drifting away from
/// the headings they mirror.
///
/// Two independent signals are used:
///
///  1. The field range itself. A TOC field spans many paragraphs — <c>begin</c>,
///     <c>instrText</c> and <c>separate</c> sit in the first entry paragraph and the
///     matching <c>end</c> in the last, with nothing in between marking the entries as
///     field content. Per-paragraph field tracking (as in <see cref="ParagraphTextMapper"/>,
///     which only has to decide what is editable *within* one paragraph) therefore sees
///     the entries as ordinary text; this scan carries the field state across paragraph
///     boundaries instead.
///  2. The paragraph style, resolved through its built-in name in <c>styles.xml</c>
///     ("toc 1" … "toc 9", "table of figures", "index 1" …). The built-in name is
///     locale-independent, unlike the style id a German Word writes ("Verzeichnis1").
///     This catches entries whose surrounding field was dissolved.
///
/// The <c>w:sdt</c> content control Word wraps an inserted TOC in is deliberately NOT
/// used as a signal: it usually also contains the directory's *title* paragraph, which is
/// static text the user wrote and — in translation mode — must still be translated.
/// </summary>
internal static class DirectoryFieldDetector
{
    private static readonly HashSet<string> DirectoryFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOC",
        "TOA",
        "INDEX"
    };

    /// <summary>
    /// Built-in style names (<c>w:name</c> in styles.xml) of generated directory entries.
    /// Heading styles ("toc heading", "index heading") are excluded on purpose: they are
    /// the user's own title text, not part of the field result.
    /// </summary>
    private static readonly HashSet<string> DirectoryStyleNames = BuildDirectoryStyleNames();

    /// <summary>
    /// Style ids of directory entries, resolved from the document's style definitions.
    /// Empty when the document has no styles part.
    /// </summary>
    public static IReadOnlySet<string> ResolveDirectoryStyleIds(WordprocessingDocument doc)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styles = doc.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return ids;
        }

        foreach (var style in styles.Elements<Style>())
        {
            var styleId = style.StyleId?.Value;
            if (string.IsNullOrEmpty(styleId))
            {
                continue;
            }

            var name = style.StyleName?.Val?.Value;
            if (!string.IsNullOrEmpty(name) && DirectoryStyleNames.Contains(NormalizeStyleKey(name)))
            {
                ids.Add(styleId);
            }
        }

        return ids;
    }

    /// <summary>
    /// All paragraphs under <paramref name="root"/> that belong to a directory: inside a
    /// directory field range, or carrying a directory entry style.
    /// </summary>
    public static IReadOnlySet<Paragraph> FindDirectoryParagraphs(
        OpenXmlElement root,
        IReadOnlySet<string> directoryStyleIds,
        IRunLogger? logger = null)
    {
        var scan = Scan(root);
        if (scan.Unbalanced)
        {
            logger?.Warning("Unbalanced field markers detected; table-of-contents detection falls back to paragraph styles only.");
        }

        var result = new HashSet<Paragraph>(scan.Paragraphs);

        foreach (var paragraph in root.Descendants<Paragraph>())
        {
            var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (string.IsNullOrEmpty(styleId))
            {
                continue;
            }

            // The id itself is checked as well, so a document without a styles part (or
            // with an English-authored one, where id and built-in name coincide) is still
            // covered.
            if (directoryStyleIds.Contains(styleId) || DirectoryStyleNames.Contains(NormalizeStyleKey(styleId)))
            {
                result.Add(paragraph);
            }
        }

        return result;
    }

    /// <summary>
    /// Flags every directory field under <paramref name="root"/> as out of date
    /// (<c>w:dirty</c>), so Word recomputes it when the document is opened. Returns the
    /// number of fields flagged.
    /// </summary>
    /// <remarks>
    /// Only the attribute is touched — no run, paragraph, style, bookmark or hyperlink of
    /// the field result is modified. The document-wide <c>w:updateFields</c> setting is
    /// deliberately not used: it would also recompute unrelated fields, and a DATE field
    /// silently rewriting itself to today is a content change nobody asked for.
    /// </remarks>
    public static int MarkDirectoryFieldsDirty(OpenXmlElement root)
    {
        var marked = 0;
        foreach (var element in Scan(root).FieldBegins)
        {
            switch (element)
            {
                case FieldChar fieldChar:
                    fieldChar.Dirty = OnOffValue.FromBoolean(true);
                    marked++;
                    break;
                case SimpleField simpleField:
                    simpleField.Dirty = OnOffValue.FromBoolean(true);
                    marked++;
                    break;
            }
        }

        return marked;
    }

    /// <summary>
    /// Single document-order walk that tracks field nesting across paragraph boundaries.
    /// </summary>
    private static DirectoryFieldScan Scan(OpenXmlElement root)
    {
        var paragraphs = new List<Paragraph>();
        var fieldBegins = new List<OpenXmlElement>();
        var openFields = new List<FieldFrame>();
        var unbalanced = false;
        Paragraph? currentParagraph = null;

        foreach (var element in root.Descendants())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    currentParagraph = paragraph;
                    if (openFields.Any(f => f.IsDirectory))
                    {
                        paragraphs.Add(paragraph);
                    }

                    break;

                case FieldChar fieldChar:
                    var charType = fieldChar.FieldCharType?.Value;
                    if (charType == FieldCharValues.Begin)
                    {
                        openFields.Add(new FieldFrame(fieldChar, currentParagraph));
                    }
                    else if (charType == FieldCharValues.End)
                    {
                        if (openFields.Count == 0)
                        {
                            unbalanced = true;
                        }
                        else
                        {
                            openFields.RemoveAt(openFields.Count - 1);
                        }
                    }

                    break;

                case FieldCode fieldCode:
                    if (openFields.Count > 0)
                    {
                        ResolveInstruction(openFields[^1], fieldCode.Text, paragraphs, fieldBegins);
                    }

                    break;

                // A directory can also be written as a self-contained w:fldSimple whose
                // result paragraphs are its own descendants.
                case SimpleField simpleField:
                    if (IsDirectoryInstruction(simpleField.Instruction?.Value))
                    {
                        fieldBegins.Add(simpleField);
                        if (currentParagraph is not null)
                        {
                            paragraphs.Add(currentParagraph);
                        }

                        paragraphs.AddRange(simpleField.Descendants<Paragraph>());
                    }

                    break;
            }
        }

        if (openFields.Count > 0)
        {
            unbalanced = true;
        }

        // An unterminated field would mark every following paragraph as directory content
        // and silently drop the rest of the document from correction. Style detection
        // still applies, so genuine TOC entries are usually caught anyway.
        return unbalanced
            ? new DirectoryFieldScan([], fieldBegins, true)
            : new DirectoryFieldScan(paragraphs, fieldBegins, false);
    }

    /// <summary>
    /// Feeds one <c>w:instrText</c> fragment into the innermost open field. Word may split
    /// an instruction across runs (" TO" + "C \o \"1-3\""), so the type stays provisional
    /// until a delimiter proves the first token is complete.
    /// </summary>
    private static void ResolveInstruction(
        FieldFrame frame,
        string? fragment,
        List<Paragraph> paragraphs,
        List<OpenXmlElement> fieldBegins)
    {
        if (frame.IsDirectory || string.IsNullOrEmpty(fragment))
        {
            return;
        }

        frame.Instruction += fragment;
        var trimmed = frame.Instruction.TrimStart();
        var tokenEnd = trimmed.IndexOfAny([' ', '\\', '"', '\t']);
        if (tokenEnd < 0)
        {
            // No delimiter yet: the token may still grow with the next fragment.
            return;
        }

        frame.IsDirectory = DirectoryFieldTypes.Contains(trimmed[..tokenEnd]);
        if (!frame.IsDirectory)
        {
            return;
        }

        fieldBegins.Add(frame.Begin);
        if (frame.BeginParagraph is not null)
        {
            // The paragraph hosting begin/instrText also hosts the first entry; it was
            // walked past before the field type was known.
            paragraphs.Add(frame.BeginParagraph);
        }
    }

    private static bool IsDirectoryInstruction(string? instruction)
    {
        var trimmed = (instruction ?? string.Empty).TrimStart();
        var tokenEnd = trimmed.IndexOfAny([' ', '\\', '"', '\t']);
        var token = tokenEnd < 0 ? trimmed : trimmed[..tokenEnd];
        return token.Length > 0 && DirectoryFieldTypes.Contains(token);
    }

    private static HashSet<string> BuildDirectoryStyleNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeStyleKey("table of figures")
        };

        for (var level = 1; level <= 9; level++)
        {
            names.Add(NormalizeStyleKey($"toc {level}"));
            names.Add(NormalizeStyleKey($"index {level}"));
        }

        return names;
    }

    /// <summary>
    /// Built-in style names are written with a space ("toc 1") while the matching style id
    /// drops it ("TOC1"); comparing without separators makes both spellings match.
    /// </summary>
    private static string NormalizeStyleKey(string value) => value.Replace(" ", string.Empty);

    private sealed class FieldFrame(OpenXmlElement begin, Paragraph? beginParagraph)
    {
        public OpenXmlElement Begin { get; } = begin;
        public Paragraph? BeginParagraph { get; } = beginParagraph;
        public string Instruction { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }
}
