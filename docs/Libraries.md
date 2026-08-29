# Libraries

Libraries are Grace's repository-ordered remote namespace and immutable-byte service. Product V1 provides the complete remote contract while deliberately leaving local filesystem participation for later work.

An authorized remote client can:

- Read, add, and remove repository-owned Libraries.
- Prepare immutable bytes and submit idempotent namespace or content changes.
- Read current items and namespace slots.
- Bootstrap current state and then pull ordered change pages.
- Recover the stable receipt for a previously submitted operation.
- Read retained immutable content through a short-lived, one-use grant.
- Read content-free repository synchronization status.

Product V1 does not include local SQLite state, filesystem publication, `library sync enable`, `library sync disable`, `library sync run`, local synchronization status, or Watch-driven synchronization.

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
| `LibraryWriter` | Reader operations plus `LibraryWrite` | Prepare bytes and submit Library changes. |

`RepositoryAdmin` manages the Library catalog and also carries Library read/write access. Broader administrator roles inherit the same operations through the existing scope hierarchy.

Authorization is rechecked against the stored repository identity before a read, write, catalog change, byte transfer, or wake subscription. Missing and cross-repository identifiers do not provide an existence oracle.

## Remote change contract

Each change request provides:

- One stable operation ID.
- The exact current Library catalog version.
- The change and item kinds.
- The exact namespace, content, or destination-slot preconditions required by that change kind.
- A prepared-content ID when complete bytes are required.

The accepted order is:

1. Validate and authorize the request against current repository state.
1. Check the Product V1 item-head and namespace-slot bounds before reservation.
1. Reserve the complete deterministic command.
1. Create the immutable repository change.
1. Repair current-state, history, and receipt projections from that change.
1. Advance the applied-through position and clear pending work.
1. Complete the stable durable receipt.
1. Attempt a best-effort content-free wake.

Only a caught-up authorized command with exact catalog, item, namespace, content, and slot preconditions can create one accepted repository change. Each repository is limited to 100,000 current item-head documents and 100,000 current namespace-slot documents. A command that would exceed either bound is rejected before it reserves the control document.

Retrying the same operation ID with the same request returns the same receipt. Reusing that ID for a different request returns `operationIdentityMismatch`.

## Bootstrap, changes, and status

Bootstrap pages read one immutable current-state baseline. Clients keep the returned cursor epoch and boundary cursor, apply each page in order, and continue with the opaque page token until it is absent.

After bootstrap, clients call `/libraries/changes/get` with their opaque cursor. Change pages contain repository-ordered accepted changes. A `rebaselineRequired` result means the client must discard its incremental position and start from a new bootstrap baseline.

`LibraryRepositoryStatusDto` is content-free. It reports whether projections are caught up, whether rebaseline is required, whether work is blocked, pending-operation count and age, projection lag, and the last completion time. It does not expose container keys, ETags, grants, local paths, content names, or internal cursor numbers.

`LibraryContentAvailable.v1` is a best-effort SignalR hint for authorized readers. It says only that the client should pull after a durable cursor. The wake can be lost or duplicated, and delivery failure does not change change acceptance or the durable result.

## Library CLI

The CLI exposes catalog operations only. Use the existing repository locator options by ID or name.

PowerShell:

```powershell
grace library list --repository-id $repositoryId
grace library get shared --repository-id $repositoryId
grace library add shared --repository-id $repositoryId --expected-version $catalogVersion --operation-id $operationId
grace library remove shared --repository-id $repositoryId --expected-version $catalogVersion --operation-id $operationId
```

bash / zsh:

```bash
grace library list --repository-id "$repository_id"
grace library get shared --repository-id "$repository_id"
grace library add shared --repository-id "$repository_id" --expected-version "$catalog_version" --operation-id "$operation_id"
grace library remove shared --repository-id "$repository_id" --expected-version "$catalog_version" --operation-id "$operation_id"
```

Library commands support the standard human and `cli-json-v1` output modes. The top-level `sync` command and `synchronize` alias do not exist. Local synchronization commands remain deferred.

## HTTP, SDK, and generated clients

The remote contract has 15 HTTP operations under `/libraries`:

- Catalog: get, list, add, and remove.
- Bootstrap: start and continue.
- Ordered state: get changes, operation receipts, current items, namespace slots, and status.
- Changes: prepare content and submit a change.
- Immutable reads: prepare a one-use read grant and redeem it.

`Grace.SDK.Libraries` is the handwritten .NET facade. The static OpenAPI sources are `src/OpenAPI/Libraries.Components.OpenAPI.yaml` and `src/OpenAPI/Libraries.Paths.OpenAPI.yaml`. The standard generator produces TypeScript, Python, and Rust raw clients behind their existing facade boundary.

## Server configuration

Grace Server requires `grace__libraries__token_secret`. The value is a base64-encoded key containing at least 32 bytes. It protects opaque cursor, page, and read-grant tokens and must be stable across server instances that serve the same deployment.

PowerShell:

```powershell
$bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$env:grace__libraries__token_secret = [Convert]::ToBase64String($bytes)
```

bash / zsh:

```bash
export grace__libraries__token_secret="$(openssl rand -base64 32)"
```

The Aspire local topology generates this value for the development run and provisions the six Session-consistent Cosmos containers with their purpose-specific partition keys. Azure and externally configured modes require the operator-supplied secret. Storage placement and partition keys are internal implementation details, not public client contracts.

## Deferred local behavior

Local synchronization arrives in a later issue. Until then:

- Watch does not subscribe to or apply Library change pages.
- Working Directory Update never publishes Library content into configured Libraries.
- No local database records Library cursors, baselines, or item state.
- No foreground or background command copies files into or out of Libraries.
- The Library catalog remains remote repository state, not per-working-copy configuration.
