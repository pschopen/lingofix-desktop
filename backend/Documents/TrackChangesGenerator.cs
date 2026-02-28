using System.Runtime.InteropServices;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

public static class TrackChangesGenerator
{
    private static readonly TimeSpan ExternalCompareTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan LibreOfficeProbeTimeout = TimeSpan.FromSeconds(15);
    private const string SOfficePathEnv = "LINGOFIX_SOFFICE_PATH";
    private const string WordprocessingNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly HashSet<string> RemoveRevisionElements = new(StringComparer.Ordinal)
    {
        "del",
        "delText",
        "moveFrom",
        "moveFromRun",
        "moveFromRangeStart",
        "moveFromRangeEnd"
    };

    private static readonly HashSet<string> UnwrapRevisionElements = new(StringComparer.Ordinal)
    {
        "ins",
        "moveTo",
        "moveToRun"
    };

    private static readonly HashSet<string> PropertyChangeElements = new(StringComparer.Ordinal)
    {
        "rPrChange",
        "pPrChange",
        "tblPrChange",
        "trPrChange",
        "tcPrChange",
        "sectPrChange",
        "numPrChange"
    };

    private static readonly HashSet<string> RevisionMarkerElements = new(StringComparer.Ordinal)
    {
        "ins",
        "del",
        "delText",
        "moveFrom",
        "moveTo",
        "moveFromRangeStart",
        "moveFromRangeEnd",
        "moveToRangeStart",
        "moveToRangeEnd",
        "rPrChange",
        "pPrChange",
        "tblPrChange",
        "trPrChange",
        "tcPrChange",
        "sectPrChange",
        "numPrChange"
    };

    public static bool ContainsTrackedChanges(string documentPath)
    {
        using var doc = WordprocessingDocument.Open(documentPath, false);
        return ContainsTrackedChanges(doc);
    }

    public static void AcceptAllTrackedChanges(string documentPath)
    {
        using var doc = WordprocessingDocument.Open(documentPath, true);
        AcceptAllTrackedChanges(doc);
        doc.Save();
    }

    public static void GenerateParagraphCompare(string originalPath, string correctedPath, string outputPath, string author)
    {
        File.Copy(originalPath, outputPath, overwrite: true);

        using var originalDoc = WordprocessingDocument.Open(originalPath, false);
        using var correctedDoc = WordprocessingDocument.Open(correctedPath, false);
        using var outputDoc = WordprocessingDocument.Open(outputPath, true);

        EnsureTrackRevisions(outputDoc);

        var revId = GetNextRevisionId(outputDoc);
        var revDate = DateTime.UtcNow;

        if (outputDoc.MainDocumentPart?.Document?.Body is not null &&
            correctedDoc.MainDocumentPart?.Document?.Body is not null)
        {
            ApplyParagraphDiff(
                outputDoc.MainDocumentPart.Document.Body,
                correctedDoc.MainDocumentPart.Document.Body,
                author,
                revDate,
                isNoteContent: false,
                ref revId);
        }

        if (outputDoc.MainDocumentPart?.FootnotesPart?.Footnotes is not null &&
            correctedDoc.MainDocumentPart?.FootnotesPart?.Footnotes is not null)
        {
            ApplyFootnoteDiff(
                outputDoc.MainDocumentPart.FootnotesPart.Footnotes,
                correctedDoc.MainDocumentPart.FootnotesPart.Footnotes,
                author,
                revDate,
                ref revId);
        }

        if (outputDoc.MainDocumentPart?.EndnotesPart?.Endnotes is not null &&
            correctedDoc.MainDocumentPart?.EndnotesPart?.Endnotes is not null)
        {
            ApplyEndnoteDiff(
                outputDoc.MainDocumentPart.EndnotesPart.Endnotes,
                correctedDoc.MainDocumentPart.EndnotesPart.Endnotes,
                author,
                revDate,
                ref revId);
        }

        if (outputDoc.MainDocumentPart is not null && correctedDoc.MainDocumentPart is not null)
        {
            var outHeaders = outputDoc.MainDocumentPart.HeaderParts.ToList();
            var corHeaders = correctedDoc.MainDocumentPart.HeaderParts.ToList();
            for (int i = 0; i < Math.Min(outHeaders.Count, corHeaders.Count); i++)
            {
                ApplyParagraphDiff(outHeaders[i].Header, corHeaders[i].Header, author, revDate, isNoteContent: false, ref revId);
            }

            var outFooters = outputDoc.MainDocumentPart.FooterParts.ToList();
            var corFooters = correctedDoc.MainDocumentPart.FooterParts.ToList();
            for (int i = 0; i < Math.Min(outFooters.Count, corFooters.Count); i++)
            {
                ApplyParagraphDiff(outFooters[i].Footer, corFooters[i].Footer, author, revDate, isNoteContent: false, ref revId);
            }

            if (outputDoc.MainDocumentPart.GlossaryDocumentPart?.GlossaryDocument is not null &&
                correctedDoc.MainDocumentPart.GlossaryDocumentPart?.GlossaryDocument is not null)
            {
                ApplyParagraphDiff(
                    outputDoc.MainDocumentPart.GlossaryDocumentPart.GlossaryDocument,
                    correctedDoc.MainDocumentPart.GlossaryDocumentPart.GlossaryDocument,
                    author,
                    revDate,
                    isNoteContent: false,
                    ref revId);
            }
        }

        RejectFormattingChanges(outputDoc);
        outputDoc.Save();
    }

    private static void EnsureTrackRevisions(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart;
        if (main is null)
        {
            return;
        }

        var settingsPart = main.DocumentSettingsPart ?? main.AddNewPart<DocumentSettingsPart>();
        if (settingsPart.Settings is null)
        {
            settingsPart.Settings = new DocumentFormat.OpenXml.Wordprocessing.Settings();
        }

        var settings = settingsPart.Settings!;
        if (settings.Elements<TrackRevisions>().Any())
        {
            return;
        }

        settings.AppendChild(new TrackRevisions());
        settings.Save();
    }

