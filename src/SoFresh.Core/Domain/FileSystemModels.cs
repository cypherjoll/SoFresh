namespace SoFresh.Core.Domain;

public enum FileEntryKind
{
    File,
    Directory
}

public enum FileCategory
{
    Other,
    Document,
    Image,
    Video,
    Audio,
    Archive,
    Installer,
    SourceCode,
    Database,
    Temporary,
    Cache,
    Log,
    CrashDump,
    System
}

public enum UserContext
{
    Unknown,
    CurrentUser,
    OtherUser,
    WindowsSystem,
    Application,
    Shared
}

public enum TopicCategory
{
    Unknown,
    Work,
    Personal,
    Finance,
    Education,
    Photography,
    Projects
}

public sealed record FileClassification(
    FileCategory Category,
    UserContext UserContext,
    TopicCategory Topic,
    string Reason);

public sealed record FileEntry
{
    public required string FullPath { get; init; }

    public required string Name { get; init; }

    public required FileEntryKind Kind { get; init; }

    public string Extension { get; init; } = string.Empty;

    public long Length { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? ModifiedAtUtc { get; init; }

    public DateTimeOffset? LastAccessAtUtc { get; init; }

    public FileAttributes Attributes { get; init; }

    public string? VolumeRoot { get; init; }

    public FileClassification Classification { get; init; } =
        new(FileCategory.Other, UserContext.Unknown, TopicCategory.Unknown, "No applicable rule");

    public bool IsDirectory => Kind == FileEntryKind.Directory;

    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);

    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);

    public bool IsSystem => Attributes.HasFlag(FileAttributes.System);
}
