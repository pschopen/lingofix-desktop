using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// A checkpoint is only resumable if it was written under the same run configuration
/// (mode/target language/prompts/model). Otherwise a half-corrected file could be
/// resumed as a translation (or vice versa), silently mixing outputs.
/// </summary>
public class ProcessingCheckpointStoreTests : IDisposable
{
    private readonly List<string> _inputPaths = [];

    private string NewInputPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lingofix-checkpoint-test-{Guid.NewGuid():N}.docx");
        _inputPaths.Add(path);
        return path;
    }

    private static Settings BuildSettings(OperationMode mode = OperationMode.Correction, string targetLanguage = "", string prompt = "Fix it", string footnotePrompt = "", string model = "mistral-large")
    {
        return new Settings
        {
            Mode = mode,
            TargetLanguage = targetLanguage,
            Prompt = prompt,
            FootnotePrompt = footnotePrompt,
            Model = model
        };
    }

    [Fact]
    public void Matching_Fingerprint_Resumes()
    {
        var inputPath = NewInputPath();
        var correctedPath = Path.GetTempFileName();
        try
        {
            var settings = BuildSettings();
            var fingerprint = ProcessingCheckpointStore.ComputeFingerprint(settings);
            ProcessingCheckpointStore.Save(inputPath, correctedPath, ["Main"], fingerprint);

            var loaded = ProcessingCheckpointStore.Load(inputPath, fingerprint, NullRunLogger.Instance);

            Assert.NotNull(loaded);
            Assert.Equal(correctedPath, loaded!.CorrectedPath);
            Assert.Contains("Main", loaded.CompletedLabels);
        }
        finally
        {
            File.Delete(correctedPath);
        }
    }

    [Fact]
    public void Mismatched_Fingerprint_Starts_Fresh()
    {
        var inputPath = NewInputPath();
        var correctedPath = Path.GetTempFileName();
        try
        {
            var correctionFingerprint = ProcessingCheckpointStore.ComputeFingerprint(BuildSettings());
            ProcessingCheckpointStore.Save(inputPath, correctedPath, ["Main"], correctionFingerprint);

            var translationFingerprint = ProcessingCheckpointStore.ComputeFingerprint(
                BuildSettings(mode: OperationMode.Translation, targetLanguage: "en"));
            var loaded = ProcessingCheckpointStore.Load(inputPath, translationFingerprint, NullRunLogger.Instance);

            Assert.Null(loaded);
        }
        finally
        {
            File.Delete(correctedPath);
        }
    }

    [Fact]
    public void Old_Checkpoint_Format_Without_Fingerprint_Starts_Fresh()
    {
        var inputPath = NewInputPath();
        var correctedPath = Path.GetTempFileName();
        try
        {
            // Simulate a checkpoint written before the fingerprint field existed: save
            // with an empty fingerprint (as the old writer effectively did) and confirm
            // the loader treats an empty/missing fingerprint as "no match".
            ProcessingCheckpointStore.Save(inputPath, correctedPath, ["Main"], fingerprint: "");

            var fingerprint = ProcessingCheckpointStore.ComputeFingerprint(BuildSettings());
            var loaded = ProcessingCheckpointStore.Load(inputPath, fingerprint, NullRunLogger.Instance);

            Assert.Null(loaded);
        }
        finally
        {
            File.Delete(correctedPath);
        }
    }

    [Fact]
    public void Fingerprint_Differs_By_Mode_Language_Prompts_And_Model()
    {
        var baseline = ProcessingCheckpointStore.ComputeFingerprint(BuildSettings());
        Assert.NotEqual(baseline, ProcessingCheckpointStore.ComputeFingerprint(BuildSettings(mode: OperationMode.Translation, targetLanguage: "en")));
        Assert.NotEqual(baseline, ProcessingCheckpointStore.ComputeFingerprint(BuildSettings(targetLanguage: "fr")));
        Assert.NotEqual(baseline, ProcessingCheckpointStore.ComputeFingerprint(BuildSettings(prompt: "Different prompt")));
        Assert.NotEqual(baseline, ProcessingCheckpointStore.ComputeFingerprint(BuildSettings(footnotePrompt: "Footnote prompt")));
        Assert.NotEqual(baseline, ProcessingCheckpointStore.ComputeFingerprint(BuildSettings(model: "other-model")));
    }

    public void Dispose()
    {
        foreach (var inputPath in _inputPaths)
        {
            try
            {
                File.Delete(PathUtils.BuildCheckpointPath(inputPath));
            }
            catch
            {
            }
        }
    }
}
