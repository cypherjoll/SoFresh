using SoFresh.Core.Domain;

namespace SoFresh.Core.Scanning;

public interface IFileSystemScanner
{
    Task<ScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
