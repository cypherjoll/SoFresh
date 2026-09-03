using SoFresh.Core.Classification;
using SoFresh.Core.Domain;
using SoFresh.Core.Utilities;

namespace SoFresh.Core.Scanning;

public sealed class FileSystemScanner(IFileClassifier? classifier = null) : IFileSystemScanner
{
    private readonly IFileClassifier _classifier = classifier ?? new FileClassifier();

    public Task<ScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Roots is null || request.Roots.Count == 0)
        {
            throw new ArgumentException("At least one scan root is required.", nameof(request));
        }

        if (request.ProgressReportInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The progress reporting interval must be positive.");
        }

        return Task.Run(() => ScanCore(request, progress, cancellationToken), cancellationToken);
    }

    private ScanResult ScanCore(
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var entries = new List<FileEntry>();
        var issues = new List<ScanIssue>();
        var scannedRoots = new List<string>();
        var excludedPaths = NormalizeExclusions(request.ExcludedPaths);
        var visitedDirectories = new HashSet<string>(PathUtilities.PathComparer);
        var requestedDirectoryRootPaths = new HashSet<string>(PathUtilities.PathComparer);
        var recordedEntries = new HashSet<string>(PathUtilities.PathComparer);
        var acceptedFileRoots = new HashSet<string>(PathUtilities.PathComparer);
        var pendingDirectories = new Stack<DirectoryInfo>();
        long fileCount = 0;
        long directoryCount = 0;
        long bytes = 0;
        long sinceLastProgress = 0;

        foreach (var requestedRoot in request.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PathUtilities.TryNormalize(requestedRoot, out var root))
            {
                issues.Add(new ScanIssue(requestedRoot, ScanIssueKind.InvalidRoot, "Invalid path."));
                continue;
            }

            if (IsExcluded(root, excludedPaths))
            {
                continue;
            }

            if (File.Exists(root))
            {
                if (!acceptedFileRoots.Add(root))
                {
                    continue;
                }

                scannedRoots.Add(root);
                TryAddEntry(new FileInfo(root));
                continue;
            }

            if (!Directory.Exists(root))
            {
                issues.Add(new ScanIssue(root, ScanIssueKind.NotFound, "The root does not exist or cannot be accessed."));
                continue;
            }

            var rootDirectory = new DirectoryInfo(root);
            if (TryGetTraversalIdentity(rootDirectory, request.FollowReparsePoints, out var identity, out var identityIssue))
            {
                requestedDirectoryRootPaths.Add(root);
                if (!visitedDirectories.Add(identity))
                {
                    continue;
                }

                scannedRoots.Add(root);
                pendingDirectories.Push(rootDirectory);
                if (request.IncludeDirectories)
                {
                    TryAddEntry(rootDirectory);
                }
            }
            else if (identityIssue is not null)
            {
                issues.Add(identityIssue);
            }
        }

        while (pendingDirectories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            directoryCount++;
            ReportIfNeeded(directory.FullName, force: false);

            IEnumerator<FileSystemInfo>? enumerator = null;
            try
            {
                enumerator = directory.EnumerateFileSystemInfos(
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        ReturnSpecialDirectories = false,
                        AttributesToSkip = 0
                    }).GetEnumerator();

                while (MoveNext(enumerator, directory.FullName, issues, out var child))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child is null || IsExcluded(child.FullName, excludedPaths))
                    {
                        continue;
                    }

                    FileAttributes attributes;
                    try
                    {
                        child.Refresh();
                        attributes = child.Attributes;
                    }
                    catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                    {
                        issues.Add(CreateIssue(child.FullName, exception, ScanIssueKind.MetadataUnavailable));
                        continue;
                    }

                    if ((!request.IncludeHidden && attributes.HasFlag(FileAttributes.Hidden))
                        || (!request.IncludeSystem && attributes.HasFlag(FileAttributes.System)))
                    {
                        continue;
                    }

                    if (child is DirectoryInfo childDirectory)
                    {
                        if (request.IncludeDirectories)
                        {
                            TryAddEntry(childDirectory);
                        }

                        if (attributes.HasFlag(FileAttributes.ReparsePoint) && !request.FollowReparsePoints)
                        {
                            issues.Add(new ScanIssue(
                                childDirectory.FullName,
                                ScanIssueKind.ReparsePointSkipped,
                                "Reparse point detected: traversal is disabled for safety."));
                            continue;
                        }

                        if (!TryGetTraversalIdentity(childDirectory, request.FollowReparsePoints, out var childIdentity, out var traversalIssue))
                        {
                            if (traversalIssue is not null)
                            {
                                issues.Add(traversalIssue);
                            }

                            continue;
                        }

                        if (!visitedDirectories.Add(childIdentity))
                        {
                            if (!requestedDirectoryRootPaths.Contains(PathUtilities.Normalize(childDirectory.FullName)))
                            {
                                issues.Add(new ScanIssue(
                                    childDirectory.FullName,
                                    ScanIssueKind.CycleDetected,
                                    "Directory already visited; a junction or symbolic link may have introduced a cycle."));
                            }

                            continue;
                        }

                        pendingDirectories.Push(childDirectory);
                    }
                    else
                    {
                        TryAddEntry(child);
                    }
                }
            }
            catch (Exception exception) when (IsRecoverableFileSystemException(exception))
            {
                issues.Add(CreateIssue(directory.FullName, exception));
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        ReportIfNeeded(scannedRoots.LastOrDefault() ?? string.Empty, force: true);
        return new ScanResult(entries, issues, scannedRoots, startedAt, DateTimeOffset.UtcNow);

        void TryAddEntry(FileSystemInfo info)
        {
            try
            {
                var entry = CreateEntry(info);
                if (!recordedEntries.Add(entry.FullPath))
                {
                    return;
                }

                entries.Add(entry);
                if (!entry.IsDirectory)
                {
                    fileCount++;
                    bytes += entry.Length;
                }

                sinceLastProgress++;
                ReportIfNeeded(entry.FullPath, force: false);
            }
            catch (Exception exception) when (IsRecoverableFileSystemException(exception))
            {
                issues.Add(CreateIssue(info.FullName, exception, ScanIssueKind.MetadataUnavailable));
            }
        }

        FileEntry CreateEntry(FileSystemInfo info)
        {
            info.Refresh();
            var attributes = info.Attributes;
            var isDirectory = info is DirectoryInfo;
            var length = info is FileInfo fileInfo ? fileInfo.Length : 0;
            return new FileEntry
            {
                FullPath = PathUtilities.Normalize(info.FullName),
                Name = info.Name,
                Kind = isDirectory ? FileEntryKind.Directory : FileEntryKind.File,
                Extension = isDirectory ? string.Empty : info.Extension,
                Length = length,
                CreatedAtUtc = ToTimestamp(info.CreationTimeUtc),
                ModifiedAtUtc = ToTimestamp(info.LastWriteTimeUtc),
                LastAccessAtUtc = ToTimestamp(info.LastAccessTimeUtc),
                Attributes = attributes,
                VolumeRoot = Path.GetPathRoot(info.FullName),
                Classification = _classifier.Classify(info.FullName, attributes, isDirectory)
            };
        }

        void ReportIfNeeded(string currentPath, bool force)
        {
            if (progress is null || (!force && sinceLastProgress < request.ProgressReportInterval))
            {
                return;
            }

            sinceLastProgress = 0;
            progress.Report(new ScanProgress(currentPath, fileCount, directoryCount, bytes, issues.Count));
        }
    }

    private static IReadOnlyList<string> NormalizeExclusions(IReadOnlyList<string>? exclusions)
    {
        if (exclusions is null || exclusions.Count == 0)
        {
            return Array.Empty<string>();
        }

        return exclusions
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathUtilities.TryNormalize(path, out var normalized) ? normalized : null)
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(PathUtilities.PathComparer)
            .ToArray();
    }

    private static bool IsExcluded(string path, IReadOnlyList<string> excludedPaths) =>
        excludedPaths.Any(excluded => PathUtilities.IsSameOrDescendant(path, excluded));

    private static bool TryGetTraversalIdentity(
        DirectoryInfo directory,
        bool followReparsePoints,
        out string identity,
        out ScanIssue? issue)
    {
        identity = string.Empty;
        issue = null;
        try
        {
            directory.Refresh();
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (!followReparsePoints)
                {
                    issue = new ScanIssue(
                        directory.FullName,
                        ScanIssueKind.ReparsePointSkipped,
                        "The root is a reparse point and will not be traversed.");
                    return false;
                }

                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                identity = PathUtilities.Normalize(target?.FullName ?? directory.FullName);
                return true;
            }

            identity = PathUtilities.Normalize(directory.FullName);
            return true;
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            issue = CreateIssue(directory.FullName, exception);
            return false;
        }
    }

    private static bool MoveNext(
        IEnumerator<FileSystemInfo> enumerator,
        string directoryPath,
        ICollection<ScanIssue> issues,
        out FileSystemInfo? current)
    {
        try
        {
            if (enumerator.MoveNext())
            {
                current = enumerator.Current;
                return true;
            }
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            issues.Add(CreateIssue(directoryPath, exception));
        }

        current = null;
        return false;
    }

    private static DateTimeOffset? ToTimestamp(DateTime value) =>
        value == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static ScanIssue CreateIssue(
        string path,
        Exception exception,
        ScanIssueKind fallback = ScanIssueKind.Unknown) =>
        new(path, exception switch
        {
            UnauthorizedAccessException => ScanIssueKind.AccessDenied,
            DirectoryNotFoundException or FileNotFoundException => ScanIssueKind.NotFound,
            PathTooLongException => ScanIssueKind.PathTooLong,
            IOException => ScanIssueKind.IoError,
            _ => fallback
        }, exception.Message);

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;
}
