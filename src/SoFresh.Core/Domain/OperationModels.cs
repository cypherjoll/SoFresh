namespace SoFresh.Core.Domain;

public enum FileOperationKind
{
    Move,
    Quarantine,
    PermanentDelete,
    Restore
}

public enum FileConflictResolution
{
    Skip,
    Rename,
    Replace
}

public enum FileOperationItemStatus
{
    Planned,
    PreviewOnly,
    Completed,
    Skipped,
    Failed,
    Blocked,
    SourceChanged
}

public sealed record FileSnapshot(
    bool IsDirectory,
    long? Length,
    DateTimeOffset? ModifiedAtUtc,
    FileAttributes Attributes);

public sealed record MoveSpecification(string SourcePath, string DestinationPath);

public sealed class FileOperationPlanItem
{
    internal FileOperationPlanItem(
        string sourcePath,
        string? destinationPath,
        FileSnapshot snapshot,
        SafetyAssessment safety,
        FileOperationItemStatus status,
        string? message)
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        Snapshot = snapshot;
        Safety = safety;
        Status = status;
        Message = message;
    }

    public string SourcePath { get; }

    public string? DestinationPath { get; }

    public FileSnapshot Snapshot { get; }

    public SafetyAssessment Safety { get; }

    public FileOperationItemStatus Status { get; }

    public string? Message { get; }
}

public sealed class FileOperationPlan
{
    internal FileOperationPlan(
        Guid operationId,
        FileOperationKind kind,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<FileOperationPlanItem> items,
        long totalBytes,
        FileConflictResolution conflictResolution)
    {
        OperationId = operationId;
        Kind = kind;
        CreatedAtUtc = createdAtUtc;
        Items = Array.AsReadOnly(items.ToArray());
        TotalBytes = totalBytes;
        ConflictResolution = conflictResolution;
    }

    public Guid OperationId { get; }

    public FileOperationKind Kind { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<FileOperationPlanItem> Items { get; }

    public long TotalBytes { get; }

    public FileConflictResolution ConflictResolution { get; }
}

public sealed record FileOperationExecutionOptions
{
    public bool DryRun { get; init; } = true;

    public bool ConfirmedByUser { get; init; }

    public bool AllowReviewRequiredItems { get; init; }

    public bool AllowPermanentDelete { get; init; }

    public bool AllowReplaceExisting { get; init; }
}

public sealed record FileOperationItemResult(
    string SourcePath,
    string? DestinationPath,
    FileOperationItemStatus Status,
    string Message);

public sealed class FileOperationReceipt
{
    internal FileOperationReceipt(
        Guid operationId,
        FileOperationKind kind,
        DateTimeOffset completedAtUtc,
        bool wasDryRun,
        bool wasCancelled,
        IReadOnlyList<FileOperationItemResult> items)
    {
        OperationId = operationId;
        Kind = kind;
        CompletedAtUtc = completedAtUtc;
        WasDryRun = wasDryRun;
        WasCancelled = wasCancelled;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public Guid OperationId { get; }

    public FileOperationKind Kind { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public bool WasDryRun { get; }

    public bool WasCancelled { get; }

    public IReadOnlyList<FileOperationItemResult> Items { get; }

    public bool IsReversible => !WasDryRun
        && Kind is FileOperationKind.Move or FileOperationKind.Quarantine or FileOperationKind.Restore
        && Items.Any(static item => item.Status == FileOperationItemStatus.Completed);
}
