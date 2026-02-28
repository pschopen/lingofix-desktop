using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lingofix.Backend.Documents;

internal static class MarkdownProcessor
{
    private const string KeepTempArtifactsEnv = "LINGOFIX_KEEP_TEMP_ARTIFACTS";

    public static async Task<string> CorrectAsync(
        string markdown,
        int chunkSize,
        LlmClient llmClient,
        IRunLogger? logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var effectiveChunkSize = Math.Clamp(chunkSize, Settings.MinChunkSize, Settings.MaxChunkSize);
        try
        {
            return await CorrectWithPandocAstAsync(markdown, effectiveChunkSize, llmClient, logger, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.Warning($"Markdown AST chunking failed, falling back to legacy chunking: {ex.Message}");
            return await CorrectWithLegacyChunkingAsync(markdown, effectiveChunkSize, llmClient, logger, cancellationToken);
        }
    }

    private static async Task<string> CorrectWithPandocAstAsync(
        string markdown,
        int chunkSize,
        LlmClient llmClient,
        IRunLogger? logger,
        CancellationToken cancellationToken)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "Lingofix", "markdown-ast", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var sourceMarkdownPath = Path.Combine(workDir, "source.md");
            var sourceJsonPath = Path.Combine(workDir, "source.json");
            await File.WriteAllTextAsync(sourceMarkdownPath, markdown, cancellationToken);
            PandocMarkdownConverter.ConvertMarkdownToPandocJson(sourceMarkdownPath, sourceJsonPath);

            var sourceDoc = await LoadPandocDocAsync(sourceJsonPath, cancellationToken);
            var chunks = await BuildAstChunksByMarkdownLengthAsync(
                sourceDoc.Blocks,
                sourceDoc.ApiVersion,
                sourceDoc.Meta,
                chunkSize,
                workDir,
                logger,
                cancellationToken);
            if (chunks.Count == 0)
            {
                return markdown;
            }

            logger?.Info($"Markdown AST mode: processing {chunks.Count} chunk(s) with chunk size {chunkSize}.");
            var correctedBlocks = new JsonArray();

            for (var i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = chunks[i];
                var chunkPrefix = Path.Combine(workDir, $"chunk-{i + 1:0000}");
                var chunkSourceJsonPath = $"{chunkPrefix}-source.json";
                var chunkSourceMarkdownPath = $"{chunkPrefix}-source.md";
                var chunkCorrectedMarkdownPath = $"{chunkPrefix}-corrected.md";
                var chunkCorrectedJsonPath = $"{chunkPrefix}-corrected.json";

                await SavePandocDocAsync(chunkSourceJsonPath, sourceDoc.ApiVersion, sourceDoc.Meta, chunk.Blocks, cancellationToken);
                PandocMarkdownConverter.ConvertPandocJsonToMarkdown(chunkSourceJsonPath, chunkSourceMarkdownPath);

                var chunkMarkdown = await File.ReadAllTextAsync(chunkSourceMarkdownPath, cancellationToken);
                var correctedChunkMarkdown = await llmClient.CorrectMarkdownAsync(chunkMarkdown, cancellationToken);
                if (string.IsNullOrWhiteSpace(correctedChunkMarkdown))
                {
                    correctedChunkMarkdown = chunkMarkdown;
                }

                await File.WriteAllTextAsync(chunkCorrectedMarkdownPath, correctedChunkMarkdown, cancellationToken);
                PandocMarkdownConverter.ConvertMarkdownToPandocJson(chunkCorrectedMarkdownPath, chunkCorrectedJsonPath);
                var correctedChunkDoc = await LoadPandocDocAsync(chunkCorrectedJsonPath, cancellationToken);
                AppendBlocks(correctedBlocks, correctedChunkDoc.Blocks);

                logger?.Progress(
                    (int)Math.Round(((i + 1d) / chunks.Count) * 100d),
                    $"Markdown AST chunk {i + 1}/{chunks.Count} ({chunk.MarkdownLength} chars)");
            }

            var mergedJsonPath = Path.Combine(workDir, "merged.json");
            var mergedMarkdownPath = Path.Combine(workDir, "merged.md");
            await SavePandocDocAsync(mergedJsonPath, sourceDoc.ApiVersion, sourceDoc.Meta, correctedBlocks, cancellationToken);
            PandocMarkdownConverter.ConvertPandocJsonToMarkdown(mergedJsonPath, mergedMarkdownPath);
            return await File.ReadAllTextAsync(mergedMarkdownPath, cancellationToken);
        }
        finally
        {
            if (!ShouldKeepTempArtifacts())
            {
                TryDeleteDirectory(workDir);
            }
            else
            {
                logger?.Info($"Markdown AST temp artifacts kept at: {workDir}");
            }
        }
    }

    private static async Task<string> CorrectWithLegacyChunkingAsync(
        string markdown,
        int chunkSize,
        LlmClient llmClient,
        IRunLogger? logger,
        CancellationToken cancellationToken)
    {
        var chunks = SplitIntoChunks(markdown, chunkSize);
        if (chunks.Count == 0)
        {
            return markdown;
        }

        logger?.Info($"Markdown legacy mode: processing {chunks.Count} chunk(s) with chunk size {chunkSize}.");
        var result = new StringBuilder(markdown.Length + Math.Max(0, chunks.Count * 8));
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            var corrected = await llmClient.CorrectMarkdownAsync(chunk, cancellationToken);
            if (string.IsNullOrWhiteSpace(corrected))
            {
                corrected = chunk;
            }

            result.Append(corrected);
            logger?.Progress((int)Math.Round(((i + 1d) / chunks.Count) * 100d), $"Markdown chunk {i + 1}/{chunks.Count}");
        }

        return result.ToString();
    }

    private static async Task<List<AstChunk>> BuildAstChunksByMarkdownLengthAsync(
        JsonArray blocks,
        JsonNode apiVersion,
        JsonNode meta,
        int chunkSize,
        string workDir,
        IRunLogger? logger,
        CancellationToken cancellationToken)
    {
        var blockList = blocks
            .Select(block => block?.DeepClone())
            .Where(block => block is not null)
            .Cast<JsonNode>()
            .ToList();

        var chunks = new List<AstChunk>();
        if (blockList.Count == 0)
        {
            return chunks;
        }

        var lengthCache = new Dictionary<(int Start, int End), int>();
        var start = 0;

        while (start < blockList.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var singleLength = await MeasureRangeMarkdownLengthAsync(
                start,
                start,
                blockList,
                apiVersion,
                meta,
                workDir,
                lengthCache,
                cancellationToken);

            if (singleLength > chunkSize)
            {
                logger?.Warning($"Markdown AST chunk {start + 1} exceeds chunk size alone ({singleLength}>{chunkSize}). Keeping as single-block chunk.");
                chunks.Add(new AstChunk(CreateBlocksSlice(blockList, start, start), singleLength));
                start++;
                continue;
            }

            var low = start;
            var high = blockList.Count - 1;
            var bestEnd = start;
            var bestLength = singleLength;

            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                var length = await MeasureRangeMarkdownLengthAsync(
                    start,
                    mid,
                    blockList,
                    apiVersion,
                    meta,
                    workDir,
                    lengthCache,
                    cancellationToken);

                if (length <= chunkSize)
                {
                    bestEnd = mid;
                    bestLength = length;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            chunks.Add(new AstChunk(CreateBlocksSlice(blockList, start, bestEnd), bestLength));
            start = bestEnd + 1;
        }

        return chunks;
    }

    private static async Task<int> MeasureRangeMarkdownLengthAsync(
        int start,
        int end,
        IReadOnlyList<JsonNode> blockList,
        JsonNode apiVersion,
        JsonNode meta,
        string workDir,
        IDictionary<(int Start, int End), int> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((start, end), out var cached))
        {
            return cached;
        }

        var probePrefix = Path.Combine(workDir, $"probe-{start:0000}-{end:0000}");
        var probeJsonPath = $"{probePrefix}.json";
        var probeMarkdownPath = $"{probePrefix}.md";
        var slice = CreateBlocksSlice(blockList, start, end);
        await SavePandocDocAsync(probeJsonPath, apiVersion, meta, slice, cancellationToken);
        PandocMarkdownConverter.ConvertPandocJsonToMarkdown(probeJsonPath, probeMarkdownPath);
        var markdown = await File.ReadAllTextAsync(probeMarkdownPath, cancellationToken);
        var length = markdown.Length;
        cache[(start, end)] = length;
        return length;
    }

    private static JsonArray CreateBlocksSlice(IReadOnlyList<JsonNode> blockList, int start, int end)
    {
        var array = new JsonArray();
        for (var i = start; i <= end; i++)
        {
            array.Add(blockList[i].DeepClone());
        }

        return array;
    }

    private static void AppendBlocks(JsonArray destination, JsonArray source)
    {
        foreach (var block in source)
        {
            if (block is null)
            {
                continue;
            }

            destination.Add(block.DeepClone());
        }
    }

    private static async Task<PandocDoc> LoadPandocDocAsync(string jsonPath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(jsonPath, cancellationToken);
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"Invalid Pandoc JSON document: {jsonPath}");
        var blocks = node["blocks"] as JsonArray
            ?? throw new InvalidOperationException($"Pandoc JSON missing blocks array: {jsonPath}");
        var apiVersion = node["pandoc-api-version"]?.DeepClone() ?? new JsonArray();
        var meta = node["meta"]?.DeepClone() ?? new JsonObject();
        return new PandocDoc(apiVersion, meta, blocks);
    }

    private static async Task SavePandocDocAsync(
        string jsonPath,
        JsonNode apiVersion,
        JsonNode meta,
        JsonArray blocks,
        CancellationToken cancellationToken)
    {
        var doc = new JsonObject
        {
            ["pandoc-api-version"] = apiVersion.DeepClone(),
            ["meta"] = meta.DeepClone(),
            ["blocks"] = blocks.DeepClone()
        };

        var json = doc.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        });
        await File.WriteAllTextAsync(jsonPath, json, cancellationToken);
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
        for (var i = limit - 1; i > start; i--)
        {
            if (i + 1 < text.Length && text[i] == '\n' && text[i + 1] == '\n')
            {
                return i + 2;
            }
        }

        for (var i = limit - 1; i > start; i--)
        {
            if (text[i] == '\n')
            {
                return i + 1;
            }
        }

        for (var i = limit - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return end;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
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

    private static bool ShouldKeepTempArtifacts()
    {
        var value = Environment.GetEnvironmentVariable(KeepTempArtifactsEnv);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AstChunk(JsonArray Blocks, int MarkdownLength);
    private sealed record PandocDoc(JsonNode ApiVersion, JsonNode Meta, JsonArray Blocks);
}
