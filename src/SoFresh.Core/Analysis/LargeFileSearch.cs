using SoFresh.Core.Domain;
using SoFresh.Core.Utilities;

namespace SoFresh.Core.Analysis;

public enum FileSizeSortDirection
{
    Descending,
    Ascending
}

public sealed record LargeFileQuery
{
    public long MinimumBytes { get; init; } = 100L * 1024 * 1024;

    public long? MaximumBytes { get; init; }

    public string? UnderPath { get; init; }

    public FileCategory? Category { get; init; }

    public UserContext? UserContext { get; init; }

    public DateTimeOffset? ModifiedBeforeUtc { get; init; }

    public FileSizeSortDirection SortDirection { get; init; } = FileSizeSortDirection.Descending;

    public int? Limit { get; init; }
}

public sealed class LargeFileSearch
{
    public IReadOnlyList<FileEntry> Find(IEnumerable<FileEntry> entries, LargeFileQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        query ??= new LargeFileQuery();
        if (query.MinimumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The minimum file size cannot be negative.");
        }

        if (query.MaximumBytes is < 0 || query.MaximumBytes < query.MinimumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The maximum file size is invalid.");
        }

        if (query.Limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The result limit must be positive.");
        }

        var filtered = entries.Where(entry =>
            !entry.IsDirectory
            && entry.Length >= query.MinimumBytes
            && (query.MaximumBytes is null || entry.Length <= query.MaximumBytes)
            && (query.UnderPath is null || PathUtilities.IsSameOrDescendant(entry.FullPath, query.UnderPath))
            && (query.Category is null || entry.Classification.Category == query.Category)
            && (query.UserContext is null || entry.Classification.UserContext == query.UserContext)
            && (query.ModifiedBeforeUtc is null || entry.ModifiedAtUtc <= query.ModifiedBeforeUtc));

        var ordered = query.SortDirection == FileSizeSortDirection.Ascending
            ? filtered.OrderBy(static entry => entry.Length).ThenBy(static entry => entry.FullPath)
            : filtered.OrderByDescending(static entry => entry.Length).ThenBy(static entry => entry.FullPath);

        return (query.Limit is null ? ordered : ordered.Take(query.Limit.Value)).ToArray();
    }
}
