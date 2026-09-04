# Libraries

Libraries are Grace's repository-ordered namespace and immutable-byte service. Product V1 includes the complete remote contract and an explicit Windows 11 working-copy synchronization tracer.

An authorized remote client can:

- Read, add, and remove repository-owned Libraries.
- Stage immutable bytes through an upload session and submit idempotent namespace or content changes.
- Read current items and namespace slots.
- Bootstrap current state and then pull ordered change pages.
- Recover the stable receipt for a previously submitted operation.
- Read retained immutable content through a short-lived signed read-only download URL backed by Grace's immutable-content access path.
- Read content-free repository synchronization status.
- Enable one working copy, publish ordinary-file changes, pull accepted changes after a durable cursor, and report local synchronization status on Windows 11.

Product V1 local synchronization accepts ordinary-file creation and content updates inside configured Libraries. It does not support deletion, rename, directories as items, per-Library participation, disable/re-enable, offline repair, or Linux/macOS execution.

## Library ownership

Every repository is created with a persisted empty `LibraryCatalogDto`. Catalog changes use the exact current catalog version and an idempotent operation ID.

A Library is one normalized repository-relative path. Libraries must be unique and non-overlapping. Adding a Library succeeds only when that path is empty in the outgoing version-control namespace. Removing a Library succeeds only when its Library namespace is empty.

Once configured, a Library and its descendants belong to Libraries rather than the version-control namespace. Save, Reference, Branch, and Working Directory Update planning query `RepositoryLibraryActor.IsInLibrary` using exact path-segment matching. For example, Library `shared` owns `shared` and `shared/design.md`, but not `shared-old`.

The actor classifies each path against its current catalog when it processes the call. A returned Boolean can become stale after return. Version-control gestures remain in their existing domains, do not route through or hold `RepositoryLibraryActor`, and must retain their existing stale-authority checks.

Configuring a Library does not copy, delete, migrate, import, or publish files. It changes repository ownership and path classification only.

## Authorization

Libraries add two repository-scoped operations and roles:

| Role | Operations | Intended use |
| --- | --- | --- |
| `LibraryReader` | `RepositoryRead`, `LibraryRead` | Read the catalog, status, items, slots, bootstrap pages, change pages, receipts, bytes, and wake hints. |
| `LibraryWriter` | Reader operations plus `LibraryWrite` | Stage bytes and submit Library changes. |

`RepositoryAdmin` manages the Library catalog and also carries Library read/write access. Broader administrator roles inherit the same operations through the existing scope hierarchy.

Authorization is rechecked against the stored repository identity before a read, write, catalog change, byte transfer, or wake subscription. Missing and cross-repository identifiers do not provide an existence oracle.

## Remote change contract

Each change request provides:

- One stable operation ID.
- The exact current Library catalog version.
- The change and item kinds.
- The exact namespace, content, or destination-slot preconditions required by that change kind.
- An UploadSession-backed prepared-content ID when complete bytes are required.

The accepted order is:

1. Validate and authorize the request against current repository state.
1. Return the existing exact receipt before consulting temporary upload state when the operation was already accepted.
1. Check the Product V1 item-head and namespace-slot bounds before reservation.
1. Reserve the complete deterministic command.
1. Create the immutable canonical repository change.
1. Idempotently persist current item and slot state, retained content location, and the stable receipt.
1. Advance the applied-through position and clear the matching pending operation.
1. Publish one stable content-available envelope, retaining an Orleans fallback record only after terminal Service Bus failure.
1. Return the stable durable receipt.

Only a caught-up authorized command with exact catalog, item, namespace, content, and slot preconditions can create one accepted repository change. Each repository is limited to 100,000 current item-head documents and 100,000 current namespace-slot documents. A command that would exceed either bound is rejected before it reserves the control document.

Retrying the same operation ID with the same request returns the same receipt. A persisted receipt is terminal only after the matching pending operation is gone; activation repair completes that cleanup before returning it. Reusing the operation ID for a different request returns `operationIdentityMismatch`.

`RepositoryLibraryActor` is the only authority that orders and accepts repository Library changes. Every Grace-owned Library metadata write uses Orleans persistence. Accepted changes, current projections, receipts, content locations, history segments, and baseline shards use point-addressed or bounded records rather than unbounded repository arrays.

