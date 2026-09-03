# Prioritized backlog

The read-only vertical slice is connected to the Core: folder selection,
scan/cancel, dashboard, treemap/tree, large files, and duplicate detection all use
real data. The planner, Microsoft Learn provider, and `SafeFileOperations` remain
unexposed Core components. UI controls that anticipate backlog features are disabled
and visually muted; they do not represent available actions.

P0 items are security gates and must not be bypassed to accelerate a release.

## P0 — before enabling real cleanup or move operations

- Separate the UI, read-only scanner, STA Shell worker, and elevated broker into
  boundaries with typed operations; the main process remains `asInvoker`.
- Replace path-based validation with handle-based identity
  (`VolumeSerialNumber + FileId`), no-follow opening, parent verification, and
  immediate revalidation at commit time.
- Extend policy coverage to Known Folders, all volumes, other users' profiles,
  package stores, registry hives, cloud placeholders, EFS, ADS, shares, mount points,
  and unknown reparse tags.
- Implement the Recycle Bin through `IFileOperation` with no permanent fallback, and
  same-volume quarantine with per-user ACLs and a crash-safe SQLite journal.
- Always distinguish selected, secured, and actually freed space; measure the volume
  again after a purge.
- Keep `Replace` disabled until the destination has a transactional backup, journal,
  and recovery verified across crashes.
- Cover TOCTOU, junction swaps, hard links, files changing during processing, crash
  recovery, and cross-volume moves in disposable VMs/VHDXs.
- Sign and constrain the broker; it must accept no arbitrary command, executable, or
  argument.

## P1 — completing read-only analysis and organization

- Add incremental SQLite indexing and resumable sessions, with migrations and
  configurable retention.
- Replace the current condensed tree with a lazy, virtualized explorer featuring
  breadcrumbs, metadata details, composable filters, and drill-down synchronized
  with charts and the treemap.
- Add USN Journal acceleration with a mandatory full-scan fallback.
- Expose a visual rule builder over the existing `OrganizationPlanner` for year,
  month, type, extension, topic, and context, with collision and destination
  previews.
- Complete the integrated duplicate analysis with a group view, a recommendation of
  which copy to retain, explicit reasoning, and revalidation before any action.
- Connect the existing Microsoft Learn provider to an “Is it safe to delete?” panel
  with sources, date, confidence, persistent cache, and a clear statement that the
  information does not authorize deletion.
- Replace the current session activity with persistent history, undo, and partial-
  outcome reconciliation, only after the P0 gates are complete.
- Open Storage/Cleanup Recommendations pages and provide DISM analysis in
  informational mode; never delete Windows remnants directly.

## P2 — scale, accessibility, and distribution

- Benchmark 100,000, 1 million, and 5 million entries, with verified budgets for
  RAM, startup time, hashing, and UI updates.
- Add progress coalescing, bounded batches, backpressure, and end-to-end
  pagination/virtualization.
- Add automated tests for keyboard navigation, screen readers, High Contrast,
  125–300% DPI, and themes.
- Provide a signed MSIX/MSI installer, authenticated updates, an SBOM, and a release
  pipeline.
- Add more localizations, a redacted diagnostic export, and operational
  documentation.
- Add optional, more advanced topic analysis, always local or explicitly opt-in.
