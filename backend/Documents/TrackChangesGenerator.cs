using System.Runtime.InteropServices;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

public static class TrackChangesGenerator
{
    private static readonly TimeSpan ExternalCompareTimeout = TimeSpan.FromMinutes(20);
    private const string KeepTempArtifactsEnv = "LINGOFIX_KEEP_TEMP_ARTIFACTS";

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
                var diffNodes = BuildWordDiffRuns(outText, corText, markers, author, revDate, ref revId);
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

    private static List<OpenXmlElement> BuildWordDiffRuns(string originalText, string correctedText, List<ReferenceMarker> markers, string author, DateTime revDate, ref int revId)
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

        foreach (var op in ops)
        {
            while (markerIndex < orderedMarkers.Count && orderedMarkers[markerIndex].Offset <= originalPos)
            {
                nodes.Add(orderedMarkers[markerIndex].Run.CloneNode(true));
                markerIndex++;
            }

            if (op.Kind == DiffKind.Equal)
            {
                AppendTextWithMarkers(op.Token, isDelete: false, ref markerIndex, orderedMarkers, ref originalPos, nodes, author, revDate, ref revId);
            }
            else if (op.Kind == DiffKind.Insert)
            {
                var ins = new InsertedRun
                {
                    Id = revId++.ToString(),
                    Author = author,
                    Date = revDate
                };
                ins.AppendChild(new RunProperties());
                ins.AppendChild(new Text(op.Token) { Space = SpaceProcessingModeValues.Preserve });
                nodes.Add(new Run(ins));
            }
            else if (op.Kind == DiffKind.Delete)
            {
                AppendTextWithMarkers(op.Token, isDelete: true, ref markerIndex, orderedMarkers, ref originalPos, nodes, author, revDate, ref revId);
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
        ref int revId)
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
                AppendSegment(text.Substring(localPos, cut - localPos), isDelete, nodes, author, revDate, ref revId);
            }

            nodes.Add(orderedMarkers[markerIndex].Run.CloneNode(true));
            markerIndex++;
            localPos = cut;
        }

        if (localPos < text.Length)
        {
            AppendSegment(text.Substring(localPos), isDelete, nodes, author, revDate, ref revId);
        }

        originalPos += text.Length;
    }

    private static void AppendSegment(
        string segment,
        bool isDelete,
        List<OpenXmlElement> nodes,
        string author,
        DateTime revDate,
        ref int revId)
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
            del.AppendChild(new RunProperties());
            del.AppendChild(new DeletedText(segment));
            nodes.Add(new Run(del));
        }
        else
        {
            nodes.Add(new Run(new Text(segment) { Space = SpaceProcessingModeValues.Preserve }));
        }
    }

    private sealed record ReferenceMarker(int Offset, Run Run);

    public static void GenerateWithWord(string originalPath, string correctedPath, string outputPath, string author)
    {
        var compareTempDir = Path.Combine(Path.GetTempPath(), "Lingofix", "compare");
        Directory.CreateDirectory(compareTempDir);
        var tempOriginalPath = Path.Combine(compareTempDir, $"orig_{Guid.NewGuid():N}{Path.GetExtension(originalPath)}");

        File.Copy(originalPath, tempOriginalPath, overwrite: true);

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                RunWordCompareMac(tempOriginalPath, correctedPath, outputPath, author);
                RejectFormattingChanges(outputPath);
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RunWordCompareWindows(tempOriginalPath, correctedPath, outputPath, author);
                RejectFormattingChanges(outputPath);
                return;
            }

            throw new PlatformNotSupportedException("Word comparison is not supported on this operating system.");
        }
        finally
        {
            try
            {
                if (!ShouldKeepTempArtifacts() && File.Exists(tempOriginalPath))
                {
                    File.Delete(tempOriginalPath);
                }
            }
            catch
            {
            }
        }
    }

    private static bool ShouldKeepTempArtifacts()
    {
        var value = Environment.GetEnvironmentVariable(KeepTempArtifactsEnv);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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

    private static void RunWordCompareMac(string originalPath, string correctedPath, string outputPath, string author)
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
            ArgumentList = { scriptPath, originalPath, correctedPath, outputPath, author },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        RunProcess(psi, "osascript failed");
    }

    private static void RunWordCompareWindows(string originalPath, string correctedPath, string outputPath, string author)
    {
        var psScript = @"
param(
    [Parameter(Mandatory = $true)][string]$orig,
    [Parameter(Mandatory = $true)][string]$corr,
    [Parameter(Mandatory = $true)][string]$outp,
    [Parameter(Mandatory = $true)][string]$author
)

$ErrorActionPreference = 'Stop'
$maxAttempts = 3
$lastError = $null

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

    try {
        $word = New-Object -ComObject Word.Application
        $word.Visible = $false
        $word.DisplayAlerts = 0
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
            $true,
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
                    author
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

}
