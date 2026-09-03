# SoFresh security model

> Document status: review of the August 24, 2026 vertical slice. Sections labeled
> **Implemented today** describe code present in the repository; **Target** and
> **release gate** sections specify requirements, not currently available features.

SoFresh analyzes data and proposes actions that could permanently destroy
information or make Windows unbootable. Security therefore cannot depend on a UI
label, a single list of paths, or a generic confirmation. The baseline rule is:
read without privileges, make mutations reversible by default, and explicitly
refuse an operation when the file's identity or context is uncertain.

## Invariants

1. Scanning does not modify the filesystem and does not require elevation.
2. Every mutation originates from an immutable plan shown to the user and a
   confirmation tied to that plan, never from a generic button.
3. A `Protected` item remains protected regardless of online search results. A web
   source may raise risk, but can never lower it or authorize deletion.
4. Policy is applied to the object's final identity, after controlled resolution of
   aliases, mount points, and links, immediately before the mutation.
5. An access error never triggers an automatic UAC request, ACL change, ownership
   takeover, or activation of special privileges.
6. If the Recycle Bin or quarantine is unavailable, the operation fails. There is
   no silent fallback to permanent deletion.
7. “Selected” space, “secured” space, and space actually freed are different
   metrics. Moving data on the same volume or into the Recycle Bin does not free
   space until that data is actually deleted.
8. Cooperative cancellation does not lose track of effects that already occurred:
   it must return a partial receipt, set `WasCancelled`, and classify items that were
   not started as skipped.
9. `Replace` remains prohibited until the destination has a transactional backup,
   durable journal, and verified recovery.

## Implemented today and differences from the target

| Control | Implemented in the vertical slice | Remaining limitation |
| --- | --- | --- |
| UI privileges | `SoFresh.App` declares `asInvoker` in its manifest. | Separate workers and an elevated broker do not yet exist. |
| Scanning | `FileSystemScanner` is cancellable, deduplicates equivalent roots, reports errors, and does not follow reparse points by default. | It runs in-process, does not record NTFS tags/identity, and `FollowReparsePoints=true` is not a safe basis for mutations. |
| Policy | `FileSafetyPolicy` blocks system files, declared reparse points, Windows/application roots, and selected upgrade/boot roots. The user-profile root takes precedence over any overlapping `ProbablySafe` root; ordinary files default to `ReviewRequired`. | Verification is lexical/path-based; it does not use Win32 Known Folders, handles, file IDs, final paths, owners, reparse tags, cloud state, or EFS. |
| Organization | `OrganizationPlanner` builds a read-only preview of destinations and collisions by properties such as year and category; it excludes reparse entries and blocks source/destination segments that cross an existing reparse point. | It is not connected to the UI and neither authorizes nor performs moves. |
| Operations | `SafeFileOperations` creates plans and receipts that cannot be constructed or mutated externally, uses dry-run by default, requires explicit confirmations, rechecks policy/snapshots, and does not delete directories recursively. Cancellation returns a partial receipt with `WasCancelled`; move, quarantine, restore, and permanent deletion are present. `Replace` is always rejected. | Actions use path-based `File.*`/`Directory.*` APIs, receipts exist only in memory, and durable quarantine is missing. The permanent-delete API is experimental, not connected to the UI, and **is not an approved production surface**. |
| Duplicates | Grouping by size, sample hash, and full SHA-256; length/date checks during hashing; and distinction of hard-link aliases when Windows returns an identity. The UI runs the pipeline and displays real KPIs, estimates, and issues. | The UI does not yet expose group members or actions. Identity uses the legacy `GetFileInformationByHandle` API, is not preserved in the mutation plan, and does not eliminate the race after hashing. |
| Online lookup | The provider accepts only a generic extension/category, queries `learn.microsoft.com`, restricts results to HTTPS hosts, and continues offline without authorizing actions. | It is not connected to the UI or policy. No Microsoft engine provides a universal judgment that an arbitrary file can be deleted. |
| Dashboard | `SoFresh.App` is connected to the Core: folder selection, scan/cancel, KPIs, categories, treemap/tree, large files, and duplicates come from real data. The UI identifies preview-only mode; backlog controls are disabled and visually muted. | The tree is condensed and in-memory. There is no persistence, complete lazy explorer, drill-down for every candidate, or mutation command. |

