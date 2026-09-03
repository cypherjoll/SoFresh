using SoFresh.Core.Domain;

namespace SoFresh.Core.Analysis;

public interface IDuplicateFinder
{
    Task<DuplicateSearchResult> FindAsync(
        IEnumerable<FileEntry> entries,
        DuplicateSearchOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
