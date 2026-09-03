using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SoFresh.Core.Domain;
using SoFresh.Core.Utilities;

namespace SoFresh.Core.Analysis;

public sealed class DuplicateFinder : IDuplicateFinder
{
    public async Task<DuplicateSearchResult> FindAsync(
        IEnumerable<FileEntry> entries,
        DuplicateSearchOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        options ??= new DuplicateSearchOptions();
        ValidateOptions(options);

        var issues = new List<DuplicateIssue>();
        var sizeCandidates = entries
            .Where(entry =>
                !entry.IsDirectory
                && !entry.IsReparsePoint
                && entry.Length >= options.MinimumFileSize)
            .GroupBy(static entry => entry.Length)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .ToArray();

        progress?.Report(new DuplicateProgress("File size", sizeCandidates.Length, sizeCandidates.Length, null));
        if (sizeCandidates.Length == 0)
        {
            return new DuplicateSearchResult(Array.Empty<DuplicateGroup>(), issues, DateTimeOffset.UtcNow);
        }

        var sampleHashes = new List<HashedEntry>(sizeCandidates.Length);
        var processed = 0;
        foreach (var entry in sizeCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DuplicateProgress("Sampling", sizeCandidates.Length, processed, entry.FullPath));
            try
            {
                var hash = await ComputeSampleHashAsync(entry, options.SampleSizeBytes, cancellationToken).ConfigureAwait(false);
                sampleHashes.Add(new HashedEntry(entry, hash));
            }
            catch (Exception exception) when (IsRecoverableHashException(exception))
            {
                issues.Add(new DuplicateIssue(entry.FullPath, exception.Message));
            }

            processed++;
        }

        var fullHashCandidates = sampleHashes
            .GroupBy(static item => (item.Entry.Length, item.Hash))
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group.Select(static item => item.Entry))
            .ToArray();

        var fullHashes = new List<HashedEntry>(fullHashCandidates.Length);
        processed = 0;
        foreach (var entry in fullHashCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DuplicateProgress("SHA-256", fullHashCandidates.Length, processed, entry.FullPath));
            try
            {
                var hash = await ComputeFullHashAsync(entry, options.BufferSizeBytes, cancellationToken).ConfigureAwait(false);
                fullHashes.Add(new HashedEntry(entry, hash));
            }
            catch (Exception exception) when (IsRecoverableHashException(exception))
            {
                issues.Add(new DuplicateIssue(entry.FullPath, exception.Message));
            }

            processed++;
        }

        var duplicateGroups = new List<DuplicateGroup>();
        foreach (var exactGroup in fullHashes
                     .GroupBy(static item => (item.Entry.Length, item.Hash))
                     .Where(static group => group.Count() > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var duplicateFiles = new List<DuplicateFile>();

            foreach (var item in exactGroup.OrderBy(static item => item.Entry.FullPath, PathUtilities.PathComparer))
            {
                var physicalIdentity = WindowsPhysicalFileIdentity.TryGet(item.Entry.FullPath);
                var uniquenessKey = physicalIdentity ?? $"path:{PathUtilities.Normalize(item.Entry.FullPath)}";
                var isAlias = !identities.Add(uniquenessKey);
                duplicateFiles.Add(new DuplicateFile(item.Entry, physicalIdentity, isAlias));
            }

            if (identities.Count < 2)
            {
                continue;
            }

            duplicateGroups.Add(new DuplicateGroup(
                exactGroup.Key.Hash,
                exactGroup.Key.Length,
                duplicateFiles,
                SaturatingMultiply(exactGroup.Key.Length, identities.Count - 1)));
        }

        progress?.Report(new DuplicateProgress("Completed", fullHashCandidates.Length, fullHashCandidates.Length, null));
        return new DuplicateSearchResult(
            duplicateGroups
                .OrderByDescending(static group => group.RecoverableBytes)
                .ThenByDescending(static group => group.FileSize)
                .ToArray(),
            issues,
            DateTimeOffset.UtcNow);
    }

    private static async Task<string> ComputeSampleHashAsync(
        FileEntry entry,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        var initial = CaptureFingerprint(entry.FullPath);
        EnsureMatchesScan(entry, initial);
        await using var stream = OpenRead(entry.FullPath, sampleSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(initial.Length));

        var buffer = ArrayPool<byte>.Shared.Rent(sampleSize);
        try
        {
            var firstRead = await ReadUpToAsync(stream, buffer, sampleSize, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, firstRead));

            if (initial.Length > sampleSize)
            {
                stream.Seek(Math.Max(sampleSize, initial.Length - sampleSize), SeekOrigin.Begin);
                var lastRead = await ReadUpToAsync(stream, buffer, sampleSize, cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, lastRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        EnsureUnchanged(entry.FullPath, initial);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> ComputeFullHashAsync(
        FileEntry entry,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        var initial = CaptureFingerprint(entry.FullPath);
        EnsureMatchesScan(entry, initial);
        await using var stream = OpenRead(entry.FullPath, bufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        EnsureUnchanged(entry.FullPath, initial);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static FileStream OpenRead(string path, int bufferSize) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

    private static async Task<int> ReadUpToAsync(
        Stream stream,
        byte[] buffer,
        int requested,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < requested)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, requested - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static FileFingerprint CaptureFingerprint(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The file no longer exists.", path);
        }

        return new FileFingerprint(file.Length, file.LastWriteTimeUtc);
    }

    private static void EnsureUnchanged(string path, FileFingerprint initial)
    {
        var current = CaptureFingerprint(path);
        if (current != initial)
        {
            throw new IOException("The file changed while its hash was being calculated and was excluded.");
        }
    }

    private static void EnsureMatchesScan(FileEntry entry, FileFingerprint current)
    {
        if (current.Length != entry.Length
            || (entry.ModifiedAtUtc is not null && current.LastWriteAtUtc != entry.ModifiedAtUtc.Value.UtcDateTime))
        {
            throw new IOException("The file changed after the scan and was excluded.");
        }
    }

    private static void ValidateOptions(DuplicateSearchOptions options)
    {
        if (options.MinimumFileSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The minimum file size cannot be negative.");
        }

        if (options.SampleSizeBytes is < 1024 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The sample size must be between 1 KiB and 4 MiB.");
        }

        if (options.BufferSizeBytes is < 4096 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The buffer size must be between 4 KiB and 4 MiB.");
        }
    }

    private static bool IsRecoverableHashException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException;

    private static long SaturatingMultiply(long value, int multiplier)
    {
        try
        {
            return checked(value * multiplier);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private sealed record HashedEntry(FileEntry Entry, string Hash);

    private readonly record struct FileFingerprint(long Length, DateTime LastWriteAtUtc);

    private static class WindowsPhysicalFileIdentity
    {
        public static string? TryGet(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                using var handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.None);
                if (!GetFileInformationByHandle(handle, out var information))
                {
                    return null;
                }

                return $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle fileHandle,
            out ByHandleFileInformation fileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
