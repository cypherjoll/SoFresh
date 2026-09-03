using SoFresh.Core.Domain;
using SoFresh.Core.Safety;

namespace SoFresh.Core.Analysis;

public sealed class DashboardAggregator(IFileSafetyPolicy? safetyPolicy = null)
{
    private readonly IFileSafetyPolicy _safetyPolicy = safetyPolicy ?? new FileSafetyPolicy();

    public DashboardSnapshot Build(
        ScanResult scan,
        DashboardSnapshot? previousSnapshot = null,
        int largestFileCount = 20,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (largestFileCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(largestFileCount));
        }

        var generatedAt = now ?? DateTimeOffset.UtcNow;
        var files = scan.Entries.Where(static entry => !entry.IsDirectory).ToArray();
        var assessed = files.Select(file => (File: file, Safety: _safetyPolicy.Evaluate(file))).ToArray();
        var totalBytes = files.Sum(static file => file.Length);
        var potentiallyRecoverable = assessed
            .Where(static item => item.Safety.Level is SafetyLevel.SafeToClean or SafetyLevel.ProbablySafe)
            .Sum(static item => item.File.Length);

        return new DashboardSnapshot(
            generatedAt,
            scan.CompletedAtUtc,
            files.LongLength,
            scan.Entries.LongCount(static entry => entry.IsDirectory),
            totalBytes,
            potentiallyRecoverable,
            previousSnapshot is null ? null : totalBytes - previousSnapshot.ScannedBytes,
            BuildVolumes(scan.ScannedRoots, files),
            BuildBuckets(files, static file => file.Classification.Category),
            BuildBuckets(files, static file => file.Classification.UserContext),
            BuildBuckets(files, static file => file.Classification.Topic),
            assessed
                .GroupBy(static item => item.Safety.Level)
                .Select(static group => new MetricBucket<SafetyLevel>(
                    group.Key,
                    group.LongCount(),
                    group.Sum(static item => item.File.Length)))
                .OrderBy(static bucket => bucket.Key)
                .ToArray(),
            BuildAgeBuckets(files, generatedAt),
            files.OrderByDescending(static file => file.Length).Take(largestFileCount).ToArray(),
            scan.Issues);
    }

    private static IReadOnlyList<MetricBucket<T>> BuildBuckets<T>(
        IEnumerable<FileEntry> files,
        Func<FileEntry, T> keySelector)
        where T : notnull =>
        files
            .GroupBy(keySelector)
            .Select(static group => new MetricBucket<T>(group.Key, group.LongCount(), group.Sum(static file => file.Length)))
            .OrderByDescending(static bucket => bucket.Bytes)
            .ToArray();

    private static IReadOnlyList<AgeBucket> BuildAgeBuckets(
        IEnumerable<FileEntry> files,
        DateTimeOffset now)
    {
        var buckets = new[]
        {
            new MutableAgeBucket("Last 30 days"),
            new MutableAgeBucket("1 to 6 months"),
            new MutableAgeBucket("6 to 12 months"),
            new MutableAgeBucket("Over 1 year"),
            new MutableAgeBucket("Unknown date")
        };

        foreach (var file in files)
        {
            var age = file.ModifiedAtUtc is null ? (TimeSpan?)null : now - file.ModifiedAtUtc.Value;
            var index = age switch
            {
                null => 4,
                { TotalDays: < 30 } => 0,
                { TotalDays: < 180 } => 1,
                { TotalDays: < 365 } => 2,
                _ => 3
            };
            buckets[index].FileCount++;
            buckets[index].Bytes += file.Length;
        }

        return buckets.Select(static bucket => new AgeBucket(bucket.Label, bucket.FileCount, bucket.Bytes)).ToArray();
    }

    private static IReadOnlyList<VolumeSnapshot> BuildVolumes(
        IEnumerable<string> roots,
        IEnumerable<FileEntry> files)
    {
        var fileArray = files.ToArray();
        return roots
            .Select(Path.GetPathRoot)
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Cast<string>()
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(root => CreateVolume(root, fileArray))
            .ToArray();
    }

    private static VolumeSnapshot CreateVolume(string root, IReadOnlyCollection<FileEntry> files)
    {
        long? total = null;
        long? free = null;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Scan data remains useful even if drive capacity is unavailable.
        }

        var matchingFiles = files.Where(file =>
            string.Equals(
                file.VolumeRoot,
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        return new VolumeSnapshot(
            root,
            total,
            free,
            matchingFiles.Sum(static file => file.Length),
            matchingFiles.LongCount());
    }

    private sealed class MutableAgeBucket(string label)
    {
        public string Label { get; } = label;

        public long FileCount { get; set; }

        public long Bytes { get; set; }
    }
}
