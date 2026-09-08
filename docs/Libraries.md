# Libraries

Libraries let authorized Windows working copies share ordinary nonempty files through a repository-ordered namespace and immutable-byte service. Each copy retains saved input, materialized edit bases, and completed progress locally.

An authorized remote client can:

- Read, add, and remove repository-owned Libraries.
- Prepare immutable bytes and submit idempotent namespace or content changes.
- Read current items and namespace slots.
- Bootstrap current state and then pull ordered change pages.
- Recover the stable receipt for a previously submitted operation.
- Read retained immutable content through a signed URL that remains valid until its fixed expiry.
- Read content-free repository synchronization status.

Local Product V1 supports Windows 11, two authorized copies of one repository, and one unchanged Library root. A new copy enables into an empty local Library directory and can join after another copy has published nonempty files and nested directories. Disable, offline/re-enable, per-Library participation, generalized repair, Cache, placeholders, and execution on other platforms are outside this release.

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

Bootstrap pages read one immutable current-state baseline. The Windows client persists all selected metadata and opaque continuation tokens before installing files. Page order is unrelated to parent order. Once metadata is complete, the client validates the live parent graph, installs parents before children and reads each selected immutable content revision. Tombstones require no local filesystem effect or historical-parent lookup.

After every required item is installed and verified, the client commits the selected baseline boundary as its applied cursor and calls `/libraries/changes/get`. Changes accepted after baseline selection then arrive through the existing ordered feed. A `rebaselineRequired` response stops this Windows client and preserves its work; automatic rebaseline of a participating copy remains deferred.

An incomplete acquisition whose continuation returns HTTP 410 can restart before any Library content is installed, provided the root remains empty and the catalog unchanged. Each successful continuation renews the next fifteen-minute token. The client treats tokens as opaque. Complete metadata remains usable after page-token expiry and continues to select its original retained content revisions.

`LibraryRepositoryStatusDto` is content-free. It reports whether projections are caught up, whether rebaseline is required, whether work is blocked, pending-operation count and age, projection lag, and the last completion time. It does not expose container keys, ETags, grants, local paths, content names, or internal cursor numbers.

`LibraryContentAvailable.v1` is a best-effort SignalR hint for authorized readers. It says only that the client should pull after a durable cursor. The wake can be lost or duplicated, and delivery failure does not change change acceptance or the durable result.

## Library CLI

Use the existing repository locator options by ID or name for catalog operations. Local synchronization targets the repository configured for the current working copy.

PowerShell:

```powershell
grace library list --repository-id $repositoryId
grace library get shared --repository-id $repositoryId
grace library add shared --repository-id $repositoryId
grace library remove shared --repository-id $repositoryId
```

bash / zsh:

```bash
grace library list --repository-id "$repository_id"
grace library get shared --repository-id "$repository_id"
grace library add shared --repository-id "$repository_id"
grace library remove shared --repository-id "$repository_id"
```

Both add and remove accept optional `--expected-version` and `--operation-id`. When the version is omitted, the CLI reads the catalog once and submits its version. When the operation ID is omitted, the CLI generates one ID for that invocation. Explicit values pass through unchanged. A failed lookup prevents submission; a stale catalog result remains visible without rereading or retrying. The server still checks version, permissions, path and emptiness.

For an exact scripted retry, retain all request details, including the same explicit version and operation ID. A new invocation with a generated ID is a new request. An omitted-version lookup may obtain a different version, so reusing only an operation ID does not preserve an earlier request.

PowerShell:

```powershell
grace library add shared --repository-id $repositoryId --expected-version $catalogVersion --operation-id $operationId
```

bash / zsh:

```bash
grace library add shared --repository-id "$repository_id" --expected-version "$catalog_version" --operation-id "$operation_id"
```

Library commands support the standard human and `cli-json-v1` output modes. The top-level `sync` command and `synchronize` alias do not exist. Run these commands in each configured Windows working copy:

```powershell
grace library sync enable
grace library sync run
grace library sync status --output Json
```

Enable A, create nonempty files and nested directories in its Library, and run synchronization in A. A fresh B can then enable into its empty local Library root. B installs the selected content and catches up with later accepted changes before normal saved-file capture starts. Edit a file in B and run synchronization in B and A. `grace watch` invokes this same finite synchronization path from its existing timer, including interrupted onboarding.

`ReturnValue.State` is `disabled`, `acquiringBaseline`, `installingBaseline`, `catchingUp`, `current`, or `blocked`. The applied cursor is absent while the baseline remains incomplete; pending-operation count includes baseline work. `current` means the latest completed pull has no remaining pages or pending local operations. An empty page with `HasMore=true` remains `catchingUp`; another run resumes from the unchanged applied cursor. Catalog changes and rebaseline responses stop application and retain saved work.

