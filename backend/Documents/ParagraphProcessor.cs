using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

public static class ParagraphProcessor
{
    private const int MaxChunkChars = 5000;
    private const int MinBatchChars = 500;
    private const int MaxBatchChars = 50000;
    private const int MinBatchParagraphs = 1;
    private const int MaxBatchParagraphs = 100;
    private const int MinParallelRequests = 1;
    private const int MaxParallelRequests = 32;
    private static readonly Regex BatchRegex = new(
        @"<<P:(\d+)>>\s*(.*?)\s*<</P:\1>>",
        RegexOptions.Singleline | RegexOptions.Compiled);

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
        var batchMaxChars = Math.Clamp(settings.BatchMaxChars, MinBatchChars, MaxBatchChars);
        var batchMaxParagraphs = Math.Clamp(settings.BatchMaxParagraphs, MinBatchParagraphs, MaxBatchParagraphs);
        var enableCache = settings.EnableCache;
        var enableParallel = settings.EnableParallelization;
        var maxParallel = Math.Clamp(settings.MaxParallelRequests, MinParallelRequests, MaxParallelRequests);
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
                var result = await ExecuteWorkBatchAsync(batch, llmClient, logger, cache, null, cancellationToken);
                ApplyBatchResult(result, cache);
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
                    var result = await ExecuteWorkBatchAsync(batch, llmClient, logger, cache, concurrency, cancellationToken);
                    lock (progressLock)
                    {
                        ApplyBatchResult(result, cache);
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

    private static async Task<string> CorrectWithChunkingAsync(string original, LlmClient llmClient, CancellationToken cancellationToken)
    {
        if (original.Length <= MaxChunkChars)
        {
            return await llmClient.CorrectAsync(original, cancellationToken);
        }

        var chunks = SplitIntoChunks(original, MaxChunkChars);
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

    private static async Task<BatchResult> ExecuteWorkBatchAsync(WorkBatch batch, LlmClient llmClient, IRunLogger? logger, ConcurrentDictionary<string, string>? cache, AdaptiveConcurrency? concurrency, CancellationToken cancellationToken)
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
                var corrected = await CorrectWithCacheAsync(item.Original, llmClient, cache, cancellationToken);
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
            response = await llmClient.CorrectAsync(request, cancellationToken);
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
            return await ProcessBatchFallbackAsync(batch, llmClient, logger, cache, concurrency, cancellationToken);
        }
        catch
        {
            logger?.Info($"Batching: LLM error, falling back to single requests (paragraphs: {batch.Items.Count}).");
            return await ProcessBatchFallbackAsync(batch, llmClient, logger, cache, concurrency, cancellationToken);
        }

        if (!TryParseBatchResponse(response, batch.Items.Count, out var parsed))
        {
            logger?.Info($"Batching: invalid response, falling back to single requests (paragraphs: {batch.Items.Count}).");
            return await ProcessBatchFallbackAsync(batch, llmClient, logger, cache, concurrency, cancellationToken);
        }

        concurrency?.Success();
        logger?.Info($"Batching: OK (paragraphs: {batch.Items.Count}).");
        return new BatchResult(batch, parsed);
    }

    private static async Task<BatchResult> ProcessBatchFallbackAsync(WorkBatch batch, LlmClient llmClient, IRunLogger? logger, ConcurrentDictionary<string, string>? cache, AdaptiveConcurrency? concurrency, CancellationToken cancellationToken)
    {
        logger?.Info($"Batching: single fallback start (paragraphs: {batch.Items.Count}).");
        var results = new Dictionary<int, string>();
        foreach (var item in batch.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string corrected;
            try
            {
                corrected = await CorrectWithCacheAsync(item.Original, llmClient, cache, cancellationToken);
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

                corrected = await CorrectWithCacheAsync(item.Original, llmClient, cache, cancellationToken);
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

    private static async Task<string> CorrectWithCacheAsync(string original, LlmClient llmClient, ConcurrentDictionary<string, string>? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.TryGetValue(original, out var cached))
        {
            return cached;
        }

        var corrected = await CorrectWithChunkingAsync(original, llmClient, cancellationToken);
        if (cache is not null && !string.IsNullOrWhiteSpace(corrected))
        {
            cache.TryAdd(original, corrected);
        }

        return corrected;
    }

    private static void ApplyBatchResult(BatchResult result, ConcurrentDictionary<string, string>? cache)
    {
        foreach (var item in result.Batch.Items)
        {
            if (!result.Corrections.TryGetValue(item.Id, out var corrected))
            {
                continue;
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
        builder.AppendLine("Correct only the text inside the tags. Return the response with the exact same tags and IDs.");
        builder.AppendLine("No extra lines outside the tags.");
        builder.AppendLine();

        foreach (var item in items)
        {
            builder.Append("<<P:");
            builder.Append(item.Id);
            builder.AppendLine(">>");
            builder.AppendLine(item.Original);
            builder.Append("<<");
            builder.Append("/P:");
            builder.Append(item.Id);
            builder.AppendLine(">>");
        }

        return builder.ToString();
    }

    private static bool TryParseBatchResponse(string response, int expectedCount, out Dictionary<int, string> results)
    {
        results = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        var matches = BatchRegex.Matches(response);
        if (matches.Count != expectedCount)
        {
            return false;
        }

        var remainder = BatchRegex.Replace(response, string.Empty);
        if (!string.IsNullOrWhiteSpace(remainder))
        {
            return false;
        }

        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups[1].Value, out var id))
            {
                return false;
            }

            if (results.ContainsKey(id))
            {
                return false;
            }

            var text = match.Groups[2].Value;
            results[id] = text.Trim();
        }

        return results.Count == expectedCount;
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
