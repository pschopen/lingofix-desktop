using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

public static class ParagraphProcessor
{
    private const string BatchParagraphSeparator = "\n\n";

    // On 429/503/529 the whole batch is re-sent (never split into single requests). After
    // this many paced retries the rate-limit error is propagated instead.
    private const int MaxBatchRateLimitRetries = 3;

    // Auto mode: internal ceiling on derived parallelism, and the conservative starting
    // worker count used until enough latency has been measured to derive a real target.
    // Matches the manual maximum (Settings.MaxMaxParallelRequests) so an unthrottled
    // provider (interval -> 0, where DeriveParallelism returns the cap) can open as many
    // slots in Auto as a user could request in Manual.
    private const int AutoParallelCap = 16;
    private const int AutoInitialParallel = 2;

    public static async Task ProcessAsync(
        IEnumerable<Paragraph> paragraphs,
        LlmClient llmClient,
        Settings settings,
        IRunLogger? logger,
        Action<int, int, string>? progressCallback = null,
        Action<int, int>? batchCheckpointCallback = null,
        int resumeCompletedBatches = 0,
        CancellationToken cancellationToken = default)
    {
        var enableBatching = settings.EnableBatching;
        var chunkSize = Math.Clamp(settings.ChunkSize, Settings.MinChunkSize, Settings.MaxChunkSize);
        var batchMaxChars = Math.Clamp(settings.BatchMaxChars, Settings.MinBatchMaxChars, Settings.MaxBatchMaxChars);
        var batchMaxParagraphs = Math.Clamp(settings.BatchMaxParagraphs, Settings.MinBatchMaxParagraphs, Settings.MaxBatchMaxParagraphs);
        var enableCache = settings.EnableCache;
        var configuredParallel = Math.Clamp(settings.MaxParallelRequests, Settings.MinMaxParallelRequests, Settings.MaxMaxParallelRequests);
        var isOllama = string.Equals(settings.Provider, "ollama", StringComparison.OrdinalIgnoreCase);
        var cache = enableCache ? new ConcurrentDictionary<string, string>(StringComparer.Ordinal) : null;
        var citationStyle = settings.CitationStyle;

        var batchItems = new List<ParagraphItem>();
        var batchChars = 0;
        var work = new List<WorkBatch>();
        var cacheHits = 0;
        var cacheMisses = 0;

        var paragraphList = paragraphs.ToList();
        var totalParagraphs = paragraphList.Count;
        var totalChars = 0;
        var extractionGapWarnings = 0;
        foreach (var paragraph in paragraphList)
        {
            var original = ParagraphTextMapper.ExtractEditableText(paragraph);

            WarnOnExtractionGap(paragraph, original, logger, ref extractionGapWarnings);

            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            totalChars += original.Length;

            if (enableCache && cache is not null && cache.TryGetValue(original, out var cached))
            {
                cacheHits++;
                ParagraphTextMapper.ApplyCorrection(paragraph, original, cached);
                continue;
            }

            cacheMisses++;

            if (!enableBatching || original.Length > batchMaxChars)
            {
                work.Add(new WorkBatch([new ParagraphItem(paragraph, original)], UseBatch: false));
                continue;
            }

            var nextEstimate = EstimateBatchLength(batchChars, original.Length);
            if (batchItems.Count >= batchMaxParagraphs || nextEstimate > batchMaxChars)
            {
                work.Add(new WorkBatch([.. batchItems], UseBatch: true));
                batchItems.Clear();
                batchChars = 0;
            }

            batchItems.Add(new ParagraphItem(paragraph, original));
            batchChars += original.Length;
        }

        if (batchItems.Count > 0)
        {
            work.Add(new WorkBatch([.. batchItems], UseBatch: true));
        }

        if (cacheHits > 0 || cacheMisses > 0)
        {
            logger?.Info($"Cache: hits {cacheHits}, misses {cacheMisses}.");
        }

        if (work.Count == 0)
        {
            return;
        }

        logger?.Info($"Processing {work.Count} batches...");
        var totalBatches = work.Count;
        var resumedBatches = Math.Clamp(resumeCompletedBatches, 0, totalBatches);
        var resumedWork = resumedBatches == 0 ? new List<WorkBatch>() : work.Take(resumedBatches).ToList();
        work = resumedBatches == 0 ? work : work.Skip(resumedBatches).ToList();
        var completedBatches = resumedBatches;
        var completedChars = resumedWork.Sum(batch => batch.Items.Sum(item => item.Original.Length));
        var processedParagraphs = resumedWork.Sum(batch => batch.Items.Count);

        var totalWork = totalChars;
        if (totalWork <= 0)
        {
            totalWork = totalParagraphs;
        }

        if (resumedBatches > 0)
        {
            logger?.Info($"Resuming at batch {resumedBatches + 1}/{totalBatches}.");
            var completedWork = totalWork == totalChars ? completedChars : processedParagraphs;
            progressCallback?.Invoke(completedWork, totalWork, $"Batch {completedBatches}/{totalBatches}");
            if (completedBatches == totalBatches)
            {
                return;
            }
        }

        // Decide the concurrency profile from the speed mode.
        //  * Auto:   parallelism is *derived* from measured latency / learned interval and
        //            re-tuned per batch; it is never a user setting.
        //  * Manual: parallelism is the user's fixed value (1 = serial).
        //  * Ollama: local, unpaced — a fixed moderate parallelism, no derivation.
        var speedMode = settings.SpeedMode;
        int concurrencyCap;
        int initialParallel;
        bool deriveParallel;
        if (isOllama)
        {
            concurrencyCap = Math.Clamp(configuredParallel, 1, AutoParallelCap);
            initialParallel = concurrencyCap;
            deriveParallel = false;
        }
        else if (speedMode == SpeedMode.Manual)
        {
            concurrencyCap = configuredParallel;
            initialParallel = configuredParallel;
            deriveParallel = false;
        }
        else
        {
            concurrencyCap = AutoParallelCap;
            initialParallel = Math.Min(AutoInitialParallel, AutoParallelCap);
            deriveParallel = true;
        }

        if (!deriveParallel && initialParallel <= 1)
        {
            foreach (var batch in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteWorkBatchAsync(batch, llmClient, settings, logger, cache, chunkSize, null, cancellationToken);
                ApplyBatchResult(result, cache, logger, citationStyle);
                completedBatches++;
                processedParagraphs += batch.Items.Count;
                completedChars += batch.Items.Sum(item => item.Original.Length);
                var completedWork = totalWork == totalChars ? completedChars : processedParagraphs;
                progressCallback?.Invoke(completedWork, totalWork, $"Batch {completedBatches}/{totalBatches}");
                batchCheckpointCallback?.Invoke(completedBatches, totalBatches);
            }

            return;
        }

        logger?.Info(deriveParallel
            ? $"Speed mode: auto (parallelism derived, cap {concurrencyCap}). Jobs: {work.Count}."
            : $"Speed mode: manual (parallelism {initialParallel}). Jobs: {work.Count}.");
        var concurrency = new DynamicConcurrencyGate(initialParallel, concurrencyCap);
        var progressLock = new object();
        var running = new List<Task>();
        var maxInFlight = Math.Max(concurrencyCap * 2, concurrencyCap + 1);

        // Batches finish out of order under parallelism, but the checkpoint's resume
        // index is positional (resumeCompletedBatches -> work.Skip). Persisting the raw
        // completion count would let a crash record e.g. "6 done" while the finished six
        // are scattered; resuming would then Skip the first six positions and silently
        // drop the unprocessed batches among them. So the persisted count may only
        // advance across a *contiguous* prefix of finished batches. The document itself
        // is still saved after every batch (durability); replaying already-applied
        // batches beyond the prefix on resume is a safe no-op.
        var completedFlags = new bool[work.Count];
        var contiguousDone = 0;

        Task StartBatchTask(WorkBatch batch, int index)
        {
            return Task.Run(async () =>
            {
                // No concurrency slot is held here: the gate (concurrency) is passed into
                // the LLM client, which acquires it only around the HTTP send — never
                // across the shared pacing wait. That keeps throughput at the provider's
                // real rate limit instead of maxParallel/latency.
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteWorkBatchAsync(batch, llmClient, settings, logger, cache, chunkSize, concurrency, cancellationToken);
                lock (progressLock)
                {
                    ApplyBatchResult(result, cache, logger, citationStyle);
                    completedBatches++;
                    processedParagraphs += batch.Items.Count;
                    completedChars += batch.Items.Sum(item => item.Original.Length);
                    var completedWork = totalWork == totalChars ? completedChars : processedParagraphs;
                    progressCallback?.Invoke(completedWork, totalWork, $"Batch {completedBatches}/{totalBatches}");
                    completedFlags[index] = true;
                    contiguousDone = AdvanceContiguousPrefix(completedFlags, contiguousDone);
                    batchCheckpointCallback?.Invoke(resumedBatches + contiguousDone, totalBatches);

                    // Auto mode: retune parallelism to keep the learned pacing interval
                    // saturated — enough workers that latency is hidden, no more.
                    if (deriveParallel)
                    {
                        concurrency.SetTarget(DeriveParallelism(
                            llmClient.AverageLatencyMs,
                            llmClient.CurrentPacingIntervalMs,
                            concurrencyCap));
                    }
                }
            }, cancellationToken);
        }

        for (var index = 0; index < work.Count; index++)
        {
            running.Add(StartBatchTask(work[index], index));
            if (running.Count < maxInFlight)
            {
                continue;
            }

            var finished = await Task.WhenAny(running);
            running.Remove(finished);
            await finished;
        }

        await Task.WhenAll(running);
    }

