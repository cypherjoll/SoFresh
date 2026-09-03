# SoFresh

SoFresh is a Windows desktop vertical slice that analyzes a folder selected by the
user and makes its storage usage easy to understand at a glance. The application is
written in C#/.NET 10 with WPF and connects the UI directly to the read-only services
in `SoFresh.Core`.

> Status as of September 3, 2026: the UI analyzes real data but operates exclusively
> in preview mode. It exposes no commands for deletion, moving, quarantine, or
> Windows cleanup.

## Quick start

Requirements:

- Windows;
- the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), using
  the version selected by `global.json`.

From PowerShell, at the repository root:

```powershell
dotnet build SoFresh.slnx -c Release
dotnet run --project src/SoFresh.App/SoFresh.App.csproj -c Release
```

In the app:

1. select a folder;
2. start the scan;
3. review the KPIs, distribution, treemap/tree, recommendations, and large files;
4. use **Cancel scan** to stop the operation cooperatively.

To run the verification runner:

```powershell
dotnet run --project tests/SoFresh.Core.Tests/SoFresh.Core.Tests.csproj -c Release
```

The latest Release verification, performed on September 3, 2026, produced `0`
warnings, `0` errors, and `17/17` passing tests.

## Implemented features

- selection of a single folder through a Windows dialog;
- real, asynchronous, cancellable scanning, with progress updates and reporting of
  inaccessible or changed items;
- local classification by type, user context, topic, and risk level;
- KPIs and summaries derived from the same real scan snapshot;
- category distribution and a compact treemap;
- a condensed filesystem tree designed to keep the UI responsive;
- a view of the largest files, sortable and filterable by name, path, category, or
  risk;
- exact duplicate detection by size, sample hash, and full SHA-256, with hard-link
  aliases handled separately;
- a conservative estimate of potentially recoverable space;
- light and dark themes;
- disabled and visually muted controls for backlog features, with tooltips that
  explain their status;
- `asInvoker` execution, with no automatic elevation.

The UI currently uses a 10 MiB threshold for the large-file view and a 1 MiB
threshold for duplicate detection. The tree is intentionally summarized: it shows
up to four levels and limits the number of items per node. It is not yet a complete,
lazy-loaded, virtualized file explorer.

## Security

The WPF surface is read-only: a scan never deletes or moves files. Reparse points
are not traversed by default, and access errors do not trigger UAC, ACL changes, or
ownership takeover.

The Core contains `OrganizationPlanner` and `SafeFileOperations`. The planner
produces previews only. File operations are path-based, use dry-run by default, and
are covered by isolated fixtures, but remain experimental and **are neither wired
to the UI nor approved for use on real data**. Read the [security
model](docs/SECURITY.md) before changing or exposing them.

Current Core hardening includes:

- deduplication of equivalent roots requested from the scanner;
- protection of the user-profile root taking precedence over broader rules that
  might classify it as probably safe;
- exclusion of reparse points, plus blocking of existing path segments that cross
  them in `OrganizationPlanner` previews;
- read-only copies of item collections and non-public constructors for plans and
  receipts, preventing external consumers from mutating their internal collections;
- a partial receipt with `WasCancelled=true` when execution is cancelled: completed
  outcomes remain recorded and items that were not started are marked as skipped;
- intentional rejection of the `Replace` strategy until a verifiable transactional
  backup of the destination exists.

## Components present but not exposed in the UI

- `MicrosoftLearnFileInformationProvider`: sanitized searches on
  `learn.microsoft.com`, with source filtering and an offline fallback;
- `OrganizationPlanner`: organization previews based on properties such as year and
  type;
- `SafeFileOperations`: planning/dry-run support and experimental primitives for
  move, quarantine, restore, and permanent deletion with explicit gates; `Replace`
  is disabled.

The presence of these components in the Core does not make them user-facing
features.

## Current limitations

The following are not implemented:

- a SQLite index or journal, scan persistence, and resume after restart;
- the Windows Recycle Bin through `IFileOperation`, and durable/crash-safe
  quarantine;
- isolated workers, an elevated broker, packaging, or code signing;
- supported cleanup of Windows, `Windows.old`, WinSxS, Windows Update, or Storage
  Sense;
- an “Is it safe to delete?” UI panel and persistent caching of Microsoft sources;
- organization, move, deletion, persistent history, or undo UI;
- a complete explorer with lazy loading, breadcrumbs, and large-scale
  virtualization;
- benchmarks and a validated Windows support matrix for a production release.

## Structure

```text
src/
  SoFresh.App/          WPF/MVVM UI, themes, and real-snapshot mapping
  SoFresh.Core/         scanning, classification, analysis, policy, and Core components
tests/
  SoFresh.Core.Tests/   console runner with isolated temporary fixtures
docs/
  ARCHITECTURE.md       current state, target architecture, and gates
  SECURITY.md           invariants, risks, and requirements before mutations
  BACKLOG.md            prioritized work
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Security model](docs/SECURITY.md)
- [Backlog](docs/BACKLOG.md)

Runtime choices follow the [.NET support
policy](https://dotnet.microsoft.com/en-us/platform/support/policy); WPF desktop APIs
are documented in the [.NET Desktop
SDK](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop).
