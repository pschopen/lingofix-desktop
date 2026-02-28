using DocumentFormat.OpenXml.Packaging;

namespace Lingofix.Backend.Documents;

public static class LingofixRunner
{
    private const string KeepTempArtifactsEnv = "LINGOFIX_KEEP_TEMP_ARTIFACTS";

    public static async Task<RunResult> RunAsync(RunOptions options, IRunLogger? logger = null, CancellationToken cancellationToken = default)
    {
        logger ??= NullRunLogger.Instance;

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var inputPath = options.InputPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("No file path provided.", nameof(options.InputPath));
        }

        var normalizedInputPath = PathUtils.NormalizeInputPath(inputPath);
        if (!File.Exists(normalizedInputPath))
        {
            throw new FileNotFoundException($"File not found: {normalizedInputPath}", normalizedInputPath);
        }

        if (!normalizedInputPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Please provide a .docx file.");
        }

        const long maxInputBytes = 30L * 1024L * 1024L;
        var inputSize = new FileInfo(normalizedInputPath).Length;
        if (inputSize > maxInputBytes)
        {
            throw new InvalidOperationException($"Input DOCX is too large ({inputSize / (1024 * 1024)} MB). Maximum allowed: {maxInputBytes / (1024 * 1024)} MB.");
        }

        var settings = options.Settings ?? throw new ArgumentNullException(nameof(options.Settings));
        var compareMode = options.CompareModeOverride ?? Settings.NormalizeCompareMode(settings.CompareMode);
        var isWordCompare = compareMode == CompareModeKind.Word;
        var isLibreOfficeCompare = compareMode == CompareModeKind.LibreOffice;
        var isSourceOdt = options.SourceKind == SourceInputKind.Odt;
        var useNativeOdtLibreOfficeCompare = isSourceOdt && isLibreOfficeCompare;
        var isExternalCompare = isWordCompare || isLibreOfficeCompare;
        var canAcceptExistingTrackChanges = options.AcceptExistingTrackChanges;
        var apiKey = Settings.ResolveApiKey(settings.ApiKey);
        var isOllama = string.Equals(settings.Provider, "ollama", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(apiKey) && !isOllama)
        {
            throw new InvalidOperationException("API key missing. Please update settings.json or set the ENV variable.");
        }

        var trackOutputPath = PathUtils.BuildOutputPath(normalizedInputPath, "_lingofix");
        var correctedOutputPath = PathUtils.BuildOutputPath(normalizedInputPath, "_corrected");
        var finalOutputPath = useNativeOdtLibreOfficeCompare
            ? Path.ChangeExtension(trackOutputPath, ".odt")
            : trackOutputPath;
        var tempOutputPath = isExternalCompare
            ? PathUtils.BuildWordCompareFilePath(normalizedInputPath, useNativeOdtLibreOfficeCompare ? "output.odt" : "output.docx")
            : PathUtils.BuildTempOutputPath(trackOutputPath);
        var tempOriginalPath = isExternalCompare
            ? PathUtils.BuildWordCompareFilePath(normalizedInputPath, "original.docx")
            : Path.Combine(Path.GetTempPath(), "Lingofix", $"orig_{Guid.NewGuid():N}{Path.GetExtension(normalizedInputPath)}");
        CopyReadableSnapshot(normalizedInputPath, tempOriginalPath);
        var checkpoint = ProcessingCheckpointStore.Load(normalizedInputPath, logger);
        var correctedPath = checkpoint?.CorrectedPath
            ?? (isExternalCompare
                ? PathUtils.BuildWordCompareFilePath(normalizedInputPath, "corrected.docx")
                : PathUtils.BuildTempCorrectedPath(normalizedInputPath));
        var completedLabels = new HashSet<string>(checkpoint?.CompletedLabels ?? [], StringComparer.Ordinal);
        var completedBatchesByLabel = checkpoint?.CompletedBatchesByLabel is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(checkpoint.CompletedBatchesByLabel, StringComparer.Ordinal);
        if (checkpoint is null)
        {
            File.Copy(tempOriginalPath, correctedPath, overwrite: true);
            ProcessingCheckpointStore.Save(normalizedInputPath, correctedPath, completedLabels, completedBatchesByLabel);
        }
        else
        {
            logger.Info($"Resuming DOCX correction from checkpoint with {completedLabels.Count} completed parts.");
        }