    private static bool ContainsTrackedChanges(WordprocessingDocument doc)
    {
        foreach (var root in EnumerateRevisionRoots(doc))
        {
            if (root.Descendants<TrackChangeType>().Any())
            {
                return true;
            }

            if (root.Descendants().Any(IsTrackedChangeElement))
            {
                return true;
            }
        }

        return false;
    }

    private static void AcceptAllTrackedChanges(WordprocessingDocument doc)
    {
        foreach (var root in EnumerateRevisionRoots(doc))
        {
            AcceptTrackedChangesInSubtree(root);
        }
    }

    private static void AcceptTrackedChangesInSubtree(OpenXmlElement root)
    {
        foreach (var child in root.ChildElements.ToList())
        {
            AcceptTrackedChangesInSubtree(child);
        }

        if (!IsWordprocessingElement(root))
        {
            return;
        }

        if (PropertyChangeElements.Contains(root.LocalName))
        {
            root.Remove();
            return;
        }

        if (RemoveRevisionElements.Contains(root.LocalName))
        {
            root.Remove();
            return;
        }

        if (UnwrapRevisionElements.Contains(root.LocalName))
        {
            var children = root.ChildElements.ToList();
            foreach (var child in children)
            {
                child.Remove();
                root.InsertBeforeSelf(child);
            }

            root.Remove();
        }
    }

    private static bool IsTrackedChangeElement(OpenXmlElement element)
    {
        return IsWordprocessingElement(element) && RevisionMarkerElements.Contains(element.LocalName);
    }

    private static bool IsWordprocessingElement(OpenXmlElement element)
    {
        return string.Equals(element.NamespaceUri, WordprocessingNamespace, StringComparison.Ordinal);
    }

    private static int GetNextRevisionId(WordprocessingDocument doc)
    {
        var maxId = 0;
        foreach (var change in EnumerateRevisionRoots(doc).SelectMany(root => root.Descendants<TrackChangeType>()))
        {
            var rawId = change.Id?.Value;
            if (rawId is null)
            {
                continue;
            }

            if (int.TryParse(rawId, out var parsed) && parsed > maxId)
            {
                maxId = parsed;
            }
        }

        return maxId + 1;
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

    private static void ApplyParagraphDiff(OpenXmlElement outputRoot, OpenXmlElement correctedRoot, string author, DateTime revDate, bool isNoteContent, ref int revId)
    {
        var outputParagraphs = outputRoot.Descendants<Paragraph>().ToList();
        var correctedParagraphs = correctedRoot.Descendants<Paragraph>().ToList();

        var max = Math.Max(outputParagraphs.Count, correctedParagraphs.Count);
        for (int i = 0; i < max; i++)
        {
            if (i >= outputParagraphs.Count)
            {
                var newPara = CreateInsertedParagraph(correctedParagraphs[i], author, revDate, ref revId);
                outputRoot.AppendChild(newPara);
                continue;
            }

            if (i >= correctedParagraphs.Count)
            {
                var deletedPara = CreateDeletedParagraph(outputParagraphs[i], author, revDate, ref revId);
                outputRoot.ReplaceChild(deletedPara, outputParagraphs[i]);
                continue;
            }

            var outText = ExtractText(outputParagraphs[i]);
            var corText = ExtractText(correctedParagraphs[i]);
            if (outText == corText)
            {
                continue;
            }

            if (isNoteContent)
            {
                ApplyNoteParagraphDiff(outputParagraphs[i], correctedParagraphs[i], author, revDate, ref revId);
            }
            else
            {
                var markers = CollectReferenceMarkers(outputParagraphs[i]);
                var originalStyleSpans = BuildRunStyleSpans(outputParagraphs[i]);
                var correctedStyleSpans = BuildRunStyleSpans(correctedParagraphs[i]);
                var diffNodes = BuildWordDiffRuns(
                    outText,
                    corText,
                    markers,
                    author,
                    revDate,
                    ref revId,
                    originalStyleSpans,
                    correctedStyleSpans);
                ReplaceParagraphRuns(outputParagraphs[i], diffNodes);
            }
        }
    }

    private static void ApplyFootnoteDiff(Footnotes output, Footnotes corrected, string author, DateTime revDate, ref int revId)
    {
        var correctedById = corrected.Elements<Footnote>()
            .Where(n => n.Id is not null)
            .ToDictionary(n => n.Id!.Value, n => n);

        foreach (var outNote in output.Elements<Footnote>())
        {
            if (outNote.Id is null)
            {
                continue;
            }

            if (!correctedById.TryGetValue(outNote.Id.Value, out var corNote))
            {
                continue;
            }

            ApplyParagraphDiff(outNote, corNote, author, revDate, isNoteContent: true, ref revId);
        }
    }

    private static void ApplyEndnoteDiff(Endnotes output, Endnotes corrected, string author, DateTime revDate, ref int revId)
    {
        var correctedById = corrected.Elements<Endnote>()
            .Where(n => n.Id is not null)
            .ToDictionary(n => n.Id!.Value, n => n);

        foreach (var outNote in output.Elements<Endnote>())
        {
            if (outNote.Id is null)
            {
                continue;
            }

            if (!correctedById.TryGetValue(outNote.Id.Value, out var corNote))
            {
                continue;
            }

            ApplyParagraphDiff(outNote, corNote, author, revDate, isNoteContent: true, ref revId);
        }
    }

    private static Run? TryGetNoteReferenceRun(Paragraph paragraph)
    {
        foreach (var run in paragraph.Elements<Run>())
        {
            if (run.Descendants<FootnoteReferenceMark>().Any() ||
                run.Descendants<EndnoteReferenceMark>().Any())
            {
                return run;
            }
        }

        return null;
    }

    private static void ApplyNoteParagraphDiff(Paragraph outputParagraph, Paragraph correctedParagraph, string author, DateTime revDate, ref int revId)
    {
        var outputRuns = outputParagraph.Elements<Run>().ToList();
        var correctedRuns = correctedParagraph.Elements<Run>().ToList();

        var outputPrefixIndex = FindNoteReferenceRunIndex(outputRuns);
        var correctedPrefixIndex = FindNoteReferenceRunIndex(correctedRuns);

        var outText = ExtractTextFromRuns(outputRuns, outputPrefixIndex + 1);
        var corText = ExtractTextFromRuns(correctedRuns, correctedPrefixIndex + 1);

        if (outText == corText && outputPrefixIndex >= 0)
        {
            // If there are no stray runs before the prefix, we can skip.
            var hasStrayBeforePrefix = outputRuns.Take(outputPrefixIndex).Any(r => r.Descendants<Text>().Any());
            if (!hasStrayBeforePrefix)
            {
                return;
            }
        }

        var diffNodes = BuildWordDiffRuns(outText, corText, new List<ReferenceMarker>(), author, revDate, ref revId);

        Run? prefixRun = outputPrefixIndex >= 0 ? outputRuns[outputPrefixIndex] : null;
        if (prefixRun is not null)
        {
            RemoveAllRunsExcept(outputParagraph, prefixRun);
            InsertNodesAfterPrefix(outputParagraph, prefixRun, diffNodes);
        }
        else
        {
            ReplaceParagraphRuns(outputParagraph, diffNodes);
        }
    }

    private static int FindNoteReferenceRunIndex(List<Run> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Descendants<FootnoteReferenceMark>().Any() ||
                runs[i].Descendants<EndnoteReferenceMark>().Any() ||
                runs[i].Descendants<FootnoteReference>().Any() ||
                runs[i].Descendants<EndnoteReference>().Any())
            {
                return i;
            }
        }

