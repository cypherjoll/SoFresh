using SoFresh.Core.Domain;
using SoFresh.Core.Safety;
using SoFresh.Core.Utilities;

namespace SoFresh.Core.Operations;

public sealed class SafeFileOperations(IFileSafetyPolicy? safetyPolicy = null) : ISafeFileOperations
{
    private readonly IFileSafetyPolicy _safetyPolicy = safetyPolicy ?? new FileSafetyPolicy();

    public Task<FileOperationPlan> PlanMoveAsync(
        IEnumerable<MoveSpecification> moves,
        FileConflictResolution conflictResolution = FileConflictResolution.Rename,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var specifications = moves.ToArray();
        return Task.Run(
            () => BuildPlan(Guid.NewGuid(), FileOperationKind.Move, specifications, conflictResolution, cancellationToken),
            cancellationToken);
    }

    public Task<FileOperationPlan> PlanQuarantineAsync(
        IEnumerable<string> sourcePaths,
        string quarantineRoot,
        FileConflictResolution conflictResolution = FileConflictResolution.Rename,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        if (!PathUtilities.TryNormalize(quarantineRoot, out var normalizedRoot))
        {
            throw new ArgumentException("The quarantine root is invalid.", nameof(quarantineRoot));
        }

        var operationId = Guid.NewGuid();
        var operationRoot = Path.Combine(normalizedRoot, operationId.ToString("N"));
        var specifications = sourcePaths.Select((source, index) =>
            new MoveSpecification(
                source,
                Path.Combine(operationRoot, $"{index + 1:D6}_{SanitizeName(Path.GetFileName(source))}")))
            .ToArray();

        return Task.Run(
            () => BuildPlan(operationId, FileOperationKind.Quarantine, specifications, conflictResolution, cancellationToken),
            cancellationToken);
    }

