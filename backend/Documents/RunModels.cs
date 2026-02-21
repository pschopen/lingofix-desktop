namespace Lingofix.Backend.Documents;

public sealed record RunOptions(
    string InputPath,
    Settings Settings,
    CompareModeKind? CompareModeOverride = null);

public sealed record RunResult(
    string OutputPath,
    bool TrackChangesCreated,
    string? TrackChangesPath);
