namespace SoFresh.Core.Domain;

public sealed record DuplicateSearchOptions
{
    public long MinimumFileSize { get; init; } = 1;

    public int SampleSizeBytes { get; init; } = 64 * 1024;

    public int BufferSizeBytes { get; init; } = 128 * 1024;
}

public sealed record DuplicateProgress(
    string Stage,
    int CandidateFiles,
    int ProcessedFiles,
    string? CurrentPath);

public sealed record DuplicateIssue(string Path, string Message);

public sealed record DuplicateFile(
    FileEntry Entry,
    string? PhysicalFileIdentity,
    bool IsHardLinkAlias);

public sealed record DuplicateGroup(
    string Sha256,
    long FileSize,
    IReadOnlyList<DuplicateFile> Files,
    long RecoverableBytes);

public sealed record DuplicateSearchResult(
    IReadOnlyList<DuplicateGroup> Groups,
    IReadOnlyList<DuplicateIssue> Issues,
    DateTimeOffset CompletedAtUtc)
{
    public long RecoverableBytes => Groups.Sum(static group => group.RecoverableBytes);
}
