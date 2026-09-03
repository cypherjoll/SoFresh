using SoFresh.Core.Domain;

namespace SoFresh.Core.Information;

public interface IFileTypeInformationProvider
{
    Task<FileTypeInformationResult> SearchAsync(
        FileTypeInformationQuery query,
        CancellationToken cancellationToken = default);
}
