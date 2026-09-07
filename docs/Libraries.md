# Libraries

Libraries let authorized Windows working copies share ordinary files through a repository-ordered namespace and immutable-byte service. Each copy retains saved input, materialized edit bases, and completed progress locally.

An authorized remote client can:

- Read, add, and remove repository-owned Libraries.
- Prepare immutable bytes and submit idempotent namespace or content changes.
- Read current items and namespace slots.
- Bootstrap current state and then pull ordered change pages.
- Recover the stable receipt for a previously submitted operation.
- Read retained immutable content through a signed URL that remains valid until its fixed expiry.
- Read content-free repository synchronization status.

Local Product V1 supports Windows 11, two authorized copies of one repository, and one initially empty Library. Enable both copies before adding files. Disable, offline/re-enable, per-Library participation, generalized repair, Cache, placeholders, and execution on other platforms are outside this release.

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
- An upload session ID when complete bytes are required.

The accepted order is:

1. Validate and authorize the request against current repository state.
1. Check the Product V1 item-head and namespace-slot bounds before reservation.
1. Reserve the complete deterministic command.
1. Create the immutable repository change.
1. Retain one permanent content reference and complete its manifest workflow for a content-bearing change.
1. Write the current item, affected slots, and stable receipt.
1. Acknowledge the tracked content add, advance the committed cursor, and clear pending work.
1. Resume compact history and best-effort content-free wake progress from the accepted change.

Only a caught-up authorized command with exact catalog, item, namespace, content, and slot preconditions can create one accepted repository change. Each repository is limited to 100,000 current item-head documents and 100,000 current namespace-slot documents. A command that would exceed either bound is rejected before it reserves the control document.

Retrying the same operation ID with the same request returns the same receipt. Reusing that ID for a different request returns `operationIdentityMismatch`.

## Bootstrap, changes, and status

Bootstrap pages read one immutable current-state baseline. Clients keep the returned cursor epoch and boundary cursor, apply each page in order, and continue with the opaque page token until it is absent.

After bootstrap, clients call `/libraries/changes/get` with their opaque cursor. Change pages contain repository-ordered accepted changes. A `rebaselineRequired` result means the client must discard its incremental position and start from a new bootstrap baseline.

`LibraryRepositoryStatusDto` is content-free. It reports whether projections are caught up, whether rebaseline is required, whether work is blocked, pending-operation count and age, projection lag, and the last completion time. It does not expose container keys, ETags, grants, local paths, content names, or internal cursor numbers.

`LibraryContentAvailable.v1` is a best-effort SignalR hint for authorized readers. It says only that the client should pull after a durable cursor. The wake can be lost or duplicated, and delivery failure does not change change acceptance or the durable result.

## Library CLI

Use the existing repository locator options by ID or name for catalog operations. Local synchronization targets the repository configured for the current working copy.

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

Library commands support the standard human and `cli-json-v1` output modes. The top-level `sync` command and `synchronize` alias do not exist. Run these commands in each configured Windows working copy:

```powershell
grace library sync enable
grace library sync run
grace library sync status --output Json
```

After both copies enable the empty baseline, create a file in A's Library and run synchronization in A and B. Edit the file in B and run synchronization in B and A. `grace watch` also invokes this same finite synchronization path from its existing timer.

`ReturnValue.State` is `disabled`, `catchingUp`, `current`, or `blocked`. `current` means the latest completed pull has no remaining pages or pending local operations. An empty page with `HasMore=true` remains `catchingUp`; another run resumes from the unchanged applied cursor. Catalog changes and rebaseline responses stop application and retain saved work.

Saved bytes are captured before upload. A later save stays separate and uses its actual materialized content revision. If the first create is still pending, its successor resolves only from that create's exact accepted, locally completed result. Stale content edits become the server's deterministic ordinary conflict sibling.

Local state uses exactly three Library tables in `.grace/grace-local.db`: repository participation/catalog/progress, materialized items, and pending/terminal operations. A Library connection uses WAL, foreign keys, and FULL synchronization. File bytes are verified before item state, terminal operation, and applied cursor commit together. Restart reuses frozen requests and verifies already-published bytes, avoiding another logical operation or completed-file rewrite.

An empty change page with `HasMore=true` retains its continuation and reports `catchingUp`. Page continuation is stored with repository progress. Each completed item clears the previous page token atomically, so interruption midway through a page resumes from the last applied cursor. A rebaseline response blocks synchronization and retains saved work; Product V1 does not run an automatic repair or bootstrap over existing files.

Library application shares root exclusion with Branch, Connect, and Watch while retaining its own completion. It creates no Save, Reference, DirectoryVersion, Attachment, or WDU completion. Terminal operations retain exact Watch echo evidence until classification and safe bounded pruning.

## HTTP, SDK, and generated clients

The remote contract has 15 HTTP operations under `/libraries`:

- Catalog: get, list, add, and remove.
- Bootstrap: start and continue.
- Ordered state: get changes, operation receipts, current items, namespace slots, and status.
- Changes: prepare content and submit a change.
- Immutable reads: prepare a signed read URL and download its accepted content until expiry.

`Grace.SDK.Libraries` is the handwritten .NET facade. The static OpenAPI sources are `src/OpenAPI/Libraries.Components.OpenAPI.yaml` and `src/OpenAPI/Libraries.Paths.OpenAPI.yaml`. The standard generator produces TypeScript, Python, and Rust raw clients behind their existing facade boundary.

## Server configuration

Grace Server requires `grace__libraries__token_secret`. The value is a base64-encoded key containing at least 32 bytes. It protects opaque cursor, page, and content-read tokens and must be stable across server instances that serve the same deployment.

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
