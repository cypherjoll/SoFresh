using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using Microsoft.Win32;
using SoFresh.App.Models;
using SoFresh.App.Services;
using SoFresh.Core.Analysis;
using SoFresh.Core.Domain;
using SoFresh.Core.Safety;
using SoFresh.Core.Scanning;
using CoreScanProgress = SoFresh.Core.Domain.ScanProgress;

namespace SoFresh.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const long LargeFileMinimumBytes = 10L * 1024 * 1024;
    private const long DuplicateMinimumBytes = 1L * 1024 * 1024;
    private const int TreeMaximumDepth = 4;
    private const int TreeMaximumChildren = 24;

    private readonly IFileSystemScanner scanner;
    private readonly DashboardAggregator dashboardAggregator;
    private readonly LargeFileSearch largeFileSearch;
    private readonly IDuplicateFinder duplicateFinder;
    private readonly FileSafetyPolicy safetyPolicy;
    private CancellationTokenSource? scanCancellation;
    private DashboardSnapshot? previousSnapshot;
    private DashboardSnapshot? currentSnapshot;
    private DuplicateSearchResult? currentDuplicates;
    private string searchText = string.Empty;
    private string selectedRoot = string.Empty;
    private double scanProgress;
    private bool isScanIndeterminate;
    private bool isScanning;
    private bool hasResults;
    private bool isTreeVisible;
    private bool isDarkTheme = true;
    private string scanStatus = "No scans yet";
    private string scanDetail = "Choose a folder to get started. SoFresh will not modify any files.";
    private string errorMessage = string.Empty;
    private string issueSummary = string.Empty;
    private DriveSnapshot systemDrive;
    private string analyzedSizeLabel = "—";
    private string distributionSubtitle = "No data analyzed";
    private string recoverableValue = "—";
    private string recoverableUnit = string.Empty;
    private string recoverySummary = "Available after the first scan";
    private double recoveryPercentage;
    private string duplicateCountLabel = "—";
    private string issueCountLabel = "—";
    private string treeSummary = "Choose a folder and start a scan";

    public MainViewModel()
    {
        safetyPolicy = new FileSafetyPolicy();
        scanner = new FileSystemScanner();
        dashboardAggregator = new DashboardAggregator(safetyPolicy);
        largeFileSearch = new LargeFileSearch();
        duplicateFinder = new DuplicateFinder();

        systemDrive = CreateDriveSnapshot(GetSystemRoot(), "System drive");

        LargeFilesView = CollectionViewSource.GetDefaultView(LargeFiles);
        LargeFilesView.Filter = MatchesSearch;
        LargeFilesView.SortDescriptions.Add(
            new SortDescription(nameof(LargeFileItem.SizeBytes), ListSortDirection.Descending));

        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => !IsScanning);
        ScanCommand = new AsyncRelayCommand(ScanSelectedRootAsync, CanStartScan);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => HasSearchText);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ShowTreeCommand = new RelayCommand(() => IsTreeVisible = true);
        ShowTreemapCommand = new RelayCommand(() => IsTreeVisible = false);

        BuildInitialKpis();
    }

    public ObservableCollection<KpiMetric> Kpis { get; } = [];
    public ObservableCollection<StorageCategory> StorageCategories { get; } = [];
    public ObservableCollection<TreemapItem> TreemapItems { get; } = [];
    public ObservableCollection<InsightItem> Insights { get; } = [];
    public ObservableCollection<ActivityItem> RecentActivities { get; } = [];
    public ObservableCollection<LargeFileItem> LargeFiles { get; } = [];
    public ObservableCollection<FileTreeNode> FileTreeRoots { get; } = [];

    public ICollectionView LargeFilesView { get; }

    public RelayCommand BrowseFolderCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ShowTreeCommand { get; }
    public RelayCommand ShowTreemapCommand { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Environment.UserName)
        ? ""
        : Environment.UserName;

    public string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening"
    };

    public string TodayLabel => DateTime.Now.ToString("dddd, MMMM d", CultureInfo.GetCultureInfo("en-US"));

    public DriveSnapshot SystemDrive
    {
        get => systemDrive;
        private set => SetProperty(ref systemDrive, value);
    }

    public string SelectedRoot
    {
        get => selectedRoot;
        private set
        {
            if (!SetProperty(ref selectedRoot, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedRootLabel));
            OnPropertyChanged(nameof(HasSelectedRoot));
            BrowseFolderCommand.RaiseCanExecuteChanged();
            ScanCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedRootLabel => HasSelectedRoot ? SelectedRoot : "No folder selected";

    public bool HasSelectedRoot => !string.IsNullOrWhiteSpace(SelectedRoot);

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? string.Empty))
            {
                return;
            }

            LargeFilesView.Refresh();
            ClearSearchCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSearchText));
            OnPropertyChanged(nameof(ResultCountLabel));
        }
    }

    public bool HasSearchText => SearchText.Length > 0;

    public string ResultCountLabel
    {
        get
        {
            if (!HasResults)
            {
                return "No results—start a scan";
            }

            return HasSearchText
                ? $"{LargeFilesView.Cast<object>().Count():N0} filtered results"
                : $"{LargeFiles.Count:N0} files over {FormatBytes(LargeFileMinimumBytes)}";
        }
    }

    public double ScanProgress
    {
        get => scanProgress;
        private set
        {
            if (SetProperty(ref scanProgress, value))
            {
                OnPropertyChanged(nameof(ScanProgressLabel));
            }
        }
    }

    public string ScanProgressLabel => IsScanIndeterminate
        ? "SCANNING"
        : $"{ScanProgress:N0}%";

    public bool IsScanIndeterminate
    {
        get => isScanIndeterminate;
        private set
        {
            if (SetProperty(ref isScanIndeterminate, value))
            {
                OnPropertyChanged(nameof(ScanProgressLabel));
            }
        }
    }

    public bool IsScanning
    {
        get => isScanning;
        private set
        {
            if (!SetProperty(ref isScanning, value))
            {
                return;
            }

            BrowseFolderCommand.RaiseCanExecuteChanged();
            ScanCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsNotScanning));
        }
    }

    public bool IsNotScanning => !IsScanning;

    public bool HasResults
    {
        get => hasResults;
        private set
        {
            if (SetProperty(ref hasResults, value))
            {
                OnPropertyChanged(nameof(HasNoResults));
                OnPropertyChanged(nameof(ResultCountLabel));
            }
        }
    }

    public bool HasNoResults => !HasResults;

    public bool IsTreeVisible
    {
        get => isTreeVisible;
        private set
        {
            if (SetProperty(ref isTreeVisible, value))
            {
                OnPropertyChanged(nameof(IsTreemapVisible));
            }
        }
    }

    public bool IsTreemapVisible => !IsTreeVisible;

    public bool IsDarkTheme
    {
        get => isDarkTheme;
        private set
        {
            if (SetProperty(ref isDarkTheme, value))
            {
                OnPropertyChanged(nameof(ThemeToggleLabel));
                OnPropertyChanged(nameof(ThemeGlyph));
            }
        }
    }

    public string ThemeToggleLabel => IsDarkTheme ? "Switch to light theme" : "Switch to dark theme";

    public string ThemeGlyph => IsDarkTheme ? "\uE706" : "\uE708";

    public string ScanStatus
    {
        get => scanStatus;
        private set => SetProperty(ref scanStatus, value);
    }

    public string ScanDetail
    {
        get => scanDetail;
        private set => SetProperty(ref scanDetail, value);
    }

    public string ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetProperty(ref errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage.Length > 0;

    public string IssueSummary
    {
        get => issueSummary;
        private set
        {
            if (SetProperty(ref issueSummary, value))
            {
                OnPropertyChanged(nameof(HasIssues));
            }
        }
    }

    public bool HasIssues => IssueSummary.Length > 0;

    public string AnalyzedSizeLabel
    {
        get => analyzedSizeLabel;
        private set => SetProperty(ref analyzedSizeLabel, value);
    }

    public string DistributionSubtitle
    {
        get => distributionSubtitle;
        private set => SetProperty(ref distributionSubtitle, value);
    }

    public string RecoverableValue
    {
        get => recoverableValue;
        private set => SetProperty(ref recoverableValue, value);
    }

    public string RecoverableUnit
    {
        get => recoverableUnit;
        private set => SetProperty(ref recoverableUnit, value);
    }

    public string RecoverySummary
    {
        get => recoverySummary;
        private set => SetProperty(ref recoverySummary, value);
    }

    public double RecoveryPercentage
    {
        get => recoveryPercentage;
        private set => SetProperty(ref recoveryPercentage, value);
    }

    public string DuplicateCountLabel
    {
        get => duplicateCountLabel;
        private set => SetProperty(ref duplicateCountLabel, value);
    }

    public string IssueCountLabel
    {
        get => issueCountLabel;
        private set => SetProperty(ref issueCountLabel, value);
    }

    public string TreeSummary
    {
        get => treeSummary;
        private set => SetProperty(ref treeSummary, value);
    }

    private bool CanStartScan() =>
        !IsScanning && HasSelectedRoot && Directory.Exists(SelectedRoot);

    private void BrowseFolder()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose a folder to scan",
                Multiselect = false
            };
            if (HasSelectedRoot && Directory.Exists(SelectedRoot))
            {
                dialog.InitialDirectory = SelectedRoot;
            }

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var normalized = Path.GetFullPath(dialog.FolderName);
            if (string.Equals(normalized, SelectedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedRoot = normalized;
            ResetAnalysisForSelectedRoot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = $"Could not select the folder: {exception.Message}";
        }
    }

    private async Task ScanSelectedRootAsync()
    {
        if (!CanStartScan())
        {
            ErrorMessage = "Choose an existing folder before starting a scan.";
            return;
        }

        scanCancellation?.Dispose();
        scanCancellation = new CancellationTokenSource();
        var cancellation = scanCancellation;
        var cancellationToken = cancellation.Token;

        ClearAnalysisResults();
        IsScanning = true;
        IsScanIndeterminate = true;
        ScanProgress = 0;
        ScanStatus = "Scanning file system";
        ScanDetail = "Preparing analysis…";
        ErrorMessage = string.Empty;
        IssueSummary = string.Empty;

        var scanCompleted = false;
        try
        {
            var progress = new Progress<CoreScanProgress>(OnScanProgress);
            var result = await scanner.ScanAsync(
                new ScanRequest
                {
                    Roots = [SelectedRoot],
                    IncludeDirectories = true,
                    IncludeHidden = true,
                    IncludeSystem = true,
                    FollowReparsePoints = false,
                    ProgressReportInterval = 128
                },
                progress,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = dashboardAggregator.Build(result, previousSnapshot);
            currentSnapshot = snapshot;
            previousSnapshot = snapshot;
            currentDuplicates = null;
            ApplyDashboard(result, snapshot);
            scanCompleted = true;

            ScanStatus = "Finding exact duplicates";
            ScanDetail = $"Safely comparing files of at least {FormatBytes(DuplicateMinimumBytes)}";
            IsScanIndeterminate = false;
            ScanProgress = 0;
            var duplicateProgress = new Progress<DuplicateProgress>(OnDuplicateProgress);
            var duplicates = await duplicateFinder.FindAsync(
                result.Entries,
                new DuplicateSearchOptions { MinimumFileSize = DuplicateMinimumBytes },
                duplicateProgress,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            currentDuplicates = duplicates;
            ApplyDuplicateResults(snapshot, duplicates);

            ScanProgress = 100;
            ScanStatus = "Scan complete";
            ScanDetail = $"{snapshot.FileCount:N0} files and {snapshot.DirectoryCount:N0} folders · read-only preview";
            AddActivity(snapshot, duplicates);
        }
        catch (OperationCanceledException)
        {
            IsScanIndeterminate = false;
            ScanStatus = "Scan stopped";
            ScanDetail = scanCompleted
                ? "File system results are available, but duplicate verification is incomplete. No files were modified."
                : "Analysis stopped. No files were modified.";
            if (scanCompleted && currentSnapshot is not null)
            {
                DuplicateCountLabel = "—";
                BuildKpis(currentSnapshot, null, duplicateAnalysisIncomplete: true);
            }
        }
        catch (Exception exception)
        {
            IsScanIndeterminate = false;
            ScanStatus = "Scan failed";
            ScanDetail = "No files were modified.";
            ErrorMessage = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(scanCancellation, cancellation))
            {
                scanCancellation.Dispose();
                scanCancellation = null;
            }

            IsScanning = false;
        }
    }

    private void CancelScan() => scanCancellation?.Cancel();

    private void OnScanProgress(CoreScanProgress progress)
    {
        ScanDetail = $"{progress.FilesScanned:N0} files · {progress.DirectoriesScanned:N0} folders · {FormatBytes(progress.BytesDiscovered)}";
        IssueCountLabel = progress.IssuesFound.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    }

    private void OnDuplicateProgress(DuplicateProgress progress)
    {
        ScanProgress = progress.CandidateFiles <= 0
            ? 100
            : Math.Clamp(progress.ProcessedFiles * 100d / progress.CandidateFiles, 0, 100);
        ScanDetail = progress.CurrentPath is null
            ? $"{progress.Stage} · {progress.ProcessedFiles:N0}/{progress.CandidateFiles:N0} candidates"
            : $"{progress.Stage} · {progress.ProcessedFiles:N0}/{progress.CandidateFiles:N0} · {Path.GetFileName(progress.CurrentPath)}";
    }

    private void ApplyDashboard(ScanResult result, DashboardSnapshot snapshot)
    {
        HasResults = true;
        AnalyzedSizeLabel = FormatBytes(snapshot.ScannedBytes);
        DistributionSubtitle = $"{snapshot.FileCount:N0} files analyzed by type";

        var recoverableParts = FormatSizeParts(snapshot.PotentiallyRecoverableBytes);
        RecoverableValue = recoverableParts.Value;
        RecoverableUnit = recoverableParts.Unit;
        RecoveryPercentage = snapshot.ScannedBytes <= 0
            ? 0
            : Math.Clamp(snapshot.PotentiallyRecoverableBytes * 100d / snapshot.ScannedBytes, 0, 100);
        RecoverySummary = snapshot.PotentiallyRecoverableBytes == 0
            ? "No low-risk items detected by local rules"
            : $"{RecoveryPercentage:N1}% of analyzed data · review required";

        var volume = snapshot.Volumes.FirstOrDefault();
        if (volume is not null)
        {
            SystemDrive = CreateDriveSnapshot(volume.Root, "Selected drive");
        }

        MapCategories(snapshot);
        MapLargeFiles(result.Entries);
        MapInsights(snapshot, result.Issues.Count);
        MapFileTree(result.Entries);

        IssueCountLabel = result.Issues.Count.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
        IssueSummary = result.Issues.Count == 0
            ? string.Empty
            : $"Partial scan: {result.Issues.Count:N0} paths were inaccessible, changed, or safely excluded.";
        DuplicateCountLabel = "…";
        BuildKpis(snapshot, null);
        OnPropertyChanged(nameof(ResultCountLabel));
    }

    private void ApplyDuplicateResults(DashboardSnapshot snapshot, DuplicateSearchResult duplicates)
    {
        DuplicateCountLabel = duplicates.Groups.Count.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
        var totalIssues = snapshot.Issues.Count + duplicates.Issues.Count;
        IssueCountLabel = totalIssues.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
        IssueSummary = totalIssues == 0
            ? string.Empty
            : $"Partial results: {snapshot.Issues.Count:N0} scan issues and {duplicates.Issues.Count:N0} files that could not be checked for duplicates.";
        BuildKpis(snapshot, duplicates);

        if (duplicates.Groups.Count > 0)
        {
            Insights.Insert(0, new InsightItem(
                "EXACT DUPLICATES",
                $"{duplicates.Groups.Count:N0} verified groups",
                "SHA-256 calculated for candidates; hard links excluded from apparent savings.",
                FormatBytes(duplicates.RecoverableBytes),
                "\uE8B7",
                "PurpleBrush",
                "Available in the dedicated table"));
        }
    }

    private void MapCategories(DashboardSnapshot snapshot)
    {
        StorageCategories.Clear();
        TreemapItems.Clear();
        var brushes = new[] { "PurpleBrush", "BlueBrush", "CyanBrush", "AccentBrush", "AmberBrush", "TextMutedBrush" };
        var categories = snapshot.ByCategory.Where(static bucket => bucket.Bytes > 0).Take(6).ToArray();
        for (var index = 0; index < categories.Length; index++)
        {
            var bucket = categories[index];
            var label = GetCategoryLabel(bucket.Key);
            var percentage = snapshot.ScannedBytes <= 0 ? 0 : bucket.Bytes * 100d / snapshot.ScannedBytes;
            StorageCategories.Add(new StorageCategory(label, FormatBytes(bucket.Bytes), percentage, brushes[index]));
            TreemapItems.Add(new TreemapItem(label, FormatBytes(bucket.Bytes), brushes[index], CategoryGlyph(bucket.Key)));
        }
    }

    private void MapLargeFiles(IReadOnlyList<FileEntry> entries)
    {
        LargeFiles.Clear();
        foreach (var entry in largeFileSearch.Find(
                     entries,
                     new LargeFileQuery
                     {
                         MinimumBytes = LargeFileMinimumBytes,
                         SortDirection = FileSizeSortDirection.Descending,
                         Limit = 100
                     }))
        {
            var assessment = safetyPolicy.Evaluate(entry);
            LargeFiles.Add(new LargeFileItem(
                entry.Name,
                Path.GetDirectoryName(entry.FullPath) ?? entry.FullPath,
                GetCategoryLabel(entry.Classification.Category),
                entry.Length,
                entry.ModifiedAtUtc,
                MapRisk(assessment.Level)));
        }

        LargeFilesView.Refresh();
    }

    private void MapInsights(DashboardSnapshot snapshot, int issueCount)
    {
        Insights.Clear();
        var temporaryBytes = snapshot.ByCategory
            .Where(static bucket => bucket.Key is FileCategory.Temporary or FileCategory.Cache or FileCategory.Log)
            .Sum(static bucket => bucket.Bytes);
        Insights.Add(new InsightItem(
            "LOCAL RULES",
            "Temporary files, caches, and logs",
            "This estimate is based on local classification; every item still requires review.",
            FormatBytes(temporaryBytes),
            "\uE74D",
            "AccentBrush",
            "No automatic cleanup"));

        var oldBucket = snapshot.ByAge.FirstOrDefault(static bucket => bucket.Label == "Over 1 year");
        Insights.Add(new InsightItem(
            "LAST MODIFIED",
            "Files older than one year",
            oldBucket is null
                ? "No data in this age range."
                : $"{oldBucket.FileCount:N0} files to review in their original context.",
            FormatBytes(oldBucket?.Bytes ?? 0),
            "\uE81C",
            "BlueBrush",
            "Age alone does not determine usefulness"));

        var largestCategory = snapshot.ByCategory.FirstOrDefault();
        Insights.Add(new InsightItem(
            "DISTRIBUTION",
            largestCategory is null ? "No dominant category" : GetCategoryLabel(largestCategory.Key),
            largestCategory is null
                ? "This folder contains no classifiable files."
                : $"Largest category by size. {issueCount:N0} access or metadata issues.",
            FormatBytes(largestCategory?.Bytes ?? 0),
            "\uEDA2",
            "AmberBrush",
            "Explore using the treemap or file tree"));
    }

    private void MapFileTree(IReadOnlyList<FileEntry> entries)
    {
        FileTreeRoots.Clear();
        if (!HasSelectedRoot)
        {
            return;
        }

        var rootPath = Path.GetFullPath(SelectedRoot);
        var rootName = new DirectoryInfo(rootPath).Name;
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = rootPath;
        }

        var rootNode = new FileTreeNode(rootName, rootPath, true, "\uE8B7");
        var nodes = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.TrimEndingDirectorySeparator(rootPath)] = rootNode
        };

        foreach (var entry in entries.Where(static entry => entry.IsDirectory).OrderBy(static entry => entry.FullPath.Length))
        {
            var relative = Path.GetRelativePath(rootPath, entry.FullPath);
            if (relative == "." || IsOutsideRoot(relative))
            {
                continue;
            }

            var parts = SplitPath(relative);
            if (parts.Length == 0 || parts.Length > TreeMaximumDepth)
            {
                continue;
            }

            var currentNode = rootNode;
            var currentPath = rootPath;
            foreach (var part in parts)
            {
                currentPath = Path.Combine(currentPath, part);
                var key = Path.TrimEndingDirectorySeparator(currentPath);
                if (!nodes.TryGetValue(key, out var node))
                {
                    node = new FileTreeNode(part, currentPath, true, "\uE8B7");
                    nodes[key] = node;
                    currentNode.Children.Add(node);
                }

                currentNode = node;
            }
        }

        var filesByParent = entries
            .Where(static entry => !entry.IsDirectory)
            .GroupBy(entry => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(entry.FullPath) ?? rootPath), StringComparer.OrdinalIgnoreCase);
        foreach (var group in filesByParent)
        {
            if (!nodes.TryGetValue(group.Key, out var parent))
            {
                continue;
            }

            foreach (var file in group.OrderByDescending(static entry => entry.Length).Take(8))
            {
                parent.Children.Add(new FileTreeNode(file.Name, file.FullPath, false, "\uE8A5")
                {
                    SizeBytes = file.Length
                });
            }
        }

        foreach (var file in entries.Where(static entry => !entry.IsDirectory))
        {
            var parentPath = Path.GetDirectoryName(file.FullPath);
            while (!string.IsNullOrWhiteSpace(parentPath))
            {
                var key = Path.TrimEndingDirectorySeparator(parentPath);
                if (nodes.TryGetValue(key, out var node))
                {
                    node.SizeBytes += file.Length;
                }

                if (string.Equals(key, Path.TrimEndingDirectorySeparator(rootPath), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var next = Path.GetDirectoryName(parentPath);
                if (string.Equals(next, parentPath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                parentPath = next;
            }
        }

        SortAndLimitTree(rootNode);
        FileTreeRoots.Add(rootNode);
        TreeSummary = $"First {TreeMaximumDepth} levels · up to {TreeMaximumChildren} items per folder";
    }

    private static void SortAndLimitTree(FileTreeNode node)
    {
        foreach (var child in node.Children.Where(static child => child.IsDirectory).ToArray())
        {
            SortAndLimitTree(child);
        }

        var ordered = node.Children
            .OrderByDescending(static child => child.IsDirectory)
            .ThenByDescending(static child => child.SizeBytes)
            .ThenBy(static child => child.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var omitted = Math.Max(0, ordered.Length - TreeMaximumChildren);
        node.Children.Clear();
        foreach (var child in ordered.Take(TreeMaximumChildren))
        {
            node.Children.Add(child);
        }

        if (omitted > 0)
        {
            node.Children.Add(new FileTreeNode(
                $"… {omitted:N0} more items not shown",
                node.FullPath,
                false,
                "\uE712",
                isSynthetic: true));
        }
    }

    private void BuildKpis(
        DashboardSnapshot snapshot,
        DuplicateSearchResult? duplicates,
        bool duplicateAnalysisIncomplete = false)
    {
        Kpis.Clear();
        Kpis.Add(new KpiMetric(
            "Data analyzed",
            FormatBytes(snapshot.ScannedBytes),
            $"{snapshot.FileCount:N0} files · {snapshot.DirectoryCount:N0} folders",
            "\uEDA2",
            "BlueBrush",
            string.Empty));
        Kpis.Add(new KpiMetric(
            "Potentially recoverable",
            FormatBytes(snapshot.PotentiallyRecoverableBytes),
            "Local estimate · review required",
            "\uE74D",
            "AccentBrush",
            string.Empty));

        var duplicateCopies = duplicates?.Groups.Sum(static group =>
            Math.Max(0, group.Files.Count(static file => !file.IsHardLinkAlias) - 1));
        Kpis.Add(new KpiMetric(
            "Exact duplicates",
            duplicateAnalysisIncomplete ? "—" : duplicates is null ? "Analyzing" : $"{duplicateCopies:N0}",
            duplicateAnalysisIncomplete
                ? "Check stopped"
                : duplicates is null
                    ? $"Threshold {FormatBytes(DuplicateMinimumBytes)}"
                    : $"{FormatBytes(duplicates.RecoverableBytes)} recoverable in preview",
            "\uE8B7",
            "PurpleBrush",
            string.Empty));

        var reviewCount = snapshot.BySafety.FirstOrDefault(static bucket => bucket.Key == SafetyLevel.ReviewRequired)?.FileCount ?? 0;
        var protectedCount = snapshot.BySafety.FirstOrDefault(static bucket => bucket.Key == SafetyLevel.Protected)?.FileCount ?? 0;
        var issueCount = snapshot.Issues.Count + (duplicates?.Issues.Count ?? 0);
        Kpis.Add(new KpiMetric(
            "Issues",
            issueCount.ToString("N0", CultureInfo.GetCultureInfo("en-US")),
            $"{reviewCount:N0} to review · {protectedCount:N0} protected",
            "\uE7BA",
            issueCount > 0 ? "AmberBrush" : "AccentBrush",
            string.Empty));
    }

    private void BuildInitialKpis()
    {
        Kpis.Clear();
        Kpis.Add(new KpiMetric(
            "System drive",
            SystemDrive.UsedLabel,
            SystemDrive.FreeLabel,
            "\uEDA2",
            "BlueBrush",
            string.Empty));
        Kpis.Add(new KpiMetric("Recoverable", "—", "Scan required", "\uE74D", "AccentBrush", string.Empty));
        Kpis.Add(new KpiMetric("Exact duplicates", "—", "Scan required", "\uE8B7", "PurpleBrush", string.Empty));
        Kpis.Add(new KpiMetric("Issues", "—", "No data analyzed", "\uE7BA", "AmberBrush", string.Empty));
    }

    private void ResetAnalysisForSelectedRoot()
    {
        ClearAnalysisResults();
        SystemDrive = CreateDriveSnapshot(SelectedRoot, "Selected drive");
        BuildInitialKpis();
        ScanStatus = "Ready to scan";
        ScanDetail = SelectedRoot;
        ErrorMessage = string.Empty;
        IssueSummary = string.Empty;
        ScanProgress = 0;
        IsScanIndeterminate = false;
    }

    private void ClearAnalysisResults()
    {
        currentSnapshot = null;
        currentDuplicates = null;
        HasResults = false;
        StorageCategories.Clear();
        TreemapItems.Clear();
        Insights.Clear();
        LargeFiles.Clear();
        FileTreeRoots.Clear();
        AnalyzedSizeLabel = "—";
        DistributionSubtitle = "Scan in progress: data is not available yet";
        RecoverableValue = "—";
        RecoverableUnit = string.Empty;
        RecoverySummary = "Waiting for verified results";
        RecoveryPercentage = 0;
        DuplicateCountLabel = "—";
        IssueCountLabel = "0";
        TreeSummary = "File tree available when the scan is complete";
        BuildInitialKpis();
        LargeFilesView.Refresh();
        OnPropertyChanged(nameof(ResultCountLabel));
    }

    private void AddActivity(DashboardSnapshot snapshot, DuplicateSearchResult duplicates)
    {
        RecentActivities.Insert(0, new ActivityItem(
            "Scan complete",
            $"{snapshot.FileCount:N0} files · {duplicates.Groups.Count:N0} duplicate groups",
            DateTime.Now.ToString("MMM d, h:mm tt", CultureInfo.GetCultureInfo("en-US")),
            "\uE73E",
            "AccentBrush"));
        while (RecentActivities.Count > 5)
        {
            RecentActivities.RemoveAt(RecentActivities.Count - 1);
        }
    }

    private void ToggleTheme()
    {
        try
        {
            ThemeManager.Apply(!IsDarkTheme);
            IsDarkTheme = !IsDarkTheme;
            RefreshThemeSensitiveCollections();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Could not change the theme: {exception.Message}";
        }
    }

    private void RefreshThemeSensitiveCollections()
    {
        SystemDrive = SystemDrive with { };
        ResetCollection(Kpis);
        ResetCollection(StorageCategories);
        ResetCollection(TreemapItems);
        ResetCollection(Insights);
        ResetCollection(RecentActivities);
        ResetCollection(LargeFiles);
        LargeFilesView.Refresh();
    }

    private static void ResetCollection<T>(ObservableCollection<T> collection)
    {
        var items = collection.ToArray();
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private bool MatchesSearch(object item)
    {
        if (item is not LargeFileItem file || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return file.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || file.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || file.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || file.RiskLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static DriveSnapshot CreateDriveSnapshot(string path, string name)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath) ?? fullPath;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return UnavailableDrive(name, root);
            }

            var used = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
            var usedPercentage = drive.TotalSize <= 0 ? 0 : used * 100d / drive.TotalSize;
            var freePercentage = drive.TotalSize <= 0 ? 0 : drive.AvailableFreeSpace * 100d / drive.TotalSize;
            var volumeName = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
            var health = freePercentage switch
            {
                >= 15 => ("Good", "AccentBrush"),
                >= 5 => ("Low space", "AmberBrush"),
                _ => ("Critical space", "RedBrush")
            };
            return new DriveSnapshot(
                name,
                $"{volumeName} ({root.TrimEnd(Path.DirectorySeparatorChar)})",
                drive.DriveFormat,
                FormatBytes(used),
                FormatBytes(drive.TotalSize),
                $"{FormatBytes(drive.AvailableFreeSpace)} free",
                usedPercentage,
                health.Item1,
                health.Item2);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return UnavailableDrive(name, path);
        }
    }

    private static DriveSnapshot UnavailableDrive(string name, string root) =>
        new(name, root, "—", "—", "—", "Capacity unavailable", 0, "Unavailable", "AmberBrush");

    private static string GetSystemRoot()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.GetPathRoot(windows)
            ?? Path.GetPathRoot(Environment.SystemDirectory)
            ?? "C:\\";
    }

    private static RiskLevel MapRisk(SafetyLevel level) => level switch
    {
        SafetyLevel.SafeToClean => RiskLevel.Safe,
        SafetyLevel.ProbablySafe => RiskLevel.Suggested,
        SafetyLevel.ReviewRequired => RiskLevel.Review,
        SafetyLevel.Protected => RiskLevel.Protected,
        _ => RiskLevel.Protected
    };

    private static string GetCategoryLabel(FileCategory category) => category switch
    {
        FileCategory.Document => "Documents",
        FileCategory.Image => "Images",
        FileCategory.Video => "Video",
        FileCategory.Audio => "Audio",
        FileCategory.Archive => "Archives",
        FileCategory.Installer => "Installer",
        FileCategory.SourceCode => "Source code",
        FileCategory.Database => "Database",
        FileCategory.Temporary => "Temporary files",
        FileCategory.Cache => "Cache",
        FileCategory.Log => "Log",
        FileCategory.CrashDump => "Crash dumps",
        FileCategory.System => "System",
        _ => "Other"
    };

    private static string CategoryGlyph(FileCategory category) => category switch
    {
        FileCategory.Video => "\uE714",
        FileCategory.Image => "\uEB9F",
        FileCategory.Audio => "\uE8D6",
        FileCategory.Document => "\uE8A5",
        FileCategory.Archive => "\uF012",
        FileCategory.System => "\uE770",
        FileCategory.SourceCode => "\uE943",
        _ => "\uE8B7"
    };

    private static string[] SplitPath(string relative) =>
        relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static bool IsOutsideRoot(string relative) =>
        relative == ".."
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static (string Value, string Unit) FormatSizeParts(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var digits = value >= 100 || unit == 0 ? 0 : 1;
        return (value.ToString($"N{digits}", CultureInfo.GetCultureInfo("en-US")), units[unit]);
    }

    private static string FormatBytes(long bytes)
    {
        var parts = FormatSizeParts(bytes);
        return $"{parts.Value} {parts.Unit}";
    }

    public void Dispose()
    {
        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        scanCancellation = null;
    }
}
