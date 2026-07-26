using System.Text.Json;

namespace Lingofix.Backend.Documents;

internal sealed class ProcessingCheckpoint
{
    public required string InputPath { get; init; }
    public required string CorrectedPath { get; init; }
    public required List<string> CompletedLabels { get; init; }
    public required Dictionary<string, int> CompletedBatchesByLabel { get; init; }
    public bool IsActive { get; init; }
    // Null on checkpoints written before the fingerprint existed (old format).
    public string? Fingerprint { get; init; }
}

internal static class ProcessingCheckpointStore
{
    /// <summary>
    /// Fingerprints the run configuration that produced a checkpoint's corrected temp
    /// file, so a resume never mixes e.g. a half-corrected file into a translation
    /// run (or vice versa). Not a security hash — collisions only need to be
    /// astronomically unlikely, not adversarially resistant.
    /// </summary>
    public static string ComputeFingerprint(Settings settings)
    {
        var raw = string.Join(
            "\n",
            settings.Mode.ToString(),
            settings.TargetLanguage,
            settings.Prompt,
            settings.FootnotePrompt,
            settings.Model);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static ProcessingCheckpoint? Load(string inputPath, string fingerprint, IRunLogger? logger)
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

            if (!parsed.IsActive)
            {
                return null;
            }

            if (string.IsNullOrEmpty(parsed.Fingerprint) || !string.Equals(parsed.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                logger?.Info("Checkpoint found, but run configuration (mode/language/prompt/model) changed. Starting fresh.");
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
        string fingerprint,
        IReadOnlyDictionary<string, int>? completedBatchesByLabel = null,
        bool isActive = true)
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
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            IsActive = isActive,
            Fingerprint = fingerprint
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions.Default);
        File.WriteAllText(checkpointPath, json);
    }
}
