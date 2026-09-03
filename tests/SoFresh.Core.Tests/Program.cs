using SoFresh.Core.Analysis;
using SoFresh.Core.Domain;
using SoFresh.Core.Information;
using SoFresh.Core.Operations;
using SoFresh.Core.Organization;
using SoFresh.Core.Safety;
using SoFresh.Core.Scanning;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoFresh.Core.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("Scanning, classification, and dashboard", ScanAndDashboardAsync),
        ("Duplicate scan roots are deduplicated", DuplicateScanRootsAsync),
        ("Large-file search and sorting", LargeFileSearchAsync),
        ("Exact duplicates and recoverable-space estimate", DuplicateSearchAsync),
        ("Hard links are not counted as physical copies", HardLinkSearchAsync),
        ("Protected-path policy", ProtectedPathPolicyAsync),
        ("The user profile takes precedence over temporary roots", UserProfilePolicyPrecedenceAsync),
        ("Dry run, quarantine, and undo", SafeOperationsAsync),
        ("Replace is rejected without transactional backup", ReplaceIsConservativelyBlockedAsync),
        ("Operation cancellation preserves the receipt", OperationCancellationReceiptAsync),
        ("Immutable plan and destination revalidation", PlanIntegrityAndDestinationRecheckAsync),
        ("Permanent-delete authorization", PermanentDeleteGateAsync),
        ("Cooperative cancellation", CancellationAsync),
        ("Microsoft Learn search with allowlisted sources", OfficialInformationAsync),
        ("Offline-safe Microsoft Learn search", OfflineInformationAsync),
        ("Organization planner by year and type", OrganizationPlannerAsync),
        ("Organization planner blocks destination junctions", OrganizationPlannerDestinationJunctionAsync)
    ];

    public static async Task<int> Main()
    {
        var failures = new List<string>();
        var skipped = new List<string>();
        Console.WriteLine($"SoFresh Core verification · {Tests.Length} tests");

        foreach (var test in Tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (TestSkippedException exception)
            {
                skipped.Add($"{test.Name}: {exception.Message}");
                Console.WriteLine($"SKIP  {test.Name}");
                Console.WriteLine($"      {exception.Message}");
            }
            catch (Exception exception)
            {
                failures.Add($"{test.Name}: {exception.Message}");
                Console.WriteLine($"FAIL  {test.Name}");
                Console.WriteLine($"      {exception}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0 && skipped.Count == 0
            ? "All tests passed."
            : $"{Tests.Length - failures.Count - skipped.Count} passed, {skipped.Count} skipped, {failures.Count} failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task ScanAndDashboardAsync()
    {
        using var fixture = CreateFixture();
        var progressEvents = new List<ScanProgress>();
        var scanner = new FileSystemScanner();
        var scan = await scanner.ScanAsync(
            new ScanRequest
            {
                Roots = [fixture.Root],
                IncludeDirectories = true,
                FollowReparsePoints = false,
                ProgressReportInterval = 1
            },
            new InlineProgress<ScanProgress>(progressEvents.Add));

        TestAssert.Equal(5L, scan.FileCount, "The scan should find every fixture file.");
        TestAssert.True(scan.DirectoryCount >= 2, "Nested directories should be included.");
        TestAssert.True(scan.TotalFileBytes > 0, "The aggregate size should reflect actual data.");
        TestAssert.True(progressEvents.Count > 0, "The scan should report progress.");
        TestAssert.False(scan.Issues.Any(issue => issue.Kind == ScanIssueKind.CycleDetected), "The fixture contains no cycles.");

        var dashboard = new DashboardAggregator().Build(scan, largestFileCount: 3);
        TestAssert.Equal(scan.FileCount, dashboard.FileCount, "The dashboard should be derived from the actual scan.");
        TestAssert.Equal(3, dashboard.LargestFiles.Count, "The largest-file limit should be respected.");
        TestAssert.True(dashboard.ByCategory.Count > 0, "The category distribution should be calculated.");
        TestAssert.True(dashboard.PotentiallyRecoverableBytes > 0, "Files in the temporary fixture should remain candidates, not automatic actions.");
    }

    private static async Task LargeFileSearchAsync()
    {
        using var fixture = CreateFixture();
        var scan = await ScanFixtureAsync(fixture);
        var results = new LargeFileSearch().Find(
            scan.Entries,
            new LargeFileQuery
            {
                MinimumBytes = 3_000,
                SortDirection = FileSizeSortDirection.Descending
            });

        TestAssert.Equal(4, results.Count, "The threshold should filter out small files.");
        TestAssert.True(results.Zip(results.Skip(1)).All(pair => pair.First.Length >= pair.Second.Length), "Descending sort order should be stable.");
        TestAssert.Equal("archive.zip", results[0].Name, "The largest file should appear first.");
    }

    private static async Task DuplicateScanRootsAsync()
    {
        using var fixture = CreateFixture();
        var scan = await new FileSystemScanner().ScanAsync(new ScanRequest
        {
            Roots = [fixture.Root, fixture.Root],
            IncludeDirectories = true,
            FollowReparsePoints = false
        });

        TestAssert.Equal(5L, scan.FileCount, "A repeated root should not duplicate files.");
        TestAssert.Equal(1, scan.ScannedRoots.Count, "The same root should appear only once in the result.");
        TestAssert.Equal(
            scan.Entries.Count,
            scan.Entries.Select(static entry => entry.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "Each path should be recorded only once.");

        var overlapping = await new FileSystemScanner().ScanAsync(new ScanRequest
        {
            Roots = [fixture.Root, fixture.Resolve("duplicates")],
            IncludeDirectories = true,
            FollowReparsePoints = false
        });
        TestAssert.False(
            overlapping.Issues.Any(static issue => issue.Kind == ScanIssueKind.CycleDetected),
            "An explicitly requested subfolder should not be mistaken for a cycle.");
        TestAssert.Equal(
            overlapping.Entries.Count,
            overlapping.Entries.Select(static entry => entry.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "Parent and child roots should not duplicate entries.");
    }

    private static async Task DuplicateSearchAsync()
    {
        using var fixture = CreateFixture();
        var scan = await ScanFixtureAsync(fixture);
        var result = await new DuplicateFinder().FindAsync(
            scan.Entries,
            new DuplicateSearchOptions
            {
                MinimumFileSize = 1_024,
                SampleSizeBytes = 1_024,
                BufferSizeBytes = 4_096
            });

        TestAssert.Equal(1, result.Groups.Count, "There should be exactly one group of exact duplicates.");
        TestAssert.Equal(2, result.Groups[0].Files.Count, "The group should contain both copies.");
        TestAssert.Equal(4_096L, result.Groups[0].RecoverableBytes, "The estimate should count only one recoverable copy.");
    }

    private static async Task HardLinkSearchAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new FixtureWorkspace();
        var original = fixture.CreateBinaryFile("original.bin", 4_096, 0x44);
        var alias = fixture.Resolve("alias.bin");
        if (!CreateHardLink(alias, original, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the fixture hard link.");
        }

        fixture.CreateBinaryFile("physical-copy.bin", 4_096, 0x44);
        var scan = await ScanFixtureAsync(fixture);
        var result = await new DuplicateFinder().FindAsync(
            scan.Entries,
            new DuplicateSearchOptions
            {
                MinimumFileSize = 1_024,
                SampleSizeBytes = 1_024,
                BufferSizeBytes = 4_096
            });

        TestAssert.Equal(1, result.Groups.Count, "The physical copy should produce one group.");
        TestAssert.Equal(1, result.Groups[0].Files.Count(static file => file.IsHardLinkAlias), "Exactly one path should be marked as a hard-link alias.");
        TestAssert.Equal(4_096L, result.Groups[0].RecoverableBytes, "The hard link should not increase recoverable space.");
    }

    private static Task ProtectedPathPolicyAsync()
    {
        using var fixture = new FixtureWorkspace();
        var protectedRoot = fixture.Resolve("protected");
        Directory.CreateDirectory(protectedRoot);
        var policy = new FileSafetyPolicy(additionalProtectedPaths: [protectedRoot]);
        var result = policy.Evaluate(Path.Combine(protectedRoot, "important.bin"));

        TestAssert.Equal(SafetyLevel.Protected, result.Level, "An alias under a protected root should remain protected.");
        TestAssert.False(result.DirectOperationAllowed, "The policy should prevent direct operations.");
        return Task.CompletedTask;
    }

    private static Task UserProfilePolicyPrecedenceAsync()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return Task.CompletedTask;
        }

        var broadProbablySafeRoot = Path.GetPathRoot(profile)!;
        var policy = new FileSafetyPolicy(additionalProbablySafePaths: [broadProbablySafeRoot]);
        var profileAssessment = policy.Evaluate(profile);
        var childAssessment = policy.Evaluate(Path.Combine(profile, "SoFreshPolicySentinel.bin"));

        TestAssert.Equal(SafetyLevel.Protected, profileAssessment.Level, "The user-profile root should remain protected.");
        TestAssert.Equal(SafetyLevel.ReviewRequired, childAssessment.Level, "An overly broad temporary root should not make the profile eligible for deletion.");
        return Task.CompletedTask;
    }

    private static async Task SafeOperationsAsync()
    {
        using var fixture = new FixtureWorkspace();
        var source = fixture.CreateTextFile(@"source\memo.txt", "reversible content");
        var quarantineRoot = fixture.Resolve("quarantine");
        var operations = new SafeFileOperations();
        var plan = await operations.PlanQuarantineAsync([source], quarantineRoot);

        TestAssert.Equal(FileOperationItemStatus.Planned, plan.Items.Single().Status, "It should be possible to plan quarantine for the fixture.");
        var preview = await operations.ExecuteAsync(plan);
        TestAssert.True(preview.WasDryRun, "The default should always be a dry run.");
        TestAssert.True(File.Exists(source), "A dry run should not modify the source.");

        var receipt = await operations.ExecuteAsync(
            plan,
            new FileOperationExecutionOptions
            {
                DryRun = false,
                ConfirmedByUser = true
            });
        var moved = receipt.Items.Single();
        TestAssert.Equal(FileOperationItemStatus.Completed, moved.Status, "Confirmed quarantine should complete within the fixture.");
        TestAssert.False(File.Exists(source), "After quarantine, the source should no longer exist at its original path.");
        TestAssert.True(File.Exists(moved.DestinationPath!), "The quarantined copy should exist.");

        var restored = await operations.UndoAsync(
            receipt,
            new FileOperationExecutionOptions
            {
                DryRun = false,
                ConfirmedByUser = true
            });
        TestAssert.Equal(FileOperationItemStatus.Completed, restored.Items.Single().Status, "Undo should restore the fixture.");
        TestAssert.True(File.Exists(source), "The source should be restored.");
    }

    private static async Task ReplaceIsConservativelyBlockedAsync()
    {
        using var fixture = new FixtureWorkspace();
        var source = fixture.CreateTextFile("replace/source.txt", "new");
        var otherSource = fixture.CreateTextFile("replace/other.txt", "other");
        var destination = fixture.CreateTextFile("replace/destination.txt", "original");
        var operations = new SafeFileOperations();
        var existingDestinationPlan = await operations.PlanMoveAsync(
            [new MoveSpecification(source, destination)],
            FileConflictResolution.Replace);

        TestAssert.Equal(FileOperationItemStatus.Blocked, existingDestinationPlan.Items.Single().Status, "Replace should be blocked when the destination already exists.");
        await ExpectThrowsAsync<InvalidOperationException>(() => operations.ExecuteAsync(
            existingDestinationPlan,
            new FileOperationExecutionOptions
            {
                DryRun = false,
                ConfirmedByUser = true,
                AllowReplaceExisting = true
            }));
        TestAssert.Equal("original", File.ReadAllText(destination), "Existing content should not be overwritten.");
        TestAssert.True(File.Exists(source), "The source should remain intact when Replace is rejected.");

        var sharedDestination = fixture.Resolve("replace/shared.txt");
        var duplicateDestinationPlan = await operations.PlanMoveAsync(
            [
                new MoveSpecification(source, sharedDestination),
                new MoveSpecification(otherSource, sharedDestination)
            ],
            FileConflictResolution.Replace);
        TestAssert.Equal(FileOperationItemStatus.Planned, duplicateDestinationPlan.Items[0].Status, "The first available destination may be planned.");
        TestAssert.Equal(FileOperationItemStatus.Blocked, duplicateDestinationPlan.Items[1].Status, "Two sources cannot reserve the same destination.");
    }

    private static async Task OperationCancellationReceiptAsync()
    {
        using var fixture = new FixtureWorkspace();
        var sourceOne = fixture.CreateTextFile("cancel/source-one.txt", "one");
        var sourceTwo = fixture.CreateTextFile("cancel/source-two.txt", "two");
        var destinationOne = fixture.Resolve("cancel-output/one.txt");
        var destinationTwo = fixture.Resolve("cancel-output/two.txt");
        using var cancellation = new CancellationTokenSource();
        var policy = new CancelDuringExecutionPolicy();
        var operations = new SafeFileOperations(policy);
        var plan = await operations.PlanMoveAsync(
            [
                new MoveSpecification(sourceOne, destinationOne),
                new MoveSpecification(sourceTwo, destinationTwo)
            ]);
        policy.Arm(cancellation, evaluationCount: 2);

        var receipt = await operations.ExecuteAsync(
            plan,
            new FileOperationExecutionOptions
            {
                DryRun = false,
                ConfirmedByUser = true
            },
            cancellation.Token);

        TestAssert.True(receipt.WasCancelled, "The receipt should record cooperative cancellation.");
        TestAssert.Equal(FileOperationItemStatus.Completed, receipt.Items[0].Status, "A completed change should appear in the receipt.");
        TestAssert.Equal(FileOperationItemStatus.Skipped, receipt.Items[1].Status, "Items that were not started should be marked as skipped.");
        TestAssert.True(receipt.IsReversible, "A partial receipt with completed moves should support undo.");
        TestAssert.True(File.Exists(destinationOne), "The completed side effect should be reconcilable from the receipt.");
        TestAssert.True(File.Exists(sourceTwo), "The next item should not be modified after cancellation.");
    }

    private static async Task PlanIntegrityAndDestinationRecheckAsync()
    {
        using var fixture = new FixtureWorkspace();
        var source = fixture.CreateTextFile("integrity/source.txt", "immutable");
        var destinationRoot = fixture.Resolve("integrity-output");
        var destination = Path.Combine(destinationRoot, "destination.txt");
        var policy = new SwitchableSafetyPolicy();
        var operations = new SafeFileOperations(policy);
        var plan = await operations.PlanMoveAsync([new MoveSpecification(source, destination)]);

        TestAssert.Equal(0, typeof(FileOperationPlan).GetConstructors().Length, "A consumer should not be able to forge a plan through a public constructor.");
        TestAssert.False(plan.Items is FileOperationPlanItem[], "The plan's transitive list should not expose the internal array.");

        policy.Protect(destinationRoot);
        var receipt = await operations.ExecuteAsync(
            plan,
            new FileOperationExecutionOptions
            {
                DryRun = false,
                ConfirmedByUser = true
            });

        TestAssert.Equal(FileOperationItemStatus.Blocked, receipt.Items.Single().Status, "A destination that became protected should be re-evaluated even if it does not exist.");
        TestAssert.True(File.Exists(source), "Blocking the destination should leave the source intact.");
        TestAssert.False(File.Exists(destination), "A protected destination should not be created.");
    }

    private static async Task PermanentDeleteGateAsync()
    {
        using var fixture = new FixtureWorkspace();
        var source = fixture.CreateTextFile("never-delete-without-gate.tmp", "guard");
        var operations = new SafeFileOperations();
        var plan = await operations.PlanPermanentDeleteAsync([source]);

        await ExpectThrowsAsync<InvalidOperationException>(() => operations.ExecuteAsync(
            plan,
            new FileOperationExecutionOptions
            {
                DryRun = false,
                ConfirmedByUser = true,
                AllowPermanentDelete = false
            }));
        TestAssert.True(File.Exists(source), "The authorization gate should leave the file intact.");
    }

    private static async Task CancellationAsync()
    {
        using var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(() => new FileSystemScanner().ScanAsync(
            new ScanRequest { Roots = [fixture.Root] },
            cancellationToken: cancellation.Token));
    }

    private static async Task OfflineInformationAsync()
    {
        using var client = new HttpClient(new ThrowingHandler());
        var provider = new MicrosoftLearnFileInformationProvider(client);
        var result = await provider.SearchAsync(new FileTypeInformationQuery { Extension = ".tmp", Locale = "en-us" });

        TestAssert.True(result.IsOffline, "A network error should produce an offline-safe result.");
        TestAssert.Equal(0, result.Sources.Count, "No source should be fabricated while offline.");
        await ExpectThrowsAsync<ArgumentException>(() => provider.SearchAsync(
            new FileTypeInformationQuery { Extension = @"C:\Users\person\secret.txt", Locale = "en-us" }));
    }

    private static async Task OrganizationPlannerAsync()
    {
        using var fixture = new FixtureWorkspace();
        var source = fixture.CreateTextFile("incoming/report.pdf", "content to organize");
        File.SetLastWriteTimeUtc(source, new DateTime(2022, 7, 15, 12, 0, 0, DateTimeKind.Utc));
        var scan = await ScanFixtureAsync(fixture);
        var destinationRoot = fixture.Resolve("organized");

        var preview = new OrganizationPlanner().BuildPreview(
            scan.Entries,
            new OrganizationPlanRequest
            {
                DestinationRoot = destinationRoot,
                GroupBy =
                [
                    OrganizationGroupingProperty.ModifiedYear,
                    OrganizationGroupingProperty.Category
                ]
            });

        var move = preview.Moves.Single();
        var expectedDestination = Path.GetFullPath(Path.Combine(destinationRoot, "2022", "Document", "report.pdf"));
        TestAssert.Equal(source, move.SourcePath, "The planner should preserve the actual source path.");
        TestAssert.Equal(expectedDestination, move.DestinationPath, "The year-and-type sequence should determine the directory hierarchy.");
        TestAssert.Equal(0, preview.Collisions.Count, "The fixture should not produce collisions.");
        TestAssert.True(File.Exists(source), "The read-only planner should not move the source.");
        TestAssert.False(Directory.Exists(destinationRoot), "The read-only planner should not create the destination root.");
    }

    private static async Task OrganizationPlannerDestinationJunctionAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new FixtureWorkspace();
        var source = fixture.CreateTextFile("incoming/junction-test.pdf", "content");
        File.SetLastWriteTimeUtc(source, new DateTime(2022, 7, 15, 12, 0, 0, DateTimeKind.Utc));
        var destinationRoot = fixture.Resolve("organized");
        var externalRoot = fixture.Resolve("external");
        Directory.CreateDirectory(destinationRoot);
        Directory.CreateDirectory(externalRoot);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(destinationRoot, "2022"), externalRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new TestSkippedException($"The system does not allow the fixture symbolic link to be created: {exception.Message}");
        }

        var scan = await ScanFixtureAsync(fixture);
        var preview = new OrganizationPlanner().BuildPreview(
            scan.Entries.Where(entry => Path.GetFullPath(entry.FullPath) == Path.GetFullPath(source)),
            new OrganizationPlanRequest
            {
                DestinationRoot = destinationRoot,
                GroupBy = [OrganizationGroupingProperty.ModifiedYear]
            });

        TestAssert.Equal(0, preview.Moves.Count, "The planner should not propose moves through a generated junction.");
        TestAssert.True(
            preview.Skipped.Any(static item => item.Reason == OrganizationSkipReason.ReparsePoint),
            "The preview should explain why the reparse point was blocked.");
    }

    private static async Task OfficialInformationAsync()
    {
        const string payload = """
            {
              "results": [
                {
                  "title": "Temporary files in Windows",
                  "url": "https://learn.microsoft.com/en-us/windows/configuration/storage/storage-sense",
                  "description": "Official storage documentation",
                  "lastUpdatedDate": "2026-01-01T00:00:00Z"
                },
                {
                  "title": "Untrusted result",
                  "url": "https://example.invalid/delete-everything",
                  "description": "Must be ignored"
                }
              ]
            }
            """;
        using var client = new HttpClient(new JsonHandler(payload));
        var provider = new MicrosoftLearnFileInformationProvider(client);
        var result = await provider.SearchAsync(new FileTypeInformationQuery { Category = FileCategory.Temporary });

        TestAssert.False(result.IsOffline, "A valid response should be processed online.");
        TestAssert.Equal(1, result.Sources.Count, "Sources outside learn.microsoft.com should be discarded.");
        TestAssert.Equal(InformationConfidence.OfficialMicrosoftSource, result.Sources[0].Confidence, "The allowlisted source should be marked as official.");
        TestAssert.False(result.SanitizedQuery.Contains("Users", StringComparison.OrdinalIgnoreCase), "The query should not contain user paths.");
    }

    private static FixtureWorkspace CreateFixture()
    {
        var fixture = new FixtureWorkspace();
        fixture.CreateTextFile(@"notes\note.tmp", "temporary");
        fixture.CreateBinaryFile(@"duplicates\copy-a.bin", 4_096, 0x2A);
        fixture.CreateBinaryFile(@"duplicates\copy-b.bin", 4_096, 0x2A);
        fixture.CreateBinaryFile(@"unique\different.bin", 4_096, 0x31);
        fixture.CreateBinaryFile("archive.zip", 8_192, 0x7F);
        return fixture;
    }

    private static Task<ScanResult> ScanFixtureAsync(FixtureWorkspace fixture) =>
        new FileSystemScanner().ScanAsync(new ScanRequest
        {
            Roots = [fixture.Root],
            IncludeDirectories = true,
            FollowReparsePoints = false
        });

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new TestFailureException($"Expected an exception of type {typeof(TException).Name}.");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CancelDuringExecutionPolicy : IFileSafetyPolicy
    {
        private CancellationTokenSource? cancellation;
        private int remainingEvaluations = int.MaxValue;

        public void Arm(CancellationTokenSource source, int evaluationCount)
        {
            cancellation = source;
            remainingEvaluations = evaluationCount;
        }

        public SafetyAssessment Evaluate(FileEntry entry) => Evaluate(entry.FullPath, entry.Attributes);

        public SafetyAssessment Evaluate(string path, FileAttributes? attributes = null)
        {
            var source = cancellation;
            if (source is not null && Interlocked.Decrement(ref remainingEvaluations) == 0)
            {
                cancellation = null;
                source.Cancel();
            }

            return Allowed(path);
        }
    }

    private sealed class SwitchableSafetyPolicy : IFileSafetyPolicy
    {
        private string? protectedRoot;

        public void Protect(string root) => protectedRoot = Normalize(root);

        public SafetyAssessment Evaluate(FileEntry entry) => Evaluate(entry.FullPath, entry.Attributes);

        public SafetyAssessment Evaluate(string path, FileAttributes? attributes = null)
        {
            var normalized = Normalize(path);
            if (protectedRoot is not null
                && (string.Equals(normalized, protectedRoot, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(protectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                return new SafetyAssessment(
                    normalized,
                    SafetyLevel.Protected,
                    "Protection was enabled after planning.",
                    "Do not modify.",
                    false);
            }

            return Allowed(normalized);
        }

        private static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static SafetyAssessment Allowed(string path) =>
        new(
            path,
            SafetyLevel.ProbablySafe,
            "Controlled fixture.",
            "Operation allowed in the test.",
            true);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline fixture"));
    }

    private sealed class JsonHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