    /// <summary>
    /// Advances <paramref name="current"/> across the leading run of completed batches
    /// in <paramref name="completedFlags"/>. Only a contiguous prefix counts, so an
    /// out-of-order completion never inflates the resume index past a batch that has not
    /// finished yet. See the parallel dispatch loop for why this matters for checkpoints.
    /// </summary>
    internal static int AdvanceContiguousPrefix(bool[] completedFlags, int current)
    {
        while (current < completedFlags.Length && completedFlags[current])
        {
            current++;
        }

        return current;
    }

    // Runtime tripwire against silent text loss during extraction. A healthy extractor
    // keeps essentially all visible w:t text, so this never fires on well-formed input;
    // it exists to make a future extraction regression (or an exotic run layout) visible
    // in the log instead of it quietly shipping half a footnote to the model.
    private const double MinExtractionCoverage = 0.75;
    private const int MinVisibleCharsForGapCheck = 20;
    private const int MaxExtractionGapWarnings = 25;

    private static void WarnOnExtractionGap(Paragraph paragraph, string extracted, IRunLogger? logger, ref int warningCount)
    {
        if (logger is null || warningCount >= MaxExtractionGapWarnings)
        {
            return;
        }

        var visible = ParagraphTextMapper.CountVisibleTextChars(paragraph);
        if (visible < MinVisibleCharsForGapCheck || extracted.Length >= visible * MinExtractionCoverage)
        {
            return;
        }

        warningCount++;
        var droppedPct = 100 - (int)Math.Round(100.0 * extracted.Length / visible);
        var snippet = extracted.Length <= 60 ? extracted : extracted[..60] + "…";
        snippet = snippet.Replace('\n', ' ').Replace('\r', ' ').Trim();
        logger.Warning($"Extraction gap: kept {extracted.Length}/{visible} visible chars (~{droppedPct}% dropped). Kept text starts: \"{snippet}\"");
        if (warningCount == MaxExtractionGapWarnings)
        {
            logger.Warning($"Extraction gap: further warnings suppressed after {MaxExtractionGapWarnings}.");
        }
    }

