namespace Lingofix.Backend.Documents;

public sealed record RunOptions(
    string InputPath,
    Settings Settings,
    CompareModeKind? CompareModeOverride = null,
    bool AcceptExistingTrackChanges = false,
    SourceInputKind SourceKind = SourceInputKind.Docx,
    string? SourceOriginalPath = null);

public enum SourceInputKind
{
    Docx,
    Odt
}

public sealed record RunResult(
    string OutputPath,
    bool TrackChangesCreated,
    string? TrackChangesPath);