History is not on the submit hot path. A `LibraryContentAvailable.v1` notification causes the repository actor to replay accepted canonical changes after its durable history position. Reprocessing the same cursor is idempotent. Baseline shard integrity hashes use BLAKE3 and bootstrap fails closed when shard bytes do not match the manifest.

## Bootstrap, changes, and status

Bootstrap pages read one immutable current-state baseline. Clients keep the returned cursor epoch and boundary cursor, apply each page in order, and continue with the opaque page token until it is absent.

After bootstrap, clients call `/libraries/changes/get` with their opaque cursor. Change pages contain repository-ordered accepted changes. A `rebaselineRequired` result means the client must discard its incremental position and start from a new bootstrap baseline.

`LibraryRepositoryStatusDto` is content-free. It reports whether projections are caught up, whether rebaseline is required, whether work is blocked, pending-operation count and age, projection lag, and the last completion time. It does not expose container keys, ETags, grants, local paths, content names, or internal cursor numbers.

`LibraryContentAvailable.v1` is a content-free pull hint for authorized readers. It says only that the client should pull after a durable cursor. SignalR delivery can be lost or duplicated, and Service Bus delivery can be duplicated after ambiguous broker acceptance. The stable accepted cursor and `MessageId` make included server consumers idempotent.

Service Bus publication is send-first. Grace lets the configured SDK retries finish before treating an exception as terminal. A successful first send creates no fallback state. A terminal failure persists the exact serialized envelope under its stable `MessageId`; actor activation resends those same bytes and clears fallback state only after send success. Notification delay or duplication does not change acceptance or the durable receipt.

## Library CLI

The CLI exposes remote catalog operations and the nested Windows working-copy synchronization tracer. Use the existing repository locator options by ID or name for catalog commands.

PowerShell:

```powershell
grace library list --repository-id $repositoryId
grace library get shared --repository-id $repositoryId
grace library add shared --repository-id $repositoryId --expected-version $catalogVersion --operation-id $operationId
grace library remove shared --repository-id $repositoryId --expected-version $catalogVersion --operation-id $operationId
grace library sync enable
grace library sync run
grace library sync status
```

bash / zsh:

```bash
grace library list --repository-id "$repository_id"
grace library get shared --repository-id "$repository_id"
grace library add shared --repository-id "$repository_id" --expected-version "$catalog_version" --operation-id "$operation_id"
grace library remove shared --repository-id "$repository_id" --expected-version "$catalog_version" --operation-id "$operation_id"
grace library sync enable
grace library sync run
grace library sync status
```

Library commands support the standard human and `cli-json-v1` output modes. The top-level `sync` command and `synchronize` alias do not exist. There is deliberately no `library sync disable` command in Product V1.

`grace library sync enable` bootstraps the current remote catalog and cursor into the existing `.grace/grace-local.db`, assigns the working copy an identity, and enables participation in the repository configuration. Run it once in each authorized Windows working copy.

`grace library sync run` first pulls repository-ordered accepted changes after the durable local cursor. It recovers any accepted receipt for the same local operation ID before it needs temporary upload state. After remote completion, it scans for the first stable local ordinary-file change, stages immutable content through the existing upload routes, and submits that exact change. Repeating the command is safe: accepted operation IDs return the same receipt, completed remote operations observe terminal bytes and SQLite state, and cursor advancement uses the exact predecessor.

`grace library sync status` reports whether synchronization is enabled, the lifecycle state, catalog version, cursor epoch, and applied cursor. A current status describes local durable state; it is not a promise that a concurrent remote change cannot arrive immediately afterward.

## Windows working-copy transaction

Library synchronization uses the existing local SQLite database at schema version 12. The six Library tables store repository participation, catalog roots, item ancestry, namespace slots, operation evidence, and conflicts. This is separate from Working Directory Update completion rows: Library synchronization does not create a fourth WDU caller or write a WDU completion.

For a remote ordinary-file change, Grace prepares and BLAKE3-verifies downloaded bytes before it acquires the shared repository-root exclusion. Under that exclusion it rereads the live catalog, exact cursor predecessor, durable ancestry, pending operation, and actual target bytes. If those facts remain current, it persists exact pending evidence, publishes through a same-directory atomic move, verifies terminal bytes, commits item, namespace, and operation state in one SQLite transaction, and then compare-and-set advances the cursor under the unchanged catalog version.

