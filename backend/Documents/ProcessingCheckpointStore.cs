using System.Text.Json;

namespace Lingofix.Backend.Documents;

internal sealed class ProcessingCheckpoint
{
    public required string InputPath { get; init; }
    public required string CorrectedPath { get; init; }
    public required List<string> CompletedLabels { get; init; }
    public required Dictionary<string, int> CompletedBatchesByLabel { get; init; }
}

internal static class ProcessingCheckpointStore
{
    public static ProcessingCheckpoint? Load(string inputPath, IRunLogger? logger)
    {
        var checkpointPath = PathUtils.BuildCheckpointPath(inputPath);
        if (!File.Exists(checkpointPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(checkpointPath);
            var parsed = JsonSerializer.Deserialize<ProcessingCheckpoint>(json, JsonOptions.Default);
            if (parsed is null)
            {
                return null;
            }

            if (!string.Equals(parsed.InputPath, inputPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!File.Exists(parsed.CorrectedPath))
            {
                logger?.Info("Checkpoint found, but corrected temp file is missing. Starting fresh.");
                return null;
            }

            return parsed;
        }
        catch
        {
            logger?.Info("Checkpoint could not be read. Starting fresh.");
            return null;
        }
    }

    public static void Save(
        string inputPath,
        string correctedPath,
        IEnumerable<string> completedLabels,
        IReadOnlyDictionary<string, int>? completedBatchesByLabel = null)
    {
        var checkpointPath = PathUtils.BuildCheckpointPath(inputPath);
        var payload = new ProcessingCheckpoint
        {
            InputPath = inputPath,
            CorrectedPath = correctedPath,
            CompletedLabels = completedLabels.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList(),
            CompletedBatchesByLabel = completedBatchesByLabel is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : completedBatchesByLabel
                    .Where(kvp => kvp.Value > 0)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);
        File.WriteAllText(checkpointPath, json);
    }

    public static void Delete(string inputPath)
    {
        var checkpointPath = PathUtils.BuildCheckpointPath(inputPath);
        if (!File.Exists(checkpointPath))
        {
            return;
        }

        File.Delete(checkpointPath);
    }
}
