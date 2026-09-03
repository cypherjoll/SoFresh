namespace SoFresh.Core.Utilities;

internal static class PathUtilities
{
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool TryNormalize(string path, out string normalized)
    {
        try
        {
            normalized = Normalize(path);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            IOException or
            System.Security.SecurityException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (root is not null && PathComparer.Equals(fullPath, root))
        {
            return fullPath;
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (!TryNormalize(candidate, out var normalizedCandidate)
            || !TryNormalize(parent, out var normalizedParent))
        {
            return false;
        }

        if (PathComparer.Equals(normalizedCandidate, normalizedParent))
        {
            return true;
        }

        var parentWithSeparator = normalizedParent.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;

        return normalizedCandidate.StartsWith(parentWithSeparator, PathComparison);
    }

    public static bool HasPathSegment(string path, string segment)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return path.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
    }
}
