using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

internal static class ParagraphTextMapper
{
    private const int MaxCharDiffLength = 2000;
    private const double MinCharSimilarity = 0.25;
    private const int MaxSafeCorrectedLengthForEmptyOriginal = 5000;
    private const double MaxLengthExpansionFactor = 4.0;
    private const double MinLengthContractionFactor = 0.2;
    // Below this original length, the ratio guard (4.0x/0.2x) would silently reject
    // legitimate short-original expansions (e.g. "Inhaltsübersicht" -> "Contents" is a
    // contraction, but "TOC" -> "Inhaltsverzeichnis" is a >4x expansion). An absolute cap
    // is used instead for these.
    private const int ShortOriginalLengthThreshold = 40;
    private const int MaxSafeTranslatedLengthForShortOriginal = 400;
    public static string ExtractEditableText(Paragraph paragraph)
    {
        var runs = BuildEditableRuns(paragraph, out var originalText);
        return runs.Count == 0 ? string.Empty : originalText;
    }

    /// <summary>
    /// Counts the <c>w:t</c> characters of <paramref name="paragraph"/> that extraction
    /// is expected to keep: visible text that is not inside a nested textbox story, not a
    /// tracked-change deletion, and not field code/result text (TOC entries, PAGEREF page
    /// numbers, etc. are deliberately never rewritten). This mirrors those exclusions with
    /// its own pass rather than reusing <see cref="BuildEditableRuns"/>, so it still serves
    /// as a runtime tripwire: a future extraction regression that drops ordinary text-bearing
    /// runs for some other reason becomes visible instead of silently corrupting citations.
    /// </summary>
    public static int CountVisibleTextChars(Paragraph paragraph)
    {
        var total = 0;
        var inField = false;

        foreach (var run in paragraph.Descendants<Run>())
        {
            if (IsDeletedRun(run))
            {
                continue;
            }

            var fieldChar = run.Descendants<FieldChar>().FirstOrDefault();
            if (fieldChar?.FieldCharType?.Value == FieldCharValues.Begin)
            {
                inField = true;
            }

            var isFieldContent = inField || run.Descendants<OpenXmlElement>().Any(e => e.LocalName == "instrText");

            if (!isFieldContent)
            {
                foreach (var text in run.Descendants<Text>())
                {
                    if (!DocumentPartUtils.IsInsideNestedTextBox(text, paragraph))
                    {
                        total += text.Text.Length;
                    }
                }
            }

            if (fieldChar?.FieldCharType?.Value == FieldCharValues.End)
            {
                inField = false;
            }
        }

        return total;
    }

    public static void ApplyCorrection(Paragraph paragraph, string original, string corrected)
    {
        corrected = XmlTextSanitizer.StripInvalidXmlChars(corrected, out _);
        if (string.IsNullOrWhiteSpace(corrected) || corrected == original)
        {
            return;
        }

        if (HasUnsafeStructure(paragraph))
        {
            return;
        }

        var editableRuns = BuildEditableRuns(paragraph, out var normalizedOriginal);
        if (editableRuns.Count == 0)
        {
            return;
        }

        if (!string.Equals(normalizedOriginal, original, StringComparison.Ordinal))
        {
            original = normalizedOriginal;
        }

        if (!IsLengthChangeSafe(original, corrected))
        {
            return;
        }

        var allTextNodes = editableRuns.SelectMany(r => r.TextNodes).ToList();
        corrected = EnsureLeadingSpaceAfterReferenceMark(paragraph, allTextNodes, corrected);

        if (TryApplyCharSpanMappedUpdate(editableRuns, original, corrected))
        {
            return;
        }

        var runs = editableRuns.Select(r => new RunInfo(r.TextNodes, r.OriginalText)).ToList();
        ApplyTokenMappedUpdate(runs, corrected);
    }

