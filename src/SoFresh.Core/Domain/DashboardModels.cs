namespace SoFresh.Core.Domain;

public sealed record MetricBucket<T>(T Key, long FileCount, long Bytes);

public sealed record AgeBucket(string Label, long FileCount, long Bytes);

public sealed record VolumeSnapshot(
    string Root,
    long? TotalBytes,
    long? FreeBytes,
    long ScannedBytes,
    long FileCount);

public sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset ScanCompletedAtUtc,
    long FileCount,
    long DirectoryCount,
    long ScannedBytes,
    long PotentiallyRecoverableBytes,
    long? ChangeSincePreviousScanBytes,
    IReadOnlyList<VolumeSnapshot> Volumes,
    IReadOnlyList<MetricBucket<FileCategory>> ByCategory,
    IReadOnlyList<MetricBucket<UserContext>> ByUserContext,
    IReadOnlyList<MetricBucket<TopicCategory>> ByTopic,
    IReadOnlyList<MetricBucket<SafetyLevel>> BySafety,
    IReadOnlyList<AgeBucket> ByAge,
    IReadOnlyList<FileEntry> LargestFiles,
    IReadOnlyList<ScanIssue> Issues);
