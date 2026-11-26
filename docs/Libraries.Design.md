# Libraries design

This is how Libraries work on the server and in the Windows client today. [Libraries](Libraries.md) describes what
users see. Earlier versions of this document, with the experiments and evidence behind each decision, are in git
history; the experiment reports are still in [`docs/design`](design/).

## What a Library is

A Library is a folder in a repository whose files sync between working copies but never enter version control: no
Saves, References, DirectoryVersions or attachments. A repository's catalog lists its Library roots: at most 128,
non-overlapping, repository-relative paths.

Each item has a stable ID, and it's placed by its parent and name, not by a stored path. The parent is either a
Library root or a directory item. Full paths are derived, so renaming a directory changes one record, not every
descendant.

## Who owns what

One `RepositoryLibraryActor` per repository orders every Library change. It's an Orleans grain, so it handles one call
at a time. It writes through the registered Orleans storage providers and uses small modules in `src/Grace.Actors`:

| Module | Job |
| --- | --- |
| `LibraryDecision` | Pure rules: names, slot keys, conflict-copy names, path checks. |
| `LibraryRecords` | Typed reads and writes through the providers, with a reread after an uncertain write. |
| `LibraryQueries` | Cosmos SQL reads: change pages, current items, live children. |
| `LibraryTransfer` | The existing upload, content-counter and manifest-workflow actors. |
| `LibraryTokens` | Signed cursors, bootstrap page tokens and content-read tokens. |
| `LibraryWake` | Starts the wake after a commit without waiting for it. |

## What the server stores

Five Cosmos containers hold Library records.

| Record | Container | What it holds and why |
| --- | --- | --- |
| Control | `grace-library-control` | The catalog, the committed cursor, at most one saved (`Pending`) decision, and the item and slot record counts that enforce capacity. |
| Accepted change | `grace-library-changes` | One immutable record per accepted change, kept forever: the change, the request hash, the placement it vacated and whether it added an item or slot record. |
| Current item and slot | `grace-library-current` | The latest state of each item and each parent/name slot, with the cursor that wrote it, so admission can check preconditions without replaying the journal. Also the retained content descriptor for each accepted content version. |
| Receipt | `grace-library-receipts` | The permanent result of each operation ID, so a retry gets the original answer. |
| Baseline | `grace-library-baselines` | Immutable shards of current state and a manifest written last, for copies that join. |

The journal is permanent and contiguous: cursors run from 1 to the committed cursor with no gaps. That's why the
server needs no history index, replay floor, epoch or rebaseline. Anything that needs history can read the journal.

## Accepting a change

Every call first finishes any saved decision. Then `Submit`:

1. Returns the recorded receipt if this operation ID was seen before, or rejects a different request under the same
   ID.
1. Checks the catalog version, preconditions, slot vacancy, capacity, and the 1,024-byte path limit. For content, it
   checks the completed upload session.
1. Saves the complete decision in control as `Pending`. This is the commit point: from here, repair finishes the
   decision rather than reconsidering it.
1. Writes the accepted change, activates retained content, writes the item and slot projections, writes the receipt,
   and acknowledges the content. Each write is idempotent at its cursor.
1. Clears `Pending` and advances the committed cursor in one control write.
1. Starts the wake and returns the receipt without waiting for the wake.

A crash at any point leaves either no decision or a saved one. Activation, the next `Submit`, and the reads repair a
saved decision before doing anything else, so it keeps its cursor even when another operation arrives first, and a
retry gets the original receipt. If two activations ever run at once, the control ETag makes a stale writer fail; a
stale write only counts as success when the stored value is identical. The
[algorithm witness for #1112](https://github.com/ScottArbeit/Grace/issues/1112#issuecomment-7181884631) checks these rules at every crash
point.

Catalog changes use the same `Pending` slot. `Library.addLibrary` and `Library.removeLibrary` in
`src/Grace.Shared/Validation/Library.Validation.fs` are the only implementation of the add and remove rules, used both
to decide and to finish a saved decision. Adding a root needs an empty outgoing version-control namespace. Removing one
needs no live item directly under the root; tombstones don't count.

## Limits

- A full item path is at most 1,024 UTF-8 bytes. Creates, renames, moves and conflict copies are checked, and a
  directory rename or move checks every live item under it. Anything longer is rejected with `pathTooLong`.
- A repository holds at most 100,000 item records and 100,000 slot records. Tombstones and vacated slots count.
- A conflict copy follows the same capacity rule as a create.

## Reading changes

Cursors are signed and bound to their repository. `GetChanges` takes a cursor and a page size and returns the changes
after it, the cursor of the last one, and whether more remain. A copy always asks with the cursor of the last change it
applied, so a pull that was interrupted or paused for days resumes exactly where it stopped. A cursor from another
repository, or one ahead of the committed cursor, is an error, because that copy no longer matches the server.

Bootstrap pages are the only paged reads with tokens. A token expires after 15 minutes; an incomplete download can
restart while its Library root is still empty.

## Wakes

After a change commits, the server sends one content-free `LibraryContentAvailable.v1` event with a stable message ID.
It doesn't wait for the broker, and it logs a failed send instead of retrying. Clients poll every 5 seconds anyway, so
a lost wake costs latency, never a change.

## Status

The server's `GetStatus` is a plain read: `ready` or `repairing`, the catalog version, the pending count and when the
last change was accepted. It never repairs.

What users see comes from the client. `grace library sync status` reports the copy's state, its last error, the
affected path, the next step and its last successful sync. See [Libraries](Libraries.md#when-library-sync-fails).

## The Windows client

The client keeps four tables in `.grace/grace-local.db`: repository participation and progress, materialized items,
pending and terminal operations, and sync health. It advances its applied cursor only after a change is verified on
disk, in the same SQLite transaction as the item and operation rows. Saved content is captured into `.grace/objects`
before its operation is recorded, and uploads read that frozen copy.

`grace watch` runs Library work on its timer. A Library failure is recorded for status and retried on a backoff; it
never stops version-control work. See [What grace watch does](What%20grace%20watch%20does.md).
