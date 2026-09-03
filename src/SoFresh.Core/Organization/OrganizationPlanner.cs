using System.Globalization;
using SoFresh.Core.Domain;
using SoFresh.Core.Safety;
using SoFresh.Core.Utilities;

namespace SoFresh.Core.Organization;

public sealed class OrganizationPlanner(IFileSafetyPolicy? safetyPolicy = null) : IOrganizationPlanner
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<char> InvalidSegmentCharacters = CreateInvalidCharacterSet();
    private readonly IFileSafetyPolicy _safetyPolicy = safetyPolicy ?? new FileSafetyPolicy();

    public OrganizationPreview BuildPreview(
        IEnumerable<FileEntry> entries,
        OrganizationPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(request);
        ValidateGrouping(request.GroupBy);

        if (!PathUtilities.TryNormalize(request.DestinationRoot, out var destinationRoot))
        {
            throw new ArgumentException("The destination root is invalid.", nameof(request));
        }

        if (File.Exists(destinationRoot))
        {
            throw new ArgumentException("The destination root points to a file.", nameof(request));
        }

        var rootAssessment = _safetyPolicy.Evaluate(destinationRoot, TryGetAttributes(destinationRoot));
        if (rootAssessment.Level == SafetyLevel.Protected)
        {
            throw new ArgumentException("The destination root is protected.", nameof(request));
        }

        if (ContainsReparsePointInExistingAncestors(destinationRoot))
        {
            throw new ArgumentException("The destination root traverses a reparse point.", nameof(request));
        }

        var moves = new List<MoveSpecification>();
        var skipped = new List<OrganizationSkippedItem>();
        var collisions = new List<OrganizationCollision>();
        var plannedDestinations = new Dictionary<string, string>(PathUtilities.PathComparer);

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.IsDirectory)
            {
                skipped.Add(new OrganizationSkippedItem(
                    entry.FullPath,
                    OrganizationSkipReason.Directory,
                    "Directories are not organized directly."));
                continue;
            }

            if (entry.IsReparsePoint)
            {
                skipped.Add(new OrganizationSkippedItem(
                    entry.FullPath,
                    OrganizationSkipReason.ReparsePoint,
                    "Reparse points are excluded from the preview."));
                continue;
            }

            var safety = _safetyPolicy.Evaluate(entry);
            if (!safety.DirectOperationAllowed || safety.Level == SafetyLevel.Protected)
            {
                skipped.Add(new OrganizationSkippedItem(
                    entry.FullPath,
                    OrganizationSkipReason.Protected,
                    safety.Reason));
                continue;
            }

            if (!PathUtilities.TryNormalize(entry.FullPath, out var sourcePath))
            {
                skipped.Add(new OrganizationSkippedItem(
                    entry.FullPath,
                    OrganizationSkipReason.InvalidPath,
                    "The source path is invalid."));
                continue;
            }

            if (ContainsReparsePointInExistingAncestors(sourcePath))
            {
                skipped.Add(new OrganizationSkippedItem(
                    sourcePath,
                    OrganizationSkipReason.ReparsePoint,
                    "The source path traverses a reparse point."));
                continue;
            }

            if (!TryBuildSegments(entry, request.GroupBy, out var segments, out var missingProperty))
            {
                skipped.Add(new OrganizationSkippedItem(
                    sourcePath,
                    OrganizationSkipReason.MissingProperty,
                    $"The {missingProperty} property is unavailable."));
                continue;
            }

            var safeName = SanitizeSegment(entry.Name, maximumLength: 240);
            var destinationParts = new string[segments.Count + 2];
            destinationParts[0] = destinationRoot;
            for (var index = 0; index < segments.Count; index++)
            {
                destinationParts[index + 1] = segments[index];
            }

            destinationParts[^1] = safeName;
            string destinationPath;
            try
            {
                destinationPath = PathUtilities.Normalize(Path.Combine(destinationParts));
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                IOException or
                NotSupportedException or
                System.Security.SecurityException)
            {
                skipped.Add(new OrganizationSkippedItem(
                    sourcePath,
                    OrganizationSkipReason.InvalidPath,
                    "The calculated destination is invalid."));
                continue;
            }

            if (!PathUtilities.IsSameOrDescendant(destinationPath, destinationRoot)
                || PathUtilities.PathComparer.Equals(destinationPath, destinationRoot))
            {
                skipped.Add(new OrganizationSkippedItem(
                    sourcePath,
                    OrganizationSkipReason.InvalidPath,
                    "The calculated destination would fall outside the selected root."));
                continue;
            }

            if (ContainsReparsePointInExistingAncestors(destinationPath))
            {
                skipped.Add(new OrganizationSkippedItem(
                    sourcePath,
                    OrganizationSkipReason.ReparsePoint,
                    "The calculated destination traverses an existing reparse point."));
                continue;
            }

            if (PathUtilities.PathComparer.Equals(sourcePath, destinationPath))
            {
                skipped.Add(new OrganizationSkippedItem(
                    sourcePath,
                    OrganizationSkipReason.AlreadyOrganized,
                    "The file is already at the intended destination."));
                continue;
            }

            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                collisions.Add(new OrganizationCollision(
                    sourcePath,
                    destinationPath,
                    OrganizationCollisionKind.ExistingDestination,
                    "The destination already exists."));
            }

            if (plannedDestinations.TryGetValue(destinationPath, out var conflictingSource))
            {
                collisions.Add(new OrganizationCollision(
                    sourcePath,
                    destinationPath,
                    OrganizationCollisionKind.MultipleSources,
                    "Multiple files would produce the same destination.",
                    conflictingSource));
            }
            else
            {
                plannedDestinations.Add(destinationPath, sourcePath);
            }

            moves.Add(new MoveSpecification(sourcePath, destinationPath));
        }

        return new OrganizationPreview(
            destinationRoot,
            request.GroupBy.ToArray(),
            moves,
            skipped,
            collisions);
    }

    private static bool TryBuildSegments(
        FileEntry entry,
        IReadOnlyList<OrganizationGroupingProperty> grouping,
        out IReadOnlyList<string> segments,
        out OrganizationGroupingProperty missingProperty)
    {
        var values = new List<string>(grouping.Count);
        foreach (var property in grouping)
        {
            string? value = property switch
            {
                OrganizationGroupingProperty.ModifiedYear =>
                    entry.ModifiedAtUtc?.Year.ToString("D4", CultureInfo.InvariantCulture),
                OrganizationGroupingProperty.ModifiedMonth =>
                    entry.ModifiedAtUtc?.Month.ToString("D2", CultureInfo.InvariantCulture),
                OrganizationGroupingProperty.Category => entry.Classification.Category.ToString(),
                OrganizationGroupingProperty.Extension => string.IsNullOrWhiteSpace(entry.Extension)
                    ? "No extension"
                    : entry.Extension.TrimStart('.').ToLowerInvariant(),
                OrganizationGroupingProperty.Topic => entry.Classification.Topic.ToString(),
                OrganizationGroupingProperty.UserContext => entry.Classification.UserContext.ToString(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(value))
            {
                segments = Array.Empty<string>();
                missingProperty = property;
                return false;
            }

            values.Add(SanitizeSegment(value, maximumLength: 80));
        }

        segments = values;
        missingProperty = default;
        return true;
    }

    private static void ValidateGrouping(IReadOnlyList<OrganizationGroupingProperty>? grouping)
    {
        if (grouping is null || grouping.Count == 0)
        {
            throw new ArgumentException("Specify at least one grouping property.", nameof(grouping));
        }

        if (grouping.Any(property => !Enum.IsDefined(property)))
        {
            throw new ArgumentOutOfRangeException(nameof(grouping), "The grouping property is invalid.");
        }

        if (grouping.Distinct().Count() != grouping.Count)
        {
            throw new ArgumentException("A grouping property cannot be repeated.", nameof(grouping));
        }
    }

    private static string SanitizeSegment(string value, int maximumLength)
    {
        var cleaned = string.Concat(value.Select(character =>
            InvalidSegmentCharacters.Contains(character) || char.IsControl(character) ? '_' : character));
        cleaned = cleaned.Trim().TrimEnd('.');
        if (cleaned is "." or ".." || string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "Unknown";
        }

        if (cleaned.Length > maximumLength)
        {
            cleaned = cleaned[..maximumLength].TrimEnd(' ', '.');
        }

        var deviceStem = cleaned.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(deviceStem))
        {
            cleaned = $"_{cleaned}";
        }

        return cleaned;
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path) : null;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return FileAttributes.System;
        }
    }

    private static bool ContainsReparsePointInExistingAncestors(string path)
    {
        string? current = File.Exists(path) || Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
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
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                return true;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathUtilities.PathComparer.Equals(parent, current))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    private static HashSet<char> CreateInvalidCharacterSet()
    {
        var characters = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (var character in "<>:\"/\\|?*")
        {
            characters.Add(character);
        }

        return characters;
    }
}
