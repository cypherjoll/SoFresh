namespace SoFresh.Core.Domain;

public enum InformationConfidence
{
    OfficialMicrosoftSource,
    NoAuthoritativeResult
}

public sealed record FileTypeInformationQuery
{
    public string? Extension { get; init; }

    public FileCategory? Category { get; init; }

    public string Locale { get; init; } = "en-us";

    public int MaximumResults { get; init; } = 5;
}

public sealed record FileTypeInformationSource(
    string Title,
    Uri Url,
    string Description,
    DateTimeOffset? LastUpdatedAt,
    InformationConfidence Confidence);

public sealed record FileTypeInformationResult(
    string SanitizedQuery,
    IReadOnlyList<FileTypeInformationSource> Sources,
    DateTimeOffset RetrievedAtUtc,
    bool IsOffline,
    bool FromCache,
    string Message);