If the process stops after atomic filesystem publication but before terminal SQLite state, the next run classifies the durable pending operation against actual target bytes. Exact bytes complete SQLite and the cursor without rewriting the file. Changed target bytes, stale catalog or cursor facts, and mismatched pending evidence fail before a new filesystem effect.

Local publication uses a stable metadata-read, complete-byte-read, metadata-reread boundary. A file that changes during the read is rejected. Grace derives a retry-stable operation ID from the working-copy identity, normalized path, and BLAKE3 hash, so response loss after server acceptance recovers the exact receipt rather than creating a second logical change.

Grace Watch subscribes to repository-scoped `LibraryContentAvailable.v1` notifications only when local synchronization is enabled. The notification is an advisory wake. Watch ignores its cursor and catalog fields as authority and runs the same durable pull after the local cursor. Lost, duplicate, delayed, or restarted delivery cannot advance the cursor or suppress a filesystem event by itself.

## HTTP, SDK, and generated clients

The remote contract has 15 HTTP operations under `/libraries`:

- Catalog: get, list, add, and remove.
- Bootstrap: start and continue.
- Ordered state: get changes, operation receipts, current items, namespace slots, and status.
- Changes: create an UploadSession-backed prepared-content descriptor and submit a change.
- Immutable reads: authorize a content read and follow the returned short-lived signed read-only download response.

`Grace.SDK.Libraries` is the handwritten .NET facade. The static OpenAPI sources are `src/OpenAPI/Libraries.Components.OpenAPI.yaml` and `src/OpenAPI/Libraries.Paths.OpenAPI.yaml`. The standard generator produces TypeScript, Python, and Rust raw clients behind their existing facade boundary.

## Server configuration

Grace Server requires `grace__libraries__token_secret`. The value is a base64-encoded key containing at least 32 bytes. It protects opaque cursor, page, and stateless content-read tokens and must be stable across server instances that serve the same deployment.

PowerShell:

```powershell
$bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$env:grace__libraries__token_secret = [Convert]::ToBase64String($bytes)
```

bash / zsh:

```bash
export grace__libraries__token_secret="$(openssl rand -base64 32)"
```

The Aspire local topology generates this value for the development run. Azure and externally configured modes require the operator-supplied secret.

Library writes work through six named Orleans persistence purposes: control, changes, current, receipts, history, and baselines. Deployments may bind those purposes to any configured Orleans storage provider. The Aspire Cosmos topology provisions six Session-consistent containers with one-level control keys, two-level changes/current keys, and three-level receipts/history/baselines keys. Cosmos SQL remains available only to storage-type-gated read adapters; it is never a Library mutation path or competing authority.

`UploadSessionActor` alone owns temporary upload coordination. Prepared-content and finalized-manifest evidence live in that actor rather than a separate preparation actor. Terminal upload coordination expires at `StartedAt + Repository.LogicalDeleteDays`; the reminder rechecks the exact session generation and deadline before deleting temporary state. Accepted immutable content is not deleted by this cleanup.

Authorized immutable reads create no durable grant record. After repository, item, and content authorization, Grace returns the existing short-lived signed read-only download form; the server uses its immutable-content SAS access behind that route.

## Product V1 local boundaries

Local synchronization intentionally remains narrow:

- Filesystem events are wake-ups, never publication authority. Stable bytes, accepted server order, current catalog policy, durable local ancestry, pending evidence, and reread target bytes decide effects.
- Exact operation, path, item, and BLAKE3 evidence suppresses only Grace's own matching Watch echo. A duplicate, delayed, restarted, or merely similar event remains observable.
- Branch, Connect, and Reference retain their existing Working Directory Update caller, target, and completion contracts. Library synchronization shares only the repository-root exclusion.
- Save, Reference, DirectoryVersion, Attachment, and WDU completion records are not created for a Library change.
- Product V1 has no background scheduler, reminder, generalized repair loop, second local database, or per-file actor.
- The Library catalog remains remote repository state, not per-working-copy configuration.