    /// <summary>
    /// Marker-free translation write-back (see docs/plans/translation-mode.md, Phase 2):
    /// the LLM never sees or produces position markers, so run-level attribution is done
    /// deterministically here instead of via the char-span/token diff used for corrections
    /// (which assumes near-identical text and is unsuitable for a full-text replacement).
    /// The paragraph is split into segments at non-text anchors (footnote/endnote refs,
    /// fields, drawings, breaks); translated text is distributed across segments
    /// proportionally to their original character share, and within each segment the
    /// full text goes into the first text node with the dominant run's formatting.
    /// </summary>
    public static void ApplyTranslation(Paragraph paragraph, string original, string translated)
    {
        translated = XmlTextSanitizer.StripInvalidXmlChars(translated, out _);
        if (string.IsNullOrWhiteSpace(translated))
        {
            return;
        }

        if (HasUnsafeStructure(paragraph))
        {
            return;
        }

        var editableRuns = BuildEditableRuns(paragraph, out var normalizedOriginal);
        if (editableRuns.Count == 0)
        {
            return;
        }

        if (!string.Equals(normalizedOriginal, original, StringComparison.Ordinal))
        {
            original = normalizedOriginal;
        }

        if (!IsTranslationLengthChangeSafe(original, translated))
        {
            return;
        }

        var segments = BuildTranslationSegments(paragraph, editableRuns);
        if (segments.Count == 0)
        {
            return;
        }

        var texts = segments.Count == 1
            ? [translated]
            : DistributeTranslationAcrossSegments(segments, translated);

        for (var i = 0; i < segments.Count; i++)
        {
            ApplyTranslationSegmentText(segments[i], texts[i]);
        }
    }

    private static bool IsTranslationLengthChangeSafe(string original, string translated)
    {
        if (original.Length < ShortOriginalLengthThreshold)
        {
            return translated.Length <= MaxSafeTranslatedLengthForShortOriginal;
        }

        return IsLengthChangeSafe(original, translated);
    }

    /// <summary>
    /// Groups the paragraph's editable runs (already stripped of deletions/textbox
    /// content by <see cref="BuildEditableRuns"/>) into segments, closing the current
    /// segment whenever a significant non-text anchor is encountered between two
    /// editable runs. Anchors themselves are never touched, so they stay exactly where
    /// they were in the run sequence.
    /// </summary>
    private static List<TranslationSegment> BuildTranslationSegments(Paragraph paragraph, List<EditableRun> editableRuns)
    {
        var segments = new List<TranslationSegment>();
        var current = new List<EditableRun>();
        var nextEditableIndex = 0;

        foreach (var run in paragraph.Descendants<Run>())
        {
            if (nextEditableIndex < editableRuns.Count && ReferenceEquals(editableRuns[nextEditableIndex].SourceRun, run))
            {
                current.Add(editableRuns[nextEditableIndex]);
                nextEditableIndex++;
                continue;
            }

            if (current.Count > 0 && IsSignificantAnchor(run))
            {
                segments.Add(new TranslationSegment(current));
                current = [];
            }
        }

        if (current.Count > 0)
        {
            segments.Add(new TranslationSegment(current));
        }

        return segments;
    }

    private static bool IsSignificantAnchor(Run run)
    {
        return run.Descendants<FootnoteReference>().Any()
            || run.Descendants<EndnoteReference>().Any()
            || run.Descendants<FootnoteReferenceMark>().Any()
            || run.Descendants<EndnoteReferenceMark>().Any()
            || run.Descendants<Drawing>().Any()
            || run.Descendants<Break>().Any()
            || run.Descendants<FieldChar>().Any();
    }