        return -1;
    }

    private static string ExtractTextFromRuns(List<Run> runs, int startIndex)
    {
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        var texts = runs.Skip(startIndex).SelectMany(r => r.Descendants<Text>().Select(t => t.Text));
        return string.Concat(texts);
    }

    private static void RemoveAllRunsExcept(Paragraph paragraph, Run keep)
    {
        var runs = paragraph.Elements<Run>().ToList();
        foreach (var run in runs)
        {
            if (!ReferenceEquals(run, keep))
            {
                run.Remove();
            }
        }
    }

    private static void InsertNodesAfterPrefix(Paragraph paragraph, Run prefix, List<OpenXmlElement> nodes)
    {
        OpenXmlElement last = prefix;
        foreach (var node in nodes)
        {
            paragraph.InsertAfter(node, last);
            last = node;
        }
    }

    private static Paragraph CreateInsertedParagraph(Paragraph source, string author, DateTime revDate, ref int revId)
    {
        var clone = (Paragraph)source.CloneNode(true);
        foreach (var run in clone.Descendants<Run>())
        {
            var runProps = run.RunProperties ?? new RunProperties();
            var ins = new InsertedRun
            {
                Id = revId++.ToString(),
                Author = author,
                Date = revDate
            };

            ins.AppendChild(new RunProperties(runProps.OuterXml));
            var texts = run.Elements<Text>().Select(t => new Text(t.Text) { Space = t.Space }).ToList();
            foreach (var text in texts)
            {
                ins.AppendChild(text);
            }

            run.RemoveAllChildren();
            run.AppendChild(ins);
        }

        return clone;
    }

    private static Paragraph CreateDeletedParagraph(Paragraph source, string author, DateTime revDate, ref int revId)
    {
        var clone = (Paragraph)source.CloneNode(true);
        foreach (var run in clone.Descendants<Run>())
        {
            var runProps = run.RunProperties ?? new RunProperties();
            var del = new DeletedRun
            {
                Id = revId++.ToString(),
                Author = author,
                Date = revDate
            };

            del.AppendChild(new RunProperties(runProps.OuterXml));
            var texts = run.Elements<Text>().Select(t => new DeletedText(t.Text)).ToList();
            foreach (var text in texts)
            {
                del.AppendChild(text);
            }

            run.RemoveAllChildren();
            run.AppendChild(del);
        }

        return clone;
    }

    private static string ExtractText(Paragraph paragraph)
    {
        var texts = paragraph.Descendants<Text>().Select(t => t.Text);
        return string.Concat(texts);
    }

    private static List<ReferenceMarker> CollectReferenceMarkers(Paragraph paragraph)
    {
        var markers = new List<ReferenceMarker>();
        var offset = 0;

        foreach (var run in paragraph.Descendants<Run>())
        {
            var referenceElements = run.Descendants<OpenXmlElement>()
                .Where(e => e is FootnoteReference ||
                            e is EndnoteReference ||
                            e is FootnoteReferenceMark ||
                            e is EndnoteReferenceMark ||
                            e is CommentReference)
                .ToList();

            if (referenceElements.Count > 0)
            {
                var markerRun = new Run();
                if (run.RunProperties is not null)
                {
                    markerRun.RunProperties = (RunProperties)run.RunProperties.CloneNode(true);
                }

                foreach (var element in referenceElements)
                {
                    markerRun.AppendChild((OpenXmlElement)element.CloneNode(true));
                }

                markers.Add(new ReferenceMarker(offset, markerRun));
            }

            var runText = string.Concat(run.Descendants<Text>().Select(t => t.Text));
            offset += runText.Length;
        }

        return markers;
    }

    private static void ReplaceParagraphRuns(Paragraph paragraph, List<OpenXmlElement> nodes)
    {
        var runs = paragraph.Elements<Run>().ToList();
        foreach (var run in runs)
        {
            run.Remove();
        }

        OpenXmlElement? last = paragraph.Elements<ParagraphProperties>().FirstOrDefault();
        foreach (var node in nodes)
        {
            if (last is null)
            {
                paragraph.AppendChild(node);
                last = node;
            }
            else
            {
                paragraph.InsertAfter(node, last);
                last = node;
            }
        }
    }

    private static List<OpenXmlElement> BuildWordDiffRuns(
        string originalText,
        string correctedText,
        List<ReferenceMarker> markers,
        string author,
        DateTime revDate,
        ref int revId,
        List<RunStyleSpan>? originalStyleSpans = null,
        List<RunStyleSpan>? correctedStyleSpans = null)
    {
        var originalTokens = DiffUtils.TokenizeWords(originalText);
        var correctedTokens = DiffUtils.TokenizeWords(correctedText);
        var diff = DiffUtils.Diff(originalTokens, correctedTokens);
        var ops = DiffUtils.MergeAdjacent(diff);

        var nodes = new List<OpenXmlElement>();
        var orderedMarkers = markers
            .Where(m => m.Offset >= 0)
            .OrderBy(m => m.Offset)
            .ToList();
        var markerIndex = 0;
        var originalPos = 0;
        var correctedPos = 0;

        foreach (var op in ops)
        {
            while (markerIndex < orderedMarkers.Count && orderedMarkers[markerIndex].Offset <= originalPos)
            {
                nodes.Add(orderedMarkers[markerIndex].Run.CloneNode(true));
                markerIndex++;
            }

            if (op.Kind == DiffKind.Equal)
            {
                AppendTextWithMarkers(
                    op.Token,
                    isDelete: false,
                    ref markerIndex,
                    orderedMarkers,
                    ref originalPos,
                    nodes,
                    author,
                    revDate,
                    ref revId,
                    originalStyleSpans);
                correctedPos += op.Token.Length;
            }
            else if (op.Kind == DiffKind.Insert)
            {
                var runProps = ResolveRunPropertiesAtOffset(correctedStyleSpans, correctedPos);
                var ins = new InsertedRun
                {
                    Id = revId++.ToString(),
                    Author = author,
                    Date = revDate
                };
                if (runProps is not null)
                {
                    ins.AppendChild(runProps);
                }
                else
                {
                    ins.AppendChild(new RunProperties());
                }
                ins.AppendChild(new Text(op.Token) { Space = SpaceProcessingModeValues.Preserve });
                nodes.Add(new Run(ins));
                correctedPos += op.Token.Length;
            }
            else if (op.Kind == DiffKind.Delete)
            {
                AppendTextWithMarkers(
                    op.Token,
                    isDelete: true,
                    ref markerIndex,
                    orderedMarkers,
                    ref originalPos,
                    nodes,
                    author,
                    revDate,
                    ref revId,
                    originalStyleSpans);
            }
        }

        while (markerIndex < orderedMarkers.Count)
        {
            nodes.Add(orderedMarkers[markerIndex].Run.CloneNode(true));
            markerIndex++;
        }

        return nodes;
    }

    private static void AppendTextWithMarkers(
        string text,
        bool isDelete,
        ref int markerIndex,
        List<ReferenceMarker> orderedMarkers,
        ref int originalPos,
        List<OpenXmlElement> nodes,
        string author,
        DateTime revDate,
        ref int revId,
        List<RunStyleSpan>? styleSpans)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var localPos = 0;
        while (markerIndex < orderedMarkers.Count)
        {
            var markerOffset = orderedMarkers[markerIndex].Offset;
            if (markerOffset < originalPos)
            {
                nodes.Add(orderedMarkers[markerIndex].Run.CloneNode(true));
                markerIndex++;
                continue;
            }

            if (markerOffset > originalPos + text.Length)
            {
                break;
            }

            var cut = markerOffset - originalPos;
            if (cut > localPos)
            {
                var segmentStart = originalPos + localPos;
                var runProps = ResolveRunPropertiesAtOffset(styleSpans, segmentStart);
                AppendSegment(text.Substring(localPos, cut - localPos), isDelete, nodes, author, revDate, ref revId, runProps);
            }

            nodes.Add(orderedMarkers[markerIndex].Run.CloneNode(true));
            markerIndex++;
            localPos = cut;
        }

        if (localPos < text.Length)
        {
            var segmentStart = originalPos + localPos;
            var runProps = ResolveRunPropertiesAtOffset(styleSpans, segmentStart);
            AppendSegment(text.Substring(localPos), isDelete, nodes, author, revDate, ref revId, runProps);
        }

        originalPos += text.Length;
    }

    private static void AppendSegment(
        string segment,
        bool isDelete,
        List<OpenXmlElement> nodes,
        string author,
        DateTime revDate,
        ref int revId,
        RunProperties? runProps)
    {
        if (segment.Length == 0)
        {
            return;
        }

        if (isDelete)
        {
            var del = new DeletedRun
            {
                Id = revId++.ToString(),
                Author = author,
                Date = revDate
            };
            if (runProps is not null)
            {
                del.AppendChild((RunProperties)runProps.CloneNode(true));
            }
            else
            {
                del.AppendChild(new RunProperties());
            }
            del.AppendChild(new DeletedText(segment));
            nodes.Add(new Run(del));
        }
        else
        {
            var run = new Run(new Text(segment) { Space = SpaceProcessingModeValues.Preserve });
            if (runProps is not null)
            {
                run.RunProperties = (RunProperties)runProps.CloneNode(true);
            }

            nodes.Add(run);
        }
    }

    private static List<RunStyleSpan> BuildRunStyleSpans(Paragraph paragraph)
    {
        var spans = new List<RunStyleSpan>();
        var offset = 0;

        foreach (var run in paragraph.Descendants<Run>())
        {
            var runText = string.Concat(run.Descendants<Text>().Select(t => t.Text));
            if (runText.Length == 0)
            {
                continue;
            }

            var props = run.RunProperties is null
                ? null
                : (RunProperties)run.RunProperties.CloneNode(true);

            var start = offset;
            offset += runText.Length;
            spans.Add(new RunStyleSpan(start, offset, props));
        }

        return spans;
    }

    private static RunProperties? ResolveRunPropertiesAtOffset(List<RunStyleSpan>? spans, int offset)
    {
        if (spans is null || spans.Count == 0)
        {
            return null;
        }

        var maxOffset = spans[^1].End;
        var clampedOffset = Math.Clamp(offset, 0, maxOffset);

        foreach (var span in spans)
        {
            if (clampedOffset >= span.Start && clampedOffset < span.End)
            {
                return span.RunProperties is null ? null : (RunProperties)span.RunProperties.CloneNode(true);
            }
        }

        var previous = spans.LastOrDefault(span => span.End <= clampedOffset);
        if (previous is not null)
        {
            return previous.RunProperties is null ? null : (RunProperties)previous.RunProperties.CloneNode(true);
        }

        var next = spans.FirstOrDefault(span => span.Start >= clampedOffset);
        if (next is not null)
        {
            return next.RunProperties is null ? null : (RunProperties)next.RunProperties.CloneNode(true);
        }

        return null;
    }

    private sealed record ReferenceMarker(int Offset, Run Run);
    private sealed record RunStyleSpan(int Start, int End, RunProperties? RunProperties);

    public static void GenerateWithWord(string originalPath, string correctedPath, string outputPath, string author, bool strictTextChangesOnly = false)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            RunWordCompareMac(originalPath, correctedPath, outputPath, author, strictTextChangesOnly);
            if (strictTextChangesOnly)
            {
                RejectNonInsertionDeletionChanges(outputPath);
            }
            else
            {
                RejectFormattingChanges(outputPath);
            }
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RunWordCompareWindows(originalPath, correctedPath, outputPath, author, strictTextChangesOnly);
            if (strictTextChangesOnly)
            {
                RejectNonInsertionDeletionChanges(outputPath);
            }
            else
            {
                RejectFormattingChanges(outputPath);
            }
            return;
        }

        throw new PlatformNotSupportedException("Word comparison is not supported on this operating system.");
    }

    public static void GenerateWithLibreOffice(string originalPath, string correctedPath, string outputPath, string author, bool strictTextChangesOnly = false)
    {
        var sofficePath = ResolveUsableLibreOfficeExecutable();
        LibreOfficeUnoCompareRunner.GenerateWithUno(
            sofficePath,
            originalPath,
            correctedPath,
            outputPath,
            author,
            ExternalCompareTimeout,
            strictTextChangesOnly ? "text-only" : "all");
        if (outputPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            if (strictTextChangesOnly)
            {
                RejectNonInsertionDeletionChanges(outputPath);
            }
            else
            {
                RejectFormattingChanges(outputPath);
            }
        }
    }

    private static void RejectNonInsertionDeletionChanges(string documentPath)
    {
        using var doc = WordprocessingDocument.Open(documentPath, true);
        RejectFormattingChanges(doc);
        RejectMoveTrackedChanges(doc);
        RejectNonInsDelRevisionElements(doc);
        NormalizeInsertedRunFormatting(doc);
        doc.Save();
    }

    private static void NormalizeInsertedRunFormatting(WordprocessingDocument doc)
    {
        foreach (var root in EnumerateRevisionRoots(doc))
        {
            foreach (var paragraph in root.Descendants<Paragraph>())
            {
                NormalizeInsertedRunFormattingInParagraph(paragraph);
            }
        }
    }

    private static void NormalizeInsertedRunFormattingInParagraph(Paragraph paragraph)
    {
        var runs = paragraph.Descendants<Run>().ToList();
        if (runs.Count == 0)
        {
            return;
        }

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (!IsInsertedRun(run))
            {
                continue;
            }

            var sourceProps = ResolveNeighborRunProperties(runs, i);
            if (sourceProps is null)
            {
                run.RunProperties?.Remove();
            }
            else
            {
                run.RunProperties = (RunProperties)sourceProps.CloneNode(true);
            }
        }

        foreach (var ins in paragraph
                     .Descendants()
                     .Where(IsWordprocessingElement)
                     .Where(e => e.LocalName == "ins")
                     .ToList())
        {
            var sourceProps = ResolveNeighborRunPropertiesForInsertedElement(paragraph, ins);
            foreach (var rPr in ins
                         .ChildElements
                         .Where(IsWordprocessingElement)
                         .Where(e => e.LocalName == "rPr")
                         .ToList())
            {
                rPr.Remove();
            }

            if (sourceProps is not null)
            {
                ins.PrependChild((RunProperties)sourceProps.CloneNode(true));
            }
        }
    }

    private static RunProperties? ResolveNeighborRunProperties(List<Run> runs, int index)
    {
        for (var left = index - 1; left >= 0; left--)
        {
            var leftRun = runs[left];
            if (IsInsertedRun(leftRun))
            {
                continue;
            }

            var leftProps = leftRun.RunProperties;
            return leftProps is null
                ? null
                : (RunProperties)leftProps.CloneNode(true);
        }

        for (var right = index + 1; right < runs.Count; right++)
        {
            var rightRun = runs[right];
            if (IsInsertedRun(rightRun))
            {
                continue;
            }

            var rightProps = rightRun.RunProperties;
            return rightProps is null
                ? null
                : (RunProperties)rightProps.CloneNode(true);
        }

        return null;
    }

    private static RunProperties? ResolveNeighborRunPropertiesForInsertedElement(Paragraph paragraph, OpenXmlElement insertedElement)
    {
        var runs = paragraph.Descendants<Run>().ToList();
        var firstInsertedRunIndex = runs.FindIndex(run => run.Ancestors().Contains(insertedElement));
        if (firstInsertedRunIndex >= 0)
        {
            return ResolveNeighborRunProperties(runs, firstInsertedRunIndex);
        }

        return null;
    }

    private static bool IsInsertedRun(Run run)
    {
        if (run.Ancestors().Any(a => IsWordprocessingElement(a) && a.LocalName == "ins"))
        {
            return true;
        }

        return run
            .Descendants()
            .Any(e => IsWordprocessingElement(e) && e.LocalName == "ins");
    }

    private static void RejectNonInsDelRevisionElements(WordprocessingDocument doc)
    {
        foreach (var root in EnumerateRevisionRoots(doc))
        {
            foreach (var element in root.Descendants().Where(IsWordprocessingElement).ToList())
            {
                var localName = element.LocalName;
                if (localName == "ins" || localName == "del" || localName == "delText")
                {
                    continue;
                }

                if (!RevisionMarkerElements.Contains(localName) && element is not TrackChangeType)
                {
                    continue;
                }

                if (localName == "moveFrom" || localName == "moveFromRun")
                {
                    var children = element.ChildElements.ToList();
                    foreach (var child in children)
                    {
                        child.Remove();
                        element.InsertBeforeSelf(child);
                    }

                    element.Remove();
                    continue;
                }

                if (localName == "moveTo" || localName == "moveToRun")
                {
                    element.Remove();
                    continue;
                }

                if (PropertyChangeElements.Contains(localName) ||
                    localName == "moveFromRangeStart" ||
                    localName == "moveFromRangeEnd" ||
                    localName == "moveToRangeStart" ||
                    localName == "moveToRangeEnd")
                {
                    element.Remove();
                    continue;
                }

                if (element is TrackChangeType)
                {
                    element.Remove();
                }
            }
        }
    }

    private static void RejectMoveTrackedChanges(WordprocessingDocument doc)
    {
        foreach (var root in EnumerateRevisionRoots(doc))
        {
            foreach (var marker in root
                         .Descendants()
                         .Where(IsWordprocessingElement)
                         .Where(e =>
                             e.LocalName == "moveFromRangeStart" ||
                             e.LocalName == "moveFromRangeEnd" ||
                             e.LocalName == "moveToRangeStart" ||
                             e.LocalName == "moveToRangeEnd")
                         .ToList())
            {
                marker.Remove();
            }

            foreach (var moveFrom in root.Descendants().Where(IsWordprocessingElement).Where(e => e.LocalName == "moveFrom" || e.LocalName == "moveFromRun").ToList())
            {
                var children = moveFrom.ChildElements.ToList();
                foreach (var child in children)
                {
                    child.Remove();
                    moveFrom.InsertBeforeSelf(child);
                }

                moveFrom.Remove();
            }

            foreach (var moveTo in root.Descendants().Where(IsWordprocessingElement).Where(e => e.LocalName == "moveTo" || e.LocalName == "moveToRun").ToList())
            {
                moveTo.Remove();
            }
        }
    }

    public static void ConvertWithLibreOffice(string inputPath, string outputPath, string targetExtension)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Missing input path for LibreOffice conversion.", nameof(inputPath));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Missing output path for LibreOffice conversion.", nameof(outputPath));
        }

        var normalizedTarget = targetExtension.Trim();
        if (!normalizedTarget.StartsWith(".", StringComparison.Ordinal))
        {
            normalizedTarget = "." + normalizedTarget;
        }

        var sofficePath = ResolveUsableLibreOfficeExecutable();
        var workDir = Path.Combine(Path.GetTempPath(), "Lingofix", "libreoffice-convert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var expectedOutput = Path.Combine(
                workDir,
                Path.GetFileNameWithoutExtension(inputPath) + normalizedTarget);

            if (File.Exists(expectedOutput))
            {
                File.Delete(expectedOutput);
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sofficePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add(normalizedTarget.TrimStart('.'));
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(workDir);
            psi.ArgumentList.Add(inputPath);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                throw new InvalidOperationException("Failed to start LibreOffice conversion process.");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)ExternalCompareTimeout.TotalMilliseconds))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException($"LibreOffice conversion timed out after {ExternalCompareTimeout.TotalSeconds:0} seconds.");
            }

            Task.WaitAll([stdoutTask, stderrTask]);
            if (proc.ExitCode != 0)
            {
                var stderr = stderrTask.GetAwaiter().GetResult();
                var stdout = stdoutTask.GetAwaiter().GetResult();
                var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"LibreOffice conversion failed: {details.Trim()}");
            }

            if (!File.Exists(expectedOutput))
            {
                throw new FileNotFoundException($"LibreOffice conversion did not produce expected file: {expectedOutput}", expectedOutput);
            }

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            File.Copy(expectedOutput, outputPath, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static void RejectFormattingChanges(string documentPath)
    {
        using var doc = WordprocessingDocument.Open(documentPath, true);
        RejectFormattingChanges(doc);
        doc.Save();
    }

    private static void RejectFormattingChanges(WordprocessingDocument doc)
    {
        if (doc.MainDocumentPart?.Document?.Body is not null)
        {
            RejectFormattingChangesInRoot(doc.MainDocumentPart.Document.Body);
        }

        if (doc.MainDocumentPart?.FootnotesPart?.Footnotes is not null)
        {
            RejectFormattingChangesInRoot(doc.MainDocumentPart.FootnotesPart.Footnotes);
        }

        if (doc.MainDocumentPart?.EndnotesPart?.Endnotes is not null)
        {
            RejectFormattingChangesInRoot(doc.MainDocumentPart.EndnotesPart.Endnotes);
        }

        if (doc.MainDocumentPart is not null)
        {
            foreach (var header in doc.MainDocumentPart.HeaderParts)
            {
                RejectFormattingChangesInRoot(header.Header);
            }

            foreach (var footer in doc.MainDocumentPart.FooterParts)
            {
                RejectFormattingChangesInRoot(footer.Footer);
            }

            if (doc.MainDocumentPart.GlossaryDocumentPart?.GlossaryDocument is not null)
            {
                RejectFormattingChangesInRoot(doc.MainDocumentPart.GlossaryDocumentPart.GlossaryDocument);
            }
        }
    }

    private static void RejectFormattingChangesInRoot(OpenXmlElement root)
    {
        foreach (var run in root.Descendants<Run>())
        {
            var rPr = run.RunProperties;
            var change = rPr?.GetFirstChild<RunPropertiesChange>();
            if (change is null)
            {
                continue;
            }

            var previous = change.GetFirstChild<RunProperties>();
            if (previous is not null)
            {
                run.RunProperties = (RunProperties)previous.CloneNode(true);
            }
            else
            {
                change.Remove();
            }
        }

        foreach (var paragraph in root.Descendants<Paragraph>())
        {
            var pPr = paragraph.ParagraphProperties;
            var change = pPr?.GetFirstChild<ParagraphPropertiesChange>();
            if (change is null)
            {
                continue;
            }

            var previous = change.GetFirstChild<ParagraphProperties>();
            if (previous is not null)
            {
                paragraph.ParagraphProperties = (ParagraphProperties)previous.CloneNode(true);
            }
            else
            {
                change.Remove();
            }
        }

        foreach (var table in root.Descendants<Table>())
        {
            var tPr = table.GetFirstChild<TableProperties>();
            var change = tPr?.GetFirstChild<TablePropertiesChange>();
            if (change is null)
            {
                continue;
            }

            var previous = change.GetFirstChild<TableProperties>();
            if (previous is not null)
            {
                if (tPr is not null)
                {
                    table.ReplaceChild((TableProperties)previous.CloneNode(true), tPr);
                }
                else
                {
                    table.PrependChild((TableProperties)previous.CloneNode(true));
                }
            }
            else
            {
                change.Remove();
            }
        }

        foreach (var row in root.Descendants<TableRow>())
        {
            var trPr = row.TableRowProperties;
            var change = trPr?.GetFirstChild<TableRowPropertiesChange>();
            if (change is null)
            {
                continue;
            }

            var previous = change.GetFirstChild<TableRowProperties>();
            if (previous is not null)
            {
                row.TableRowProperties = (TableRowProperties)previous.CloneNode(true);
            }
            else
            {
                change.Remove();
            }
        }

        foreach (var cell in root.Descendants<TableCell>())
        {
            var tcPr = cell.TableCellProperties;
            var change = tcPr?.GetFirstChild<TableCellPropertiesChange>();
            if (change is null)
            {
                continue;
            }

            var previous = change.GetFirstChild<TableCellProperties>();
            if (previous is not null)
            {
                cell.TableCellProperties = (TableCellProperties)previous.CloneNode(true);
            }
            else
            {
                change.Remove();
            }
        }

        foreach (var section in root.Descendants<SectionProperties>())
        {
            var change = section.GetFirstChild<SectionPropertiesChange>();
            if (change is null)
            {
                continue;
            }

            var previous = change.GetFirstChild<SectionProperties>();
            if (previous is not null)
            {
                var replacement = (SectionProperties)previous.CloneNode(true);
                var parent = section.Parent;
                if (parent is not null)
                {
                    parent.ReplaceChild(replacement, section);
                }
            }
            else
            {
                change.Remove();
            }
        }
    }

    private static void RunWordCompareMac(string originalPath, string correctedPath, string outputPath, string author, bool strictTextChangesOnly)
    {
        // Search for word-compare.scpt in multiple locations
        var searchPaths = new[]
        {
            // 1. App base directory
            Path.Combine(AppContext.BaseDirectory, "word-compare.scpt"),
            // 2. Legacy binaries folder
            Path.Combine(AppContext.BaseDirectory, "binaries", "word-compare.scpt"),
            // 3. Current working directory (dev mode)
            Path.Combine(Directory.GetCurrentDirectory(), "word-compare.scpt"),
            // 4. Current working directory, binaries subfolder
            Path.Combine(Directory.GetCurrentDirectory(), "binaries", "word-compare.scpt"),
            // 5. macOS app bundle Resources directory
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "word-compare.scpt"),
            // 6. macOS app bundle Resources directory, binaries subfolder
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "binaries", "word-compare.scpt"),
            // 7. Alternative macOS bundle location
            Path.Combine(AppContext.BaseDirectory, "..", "..", "Resources", "word-compare.scpt"),
            // 8. Alternative macOS bundle location with binaries/
            Path.Combine(AppContext.BaseDirectory, "..", "..", "Resources", "binaries", "word-compare.scpt"),
        };

        string? scriptPath = null;
        foreach (var path in searchPaths)
        {
            var resolved = Path.GetFullPath(path);
            if (File.Exists(resolved))
            {
                scriptPath = resolved;
                break;
            }
        }

        if (scriptPath is null)
        {
            var searchedPaths = string.Join("\n  ", searchPaths.Select(Path.GetFullPath));
            throw new FileNotFoundException(
                $"word-compare.scpt not found in any of the following locations:\n  {searchedPaths}\n\n" +
                $"BaseDirectory: {AppContext.BaseDirectory}\n" +
                $"CurrentDirectory: {Directory.GetCurrentDirectory()}"
            );
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList = { scriptPath, originalPath, correctedPath, outputPath, author, strictTextChangesOnly ? "strict" : "default" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        RunProcess(psi, "osascript failed");
    }

    private static void RunWordCompareWindows(string originalPath, string correctedPath, string outputPath, string author, bool strictTextChangesOnly)
    {
        var psScript = @"
param(
    [Parameter(Mandatory = $true)][string]$orig,
    [Parameter(Mandatory = $true)][string]$corr,
    [Parameter(Mandatory = $true)][string]$outp,
    [Parameter(Mandatory = $true)][string]$author,
    [Parameter(Mandatory = $true)][string]$mode
)

$ErrorActionPreference = 'Stop'
$maxAttempts = 3
$lastError = $null
$strictMode = $mode -eq 'strict'
$compareFormatting = if ($strictMode) { $false } else { $true }

function Release-ComObjectSafe {
    param([object]$obj)
    if ($null -eq $obj) {
        return
    }

    try {
        if ([System.Runtime.InteropServices.Marshal]::IsComObject($obj)) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($obj)
        }
    } catch {
    }
}

for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    $word = $null
    $doc1 = $null
    $doc2 = $null
    $comp = $null
    $previousUserName = $null

    try {
        $word = New-Object -ComObject Word.Application
        $word.Visible = $false
        $word.DisplayAlerts = 0
        try { $previousUserName = [string]$word.UserName } catch { $previousUserName = $null }
        $word.UserName = $author

        try { $word.ScreenUpdating = $false } catch { }
        try { $word.Options.Pagination = $false } catch { }
        try { $word.Options.CheckSpellingAsYouType = $false } catch { }
        try { $word.Options.CheckGrammarAsYouType = $false } catch { }
        try { $word.Options.AnimateScreenMovements = $false } catch { }
        try { $word.Options.UpdateLinksAtOpen = $false } catch { }

        $doc1 = $word.Documents.Open($orig, $false, $true, $false)
        $doc2 = $word.Documents.Open($corr, $false, $true, $false)

        $comp = $word.CompareDocuments(
            $doc1,
            $doc2,
            2,
            1,
            $compareFormatting,
            $true,
            $true,
            $true,
            $true,
            $true,
            $true,
            $true,
            $true,
            $true,
            $author,
            $true
        )

        if ($null -eq $comp) {
            throw 'Word returned no comparison document.'
        }

        $comp.TrackRevisions = $true
        $comp.ShowRevisions = $true
        $comp.SaveAs($outp)
        $lastError = $null
        break
    } catch {
        $lastError = $_

        if ($attempt -lt $maxAttempts) {
            Start-Sleep -Milliseconds (1500 * $attempt)
        }
    } finally {
        if ($null -ne $comp) {
            try { $comp.Close($false) } catch { }
        }
        if ($null -ne $doc2) {
            try { $doc2.Close($false) } catch { }
        }
        if ($null -ne $doc1) {
            try { $doc1.Close($false) } catch { }
        }
        if ($null -ne $word) {
            if ($null -ne $previousUserName) {
                try { $word.UserName = $previousUserName } catch { }
            }
            try { $word.Quit() } catch { }
        }

        Release-ComObjectSafe $comp
        Release-ComObjectSafe $doc2
        Release-ComObjectSafe $doc1
        Release-ComObjectSafe $word

        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

if ($null -ne $lastError) {
    $hResult = ''
    try {
        $hResult = (' (HRESULT: 0x{0:X8})' -f ($lastError.Exception.HResult -band 0xffffffff))
    } catch {
    }

    throw ""Word comparison failed after $maxAttempts attempts. $($lastError.Exception.Message)$hResult""
}
";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"lingofix-word-compare-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, psScript);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoProfile",
                    "-Sta",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    scriptPath,
                    originalPath,
                    correctedPath,
                    outputPath,
                    author,
                    strictTextChangesOnly ? "strict" : "default"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            RunProcess(psi, "PowerShell Word comparison failed");

            if (!File.Exists(outputPath))
            {
                throw new FileNotFoundException(
                    $"PowerShell Word comparison did not create output file: {outputPath}",
                    outputPath);
            }
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }

    private static void RunProcess(System.Diagnostics.ProcessStartInfo psi, string errorPrefix)
    {
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException($"{errorPrefix}: Process could not be started.");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit((int)ExternalCompareTimeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"{errorPrefix}: timed out after {ExternalCompareTimeout.TotalMinutes:0} minutes.");
        }

        Task.WaitAll([stdoutTask, stderrTask]);
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            var errorMsg = $"{errorPrefix}:\n";
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                errorMsg += $"Error: {stderr}\n";
            }
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                errorMsg += $"Output: {stdout}\n";
            }
            errorMsg += $"Exit code: {proc.ExitCode}";
            
            // Add helpful hint for common Word issues
            if (psi.FileName == "osascript" && stderr.Contains("Microsoft Word"))
            {
                errorMsg += "\n\nHint: Make sure Microsoft Word is installed and has the necessary permissions.";
            }
            
            throw new InvalidOperationException(errorMsg.Trim());
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string ResolveUsableLibreOfficeExecutable()
    {
        var candidates = ResolveLibreOfficeCandidates().ToList();
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = candidate,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                psi.ArgumentList.Add("--version");

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null)
                {
                    failures.Add($"{candidate}: process could not be started");
                    continue;
                }

                if (!proc.WaitForExit((int)LibreOfficeProbeTimeout.TotalMilliseconds))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    failures.Add($"{candidate}: probe timed out");
                    continue;
                }

                if (proc.ExitCode == 0)
                {
                    return candidate;
                }

                var stderr = proc.StandardError.ReadToEnd().Trim();
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                failures.Add(string.IsNullOrWhiteSpace(details)
                    ? $"{candidate}: exit code {proc.ExitCode}"
                    : $"{candidate}: {details}");
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        var attempted = failures.Count == 0
            ? "no executable candidates"
            : string.Join(Environment.NewLine, failures);
        throw new InvalidOperationException(
            $"LibreOffice comparison requires LibreOffice (soffice), but no usable installation was found.\n" +
            $"Install LibreOffice and retry, or set {SOfficePathEnv} to the absolute soffice path.\n\n" +
            $"Details:\n{attempted}");
    }

    private static IEnumerable<string> ResolveLibreOfficeCandidates()
    {
        var yieldReturnList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var candidate = value.Trim();
            if (!Path.IsPathFullyQualified(candidate) || File.Exists(candidate))
            {
                if (seen.Add(candidate))
                {
                    yieldReturnList.Add(candidate);
                }
            }
        }

        void AddWindowsSofficeCandidates(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var candidate = value.Trim();
            var normalized = candidate.Replace('\\', '/');

            if (normalized.EndsWith("/soffice.exe", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("soffice.exe", StringComparison.OrdinalIgnoreCase))
            {
                Add(Path.ChangeExtension(candidate, ".com"));
                Add(candidate);
                return;
            }

            if (normalized.EndsWith("/soffice.com", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("soffice.com", StringComparison.OrdinalIgnoreCase))
            {
                Add(candidate);
                Add(Path.ChangeExtension(candidate, ".exe"));
                return;
            }

            Add(candidate);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AddWindowsSofficeCandidates(Environment.GetEnvironmentVariable(SOfficePathEnv));
        }
        else
        {
            Add(Environment.GetEnvironmentVariable(SOfficePathEnv));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Add("/Applications/LibreOffice.app/Contents/MacOS/soffice");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AddWindowsSofficeCandidates("C:/Program Files/LibreOffice/program/soffice.exe");
            AddWindowsSofficeCandidates("C:/Program Files (x86)/LibreOffice/program/soffice.exe");
            AddWindowsSofficeCandidates("soffice.exe");
        }
        else
        {
            Add("/usr/bin/soffice");
            Add("/usr/local/bin/soffice");
            Add("soffice");
        }

        return yieldReturnList;
    }

}
