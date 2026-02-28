using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

public static class ParagraphProcessor
{
    private const string BatchItemSeparator = "---";

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

        var request = BuildBatchRequest(batch.Items, settings.BatchPrompt);
        string response;
        try
        {
            response = await llmClient.CorrectBatchAsync(request, cancellationToken);
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

        concurrency?.Success();
        logger?.Info($"Batching: OK (paragraphs: {batch.Items.Count}).");
        return new BatchResult(batch, parsed);
    }

    private static async Task<BatchResult> ProcessBatchFallbackAsync(WorkBatch batch, LlmClient llmClient, IRunLogger? logger, ConcurrentDictionary<string, string>? cache, int chunkSize, AdaptiveConcurrency? concurrency, CancellationToken cancellationToken)
    {
        logger?.Info($"Batching: single fallback start (paragraphs: {batch.Items.Count}).");
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

        logger?.Info("Batching: single fallback done.");
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

    private static string BuildBatchRequest(List<ParagraphItem> items, string batchPrompt)
    {
        var builder = new StringBuilder();
        builder.AppendLine(batchPrompt.Trim());
        builder.AppendLine();
        AppendBatchItems(builder, items);
        return builder.ToString();
    }

    private static void AppendBatchItems(StringBuilder builder, List<ParagraphItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            builder.Append("ITEM ");
            builder.AppendLine(item.Id.ToString());
            builder.AppendLine(item.Original);

            if (i < items.Count - 1)
            {
                builder.AppendLine();
                builder.AppendLine(BatchItemSeparator);
                builder.AppendLine();
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
        var expectedIds = expectedItems.Select(item => item.Id).ToHashSet();
        var parsedItems = ParseItemBlocks(response);
        if (parsedItems is null)
        {
            failureCode = "invalid_item_format";
            return false;
        }

        if (parsedItems.Count != expectedItems.Count)
        {
            failureCode = "count_mismatch";
            return false;
        }

        foreach (var item in parsedItems)
        {
            if (!expectedIds.Contains(item.Id))
            {
                failureCode = "unknown_id";
                return false;
            }

            if (results.ContainsKey(item.Id))
            {
                failureCode = "duplicate_id";
                return false;
            }

            if (item.Text is null)
            {
                failureCode = "missing_text";
                return false;
            }

            results[item.Id] = LlmClient.SanitizeCorrection(item.Text);
        }

        if (results.Count != expectedItems.Count)
        {
            failureCode = "missing_id";
            return false;
        }

        failureCode = "ok";
        return true;
    }

    private static List<BatchOutputItem>? ParseItemBlocks(string response)
    {
        var normalized = RemoveCodeFence(response)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        var lines = normalized.Split('\n');
        var items = new List<BatchOutputItem>();
        var index = 0;

        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            var header = lines[index].Trim();
            if (!header.StartsWith("ITEM ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var idText = header.Substring(5).Trim();
            if (!int.TryParse(idText, out var id))
            {
                return null;
            }

            index++;
            var textLines = new List<string>();
            while (index < lines.Length)
            {
                var current = lines[index];
                if (current.Trim().Equals(BatchItemSeparator, StringComparison.Ordinal))
                {
                    index++;
                    break;
                }

                textLines.Add(current);
                index++;
            }

            var text = string.Join("\n", textLines);
            items.Add(new BatchOutputItem
            {
                Id = id,
                Text = text
            });
        }

        return items;
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

    private sealed class BatchOutputItem
    {
        public int Id { get; set; }
        public string? Text { get; set; }
    }

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
