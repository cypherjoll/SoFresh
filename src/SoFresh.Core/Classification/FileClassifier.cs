using SoFresh.Core.Domain;
using SoFresh.Core.Utilities;

namespace SoFresh.Core.Classification;

public sealed class FileClassifier : IFileClassifier
{
    private static readonly HashSet<string> DocumentExtensions = CreateSet(
        ".doc", ".docx", ".odt", ".pdf", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".ods", ".ppt", ".pptx");

    private static readonly HashSet<string> ImageExtensions = CreateSet(
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".raw", ".svg");

    private static readonly HashSet<string> VideoExtensions = CreateSet(
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpeg", ".mpg");

    private static readonly HashSet<string> AudioExtensions = CreateSet(
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma");

    private static readonly HashSet<string> ArchiveExtensions = CreateSet(
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".cab");

    private static readonly HashSet<string> InstallerExtensions = CreateSet(
        ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".exe");

    private static readonly HashSet<string> CodeExtensions = CreateSet(
        ".cs", ".fs", ".vb", ".cpp", ".c", ".h", ".hpp", ".java", ".kt", ".swift", ".js", ".ts", ".tsx", ".jsx", ".py", ".rb", ".go", ".rs", ".php", ".html", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".sql", ".sh", ".ps1");

    private static readonly HashSet<string> DatabaseExtensions = CreateSet(
        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".mdf", ".bak");

    private static readonly HashSet<string> TemporaryExtensions = CreateSet(
        ".tmp", ".temp", ".partial", ".part", ".crdownload");

    private static readonly HashSet<string> LogExtensions = CreateSet(
        ".log", ".etl", ".trace");

    private static readonly HashSet<string> DumpExtensions = CreateSet(
        ".dmp", ".mdmp", ".hdmp");

    private readonly string? _windowsPath = NormalizeKnownPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private readonly string? _userProfilePath = NormalizeKnownPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private readonly string? _publicPath = NormalizeKnownPath(Environment.GetEnvironmentVariable("PUBLIC"));
    private readonly string? _programFilesPath = NormalizeKnownPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private readonly string? _programFilesX86Path = NormalizeKnownPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    private readonly string? _programDataPath = NormalizeKnownPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    private readonly string? _tempPath = NormalizeKnownPath(Path.GetTempPath());

    public FileClassification Classify(string fullPath, FileAttributes attributes, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var extension = isDirectory ? string.Empty : Path.GetExtension(fullPath);
        var context = ClassifyContext(fullPath);
        var category = ClassifyCategory(fullPath, extension, context, attributes);
        var topic = ClassifyTopic(fullPath);

        return new FileClassification(category, context, topic, BuildReason(category, context));
    }

    private FileCategory ClassifyCategory(
        string path,
        string extension,
        UserContext context,
        FileAttributes attributes)
    {
        if (TemporaryExtensions.Contains(extension))
        {
            return FileCategory.Temporary;
        }

        if (PathUtilities.HasPathSegment(path, "cache")
            || PathUtilities.HasPathSegment(path, "caches")
            || PathUtilities.HasPathSegment(path, "inetcache"))
        {
            return FileCategory.Cache;
        }

        if (DumpExtensions.Contains(extension))
        {
            return FileCategory.CrashDump;
        }

        if (LogExtensions.Contains(extension))
        {
            return FileCategory.Log;
        }

        if (DocumentExtensions.Contains(extension)) return FileCategory.Document;
        if (ImageExtensions.Contains(extension)) return FileCategory.Image;
        if (VideoExtensions.Contains(extension)) return FileCategory.Video;
        if (AudioExtensions.Contains(extension)) return FileCategory.Audio;
        if (ArchiveExtensions.Contains(extension)) return FileCategory.Archive;
        if (InstallerExtensions.Contains(extension)) return FileCategory.Installer;
        if (CodeExtensions.Contains(extension)) return FileCategory.SourceCode;
        if (DatabaseExtensions.Contains(extension)) return FileCategory.Database;

        if (IsUnder(path, _tempPath))
        {
            return FileCategory.Temporary;
        }

        if (context == UserContext.WindowsSystem || attributes.HasFlag(FileAttributes.System))
        {
            return FileCategory.System;
        }

        return FileCategory.Other;
    }

    private UserContext ClassifyContext(string path)
    {
        if (IsUnder(path, _windowsPath)
            || HasAnySegment(path, "Windows.old", "$WINDOWS.~BT", "$WINDOWS.~WS"))
        {
            return UserContext.WindowsSystem;
        }
        if (IsUnder(path, _publicPath)) return UserContext.Shared;
        if (IsUnder(path, _programFilesPath) || IsUnder(path, _programFilesX86Path) || IsUnder(path, _programDataPath))
        {
            return UserContext.Application;
        }

        if (IsUnder(path, _userProfilePath)) return UserContext.CurrentUser;

        var usersRoot = _userProfilePath is null ? null : Directory.GetParent(_userProfilePath)?.FullName;
        if (IsUnder(path, usersRoot)) return UserContext.OtherUser;

        return UserContext.Unknown;
    }

    private static TopicCategory ClassifyTopic(string path)
    {
        if (HasAnySegment(path, "projects", "progetti", "repos", "source", "src")) return TopicCategory.Projects;
        if (HasAnySegment(path, "work", "lavoro", "office")) return TopicCategory.Work;
        if (HasAnySegment(path, "finance", "finanza", "bank", "banca", "tax", "tasse")) return TopicCategory.Finance;
        if (HasAnySegment(path, "school", "scuola", "university", "universita", "study", "studio")) return TopicCategory.Education;
        if (HasAnySegment(path, "photos", "photo", "pictures", "foto", "immagini")) return TopicCategory.Photography;
        if (HasAnySegment(path, "personal", "personale", "family", "famiglia")) return TopicCategory.Personal;
        return TopicCategory.Unknown;
    }

    private static string BuildReason(FileCategory category, UserContext context) =>
        $"Local rules: category {category}, context {context}";

    private static bool HasAnySegment(string path, params string[] segments) =>
        segments.Any(segment => PathUtilities.HasPathSegment(path, segment));

    private static bool IsUnder(string path, string? root) =>
        root is not null && PathUtilities.IsSameOrDescendant(path, root);

    private static string? NormalizeKnownPath(string? path) =>
        string.IsNullOrWhiteSpace(path) || !PathUtilities.TryNormalize(path, out var normalized)
            ? null
            : normalized;

    private static HashSet<string> CreateSet(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);
}