        var llmClient = new LlmClient(
            settings.Provider,
            settings.ApiBase,
            settings.Model,
            settings.Prompt,
            settings.SystemPrompt,
            settings.Temperature,
            settings.TemperatureSupportedHint,
            settings.ReasoningEffortSupportedHint,
            logger);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            llmClient.ApplyAuth(apiKey);
        }

        var trackCreated = false;
        var completedSuccessfully = false;
        var keepTempArtifacts = ShouldKeepTempArtifacts();
        try
        {
            if (compareMode == CompareModeKind.OpenXml)
            {
                var hasTrackedChanges = TrackChangesGenerator.ContainsTrackedChanges(tempOriginalPath);
                if (hasTrackedChanges)
                {
                    if (!canAcceptExistingTrackChanges)
                    {
                        throw new InvalidOperationException(
                            "OpenXML requires accepting all existing track changes first. Start again and confirm that Lingofix may automatically accept existing changes before correction.");
                    }

                    logger.Warning("OpenXML detected existing track changes. Lingofix is accepting all existing changes before correction.");
                    TrackChangesGenerator.AcceptAllTrackedChanges(tempOriginalPath);
                    TrackChangesGenerator.AcceptAllTrackedChanges(correctedPath);
                    logger.Info("Existing track changes were accepted automatically before correction.");
                }
            }

            DocxCoverageReport coverage;

            using (var doc = WordprocessingDocument.Open(correctedPath, true))
            {
                coverage = DocxPartScanner.Scan(doc);

                foreach (var item in coverage.WorkItems)
                {
                    logger.Info($"{item.Label}: {item.Paragraphs.Count} paragraphs");
                }

                if (coverage.CommentCount > 0)
                {
                    logger.Info($"Comments: {coverage.CommentCount} entries detected (preserved unchanged; excluded from correction).");
                }

                logger.Info($"Glossary: {coverage.GlossaryParagraphs} paragraphs");

                if (coverage.AltChunkCount > 0)
                {
                    logger.Info($"Alternative format chunks detected ({coverage.AltChunkCount}); these sections are preserved unchanged.");
                }

                if (coverage.SpecialContentAudit.OleObjectCount > 0)
                {
                    logger.Info($"OLE/embedded objects detected ({coverage.SpecialContentAudit.OleObjectCount}); preserved unchanged by policy.");
                }

                if (coverage.SpecialContentAudit.VmlTextboxCount > 0)
                {
                    logger.Info($"Legacy VML textboxes detected ({coverage.SpecialContentAudit.VmlTextboxCount}); included via paragraph traversal where possible.");
                }

                if (coverage.SpecialContentAudit.ChartTextNodeCount > 0 || coverage.SpecialContentAudit.SmartArtTextNodeCount > 0)
                {
                    logger.Info($"Embedded text nodes detected: charts={coverage.SpecialContentAudit.ChartTextNodeCount}, smartart={coverage.SpecialContentAudit.SmartArtTextNodeCount}.");
                }

                if (coverage.SpecialContentAudit.FieldAudit.UnsafeByType.Count > 0)
                {
                    var unsafeSummary = string.Join(", ",
                        coverage.SpecialContentAudit.FieldAudit.UnsafeByType
                            .OrderByDescending(kvp => kvp.Value)
                            .Select(kvp => $"{kvp.Key}:{kvp.Value}"));
                    logger.Info($"Dynamic/unsafe field types detected and preserved: {unsafeSummary}");
                }

                if (coverage.SpecialContentAudit.FieldAudit.SafeByType.Count > 0)
                {
                    var safeSummary = string.Join(", ",
                        coverage.SpecialContentAudit.FieldAudit.SafeByType
                            .OrderByDescending(kvp => kvp.Value)
                            .Select(kvp => $"{kvp.Key}:{kvp.Value}"));
                    logger.Info($"Safe field types detected: {safeSummary}");
                }

                logger.Info($"Total: {coverage.TotalParagraphs} paragraphs in {coverage.WorkItems.Count} document parts");

                var currentProgress = 0;
                for (var i = 0; i < coverage.WorkItems.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = coverage.WorkItems[i];
                    var partNumber = i + 1;

                    if (completedLabels.Contains(item.Label))
                    {
                        currentProgress += item.Weight;
                        logger.Info($"[{partNumber}/{coverage.WorkItems.Count}] {item.Label} already completed in checkpoint. Skipping.");
                        continue;
                    }

                    logger.Info($"[{partNumber}/{coverage.WorkItems.Count}] Processing {item.Label}...");
                    logger.Progress(currentProgress, $"{item.Label} ({partNumber}/{coverage.WorkItems.Count})");

                    var partProgressStart = currentProgress;
                    var partWeight = item.Weight;
                    var resumeBatches = completedBatchesByLabel.GetValueOrDefault(item.Label, 0);
                    var partSettings = settings;
                    if (settings.EnableBatching)
                    {
                        partSettings = new Settings
                        {
                            Provider = settings.Provider,
                            ApiBase = settings.ApiBase,
                            Model = settings.Model,
                            ApiKey = settings.ApiKey,
                            Prompt = settings.Prompt,
                            SystemPrompt = settings.SystemPrompt,
                            BatchPrompt = settings.BatchPrompt,
                            CompareMode = settings.CompareMode,
                            Temperature = settings.Temperature,
                            ChunkSize = settings.ChunkSize,
                            EnableBatching = settings.BatchingParts.Contains(item.Kind),
                            BatchingParts = settings.BatchingParts,
                            BatchMaxChars = settings.BatchMaxChars,
                            BatchMaxParagraphs = settings.BatchMaxParagraphs,
                            EnableCache = settings.EnableCache,
                            EnableParallelization = settings.EnableParallelization,
                            MaxParallelRequests = settings.MaxParallelRequests
                        };
                    }

                    await ParagraphProcessor.ProcessAsync(item.Paragraphs, llmClient, partSettings, logger, (completedWork, totalWork, batchMsg) =>
                    {
                        if (totalWork <= 0)
                        {
                            return;
                        }

                        var progressRatio = (double)completedWork / totalWork;
                        var partProgress = (int)Math.Clamp(
                            Math.Round(partProgressStart + (partWeight * progressRatio)),
                            partProgressStart,
                            partProgressStart + partWeight - 1);
                        logger.Progress(partProgress, $"{item.Label}: {batchMsg}");
                    }, (doneBatches, _) =>
                    {
                        completedBatchesByLabel[item.Label] = doneBatches;
                        doc.Save();
                        ProcessingCheckpointStore.Save(normalizedInputPath, correctedPath, completedLabels, completedBatchesByLabel);
                    }, resumeBatches, cancellationToken);

                    currentProgress += item.Weight;
                    completedLabels.Add(item.Label);
                    completedBatchesByLabel.Remove(item.Label);
                    ProcessingCheckpointStore.Save(normalizedInputPath, correctedPath, completedLabels, completedBatchesByLabel);
                    logger.Info($"[{partNumber}/{coverage.WorkItems.Count}] {item.Label} completed");
                }

                logger.Progress(80, "Processing embedded chart/smartart text...");
                await EmbeddedTextProcessor.ProcessAsync(doc, llmClient, logger, cancellationToken);

                doc.Save();
            }

            logger.Progress(85, "Generating comparison...");
            try
            {
                if (compareMode == CompareModeKind.Word)
                {
                    TrackChangesGenerator.GenerateWithWord(tempOriginalPath, correctedPath, tempOutputPath, "Lingofix");
                    trackCreated = true;
                    logger.Info("Track changes generated with Word");
                }
                else if (compareMode == CompareModeKind.LibreOffice)
                {
                    if (useNativeOdtLibreOfficeCompare)
                    {
                        var sourceOriginalPath = PathUtils.NormalizeInputPath(options.SourceOriginalPath ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(sourceOriginalPath) || !File.Exists(sourceOriginalPath))
                        {
                            throw new InvalidOperationException("Native ODT compare requires the original ODT source path.");
                        }

                        if (!sourceOriginalPath.EndsWith(".odt", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Native ODT compare requires an .odt source file.");
                        }

                        var correctedOdtPath = PathUtils.BuildWordCompareFilePath(normalizedInputPath, "corrected.odt");
                        TrackChangesGenerator.ConvertWithLibreOffice(correctedPath, correctedOdtPath, ".odt");
                        TrackChangesGenerator.GenerateWithLibreOffice(sourceOriginalPath, correctedOdtPath, tempOutputPath, "Lingofix");
                        logger.Info("Track changes generated with LibreOffice (native ODT compare)");
                    }
                    else
                    {
                        TrackChangesGenerator.GenerateWithLibreOffice(tempOriginalPath, correctedPath, tempOutputPath, "Lingofix");
                        logger.Info("Track changes generated with LibreOffice");
                    }
                    trackCreated = true;
                }
                else
                {
                    TrackChangesGenerator.GenerateParagraphCompare(tempOriginalPath, correctedPath, tempOutputPath, "Lingofix");
                    trackCreated = true;
                    logger.Info("Track changes generated with OpenXML");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Comparison step failed ({compareMode}): {ex.Message}");
                throw;
            }

            if (!trackCreated)
            {
                finalOutputPath = correctedOutputPath;
                File.Copy(correctedPath, tempOutputPath, overwrite: true);
                logger.Info("Copied corrected file (no track changes)");
            }

            if (!isExternalCompare)
            {
                CommentPreserver.PreserveOriginalComments(tempOriginalPath, tempOutputPath, logger);
            }
            else
            {
                logger.Info("Skipping comment-preserver in external compare mode.");
            }

            try
            {
                if (useNativeOdtLibreOfficeCompare)
                {
                    if (!File.Exists(tempOutputPath))
                    {
                        throw new FileNotFoundException($"ODT output file was not created: {tempOutputPath}", tempOutputPath);
                    }

                    using var _ = new System.IO.Compression.ZipArchive(File.OpenRead(tempOutputPath), System.IO.Compression.ZipArchiveMode.Read);
                }
                else if (isExternalCompare)
                {
                    DocxIntegrityValidator.ValidateForWordCompare(tempOriginalPath, tempOutputPath);
                }
                else
                {
                    DocxIntegrityValidator.Validate(tempOriginalPath, tempOutputPath);
                }
                logger.Info("DOCX integrity checks passed.");
            }
            catch (Exception ex)
            {
                if (isExternalCompare)
                {
                    logger.Warning($"Integrity check warning (external compare mode): {ex.Message} The output file will still be provided.");
                }
                else
                {
                    logger.Warning($"Integrity check warning: {ex.Message} The output file will still be provided.");
                }
            }

            PathUtils.PromoteTempToFinal(tempOutputPath, finalOutputPath);
            logger.Progress(100, "Done");
            completedSuccessfully = true;
            return new RunResult(finalOutputPath, trackCreated, trackCreated ? finalOutputPath : null);
        }
        finally
        {
            try
            {
                if (!keepTempArtifacts && completedSuccessfully && File.Exists(correctedPath))
                {
                    File.Delete(correctedPath);
                }
            }
            catch
            {
            }

            try
            {
                if (!keepTempArtifacts && File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
            }
            catch
            {
            }

            try
            {
                if (!keepTempArtifacts && File.Exists(tempOriginalPath))
                {
                    File.Delete(tempOriginalPath);
                }
            }
            catch
            {
            }

            try
            {
                if (completedSuccessfully)
                {
                    ProcessingCheckpointStore.Delete(normalizedInputPath);
                }
                else
                {
                    ProcessingCheckpointStore.Save(normalizedInputPath, correctedPath, completedLabels, completedBatchesByLabel);
                }
            }
            catch
            {
            }
        }
    }

    private static void CopyReadableSnapshot(string sourcePath, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < 5)
                {
                    Thread.Sleep(300 * attempt);
                }
            }
        }

        throw new IOException($"Could not create readable snapshot of input file: {sourcePath}", lastError);
    }

    private static bool ShouldKeepTempArtifacts()
    {
        var value = Environment.GetEnvironmentVariable(KeepTempArtifactsEnv);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