    public Task<FileOperationPlan> PlanPermanentDeleteAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var specifications = sourcePaths.Select(static source => new MoveSpecification(source, string.Empty)).ToArray();
        return Task.Run(
            () => BuildPlan(
                Guid.NewGuid(),
                FileOperationKind.PermanentDelete,
                specifications,
                FileConflictResolution.Skip,
                cancellationToken),
            cancellationToken);
    }

    public Task<FileOperationReceipt> ExecuteAsync(
        FileOperationPlan plan,
        FileOperationExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new FileOperationExecutionOptions();
        ValidateExecutionAuthorization(plan.Kind, plan.ConflictResolution, options);

        return Task.Run(() => ExecuteCore(plan, options, cancellationToken), cancellationToken);
    }

    public Task<FileOperationReceipt> UndoAsync(
        FileOperationReceipt receipt,
        FileOperationExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsReversible)
        {
            throw new InvalidOperationException("The receipt does not describe an executed operation that can be reversed.");
        }

        var reverseMoves = receipt.Items
            .Where(static item => item.Status == FileOperationItemStatus.Completed && item.DestinationPath is not null)
            .Select(static item => new MoveSpecification(item.DestinationPath!, item.SourcePath))
            .ToArray();
        var plan = BuildPlan(
            Guid.NewGuid(),
            FileOperationKind.Restore,
            reverseMoves,
            FileConflictResolution.Skip,
            cancellationToken);
        return ExecuteAsync(plan, options, cancellationToken);
    }

    private FileOperationPlan BuildPlan(
        Guid operationId,
        FileOperationKind kind,
        IReadOnlyList<MoveSpecification> specifications,
        FileConflictResolution conflictResolution,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(conflictResolution))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictResolution));
        }

        var items = new List<FileOperationPlanItem>(specifications.Count);
        var reservedDestinations = new HashSet<string>(PathUtilities.PathComparer);
        var plannedSources = new HashSet<string>(PathUtilities.PathComparer);
        long totalBytes = 0;

        foreach (var specification in specifications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PathUtilities.TryNormalize(specification.SourcePath, out var source))
            {
                items.Add(InvalidPlanItem(specification.SourcePath, "Invalid source path."));
                continue;
            }

            if (!TryCaptureSnapshot(source, out var snapshot, out var snapshotError))
            {
                items.Add(new FileOperationPlanItem(
                    source,
                    null,
                    EmptySnapshot,
                    _safetyPolicy.Evaluate(source),
                    FileOperationItemStatus.Blocked,
                    snapshotError));
                continue;
            }

            if (!plannedSources.Add(source))
            {
                items.Add(new FileOperationPlanItem(
                    source,
                    null,
                    snapshot,
                    _safetyPolicy.Evaluate(source, snapshot.Attributes),
                    FileOperationItemStatus.Blocked,
                    "The same item appears more than once in the plan."));
                continue;
            }

            if (HasReparsePointInExistingPath(source, out var unsafeSourcePart))
            {
                items.Add(new FileOperationPlanItem(
                    source,
                    null,
                    snapshot,
                    _safetyPolicy.Evaluate(source, snapshot.Attributes),
                    FileOperationItemStatus.Blocked,
                    $"The path traverses a reparse point ({unsafeSourcePart})."));
                continue;
            }

            var assessment = _safetyPolicy.Evaluate(source, snapshot.Attributes);
            if (!assessment.DirectOperationAllowed || assessment.Level == SafetyLevel.Protected)
            {
                items.Add(new FileOperationPlanItem(
                    source,
                    null,
                    snapshot,
                    assessment,
                    FileOperationItemStatus.Blocked,
                    assessment.Reason));
                continue;
            }

            string? destination = null;
            string? message = null;
            var status = FileOperationItemStatus.Planned;
            if (kind != FileOperationKind.PermanentDelete)
            {
                if (!PathUtilities.TryNormalize(specification.DestinationPath, out destination))
                {
                    items.Add(new FileOperationPlanItem(
                        source,
                        specification.DestinationPath,
                        snapshot,
                        assessment,
                        FileOperationItemStatus.Blocked,
                        "Invalid destination path."));
                    continue;
                }

                if (PathUtilities.PathComparer.Equals(source, destination))
                {
                    items.Add(new FileOperationPlanItem(
                        source,
                        destination,
                        snapshot,
                        assessment,
                        FileOperationItemStatus.Skipped,
                        "The source and destination are the same."));
                    continue;
                }

                if (snapshot.IsDirectory && PathUtilities.IsSameOrDescendant(destination, source))
                {
                    items.Add(new FileOperationPlanItem(
                        source,
                        destination,
                        snapshot,
                        assessment,
                        FileOperationItemStatus.Blocked,
                        "A directory cannot be moved into itself."));
                    continue;
                }

                if (HasReparsePointInExistingPath(destination, out var unsafeDestinationPart))
                {
                    items.Add(new FileOperationPlanItem(
                        source,
                        destination,
                        snapshot,
                        assessment,
                        FileOperationItemStatus.Blocked,
                        $"The destination traverses a reparse point ({unsafeDestinationPart})."));
                    continue;
                }

                var destinationAssessment = _safetyPolicy.Evaluate(destination);
                if (destinationAssessment.Level == SafetyLevel.Protected)
                {
                    items.Add(new FileOperationPlanItem(
                        source,
                        destination,
                        snapshot,
                        assessment,
                        FileOperationItemStatus.Blocked,
                        "The destination is within a protected path."));
                    continue;
                }

                var destinationExists = Exists(destination) || reservedDestinations.Contains(destination);
                if (destinationExists)
                {
                    switch (conflictResolution)
                    {
                        case FileConflictResolution.Skip:
                            status = FileOperationItemStatus.Skipped;
                            message = "The destination already exists.";
                            break;
                        case FileConflictResolution.Rename:
                            destination = FindAvailableDestination(destination, reservedDestinations);
                            message = "The name was changed in the preview to avoid a conflict.";
                            break;
                        case FileConflictResolution.Replace:
                            status = FileOperationItemStatus.Blocked;
                            message = "Replacement is disabled until a transactional backup of the destination is available.";
                            break;
                    }
                }

                if (status == FileOperationItemStatus.Planned)
                {
                    reservedDestinations.Add(destination);
                }
            }

            if (snapshot.Length is > 0)
            {
                totalBytes = SaturatingAdd(totalBytes, snapshot.Length.Value);
            }

            items.Add(new FileOperationPlanItem(source, destination, snapshot, assessment, status, message));
        }

        return new FileOperationPlan(
            operationId,
            kind,
            DateTimeOffset.UtcNow,
            items,
            totalBytes,
            conflictResolution);
    }

    private FileOperationReceipt ExecuteCore(
        FileOperationPlan plan,
        FileOperationExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var planItems = plan.Items.ToArray();
        var results = new List<FileOperationItemResult>(planItems.Length);
        var executionSources = new HashSet<string>(PathUtilities.PathComparer);
        var executionDestinations = new HashSet<string>(PathUtilities.PathComparer);
        var wasCancelled = false;

        for (var index = 0; index < planItems.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
                for (var remainingIndex = index; remainingIndex < planItems.Length; remainingIndex++)
                {
                    var remaining = planItems[remainingIndex];
                    results.Add(new FileOperationItemResult(
                        remaining.SourcePath,
                        remaining.DestinationPath,
                        FileOperationItemStatus.Skipped,
                        "The operation was canceled before this item was processed."));
                }

                break;
            }

            var item = planItems[index];
            if (item.Status is FileOperationItemStatus.Blocked or FileOperationItemStatus.Skipped)
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    item.Status,
                    item.Message ?? "This item cannot be processed."));
                continue;
            }

            if (item.Status != FileOperationItemStatus.Planned)
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.Blocked,
                    "The plan contains a state that cannot be executed."));
                continue;
            }

            if (!PathUtilities.TryNormalize(item.SourcePath, out var normalizedSource)
                || !PathUtilities.PathComparer.Equals(normalizedSource, item.SourcePath)
                || !executionSources.Add(normalizedSource))
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.Blocked,
                    "The source in the plan is not canonical or appears more than once."));
                continue;
            }

            string? normalizedDestination = null;
            if (plan.Kind != FileOperationKind.PermanentDelete)
            {
                if (item.DestinationPath is null
                    || !PathUtilities.TryNormalize(item.DestinationPath, out normalizedDestination)
                    || !PathUtilities.PathComparer.Equals(normalizedDestination, item.DestinationPath)
                    || !executionDestinations.Add(normalizedDestination))
                {
                    results.Add(new FileOperationItemResult(
                        item.SourcePath,
                        item.DestinationPath,
                        FileOperationItemStatus.Blocked,
                        "The destination in the plan is not canonical or appears more than once."));
                    continue;
                }
            }

            if (options.DryRun)
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.PreviewOnly,
                    "Preview: no changes were made."));
                continue;
            }

            var currentAssessment = _safetyPolicy.Evaluate(item.SourcePath);
            if (!currentAssessment.DirectOperationAllowed || currentAssessment.Level == SafetyLevel.Protected)
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.Blocked,
                    "The final safety check blocked this item immediately before the action."));
                continue;
            }

            if (plan.Kind != FileOperationKind.Restore
                && currentAssessment.Level == SafetyLevel.ReviewRequired
                && !options.AllowReviewRequiredItems)
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.Blocked,
                    "This item requires explicit manual review."));
                continue;
            }

            if (!TryCaptureSnapshot(item.SourcePath, out var currentSnapshot, out var snapshotError))
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.Failed,
                    snapshotError ?? "The source is unavailable."));
                continue;
            }

            if (HasReparsePointInExistingPath(normalizedSource, out _)
                || (normalizedDestination is not null && HasReparsePointInExistingPath(normalizedDestination, out _)))
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.Blocked,
                    "A reparse point appeared in the path after the preview was created."));
                continue;
            }

            if (normalizedDestination is not null)
            {
                FileAttributes? destinationAttributes = TryGetExistingAttributes(normalizedDestination, out var existingAttributes)
                    ? existingAttributes
                    : null;
                var destinationAssessment = _safetyPolicy.Evaluate(normalizedDestination, destinationAttributes);
                if (!destinationAssessment.DirectOperationAllowed || destinationAssessment.Level == SafetyLevel.Protected)
                {
                    results.Add(new FileOperationItemResult(
                        item.SourcePath,
                        normalizedDestination,
                        FileOperationItemStatus.Blocked,
                        "The final safety check blocked the destination immediately before the action."));
                    continue;
                }
            }

            if (currentSnapshot != item.Snapshot)
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.SourceChanged,
                    "The source changed after the preview; the operation was canceled for this item."));
                continue;
            }

            try
            {
                results.Add(ExecuteItem(plan, item, currentSnapshot));
            }
            catch (Exception exception) when (IsRecoverableOperationException(exception))
            {
                results.Add(new FileOperationItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    FileOperationItemStatus.Failed,
                    exception.Message));
            }
        }

        wasCancelled |= cancellationToken.IsCancellationRequested;
        return new FileOperationReceipt(
            plan.OperationId,
            plan.Kind,
            DateTimeOffset.UtcNow,
            options.DryRun,
            wasCancelled,
            results);
    }

    private static FileOperationItemResult ExecuteItem(
        FileOperationPlan plan,
        FileOperationPlanItem item,
        FileSnapshot snapshot)
    {
        if (plan.Kind == FileOperationKind.PermanentDelete)
        {
            if (snapshot.IsDirectory)
            {
                Directory.Delete(item.SourcePath, recursive: false);
            }
            else
            {
                File.Delete(item.SourcePath);
            }

            return new FileOperationItemResult(
                item.SourcePath,
                null,
                FileOperationItemStatus.Completed,
                "Permanent deletion completed.");
        }

        var destination = item.DestinationPath
            ?? throw new InvalidOperationException("A move operation is missing its destination.");
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The destination directory is invalid.");
        Directory.CreateDirectory(destinationDirectory);

        if (Exists(destination))
        {
            switch (plan.ConflictResolution)
            {
                case FileConflictResolution.Skip:
                    return new FileOperationItemResult(
                        item.SourcePath,
                        destination,
                        FileOperationItemStatus.Skipped,
                        "The destination appeared after the preview was created.");
                case FileConflictResolution.Rename:
                    destination = FindAvailableDestination(destination, reservedDestinations: null);
                    break;
                case FileConflictResolution.Replace:
                    return new FileOperationItemResult(
                        item.SourcePath,
                        destination,
                        FileOperationItemStatus.Blocked,
                        "Replacement is disabled until a transactional backup is available.");
            }
        }

        if (snapshot.IsDirectory)
        {
            Directory.Move(item.SourcePath, destination);
        }
        else
        {
            File.Move(item.SourcePath, destination);
        }

        return new FileOperationItemResult(
            item.SourcePath,
            destination,
            FileOperationItemStatus.Completed,
            plan.Kind == FileOperationKind.Restore ? "Item restored." : "Item moved.");
    }

    private static void ValidateExecutionAuthorization(
        FileOperationKind kind,
        FileConflictResolution conflictResolution,
        FileOperationExecutionOptions options)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(conflictResolution))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictResolution));
        }

        if (options.DryRun)
        {
            return;
        }

        if (!options.ConfirmedByUser)
        {
            throw new InvalidOperationException("Actual execution requires ConfirmedByUser=true.");
        }

        if (kind == FileOperationKind.PermanentDelete && !options.AllowPermanentDelete)
        {
            throw new InvalidOperationException("Permanent deletion requires AllowPermanentDelete=true.");
        }

        if (conflictResolution == FileConflictResolution.Replace)
        {
            throw new InvalidOperationException(
                "Replacement is disabled until a transactional backup of the destination is available.");
        }
    }

    private static bool TryCaptureSnapshot(string path, out FileSnapshot snapshot, out string? error)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                file.Refresh();
                snapshot = new FileSnapshot(false, file.Length, file.LastWriteTimeUtc, file.Attributes);
                error = null;
                return true;
            }

            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                directory.Refresh();
                snapshot = new FileSnapshot(true, null, directory.LastWriteTimeUtc, directory.Attributes);
                error = null;
                return true;
            }

            snapshot = EmptySnapshot;
            error = "The source does not exist or cannot be accessed.";
            return false;
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            snapshot = EmptySnapshot;
            error = exception.Message;
            return false;
        }
    }

    private FileOperationPlanItem InvalidPlanItem(string path, string message) =>
        new(
            path,
            null,
            EmptySnapshot,
            _safetyPolicy.Evaluate(path),
            FileOperationItemStatus.Blocked,
            message);

    private static string FindAvailableDestination(string desired, HashSet<string>? reservedDestinations)
    {
        var directory = Path.GetDirectoryName(desired)
            ?? throw new ArgumentException("The destination has no directory component.", nameof(desired));
        var extension = Path.GetExtension(desired);
        var stem = Path.GetFileNameWithoutExtension(desired);

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!Exists(candidate) && (reservedDestinations is null || !reservedDestinations.Contains(candidate)))
            {
                return candidate;
            }
        }

        throw new IOException("No available destination name could be found.");
    }

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "item";
        }

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool TryGetExistingAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            if (Exists(path))
            {
                attributes = File.GetAttributes(path);
                return true;
            }
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            attributes = FileAttributes.System;
            return true;
        }

        attributes = FileAttributes.Normal;
        return false;
    }

    private static bool HasReparsePointInExistingPath(string path, out string? reparsePoint)
    {
        reparsePoint = null;
        string? current = Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    reparsePoint = current;
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                current = Path.GetDirectoryName(current);
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                current = Path.GetDirectoryName(current);
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                reparsePoint = current;
                return true;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathUtilities.PathComparer.Equals(parent, current))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private static bool IsRecoverableOperationException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static FileSnapshot EmptySnapshot { get; } =
        new(false, null, null, FileAttributes.Normal);
}
