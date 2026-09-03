using SoFresh.Core.Domain;
using SoFresh.Core.Utilities;

namespace SoFresh.Core.Safety;

public sealed class FileSafetyPolicy : IFileSafetyPolicy
{
    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "bootmgr",
        "bootnxt",
        "ntldr",
        "ntdetect.com"
    };

    private readonly IReadOnlyList<string> _protectedPaths;
    private readonly IReadOnlyList<string> _probablySafePaths;
    private readonly string? _userProfile;

    public FileSafetyPolicy(
        IEnumerable<string>? additionalProtectedPaths = null,
        IEnumerable<string>? additionalProbablySafePaths = null)
    {
        _userProfile = TryNormalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _protectedPaths = BuildProtectedPaths(additionalProtectedPaths);
        _probablySafePaths = BuildProbablySafePaths(additionalProbablySafePaths)
            .Where(path => _userProfile is null || !PathUtilities.IsSameOrDescendant(_userProfile, path))
            .ToArray();
    }

    public SafetyAssessment Evaluate(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Evaluate(entry.FullPath, entry.Attributes);
    }

    public SafetyAssessment Evaluate(string path, FileAttributes? attributes = null)
    {
        if (!PathUtilities.TryNormalize(path, out var normalized))
        {
            return Protected(path, "The path is invalid or cannot be normalized.");
        }

        var name = Path.GetFileName(normalized);
        if (ProtectedFileNames.Contains(name))
        {
            return Protected(normalized, "This file is essential for Windows startup, virtual memory, or hibernation.");
        }

        if (attributes?.HasFlag(FileAttributes.ReparsePoint) == true)
        {
            return Protected(normalized, "The cleanup engine cannot modify reparse points directly.");
        }

        if (attributes?.HasFlag(FileAttributes.System) == true)
        {
            return Protected(normalized, "This item is marked as a system file.");
        }

        if (_protectedPaths.Any(root => PathUtilities.IsSameOrDescendant(normalized, root)))
        {
            return Protected(
                normalized,
                "This path is managed by Windows or an application. Use the vendor's official tool.");
        }

        var volumeRoot = Path.GetPathRoot(normalized);
        if (volumeRoot is not null && PathUtilities.PathComparer.Equals(normalized, volumeRoot))
        {
            return Protected(normalized, "A volume root is not a valid target for direct cleanup.");
        }

        if (_userProfile is not null && PathUtilities.PathComparer.Equals(normalized, _userProfile))
        {
            return Protected(normalized, "A user profile cannot be moved or deleted as a single unit.");
        }

        if (_probablySafePaths.Any(root => PathUtilities.IsSameOrDescendant(normalized, root)))
        {
            return new SafetyAssessment(
                normalized,
                SafetyLevel.ProbablySafe,
                "This item is in a user temporary folder.",
                "Review the preview and move it to quarantine first.",
                true);
        }

        return new SafetyAssessment(
            normalized,
            SafetyLevel.ReviewRequired,
            "No local rule can determine that this item is unnecessary.",
            "Review its contents and origin; prefer quarantine or another reversible move.",
            true);
    }

    private static SafetyAssessment Protected(string path, string reason) =>
        new(
            path,
            SafetyLevel.Protected,
            reason,
            "Do not modify this item directly.",
            false);

    private static IReadOnlyList<string> BuildProtectedPaths(IEnumerable<string>? additionalPaths)
    {
        var paths = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
        };

        if (additionalPaths is not null)
        {
            paths.AddRange(additionalPaths);
        }

        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var volumeRoot = string.IsNullOrWhiteSpace(windowsPath) ? null : Path.GetPathRoot(windowsPath);
        if (!string.IsNullOrWhiteSpace(volumeRoot))
        {
            paths.Add(Path.Combine(volumeRoot, "Windows.old"));
            paths.Add(Path.Combine(volumeRoot, "$WINDOWS.~BT"));
            paths.Add(Path.Combine(volumeRoot, "$WINDOWS.~WS"));
            paths.Add(Path.Combine(volumeRoot, "$Recycle.Bin"));
            paths.Add(Path.Combine(volumeRoot, "Boot"));
            paths.Add(Path.Combine(volumeRoot, "Recovery"));
        }

        return NormalizeDistinct(paths);
    }

    private static IReadOnlyList<string> BuildProbablySafePaths(IEnumerable<string>? additionalPaths)
    {
        var paths = new List<string?> { Path.GetTempPath() };
        if (additionalPaths is not null)
        {
            paths.AddRange(additionalPaths);
        }

        return NormalizeDistinct(paths);
    }

    private static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string?> paths) =>
        paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(TryNormalize)
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(PathUtilities.PathComparer)
            .ToArray();

    private static string? TryNormalize(string? path) =>
        !string.IsNullOrWhiteSpace(path) && PathUtilities.TryNormalize(path, out var normalized)
            ? normalized
            : null;
}
