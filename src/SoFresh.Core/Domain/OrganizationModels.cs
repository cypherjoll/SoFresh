namespace SoFresh.Core.Domain;

public enum OrganizationGroupingProperty
{
    ModifiedYear,
    ModifiedMonth,
    Category,
    Extension,
    Topic,
    UserContext
}

public enum OrganizationSkipReason
{
    Directory,
    ReparsePoint,
    Protected,
    MissingProperty,
    InvalidPath,
    AlreadyOrganized
}

public enum OrganizationCollisionKind
{
    ExistingDestination,
    MultipleSources
}

public sealed record OrganizationPlanRequest
{
    public required string DestinationRoot { get; init; }

    public required IReadOnlyList<OrganizationGroupingProperty> GroupBy { get; init; }
}

public sealed record OrganizationSkippedItem(
    string SourcePath,
    OrganizationSkipReason Reason,
    string Message);

public sealed record OrganizationCollision(
    string SourcePath,
    string DestinationPath,
    OrganizationCollisionKind Kind,
    string Message,
    string? ConflictingSourcePath = null);

public sealed record OrganizationPreview(
    string DestinationRoot,
    IReadOnlyList<OrganizationGroupingProperty> GroupBy,
    IReadOnlyList<MoveSpecification> Moves,
    IReadOnlyList<OrganizationSkippedItem> Skipped,
    IReadOnlyList<OrganizationCollision> Collisions);
