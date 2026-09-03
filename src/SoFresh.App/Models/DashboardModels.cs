using System.Collections.ObjectModel;
using System.Globalization;

namespace SoFresh.App.Models;

/// <summary>
/// UI-neutral projections of SoFresh.Core results. They contain no sample values and
/// keep WPF-specific presentation concerns out of the scanning engine.
/// </summary>
public sealed record KpiMetric(
    string Label,
    string Value,
    string Detail,
    string IconGlyph,
    string AccentResourceKey,
    string TrendGlyph);

public sealed record DriveSnapshot(
    string Name,
    string VolumeLabel,
    string FileSystem,
    string UsedLabel,
    string TotalLabel,
    string FreeLabel,
    double UsedPercentage,
    string HealthLabel,
    string HealthResourceKey);

public sealed record StorageCategory(
    string Name,
    string SizeLabel,
    double Percentage,
    string BrushResourceKey);

public sealed record TreemapItem(
    string Name,
    string Detail,
    string BrushResourceKey,
    string IconGlyph);

public sealed record InsightItem(
    string Eyebrow,
    string Title,
    string Description,
    string Value,
    string IconGlyph,
    string BrushResourceKey,
    string ActionLabel);

public sealed record ActivityItem(
    string Title,
    string Detail,
    string TimeLabel,
    string IconGlyph,
    string BrushResourceKey);

public enum RiskLevel
{
    Safe,
    Suggested,
    Review,
    Protected
}

public sealed record LargeFileItem(
    string Name,
    string Path,
    string Category,
    long SizeBytes,
    DateTimeOffset? ModifiedAtUtc,
    RiskLevel Risk)
{
    public string ModifiedLabel => ModifiedAtUtc?.ToLocalTime().ToString(
        "dd MMM yyyy",
        CultureInfo.GetCultureInfo("en-US")) ?? "Unknown";

    public string RiskLabel => Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Suggested => "Suggested",
        RiskLevel.Review => "Review",
        RiskLevel.Protected => "Protected",
        _ => "Unknown"
    };

    public string RiskBrushResourceKey => Risk switch
    {
        RiskLevel.Safe => "AccentBrush",
        RiskLevel.Suggested => "BlueBrush",
        RiskLevel.Review => "AmberBrush",
        RiskLevel.Protected => "RedBrush",
        _ => "TextMutedBrush"
    };

    public string RiskTintResourceKey => Risk switch
    {
        RiskLevel.Safe => "AccentTintBrush",
        RiskLevel.Suggested => "BlueTintBrush",
        RiskLevel.Review => "AmberTintBrush",
        RiskLevel.Protected => "RedTintBrush",
        _ => "SurfaceHoverBrush"
    };
}

public sealed class FileTreeNode(
    string name,
    string fullPath,
    bool isDirectory,
    string iconGlyph,
    bool isSynthetic = false)
{
    public string Name { get; } = name;

    public string FullPath { get; } = fullPath;

    public bool IsDirectory { get; } = isDirectory;

    public bool IsSynthetic { get; } = isSynthetic;

    public string IconGlyph { get; } = iconGlyph;

    public long SizeBytes { get; set; }

    public ObservableCollection<FileTreeNode> Children { get; } = [];

    public string Detail => IsSynthetic
        ? string.Empty
        : BytesToDisplay(SizeBytes);

    private static string BytesToDisplay(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.GetCultureInfo("en-US"))} {units[unit]}";
    }
}