    private static async Task<string> CorrectWithChunkingAsync(string original, int chunkSize, LlmClient llmClient, IConcurrencyGate? gate, CancellationToken cancellationToken)
    {
        if (original.Length <= chunkSize)
        {
            return await llmClient.CorrectAsync(original, gate, cancellationToken);
        }

        var chunks = SplitIntoChunks(original, chunkSize);
        if (chunks.Count == 1)
        {
            return await llmClient.CorrectAsync(original, gate, cancellationToken);
        }

        var builder = new StringBuilder(original.Length);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var correctedChunk = await llmClient.CorrectAsync(chunk, gate, cancellationToken);
            if (string.IsNullOrWhiteSpace(correctedChunk))
            {
                builder.Append(chunk);
            }
            else
            {
                builder.Append(correctedChunk);
            }
        }

        return builder.ToString();
    }

    private static async Task<BatchResult> ExecuteWorkBatchAsync(
        WorkBatch batch,
        LlmClient llmClient,
        Settings settings,
        IRunLogger? logger,
        ConcurrentDictionary<string, string>? cache,
        int chunkSize,
        IConcurrencyGate? concurrency,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, string>();

        if (batch.Items.Count == 0)
        {
            return new BatchResult(batch, results);
        }

        if (!batch.UseBatch)
        {
            foreach (var item in batch.Items)
            {
                var corrected = await CorrectWithCacheAsync(item.Original, chunkSize, llmClient, cache, concurrency, cancellationToken);
                if (string.IsNullOrWhiteSpace(corrected))
                {
                    continue;
                }

                results[item.Id] = corrected;
            }

            return new BatchResult(batch, results);
        }

        var request = BuildBatchRequest(batch.Items);
        string response;
        var rateLimitRetries = 0;
        while (true)
        {
            try
            {
                response = await llmClient.CorrectBatchAsync(request, settings.BatchPrompt, concurrency, cancellationToken);
                break;
            }
            catch (LlmRateLimitException) when (rateLimitRetries < MaxBatchRateLimitRetries)
            {
                // 429/503/529: retry the SAME batch rather than exploding it into single
                // requests (that would multiply load exactly during overload). The shared
                // rate limiter has already paced the next slot past any Retry-After, so the
                // next CorrectBatchAsync waits the right amount before sending.
                rateLimitRetries++;
                logger?.Info($"Batching: rate limit, retrying same batch ({rateLimitRetries}/{MaxBatchRateLimitRetries}, paragraphs: {batch.Items.Count}).");
                continue;
            }
            catch (LlmRateLimitException)
            {
                // Exhausted batch-level rate-limit retries: propagate. We deliberately do
                // NOT fall back to single requests on rate limits.
                logger?.Info($"Batching: rate limit persisted after {MaxBatchRateLimitRetries} batch retries; propagating (paragraphs: {batch.Items.Count}).");
                throw;
            }
            catch
            {
                logger?.Info($"Batching: LLM error, falling back to single requests (paragraphs: {batch.Items.Count}).");
                return await ProcessBatchFallbackAsync(batch, llmClient, logger, cache, chunkSize, concurrency, cancellationToken);
            }
        }

        if (!TryParseBatchResponse(response, batch.Items, out var parsed, out var parseFailure))
        {
            logger?.Info($"Batching: invalid response ({parseFailure}), falling back to single requests (paragraphs: {batch.Items.Count}).");
            return await ProcessBatchFallbackAsync(batch, llmClient, logger, cache, chunkSize, concurrency, cancellationToken);
        }

        if (parsed.Count < batch.Items.Count)
        {
            var missingItems = batch.Items
                .Where(item => !parsed.ContainsKey(item.Id))
                .ToList();
            logger?.Info($"Batching: partial fallback start (missing: {missingItems.Count}/{batch.Items.Count}).");
            var partialBatch = new WorkBatch(missingItems, UseBatch: false);
            var partialResult = await ProcessBatchFallbackAsync(
                partialBatch,
                llmClient,
                logger,
                cache,
                chunkSize,
                concurrency,
                cancellationToken,
                context: "partial fallback");
            foreach (var pair in partialResult.Corrections)
            {
                parsed[pair.Key] = pair.Value;
            }

            logger?.Info("Batching: partial fallback done.");
        }

        logger?.Info($"Batching: OK (paragraphs: {batch.Items.Count}).");
        return new BatchResult(batch, parsed);
    }

    private static async Task<BatchResult> ProcessBatchFallbackAsync(
        WorkBatch batch,
        LlmClient llmClient,
        IRunLogger? logger,
        ConcurrentDictionary<string, string>? cache,
        int chunkSize,
        IConcurrencyGate? concurrency,
        CancellationToken cancellationToken,
        string context = "single fallback")
    {
        logger?.Info($"Batching: {context} start (paragraphs: {batch.Items.Count}).");
        var results = new Dictionary<int, string>();
        foreach (var item in batch.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string corrected;
            try
            {
                corrected = await CorrectWithCacheAsync(item.Original, chunkSize, llmClient, cache, concurrency, cancellationToken);
            }
            catch (LlmRateLimitException)
            {
                // The rate limiter already paced the next slot; retry the single item once
                // more without touching parallelism (the limiter is the only 429 regulator).
                corrected = await CorrectWithCacheAsync(item.Original, chunkSize, llmClient, cache, concurrency, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(corrected))
            {
                continue;
            }

            results[item.Id] = corrected;
        }

        logger?.Info($"Batching: {context} done.");
        return new BatchResult(batch, results);
    }

    private static async Task<string> CorrectWithCacheAsync(string original, int chunkSize, LlmClient llmClient, ConcurrentDictionary<string, string>? cache, IConcurrencyGate? gate, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.TryGetValue(original, out var cached))
        {
            return cached;
        }

        var corrected = await CorrectWithChunkingAsync(original, chunkSize, llmClient, gate, cancellationToken);
        if (cache is not null && !string.IsNullOrWhiteSpace(corrected))
        {
            cache.TryAdd(original, corrected);
        }

        return corrected;
    }

    private static void ApplyBatchResult(BatchResult result, ConcurrentDictionary<string, string>? cache, IRunLogger? logger, CitationNormalizer.CitationStyle? citationStyle)
    {
        foreach (var item in result.Batch.Items)
        {
            if (!result.Corrections.TryGetValue(item.Id, out var corrected))
            {
                continue;
            }

            corrected = XmlTextSanitizer.StripInvalidXmlChars(corrected, out var removedChars);
            if (removedChars > 0)
            {
                logger?.Warning($"Batching: removed {removedChars} invalid XML character(s) from item {item.Id}.");
            }

            if (string.IsNullOrWhiteSpace(corrected))
            {
                continue;
            }

            if (citationStyle is not null)
            {
                corrected = CitationNormalizer.Normalize(corrected, citationStyle.Value);
            }

            ParagraphTextMapper.ApplyCorrection(item.Paragraph, item.Original, corrected);

            if (cache is not null)
            {
                cache.TryAdd(item.Original, corrected);
            }
        }
    }

    private static string BuildBatchRequest(List<ParagraphItem> items)
    {
        var builder = new StringBuilder();
        AppendBatchItems(builder, items);
        return builder.ToString();
    }

    private static void AppendBatchItems(StringBuilder builder, List<ParagraphItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            builder.Append(item.Original);

            if (i < items.Count - 1)
            {
                builder.Append(BatchParagraphSeparator);
            }
        }
    }

    private static bool TryParseBatchResponse(
        string response,
        IReadOnlyList<ParagraphItem> expectedItems,
        out Dictionary<int, string> results,
        out string failureCode)
    {
        results = new Dictionary<int, string>();
        failureCode = "unknown";
        if (string.IsNullOrWhiteSpace(response))
        {
            failureCode = "empty_response";
            return false;
        }
        var parsedParagraphs = ParseBatchParagraphs(response);
        if (parsedParagraphs is null)
        {
            failureCode = "invalid_item_format";
            return false;
        }

        if (parsedParagraphs.Count != expectedItems.Count)
        {
            if (parsedParagraphs.Count < expectedItems.Count &&
                TryAlignBatchParagraphs(expectedItems, parsedParagraphs, out var aligned))
            {
                results = aligned;
                failureCode = "partial_count_mismatch";
                return true;
            }

            failureCode = "count_mismatch";
            return false;
        }

        for (var i = 0; i < expectedItems.Count; i++)
        {
            results[expectedItems[i].Id] = LlmClient.SanitizeCorrection(parsedParagraphs[i]);
        }

        failureCode = "ok";
        return true;
    }

    private static List<string>? ParseBatchParagraphs(string response)
    {
        var normalized = RemoveCodeFence(response)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        var parts = normalized
            .Split([BatchParagraphSeparator], StringSplitOptions.None)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        return parts;
    }

    private static string RemoveCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var lines = trimmed.Split('\n').ToList();
        if (lines.Count < 2)
        {
            return text;
        }

        if (lines[0].StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && lines[^1].Trim().Equals("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines).Trim();
    }

    private static int EstimateBatchLength(int currentLength, int nextParagraphLength)
    {
        return currentLength + nextParagraphLength + 16;
    }

    private static bool TryAlignBatchParagraphs(
        IReadOnlyList<ParagraphItem> expectedItems,
        IReadOnlyList<string> parsedParagraphs,
        out Dictionary<int, string> aligned)
    {
        aligned = new Dictionary<int, string>();
        if (parsedParagraphs.Count == 0 || parsedParagraphs.Count > expectedItems.Count)
        {
            return false;
        }

        var n = parsedParagraphs.Count;
        var m = expectedItems.Count;
        var scores = new double[n, m];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                scores[i, j] = ParagraphSimilarity(parsedParagraphs[i], expectedItems[j].Original);
            }
        }

        var dp = new double[n + 1, m + 1];
        var chooseMatch = new bool[n + 1, m + 1];
        const double negInf = -1_000_000d;

        for (var i = 1; i <= n; i++)
        {
            dp[i, 0] = negInf;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var skip = dp[i, j - 1];
                var match = dp[i - 1, j - 1];
                if (match > negInf / 2)
                {
                    match += scores[i - 1, j - 1];
                }

                if (match >= skip)
                {
                    dp[i, j] = match;
                    chooseMatch[i, j] = true;
                }
                else
                {
                    dp[i, j] = skip;
                }
            }
        }

        if (dp[n, m] <= negInf / 2)
        {
            return false;
        }

        var mapping = new int[n];
        Array.Fill(mapping, -1);
        var row = n;
        var col = m;
        while (row > 0 && col > 0)
        {
            if (chooseMatch[row, col])
            {
                mapping[row - 1] = col - 1;
                row--;
                col--;
            }
            else
            {
                col--;
            }
        }

        if (row > 0 || mapping.Any(index => index < 0))
        {
            return false;
        }

        for (var i = 0; i < n; i++)
        {
            var expectedIndex = mapping[i];
            var score = scores[i, expectedIndex];
            if (score < 0.34)
            {
                return false;
            }

            var expected = expectedItems[expectedIndex];
            aligned[expected.Id] = LlmClient.SanitizeCorrection(parsedParagraphs[i]);
        }

        return true;
    }

    private static double ParagraphSimilarity(string left, string right)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Count(token => rightTokens.Contains(token));
        if (intersection == 0)
        {
            return 0;
        }

        return (2.0 * intersection) / (leftTokens.Count + rightTokens.Count);
    }

    private static HashSet<string> Tokenize(string input)
    {
        var normalized = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            normalized.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return normalized
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<string> SplitIntoChunks(string text, int maxChars)
    {
        var chunks = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= maxChars)
            {
                chunks.Add(text.Substring(start, remaining));
                break;
            }

            var end = start + maxChars;
            var splitAt = FindSplitPoint(text, start, end);
            if (splitAt <= start)
            {
                splitAt = end;
            }

            chunks.Add(text.Substring(start, splitAt - start));
            start = splitAt;
        }

        return chunks;
    }

    private static int FindSplitPoint(string text, int start, int end)
    {
        var limit = Math.Min(end, text.Length);
        for (int i = limit - 1; i > start; i--)
        {
            var ch = text[i];
            if (ch == '.' || ch == '!' || ch == '?' || ch == '\n' || ch == '\r')
            {
                return i + 1;
            }
        }

        for (int i = limit - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return end;
    }

    private sealed record ParagraphItem(Paragraph Paragraph, string Original)
    {
        public int Id { get; } = NextId();
        private static int _nextId;
        private static int NextId() => Interlocked.Increment(ref _nextId);
    }

    private sealed record WorkBatch(List<ParagraphItem> Items, bool UseBatch);

    private sealed record BatchResult(WorkBatch Batch, Dictionary<int, string> Corrections);

    /// <summary>
    /// Auto mode: how many workers keep the pacing interval saturated. If each request
    /// takes <paramref name="latencyMs"/> and one may start every <paramref name="intervalMs"/>,
    /// then ⌈latency/interval⌉ requests are in flight at the steady state. Clamped to
    /// [1, cap]. An unthrottled interval (0) means the rate limiter is not the bottleneck,
    /// so run at the full cap; no latency sample yet means start with one worker.
    /// </summary>
    internal static int DeriveParallelism(double latencyMs, double intervalMs, int cap)
    {
        cap = Math.Max(1, cap);
        if (intervalMs <= 0)
        {
            return cap;
        }

        if (latencyMs <= 0)
        {
            return 1;
        }

        var workers = (int)Math.Ceiling(latencyMs / intervalMs);
        return Math.Clamp(workers, 1, cap);
    }

    /// <summary>
    /// Bounds in-flight HTTP to a target that can be raised or lowered at runtime. Raising
    /// releases permits (up to the cap); lowering lets excess permits expire on the next
    /// Release via <c>_pendingReductions</c> rather than yanking an in-use slot.
    /// </summary>
    private sealed class DynamicConcurrencyGate : IConcurrencyGate
    {
        private readonly int _cap;
        private int _current;
        private int _pendingReductions;
        private readonly SemaphoreSlim _semaphore;
        private readonly object _sync = new();

        public DynamicConcurrencyGate(int initialTarget, int cap)
        {
            _cap = Math.Max(1, cap);
            _current = Math.Clamp(initialTarget, 1, _cap);
            _semaphore = new SemaphoreSlim(_current, _cap);
        }

        public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

        public void Release()
        {
            lock (_sync)
            {
                if (_pendingReductions > 0)
                {
                    _pendingReductions--;
                    return;
                }
            }

            _semaphore.Release();
        }

        public void SetTarget(int target)
        {
            target = Math.Clamp(target, 1, _cap);
            var toRelease = 0;
            lock (_sync)
            {
                if (target == _current)
                {
                    return;
                }

                if (target > _current)
                {
                    var delta = target - _current;
                    _current = target;
                    // Cancel queued reductions first (those Releases now add a permit back),
                    // then release any remaining shortfall as fresh permits.
                    var cancel = Math.Min(_pendingReductions, delta);
                    _pendingReductions -= cancel;
                    toRelease = delta - cancel;
                }
                else
                {
                    _pendingReductions += _current - target;
                    _current = target;
                }
            }

            for (var i = 0; i < toRelease; i++)
            {
                _semaphore.Release();
            }
        }
    }
}