## Risk-level matrix

Classification is conservative and monotonic: uncertainty always raises the level.

| Level | Minimum conditions | Action allowed in the target | Vertical-slice status |
| --- | --- | --- | --- |
| `SafeToClean` | Rebuildable artifacts created by SoFresh, or items directly managed by a documented Microsoft mechanism. The object is local, owned by the current user, not reparse/cloud/EFS, and unchanged. | Preview; Recycle Bin or quarantine. Never automatic direct deletion. | The enum exists, but the current policy does not assign this level to arbitrary files. |
| `ProbablySafe` | Cache/temp/log/dump data belonging to the current user and recognized by a specific rule, with an age threshold and no active application use. | Group selection, rule explanation, and unelevated quarantine/Recycle Bin. | Currently assigned mainly beneath the current temp directory; owner, use, cloud state, and identity are not yet verified. |
| `ReviewRequired` | Downloads, documents, large/old files, duplicates, installers, unrecognized AppData, other volumes, multiple hard links, or incomplete metadata. | Per-item selection and explicit confirmation, with a preference for reversible action. | The default for files not covered by rules. Execution requires `AllowReviewRequiredItems=true`. |
| `Protected` | Windows/application paths or names, reparse points, system files, other users' profiles, out-of-scope destinations, or unverifiable identity. | No direct mutation. A typed, official Microsoft tool may be delegated to when appropriate. | Coverage is partially path-based and must be extended before mutations are exposed in the UI. |

## Always-protected paths and actions

“Protected” means direct delete, move, replace, quarantine, and ACL changes are
prohibited. Real paths must be obtained through [Known Folders and
`SHGetKnownFolderPath`](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath),
not by assuming `C:\Windows` or `C:\Users`. Microsoft documents the [Known Folder
IDs](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid).

| Protected group | Examples | Current coverage |
| --- | --- | --- |
| Windows and servicing | Windows, System/SystemX86, `System32\Config`, `WinSxS`, `servicing`, `SoftwareDistribution`, `SystemApps` | The Windows root is blocked; subfolders are blocked through lexical descent. |
| Installer, driver, and package stores | `%WINDIR%\Installer`, `System32\DriverStore`, Program Files/Common Files, `WindowsApps`, and package volumes on every drive | Local Windows/Program Files are blocked; package volumes on other drives remain backlog. |
| Boot, recovery, and volume metadata | `EFI`, `Boot`, `Recovery`, `System Volume Information`, and volume roots | The Windows volume root, Boot, and Recovery are covered; mounts/other volumes and `System Volume Information` require handle-based rules. |
| Special system files | `pagefile.sys`, `swapfile.sys`, `hiberfil.sys`, boot manager, and the `SAM`, `SECURITY`, `SYSTEM`, `SOFTWARE`, and `DEFAULT` hives | Main paging/hibernation/boot names are blocked; registry hives need additional explicit coverage. |
| Profiles | Profile roots, profiles other than the current user's, `NTUSER.DAT`, `UsrClass.dat` | The current profile root is blocked with precedence over overlapping `ProbablySafe` roots; other profiles and user hives remain backlog. |
| Upgrade and rollback | `Windows.old`, `$WINDOWS.~BT`, `$WINDOWS.~WS` | All three roots on the Windows volume are blocked directly. |
| Internal management | `$Recycle.Bin`, and active SoFresh databases, logs, journals, and quarantine | `$Recycle.Bin` on the Windows volume is blocked; SoFresh artifacts do not yet exist and will need to be excluded. |
| Untrusted boundaries | UNC/network shares, cloud placeholders, removable media, mount points, unknown reparse tags, targets outside the scanned root, ADS | Declared reparse points are blocked; all other cases remain backlog and must fail closed. |

The following are always prohibited:

- taking ownership, changing ACLs, or enabling `SeBackupPrivilege`,
  `SeRestorePrivilege`, or `SeTakeOwnershipPrivilege` to “make cleanup work”;
- deleting at restart, following junctions/symlinks during a mutation, or using
  recursive deletion on an untrusted tree;