Restart resumes durable baseline work. Already prepared files with exact selected bytes are reused without rewriting. An occupied unprepared target blocks even if its bytes match; a prepared empty directory can resume, but unexpected children block its completion. A changed completed file or parent blocks the baseline boundary. Resolve local obstructions deliberately, then rerun `grace library sync run`; enable also resumes incomplete onboarding. This is not existing-file reconciliation or catalog adoption.

Saved bytes are captured before upload. A later save stays separate and uses its actual materialized content revision. If the first create is still pending, its successor resolves only from that create's exact accepted, locally completed result. Stale content edits become the server's deterministic ordinary conflict sibling.

Zero-byte local files are excluded before pending input or upload preparation. They remain present: truncating a tracked file to zero does not submit an update or deletion, and a later nonempty save retains its previous materialized edit base. Incoming changes cannot overwrite or delete an excluded empty file. Such a change leaves synchronization blocked and its applied cursor unchanged until the local obstruction is resolved. A previously captured nonempty source and frozen request remain available after a later zero-length save; installing its accepted result cannot overwrite that empty file.

A saved edit during an incoming file rename retains its original item and revision, including saves at either path during interrupted application. Grace removes changed old-path bytes only when those exact positive bytes are already accepted and retained by their pending operation. An edit submitted after the server deletes its item retains the `ItemTombstoned` rejection, saved bytes, and pending request; synchronization does not resurrect the item or manufacture a conflict sibling for that rejection.

Local state uses exactly three Library tables in `.grace/grace-local.db`: repository participation/catalog/progress including baseline selection, materialized items, and pending/terminal operations including selected baseline item work. A Library connection uses WAL, foreign keys, and FULL synchronization. Baseline item completion commits the verified item and terminal work together without advancing the applied cursor. A separate transaction commits the boundary after all required work is verified. Genuine accepted-change completion then commits item, operation and cursor together. Restart reuses frozen requests and verifies already-published bytes, avoiding another logical operation or completed-file rewrite.

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

## Rename a synchronized file

Use `grace library rename <path> <new-name>` in the configured Windows working copy after onboarding and synchronization have completed. The source must be a clean materialized nonempty file and the destination must be absent in the same parent. Grace derives the item and namespace version and retains one generated operation ID; this command takes no internal IDs or version options.

PowerShell:

```powershell
grace library rename Library/design/notes.txt overview.txt
grace library rename Library/design/notes.txt overview.txt --output Json
```

bash / zsh:

```bash
grace library rename Library/design/notes.txt overview.txt
grace library rename Library/design/notes.txt overview.txt --output Json
```

The shell examples describe the same Windows-only client. Directory rename, cross-parent moves, case-only or normalized-equivalent names, dirty or empty sources, reparse paths, and incompatible pending work are excluded.

Grace saves the selected intent and exact namespace-only request before submission. It changes filenames only after server acceptance and ordered application of preceding changes. Compatible remote content edits keep their order and content history. Restart with the same source and name resumes the original request; another name cannot replace pending work.

Human output and `cli-json-v1` data distinguish four outcomes:

| Outcome | Meaning and next action |
| --- | --- |
| `completed` | The accepted rename and local completion committed. The item keeps its identity at the new name. Exit code is zero. |
| `rejected` | The server definitively rejected the request. The command retained its receipt/reason and changed no local filename, item or applied cursor. Later `grace library sync run` can apply unrelated or competing accepted changes. |
| `ambiguous` | Acceptance is unresolved, including cancellation or a lost response. Rerun the same command to resume the retained operation. |
| `acceptedButObstructed` | The server accepted, but local application is incomplete. Preserve and resolve the reported obstruction, then rerun the same command. |

All incomplete or rejected outcomes return a nonzero exit code. A destination created before rename preparation remains an obstruction; Grace does not capture it as a new file or interpret it as an edit to the renamed item. Preserve those bytes outside the destination, then retry the same command. Saved edits at either path during prepared application retain their existing materialized content revision. Empty files and obstructions remain protected. Rejected saved-content operations retain their bytes and pending request; only a definitively rejected unprepared rename retires without an application effect.

## Deferred capabilities

Explicit same-parent file rename follows the [accepted design and experiment](Libraries.Design.md#explicit-file-rename-accepted-next-slice). Automatic recognition of a local Explorer rename remains deferred.

Product V1 includes the Windows synchronization commands, Watch wake handling and local persistence described above. Disable/offline/re-enable, per-Library participation, generalized repair, Cache, placeholders, and Linux/macOS execution remain deferred. Working Directory Update retains its separate ownership and never publishes Library content or records Library completion.
