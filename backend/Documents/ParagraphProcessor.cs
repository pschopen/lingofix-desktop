using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

public static class ParagraphProcessor
{
    private const string BatchParagraphSeparator = "\n\n";

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
        var enableParallel = settings.EnableParallelization;
        var maxParallel = Math.Clamp(settings.MaxParallelRequests, Settings.MinMaxParallelRequests, Settings.MaxMaxParallelRequests);
        var cache = enableCache ? new ConcurrentDictionary<string, string>(StringComparer.Ordinal) : null;

        var batchItems = new List<ParagraphItem>();
        var batchChars = 0;
        var work = new List<WorkBatch>();
        var cacheHits = 0;
        var cacheMisses = 0;

        var paragraphList = paragraphs.ToList();
        var totalParagraphs = paragraphList.Count;
        var totalChars = 0;
        foreach (var paragraph in paragraphList)
        {
            var original = ParagraphTextMapper.ExtractEditableText(paragraph);
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

        if (!enableParallel || maxParallel <= 1)
        {
            foreach (var batch in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteWorkBatchAsync(batch, llmClient, settings, logger, cache, chunkSize, null, cancellationToken);
                ApplyBatchResult(result, cache, logger);
                completedBatches++;
                processedParagraphs += batch.Items.Count;
                completedChars += batch.Items.Sum(item => item.Original.Length);
                var completedWork = totalWork == totalChars ? completedChars : processedParagraphs;
                progressCallback?.Invoke(completedWork, totalWork, $"Batch {completedBatches}/{totalBatches}");
                batchCheckpointCallback?.Invoke(completedBatches, totalBatches);
            }

            return;
        }

        logger?.Info($"Parallelization enabled (max {maxParallel}). Jobs: {work.Count}.");
        var concurrency = new AdaptiveConcurrency(maxParallel);
        var progressLock = new object();
        var running = new List<Task>();
        var maxInFlight = Math.Max(maxParallel * 2, maxParallel + 1);

        Task StartBatchTask(WorkBatch batch)
        {
            return Task.Run(async () =>
            {
                await concurrency.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await ExecuteWorkBatchAsync(batch, llmClient, settings, logger, cache, chunkSize, concurrency, cancellationToken);
                    lock (progressLock)
                    {
                        ApplyBatchResult(result, cache, logger);
                        completedBatches++;
                        processedParagraphs += batch.Items.Count;
                        completedChars += batch.Items.Sum(item => item.Original.Length);
                        var completedWork = totalWork == totalChars ? completedChars : processedParagraphs;
                        progressCallback?.Invoke(completedWork, totalWork, $"Batch {completedBatches}/{totalBatches}");
                        batchCheckpointCallback?.Invoke(completedBatches, totalBatches);
                    }
                }
                finally
                {
                    concurrency.Release();
                }
            }, cancellationToken);
        }

        foreach (var batch in work)
        {
            running.Add(StartBatchTask(batch));
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

    private static async Task<string> CorrectWithChunkingAsync(string original, int chunkSize, LlmClient llmClient, CancellationToken cancellationToken)
    {
        if (original.Length <= chunkSize)
        {
            return await llmClient.CorrectAsync(original, cancellationToken);
        }

        var chunks = SplitIntoChunks(original, chunkSize);
        if (chunks.Count == 1)
        {
            return await llmClient.CorrectAsync(original, cancellationToken);
        }

        var builder = new StringBuilder(original.Length);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var correctedChunk = await llmClient.CorrectAsync(chunk, cancellationToken);
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
        AdaptiveConcurrency? concurrency,
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
                var corrected = await CorrectWithCacheAsync(item.Original, chunkSize, llmClient, cache, cancellationToken);
                if (string.IsNullOrWhiteSpace(corrected))
                {
                    continue;
                }

                results[item.Id] = corrected;
            }

            concurrency?.Success();
            return new BatchResult(batch, results);
        }

        var request = BuildBatchRequest(batch.Items);
        string response;
        try
        {
            response = await llmClient.CorrectBatchAsync(request, settings.BatchPrompt, cancellationToken);
        }
        catch (LlmRateLimitException ex)
        {
            var newLimit = concurrency?.Backoff();
            if (newLimit.HasValue)
            {
                logger?.Info($"Rate limit: parallelism reduced to {newLimit.Value}.");
            }

            if (ex.RetryAfterSeconds.HasValue)
            {
                await Task.Delay(ex.RetryAfterSeconds.Value * 1000, cancellationToken);
            }

            logger?.Info($"Batching: rate limit, falling back to single requests (paragraphs: {batch.Items.Count}).");
            return await ProcessBatchFallbackAsync(batch, llmClient, logger, cache, chunkSize, concurrency, cancellationToken);
        }
        catch
        {
            logger?.Info($"Batching: LLM error, falling back to single requests (paragraphs: {batch.Items.Count}).");
            return await ProcessBatchFallbackAsync(batch, llmClient, logger, cache, chunkSize, concurrency, cancellationToken);
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

        concurrency?.Success();
        logger?.Info($"Batching: OK (paragraphs: {batch.Items.Count}).");
        return new BatchResult(batch, parsed);
    }

    private static async Task<BatchResult> ProcessBatchFallbackAsync(
        WorkBatch batch,
        LlmClient llmClient,
        IRunLogger? logger,
        ConcurrentDictionary<string, string>? cache,
        int chunkSize,
        AdaptiveConcurrency? concurrency,
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
                corrected = await CorrectWithCacheAsync(item.Original, chunkSize, llmClient, cache, cancellationToken);
            }
            catch (LlmRateLimitException ex)
            {
                var newLimit = concurrency?.Backoff();
                if (newLimit.HasValue)
                {
                    logger?.Info($"Rate limit: parallelism reduced to {newLimit.Value}.");
                }

                if (ex.RetryAfterSeconds.HasValue)
                {
                    await Task.Delay(ex.RetryAfterSeconds.Value * 1000, cancellationToken);
                }

                corrected = await CorrectWithCacheAsync(item.Original, chunkSize, llmClient, cache, cancellationToken);
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

    private static async Task<string> CorrectWithCacheAsync(string original, int chunkSize, LlmClient llmClient, ConcurrentDictionary<string, string>? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.TryGetValue(original, out var cached))
        {
            return cached;
        }

        var corrected = await CorrectWithChunkingAsync(original, chunkSize, llmClient, cancellationToken);
        if (cache is not null && !string.IsNullOrWhiteSpace(corrected))
        {
            cache.TryAdd(original, corrected);
        }

        return corrected;
    }

    private static void ApplyBatchResult(BatchResult result, ConcurrentDictionary<string, string>? cache, IRunLogger? logger)
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

    private sealed class AdaptiveConcurrency
    {
        private readonly int _max;
        private int _current;
        private int _pendingReductions;
        private readonly SemaphoreSlim _semaphore;
        private readonly object _sync = new();

        public AdaptiveConcurrency(int max)
        {
            _max = Math.Max(1, max);
            _current = _max;
            _semaphore = new SemaphoreSlim(_max, _max);
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

        public int? Backoff()
        {
            lock (_sync)
            {
                var target = Math.Max(1, _current / 2);
                var delta = _current - target;
                if (delta <= 0)
                {
                    return null;
                }

                _current = target;
                _pendingReductions += delta;
                return target;
            }
        }

        public void Success()
        {
            var shouldRelease = false;
            lock (_sync)
            {
                if (_current >= _max)
                {
                    return;
                }

                _current++;
                shouldRelease = true;
            }

            if (shouldRelease)
            {
                _semaphore.Release();
            }
        }
    }
}