- replacing a destination without a preview and specific consent;
- running arbitrary elevated commands or accepting an executable path from the
  client;
- secure erase/free-space wiping and cleanup of other users' profiles in the MVP.

Microsoft documents that `SeBackupPrivilege` and `SeRestorePrivilege` can bypass
normal access checks; [`AdjustTokenPrivileges` cannot add privileges absent from the
token](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges).
Available privileges are described in the [official
list](https://learn.microsoft.com/en-us/windows/win32/secauthz/privilege-constants).

## TOCTOU, links, mount points, and hard links

### Current risk

`Path.GetFullPath`, case-insensitive comparison, and a length/date/attribute snapshot
reduce accidental errors, but do not form a security boundary. Between preview and
action, an attacker or concurrent process can replace a file, parent, or destination
with a symlink, junction, or mount point while preserving the same visible metadata.
The current path-based APIs (`File.Move`, `File.Delete`, `Directory.Move`,
`Directory.Delete`) act on the name resolved at that moment. Microsoft documentation
shows that [reparse points can represent many kinds of
objects](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points) and
that ordinary APIs may follow [symbolic
links](https://learn.microsoft.com/en-us/windows/win32/fileio/symbolic-link-effects-on-file-systems-functions).

Hard links are not physical duplicates: multiple names can refer to the same file
on one volume. Deleting one name does not necessarily recover the displayed logical
size. See the documentation on [hard links and
junctions](https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions).

### Target protocol before enabling mutations

1. During scanning, open the object with minimal access and
   `FILE_FLAG_OPEN_REPARSE_POINT` (plus `FILE_FLAG_BACKUP_SEMANTICS` for a directory)
   without traversing it. [`CreateFile`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
   documents these flags.
2. Record attributes and reparse tag, volume, final path, owner, logical/allocation
   size, link count, and `(VolumeSerialNumber, FileId)` identity. Reference structures
   are [`FILE_ID_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)
   and [`FILE_STANDARD_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info);
   obtain the final path with
   [`GetFinalPathNameByHandle`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew).
3. Do not traverse reparse points by default. Any read-only exploration must accept
   known tags only, remain within the same root/volume, and use a visited-identity
   set to stop cycles.
4. Immediately before a mutation, reopen the source, parent, and destination in
   no-follow mode, resolve the final path, and reapply the full policy. If ID,
   volume, tag, owner, parent, length, or timestamp differs, abort and require a new
   scan.
5. For same-volume quarantine, use a handle-bound rename into an already opened and
   verified destination directory through
   [`SetFileInformationByHandle`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)
   and [`FILE_RENAME_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info).
6. Process directories leaf-first, one item at a time; never recurse blindly.
7. A cross-volume move is copy + hash verification + source removal. It is not
   atomic and can change ACLs. The preview, destination space, collisions, recovery,
   and partial failures must be explicit.
8. When introduced, the elevated broker repeats every check server-side; it does not
   trust a decision or path previously “validated” by the UI.

Reading an owner by name also has a documented race in
[`GetNamedSecurityInfo`](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getnamedsecurityinfow),
so mutation must rely on verified handles. `LastAccessTime` cannot be the sole proof
that a file is old or unused: Windows may disable or defer updates, as specified in
[`GetFileTime`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfiletime).

## Recycle Bin, quarantine, and deletion

In the target, the Recycle Bin uses
[`IFileOperation`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation)
on a dedicated STA worker, never on the UI thread. Recoverable deletion uses
`FOFX_RECYCLEONDELETE` and, where available, `FOFX_ADDUNDORECORD`. These flags and
the default behavior of not traversing junctions are documented under
[`SetOperationFlags`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags).
`FOFX_NOSKIPJUNCTIONS` is prohibited.

`PerformOperations` can report success even after user cancellation, so the worker
must call
[`GetAnyOperationsAborted`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-getanyoperationsaborted)
and record each item's outcome. `IFileOperation` does not expose a transactional
delete primitive bound to a file handle, leaving a residual race. The Recycle Bin is
therefore allowed only for local, non-reparse trees owned by the current user, with
final revalidation and no permanent fallback. Network resources must not be
presented as recoverable from the local Recycle Bin.

The target quarantine is a per-volume directory with an ACL limited to the user's
SID, an append-only journal, and persistent receipts. A crash must leave every item
in a reconcilable state (`planned`, `moved`, `restored`, `purged`, `failed`).
Permanent deletion remains a separate expert action and is never bulk-by-default.
[`SHEmptyRecycleBin`](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shemptyrecyclebinw)
is public but permanent, and may be exposed only as an explicit purge.

## Official procedures for Windows remnants

SoFresh does not treat Windows remnants as ordinary files. The UI may explain,
analyze, or delegate a documented operation, but cannot delete their directories
directly.

| Object | Supported procedure | Prohibition and public API status |
| --- | --- | --- |
| `Windows.old` / previous version | Open Settings > System > Storage > Cleanup recommendations (`ms-settings:storagerecommendations`) and have the user confirm “Previous Windows installation(s).” Microsoft warns that removal is irreversible and eliminates rollback. | No recursive deletion. No dedicated public API is documented that returns both a preview and structured outcome. [Microsoft procedure](https://support.microsoft.com/en-us/windows/deployment/install-upgrade/delete-your-previous-version-of-windows). |
| `$WINDOWS.~BT` | Preserve it while rollback may be needed; the recovery procedure depends on this folder as well as `Windows.old`. | No direct mutation. [Rollback requirements](https://support.microsoft.com/en-us/windows/deployment/install-upgrade/go-back-to-the-previous-version-of-windows). |
| `WinSxS` component store | First run `Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore`; in a separate, confirmed action run `Dism.exe /Online /Cleanup-Image /StartComponentCleanup`, or the Microsoft task `\Microsoft\Windows\Servicing\StartComponentCleanup`. | Never delete files from the folder. `/ResetBase` is absent from the normal UI because it prevents installed updates from being uninstalled. [Analysis](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/determine-the-actual-size-of-the-winsxs-folder?view=windows-11), [cleanup](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder?view=windows-11), [DISM options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options?view=windows-11). |
| Windows Update cleanup | Use Storage/Cleanup Recommendations, Disk Cleanup, or supported DISM servicing. | Never delete `SoftwareDistribution` directly. `cleanmgr` has a documented CLI, but no structured contract for per-file preview, rollback, and machine-readable results. [`cleanmgr`](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cleanmgr). |
| Storage Sense | Open the appropriate Settings page and leave control with the user; honor existing policies in managed environments. | Public documentation exposes UI and policy/CSP, not a supported “Run now” API with candidate enumeration and outcomes. This is an inference from the documented surface, not proof that internal APIs do not exist. [Storage Sense](https://learn.microsoft.com/en-us/windows/configuration/storage/storage-sense), [Policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-storage), [Settings URIs](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings). |
| Delivery Optimization | Leave management to Windows or use Disk Cleanup; any expert integration must use the documented cmdlet. | Never delete the cache manually. [Cache behavior](https://support.microsoft.com/en-us/windows/deployment/updates-lifecycle/delivery-optimization-in-windows), [cache management and cmdlet](https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-monitor). |
| Windows Installer cache | SoFresh performs no cleanup. | `%WINDIR%\Installer` must remain intact; missing files can prevent updates, repair, and uninstall. [Microsoft guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/missing-windows-installer-cache). |
| Driver Store | Out of MVP scope; allow only a dedicated administrative workflow with inventory and confirmation. | Never modify `DriverStore` directly. Remove packages only through PnPUtil/`DiUninstallDriver`. [Driver Store](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-store), [package removal](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-devices-and-driver-packages-are-uninstalled). |
| `WindowsApps` / MSIX | No file-level cleanup. Uninstall or repair packages through Windows. | Content is protected and read-only; tampering may prevent apps from starting. [MSIX internals](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes). |
| `hiberfil.sys` | A separate expert feature may run `powercfg /hibernate off` while explaining that it disables hibernation. | Never delete the file directly. [`powercfg`](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options). |

The [documented DISM
API](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism/dism-api-functions?view=windows-11)
does not list a function equivalent to `StartComponentCleanup`; integration must
therefore use the official CLI/PowerShell/task and must not simulate a private API.
The broker accepts typed operations such as `AnalyzeComponentStore` and
`StartComponentCleanup`, with a known absolute executable and fixed arguments, never
arbitrary command strings.

## Online lookup and privacy

A remote query may contain only a generic token: an extension (`.cab`), category, a
known Windows name without user data, and the Windows build. Full paths, usernames,
SIDs, hashes, content, project names, and document fragments are prohibited. Allowed
sources are Microsoft HTTPS pages; the UI displays the URL, date, confidence, and
the message “informational; does not authorize deletion.”

No public Microsoft API determines whether **every** arbitrary file is necessary.
There is also no documented public feed of Cleanup Recommendations candidates.
These limits must be stated in the UI, not bypassed through scraping or heuristics
presented as certainty.

## Target privilege model

The UI remains `asInvoker`. Under the [UAC broker
model](https://learn.microsoft.com/en-us/windows/win32/secauthz/administrator-broker-model)
and [application manifest
documentation](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests),
the rare administrative actions go through a signed, short-lived helper launched
with `runas`. The protocol uses versioned messages and allowlisted operations, and
verifies SID, session, client integrity, nonce, and expiry. The broker recalculates
policy and accepts no executable, working directory, or free-form arguments from the
client.

## MVP limitations

The vertical slice is a read-only analyzer connected to the Core. It does not
include, and must not claim to provide:

- real cleanup of Windows, Storage Sense, Windows.old, or WinSxS;
- Recycle Bin support through `IFileOperation`, an STA worker, or an elevated broker;
- durable quarantine, a SQLite index/journal, crash-safe recovery, or persistent
  auditing;
- handle-based TOCTOU verification, Win32 Known Folders, cloud/EFS/ADS state,
  ownership, or reparse tags;
- a complete lazy/virtualized explorer, bulk organization, persistent history, or a
  detailed duplicate-group view; the current tree is a condensed projection of real
  results;
- universal calculation of physically recoverable space for sparse, compressed,
  deduplicated, hard-linked, or cloud-placeholder files;
- support for network shares, other users' profiles, secure erase, or driver/package
  cleanup;
- an automatic decision that “this Windows file can be deleted.”

## Acceptance gates before exposing mutations

The following criteria remain release backlog until demonstrated by automated tests
and tests on disposable VMs/VHDXs:

- aliases with different casing, `\\?\`, volume GUIDs, 8.3 names, trailing dots or
  spaces, ADS, mounts, junctions, and symlinks cannot bypass any protected root;
- if a benign directory is replaced with a junction to a protected target between
  preview and commit, the action aborts and the target remains intact;
- loops and unknown reparse tags do not cause traversal, hangs, or duplicate counts;
- an out-of-root link is visible as an issue but contributes neither candidates nor
  space;
- hard links with the same file ID are not recoverable duplicates; deleting one
  alias does not attribute the full size as freed space;
- a file changed during sample/full hashing is excluded and never enters a group;
- `LastAccessTime` is never the only reason for proposing cleanup;
- when `IFileOperation` is cancelled, `GetAnyOperationsAborted` produces an
  incomplete receipt; when the Recycle Bin is unavailable, no permanent deletion
  occurs;
- cross-volume copy verifies the hash, space, collisions, and ACL behavior before
  removing the source, and recovers deterministically from an intermediate crash;
- `Replace` remains blocked until a transactional destination backup, journal, and
  post-crash recovery are implemented and verified;
- the quarantine journal supports reconciliation and restore after process
  termination or power loss;
- `AnalyzeComponentStore` mutates nothing; `StartComponentCleanup` requires a new
  confirmation; `/ResetBase` is unavailable in the standard UI;
- no Windows.old/WinSxS/Installer/DriverStore/WindowsApps path is passed to
  `File.Delete`, `Directory.Delete`, `Move`, or replace;
- online queries and logs contain no user data, and no online result can lower
  `Protected`;
- the dashboard never calls merely moved/quarantined space “freed,” and reads
  available space again with
  [`GetDiskFreeSpaceEx`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexa)
  after an actual purge;
- destructive tests run only in isolated fixtures, VHD/VHDXs, or disposable Windows
  VMs, never on a developer's real profile or volume.
