namespace SoFresh.Core.Domain;

public enum ScanIssueKind
{
    InvalidRoot,
    AccessDenied,
    NotFound,
    PathTooLong,
    IoError,
    MetadataUnavailable,
    ReparsePointSkipped,
    CycleDetected,
    Unknown
}

public sealed record ScanIssue(
    string Path,
    ScanIssueKind Kind,
    string Message);

public sealed record ScanRequest
{
    public required IReadOnlyList<string> Roots { get; init; }

    public bool IncludeDirectories { get; init; } = true;

    public bool IncludeHidden { get; init; } = true;

    public bool IncludeSystem { get; init; } = true;

    public bool FollowReparsePoints { get; init; }

    public IReadOnlyList<string> ExcludedPaths { get; init; } = Array.Empty<string>();

    public int ProgressReportInterval { get; init; } = 128;
}

public sealed record ScanProgress(
    string CurrentPath,
    long FilesScanned,
    long DirectoriesScanned,
    long BytesDiscovered,
    int IssuesFound);

public sealed record ScanResult(
    IReadOnlyList<FileEntry> Entries,
    IReadOnlyList<ScanIssue> Issues,
    IReadOnlyList<string> ScannedRoots,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

    public long FileCount => Entries.LongCount(static entry => !entry.IsDirectory);

    public long DirectoryCount => Entries.LongCount(static entry => entry.IsDirectory);

    public long TotalFileBytes => Entries.Where(static entry => !entry.IsDirectory).Sum(static entry => entry.Length);
}