    /// <summary>
    /// Splits the translated text across segments proportionally to each segment's
    /// share of the original character count, snapping every internal cut point to the
    /// nearest word boundary. Exact positional fidelity of anchors is impossible without
    /// markers; this keeps them approximately in place, which is the accepted trade-off.
    /// </summary>
    private static List<string> DistributeTranslationAcrossSegments(List<TranslationSegment> segments, string translated)
    {
        var totalOriginalLength = segments.Sum(s => s.OriginalLength);
        var boundaries = new int[segments.Count + 1];
        boundaries[segments.Count] = translated.Length;

        var cumulative = 0;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            cumulative += segments[i].OriginalLength;
            var raw = totalOriginalLength == 0
                ? 0
                : (int)Math.Round(translated.Length * (double)cumulative / totalOriginalLength);
            boundaries[i + 1] = SnapToNearestWordBoundary(translated, Math.Clamp(raw, 0, translated.Length));
        }

        // Word-boundary snapping can push a cut point before the previous one when two
        // segments are very short; re-enforce monotonic order defensively.
        for (var i = 1; i <= segments.Count; i++)
        {
            if (boundaries[i] < boundaries[i - 1])
            {
                boundaries[i] = boundaries[i - 1];
            }
        }

        var texts = new List<string>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var start = boundaries[i];
            var end = boundaries[i + 1];
            texts.Add(translated.Substring(start, end - start));
        }

        return texts;
    }

    private static int SnapToNearestWordBoundary(string text, int pos)
    {
        if (pos <= 0)
        {
            return 0;
        }

        if (pos >= text.Length)
        {
            return text.Length;
        }

        if (!(IsWordChar(text[pos - 1]) && IsWordChar(text[pos])))
        {
            return pos;
        }

        var before = pos;
        while (before > 0 && IsWordChar(text[before - 1]) && IsWordChar(text[before]))
        {
            before--;
        }

        var after = pos;
        while (after < text.Length && IsWordChar(text[after - 1]) && IsWordChar(text[after]))
        {
            after++;
        }

        return (pos - before) <= (after - pos) ? before : after;
    }

    /// <summary>
    /// Writes a segment's share of the translated text into its first text node
    /// (clearing the rest) and clones the dominant run's (most original characters)
    /// formatting onto that first node's run, so e.g. a mostly-italic paragraph stays
    /// italic even though a single bold word inside it loses its bold formatting.
    /// </summary>
    private static void ApplyTranslationSegmentText(TranslationSegment segment, string text)
    {
        var textNodes = segment.Runs.SelectMany(r => r.TextNodes).ToList();
        if (textNodes.Count == 0)
        {
            return;
        }

        var dominant = segment.Runs
            .Select((run, index) => (run, index))
            .OrderByDescending(x => x.run.OriginalText.Length)
            .ThenBy(x => x.index)
            .First().run;

        var targetRun = segment.Runs[0].SourceRun;
        if (!ReferenceEquals(targetRun, dominant.SourceRun))
        {
            targetRun.RunProperties?.Remove();
            if (dominant.SourceRun.RunProperties is not null)
            {
                targetRun.RunProperties = (RunProperties)dominant.SourceRun.RunProperties.CloneNode(true);
            }
        }

        textNodes[0].Text = text;
        textNodes[0].Space = NeedsPreserveSpace(text) ? SpaceProcessingModeValues.Preserve : null;

        for (var i = 1; i < textNodes.Count; i++)
        {
            textNodes[i].Text = string.Empty;
            textNodes[i].Space = null;
        }
    }

    private static void ApplyConservativeTextUpdate(List<Text> textNodes, string corrected)
    {
        var remaining = corrected;

        for (int i = 0; i < textNodes.Count; i++)
        {
            var node = textNodes[i];
            var isLast = i == textNodes.Count - 1;
            var targetLength = node.Text.Length;

            string nextText;
            if (isLast)
            {
                nextText = remaining;
            }
            else
            {
                var take = Math.Min(targetLength, remaining.Length);
                nextText = remaining.Substring(0, take);
                remaining = remaining.Substring(take);
            }

            node.Text = nextText;
            node.Space = NeedsPreserveSpace(nextText) ? SpaceProcessingModeValues.Preserve : null;
        }
    }

    private static bool NeedsPreserveSpace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]);
    }

    private static void ApplyTokenMappedUpdate(List<RunInfo> runs, string corrected)
    {
        var originalTokens = new List<string>();
        var tokenRunIndexes = new List<int>();

        for (int i = 0; i < runs.Count; i++)
        {
            var runText = runs[i].OriginalText;
            var tokens = DiffUtils.TokenizeWithWhitespaceGroups(runText);
            for (int t = 0; t < tokens.Count; t++)
            {
                originalTokens.Add(tokens[t]);
                tokenRunIndexes.Add(i);
            }
        }

        if (originalTokens.Count == 0)
        {
            return;
        }

        var correctedTokens = DiffUtils.TokenizeWithWhitespaceGroups(corrected);
        var ops = DiffUtils.Diff(originalTokens, correctedTokens);

        var perRunTokens = new List<List<string>>(runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            perRunTokens.Add(new List<string>());
        }

        var lastRunIndex = 0;
        var hasLastRun = false;
        var originalIndex = 0;

        foreach (var op in ops)
        {
            if (op.Kind == DiffKind.Equal)
            {
                var runIndex = tokenRunIndexes[originalIndex];
                perRunTokens[runIndex].Add(op.Token);
                lastRunIndex = runIndex;
                hasLastRun = true;
                originalIndex++;
                continue;
            }

            if (op.Kind == DiffKind.Delete)
            {
                originalIndex++;
                continue;
            }

            if (op.Kind == DiffKind.Insert)
            {
                var runIndex = hasLastRun ? lastRunIndex : 0;
                perRunTokens[runIndex].Add(op.Token);
            }
        }

        for (int i = 0; i < runs.Count; i++)
        {
            var newText = string.Concat(perRunTokens[i]);
            ApplyConservativeTextUpdate(runs[i].TextNodes, newText);
        }
    }

    private static bool TryApplyCharSpanMappedUpdate(List<EditableRun> runs, string original, string corrected)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(corrected))
        {
            return false;
        }

        if (original.Length > MaxCharDiffLength || corrected.Length > MaxCharDiffLength)
        {
            return false;
        }

        var opcodes = BuildCharOpcodes(original, corrected);
        if (opcodes.Count == 0)
        {
            return false;
        }

        var similarity = ComputeSimilarity(opcodes, original.Length, corrected.Length);
        if (similarity < MinCharSimilarity)
        {
            return false;
        }

        var mapping = BuildBoundaryMapping(opcodes, original.Length, corrected.Length);
        if (mapping is null || mapping.Length != original.Length + 1)
        {
            return false;
        }

        var newSpans = new List<(int Start, int End)>(runs.Count);
        foreach (var run in runs)
        {
            var mappedStart = mapping[run.StartChar];
            var mappedEnd = mapping[run.EndChar];
            if (mappedStart > mappedEnd)
            {
                (mappedStart, mappedEnd) = (mappedEnd, mappedStart);
            }

            if (!run.HadMidWordSplit)
            {
                if (mappedStart > 0 && mappedStart < corrected.Length &&
                    IsWordChar(corrected[mappedStart - 1]) && IsWordChar(corrected[mappedStart]))
                {
                    mappedStart = SnapToWordStart(corrected, mappedStart);
                }

                if (mappedEnd > 0 && mappedEnd < corrected.Length &&
                    IsWordChar(corrected[mappedEnd - 1]) && IsWordChar(corrected[mappedEnd]))
                {
                    mappedEnd = SnapToWordEnd(corrected, mappedEnd);
                }
            }

            newSpans.Add((mappedStart, mappedEnd));
        }

        var normalized = NormalizeSpans(newSpans, corrected.Length);
        if (normalized is null)
        {
            return false;
        }

        for (int i = 0; i < runs.Count; i++)
        {
            var (start, end) = normalized[i];
            var slice = corrected.Substring(start, end - start);
            ApplyConservativeTextUpdate(runs[i].TextNodes, slice);
        }

        return true;
    }

    private static List<(int Start, int End)>? NormalizeSpans(List<(int Start, int End)> spans, int maxLength)
    {
        if (spans.Count == 0)
        {
            return spans;
        }

        var normalized = new List<(int Start, int End)>(spans.Count);
        var prevEnd = 0;

        for (int i = 0; i < spans.Count; i++)
        {
            var (start, end) = spans[i];
            start = Math.Max(0, Math.Min(start, maxLength));
            end = Math.Max(0, Math.Min(end, maxLength));

            if (start < prevEnd)
            {
                start = prevEnd;
            }

            if (end < start)
            {
                end = start;
            }

            normalized.Add((start, end));
            prevEnd = end;
        }

        if (normalized.Count > 0)
        {
            var last = normalized[^1];
            if (last.End < maxLength)
            {
                normalized[^1] = (last.Start, maxLength);
            }
        }

        return normalized;
    }

    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '-' || c == '\'';
    }

    private static int SnapToWordEnd(string text, int pos)
    {
        if (pos >= text.Length)
        {
            return text.Length;
        }

        if (!IsWordChar(text[pos]))
        {
            return pos;
        }

        var i = pos;
        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int SnapToWordStart(string text, int pos)
    {
        if (pos <= 0)
        {
            return 0;
        }

        if (!IsWordChar(text[pos - 1]))
        {
            return pos;
        }

        var i = pos - 1;
        while (i >= 0 && IsWordChar(text[i]))
        {
            i--;
        }

        return i + 1;
    }

    private static List<Opcode> BuildCharOpcodes(string original, string corrected)
    {
        var originalTokens = original.Select(c => c.ToString()).ToList();
        var correctedTokens = corrected.Select(c => c.ToString()).ToList();
        var ops = DiffUtils.Diff(originalTokens, correctedTokens);

        var opcodes = new List<Opcode>();
        var oldIndex = 0;
        var newIndex = 0;
        var currentKind = (DiffKind?)null;
        var blockOldStart = 0;
        var blockNewStart = 0;

        void FlushBlock()
        {
            if (currentKind is null)
            {
                return;
            }

            opcodes.Add(new Opcode(currentKind.Value, blockOldStart, oldIndex, blockNewStart, newIndex));
            currentKind = null;
        }

        foreach (var op in ops)
        {
            if (currentKind != op.Kind)
            {
                FlushBlock();
                currentKind = op.Kind;
                blockOldStart = oldIndex;
                blockNewStart = newIndex;
            }

            if (op.Kind == DiffKind.Equal)
            {
                oldIndex++;
                newIndex++;
            }
            else if (op.Kind == DiffKind.Delete)
            {
                oldIndex++;
            }
            else if (op.Kind == DiffKind.Insert)
            {
                newIndex++;
            }
        }

        FlushBlock();

        if (opcodes.Count <= 1)
        {
            return opcodes;
        }

        var merged = new List<Opcode>();
        for (int i = 0; i < opcodes.Count; i++)
        {
            var current = opcodes[i];
            if (current.Kind == OpcodeKind.Delete && i + 1 < opcodes.Count && opcodes[i + 1].Kind == OpcodeKind.Insert)
            {
                var next = opcodes[i + 1];
                merged.Add(new Opcode(OpcodeKind.Replace, current.OldStart, current.OldEnd, next.NewStart, next.NewEnd));
                i++;
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static double ComputeSimilarity(List<Opcode> opcodes, int oldLength, int newLength)
    {
        if (oldLength == 0 && newLength == 0)
        {
            return 1.0;
        }

        var equal = 0;
        foreach (var op in opcodes)
        {
            if (op.Kind == OpcodeKind.Equal)
            {
                equal += op.OldEnd - op.OldStart;
            }
        }

        return (2.0 * equal) / Math.Max(1, oldLength + newLength);
    }

    private static int[]? BuildBoundaryMapping(List<Opcode> opcodes, int oldLength, int newLength)
    {
        var map = new int[oldLength + 1];

        foreach (var op in opcodes)
        {
            var oldSpan = op.OldEnd - op.OldStart;
            var newSpan = op.NewEnd - op.NewStart;

            if (op.Kind == OpcodeKind.Equal)
            {
                for (int i = 0; i <= oldSpan; i++)
                {
                    map[op.OldStart + i] = op.NewStart + i;
                }

                continue;
            }

            if (op.Kind == OpcodeKind.Replace && oldSpan > 0)
            {
                for (int i = 0; i <= oldSpan; i++)
                {
                    var ratio = (double)i / oldSpan;
                    var mapped = op.NewStart + (int)Math.Round(ratio * newSpan);
                    map[op.OldStart + i] = mapped;
                }

                continue;
            }

            if (op.Kind == OpcodeKind.Delete)
            {
                for (int i = 0; i <= oldSpan; i++)
                {
                    map[op.OldStart + i] = op.NewStart;
                }
            }
        }

        map[0] = Math.Clamp(map[0], 0, newLength);
        map[^1] = Math.Clamp(map[^1], 0, newLength);
        return map;
    }

    private static List<EditableRun> BuildEditableRuns(Paragraph paragraph, out string originalText)
    {
        var runs = new List<EditableRun>();
        var builder = new StringBuilder();
        var inField = false;

        foreach (var run in paragraph.Descendants<Run>())
        {
            // Tracked-change deletions and runs nested inside a textbox belong to a
            // different text stream (the deletion history, or the textbox's own
            // paragraph which is processed separately). They must never contribute
            // to this paragraph's editable stream.
            if (IsDeletedRun(run) || DocumentPartUtils.IsInsideNestedTextBox(run, paragraph))
            {
                continue;
            }

            var fieldChar = run.Descendants<FieldChar>().FirstOrDefault();
            if (fieldChar?.FieldCharType?.Value == FieldCharValues.Begin)
            {
                inField = true;
            }

            // Editability is derived from the presence of editable <w:t> text, NOT
            // from the absence of structural siblings. A single run may legally mix
            // a tab, break, symbol, drawing or reference mark with real text; those
            // structural children contribute nothing to the editable stream but must
            // never cause adjacent text in the same run to be dropped. Field regions
            // (instructions between fldChar begin/end, and computed results) are the
            // only in-run content that is deliberately excluded, because rewriting a
            // field's text would corrupt the field.
            var isFieldContent = inField
                || run.Descendants<OpenXmlElement>().Any(e => e.LocalName == "instrText");

            if (!isFieldContent)
            {
                // Text nested inside a textbox story (w:txbxContent) that is hosted
                // by this run is excluded exactly as in DocxPartScanner: it belongs
                // to its own textbox paragraph and is corrected there.
                var textNodes = run.Descendants<Text>()
                    .Where(t => !DocumentPartUtils.IsInsideNestedTextBox(t, paragraph))
                    .ToList();
                var runText = string.Concat(textNodes.Select(t => t.Text));
                if (!string.IsNullOrEmpty(runText))
                {
                    var start = builder.Length;
                    builder.Append(runText);
                    runs.Add(new EditableRun(textNodes, runText, start, builder.Length, false, run));
                }
            }

            if (fieldChar?.FieldCharType?.Value == FieldCharValues.End)
            {
                inField = false;
            }
        }

        originalText = builder.ToString();
        if (runs.Count == 0)
        {
            return runs;
        }

        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var hadMidWordSplit = HasMidWordSplit(originalText, run.StartChar, run.EndChar);
            runs[i] = run with { HadMidWordSplit = hadMidWordSplit };
        }

        return runs;
    }

    private static bool HasMidWordSplit(string text, int start, int end)
    {
        if (start > 0 && start < text.Length && IsWordChar(text[start - 1]) && IsWordChar(text[start]))
        {
            return true;
        }

        if (end > 0 && end < text.Length && IsWordChar(text[end - 1]) && IsWordChar(text[end]))
        {
            return true;
        }

        return false;
    }

    private static bool IsDeletedRun(Run run)
    {
        if (run.Descendants<DeletedText>().Any())
        {
            return true;
        }

        if (run.Ancestors<DeletedRun>().Any())
        {
            return true;
        }

        return false;
    }

    private static bool IsLengthChangeSafe(string original, string corrected)
    {
        if (original.Length == 0)
        {
            return corrected.Length <= MaxSafeCorrectedLengthForEmptyOriginal;
        }

        var ratio = (double)corrected.Length / original.Length;
        return ratio <= MaxLengthExpansionFactor && ratio >= MinLengthContractionFactor;
    }

    private static bool HasUnsafeStructure(Paragraph paragraph)
    {
        return paragraph.Descendants<OpenXmlElement>().Any(e =>
            e.LocalName == "altChunk" ||
            e.LocalName == "customXml" ||
            e.LocalName == "oMath" ||
            e.LocalName == "oMathPara") ||
            ContainsUnsafeFieldType(paragraph);
    }

    private static bool ContainsUnsafeFieldType(Paragraph paragraph)
    {
        foreach (var instr in paragraph.Descendants<OpenXmlElement>().Where(e => e.LocalName == "instrText"))
        {
            var text = instr.InnerText?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            var token = text.Split([' ', '\\', '"'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!FieldTypePolicy.SafeFieldTypes.Contains(token.ToUpperInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    private static string EnsureLeadingSpaceAfterReferenceMark(Paragraph paragraph, List<Text> textNodes, string corrected)
    {
        if (textNodes.Count == 0 || string.IsNullOrEmpty(corrected))
        {
            return corrected;
        }

        var firstText = textNodes[0].Text;
        if (string.IsNullOrEmpty(firstText) || !char.IsWhiteSpace(firstText[0]))
        {
            return corrected;
        }

        if (char.IsWhiteSpace(corrected[0]))
        {
            return corrected;
        }

        if (!HasReferenceMarkBeforeFirstText(paragraph, textNodes[0]))
        {
            return corrected;
        }

        return " " + corrected;
    }

    private static bool HasReferenceMarkBeforeFirstText(Paragraph paragraph, Text firstTextNode)
    {
        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Descendants<Text>().Any(t => ReferenceEquals(t, firstTextNode)))
            {
                return false;
            }

            if (run.Descendants<FootnoteReference>().Any() ||
                run.Descendants<EndnoteReference>().Any() ||
                run.Descendants<FootnoteReferenceMark>().Any() ||
                run.Descendants<EndnoteReferenceMark>().Any())
            {
                return true;
            }
        }

        return false;
    }

    private sealed record RunInfo(List<Text> TextNodes, string OriginalText);

    private sealed record EditableRun(List<Text> TextNodes, string OriginalText, int StartChar, int EndChar, bool HadMidWordSplit, Run SourceRun);

    private sealed record TranslationSegment(List<EditableRun> Runs)
    {
        public int OriginalLength => Runs.Sum(r => r.OriginalText.Length);
    }

    private sealed record Opcode(OpcodeKind Kind, int OldStart, int OldEnd, int NewStart, int NewEnd)
    {
        public Opcode(DiffKind kind, int oldStart, int oldEnd, int newStart, int newEnd)
            : this(ConvertKind(kind), oldStart, oldEnd, newStart, newEnd)
        {
        }

        private static OpcodeKind ConvertKind(DiffKind kind)
        {
            return kind switch
            {
                DiffKind.Equal => OpcodeKind.Equal,
                DiffKind.Insert => OpcodeKind.Insert,
                DiffKind.Delete => OpcodeKind.Delete,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
    }

    private enum OpcodeKind
    {
        Equal,
        Insert,
        Delete,
        Replace
    }
}
