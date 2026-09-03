using SoFresh.Core.Domain;

namespace SoFresh.Core.Operations;

public interface ISafeFileOperations
{
    Task<FileOperationPlan> PlanMoveAsync(
        IEnumerable<MoveSpecification> moves,
        FileConflictResolution conflictResolution = FileConflictResolution.Rename,
        CancellationToken cancellationToken = default);

    Task<FileOperationPlan> PlanQuarantineAsync(
        IEnumerable<string> sourcePaths,
        string quarantineRoot,
        FileConflictResolution conflictResolution = FileConflictResolution.Rename,
        CancellationToken cancellationToken = default);

    Task<FileOperationPlan> PlanPermanentDeleteAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default);

    Task<FileOperationReceipt> ExecuteAsync(
        FileOperationPlan plan,
        FileOperationExecutionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<FileOperationReceipt> UndoAsync(
        FileOperationReceipt receipt,
        FileOperationExecutionOptions? options = null,
        CancellationToken cancellationToken = default);
}
