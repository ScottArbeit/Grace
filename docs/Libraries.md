# Libraries

Libraries hold ordinary shared files outside Grace's version-controlled history and WorkItem Attachments. A repository administrator configures empty repository-relative roots, and authorized participants synchronize files in those Libraries.

## Delivery status

Scott accepted a simpler Library design on 2026-09-06. [Libraries design](Libraries.Design.md) is the current implementation contract. [The type plan](design/Libraries.Type-Plan.md) records the accepted removals, changes and retained declarations.

Issue #1038 delivered the original remote implementation. The open server and client candidates, PR #1043 and PR #1044, use the older model and are preserved for selective reuse. Issue #1042 owns the server replacement; Issue #1039 owns the Windows two-copy client adaptation. The accepted design is not yet implemented.

## Ownership and permissions

One configured root is one Library. The repository owns a sorted catalog of non-overlapping roots. Adding a Library requires an empty outgoing version-control namespace; removing it requires an empty Library namespace. Configuring a Library changes path ownership without copying, deleting or importing files.

`shared` owns `shared/file.txt`, not `shared-old`. Save, Reference, Branch and Working Directory Update retain their existing path-classification and revalidation responsibilities. Library synchronization does not create a Save or a WDU completion.

| Role | Allowed behavior |
| --- | --- |
| `LibraryReader` | Read permitted catalog, items, slots, pages, receipts, status, content and wake hints. |
| `LibraryWriter` | Reader behavior plus upload preparation and Library changes. |
| `RepositoryAdmin` | Manage the Library catalog and read/write its content. |

Permissions and repository identity are checked before disclosure, acceptance and content transfer. Missing and cross-repository identifiers retain the existing no-oracle behavior.

## Accepted synchronization behavior

One `RepositoryLibraryActor` orders changes for a repository. Stable item identity and parent/name links preserve directory and file identity through moves. A content revision records edit order independently of immutable byte identity.

The client preserves a saved local edit and its original base when another participant changes the same file. Submitting against a stale content revision produces a deterministic conflict copy. Notifications prompt a pull; they do not change local files or advance local completion by themselves.

A baseline represents one complete committed snapshot. Grace may pause Library changes for every baseline build, writes all shards first, and publishes its manifest last. After bootstrap, the client pulls ordered changes. Local progress advances only after the filesystem bytes and the existing SQLite database record completion.

Uploads use `UploadSessionActor`. Accepted historical bytes remain retrievable after upload-session cleanup. Content reads use short-lived signed URLs naming an accepted immutable version. No persisted one-use grant is part of the replacement.

All Library metadata writes use Orleans persistence. Cosmos deployments query records directly with Cosmos SQL. See the design for partition keys, retained records, recovery and measured limits.

## Commands

Catalog commands retain the normal repository locator and output options.

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

Issue #1039 delivers `grace library sync enable|run|status` for the Windows two-copy slice. The commands remain under `library`; there is no top-level `sync` or `synchronize` alias. Human output and `cli-json-v1` remain supported.

## Configuration and public contracts

`grace__libraries__token_secret` is a base64-encoded signing key of at least 32 bytes, stable across deployment instances. It protects opaque cursors and page/read tokens. Do not log it or commit it.

Keep the fifteen existing `/libraries` operations, the handwritten `Grace.SDK.Libraries` facade, static OpenAPI and generated TypeScript, Python and Rust clients aligned with the redesigned DTOs. The precise affected fields and validation are in the maintained design.

Later epic slices cover participation/offline behavior, broader namespace competition and ownership transitions, and Linux/macOS conformance. Public history browsing, restore, diff, search, AI assistance, Cache and placeholders remain deferred.
