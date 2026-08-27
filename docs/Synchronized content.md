# Synchronized Content

Synchronized Content is Grace's repository-ordered remote namespace and immutable-byte service. Product V1 provides the complete remote contract while deliberately leaving local filesystem participation for later work.

An authorized remote client can:

- Read, add, and remove repository-owned synchronized roots.
- Prepare immutable bytes and submit idempotent namespace or content mutations.
- Read current items and namespace slots.
- Bootstrap current state and then pull ordered deltas.
- Recover the stable receipt for a previously submitted operation.
- Read retained immutable content through a short-lived, one-use grant.
- Read content-free repository synchronization status.

Product V1 does not include local SQLite state, filesystem publication, `sync enable`, `sync disable`, `sync run`, local `sync status`, or Watch-driven synchronization.

## Root ownership

Every repository is created with a persisted empty `SynchronizedRootConfigurationDto`. Root changes use the exact current configuration version and an idempotent operation ID.

A synchronized root is a normalized repository-relative path. Roots must be unique and non-overlapping. Adding a root succeeds only when that path is empty in the outgoing version-control namespace. Removing a root succeeds only when the synchronized namespace under it is empty.

Once configured, a root and its descendants belong to Synchronized Content rather than the version-control namespace. Save, Reference, Branch, and Working Directory Update planning exclude those paths using exact path-segment matching. For example, root `shared` owns `shared` and `shared/design.md`, but not `shared-old`.

Checking the box that says a directory is synchronized does not copy, delete, migrate, import, or publish files. It changes repository ownership and path classification only.

## Authorization

Synchronized Content adds two repository-scoped operations and roles:

| Role | Operations | Intended use |
| --- | --- | --- |
| `SynchronizedContentReader` | `RepositoryRead`, `SynchronizedContentRead` | Read roots, status, items, slots, bootstrap, deltas, receipts, bytes, and wake hints. |
| `SynchronizedContentWriter` | Reader operations plus `SynchronizedContentWrite` | Prepare bytes and submit synchronized mutations. |

`RepositoryAdmin` manages roots and also carries synchronized read/write access. Broader administrator roles inherit the same operations through the existing scope hierarchy.

Authorization is rechecked against the stored repository identity before a read, write, root change, byte transfer, or wake subscription. Missing and cross-repository identifiers do not provide an existence oracle.

## Remote mutation contract

Each mutation request provides:

- One stable operation ID.
- The exact current root-configuration version.
- The mutation and item kinds.
- The exact namespace, content, or destination-slot preconditions required by that mutation kind.
- A prepared-content ID when complete bytes are required.

The accepted order is:

1. Validate and authorize the request against current repository state.
1. Reserve the complete deterministic command.
1. Create the immutable repository mutation.
1. Repair current-state, history, and receipt projections from that mutation.
1. Advance the applied-through position and clear pending work.
1. Complete the stable durable receipt.
1. Attempt a best-effort content-free wake.

Retrying the same operation ID with the same request returns the same receipt. Reusing that ID for a different request returns `operationIdentityMismatch`.

## Bootstrap, deltas, and status

Bootstrap pages read one immutable current-state baseline. Clients keep the returned cursor epoch and boundary cursor, apply each page in order, and continue with the opaque page token until it is absent.

After bootstrap, clients call `/sync/deltas/get` with their opaque cursor. Deltas are ordered repository mutations. A `rebaselineRequired` result means the client must discard its incremental position and start from a new bootstrap baseline.

`SynchronizedRepositoryStatusDto` is content-free. It reports whether projections are caught up, whether rebaseline is required, whether work is blocked, pending-operation count and age, projection lag, and the last completion time. It does not expose container keys, ETags, grants, local paths, content names, or internal cursor numbers.

`SynchronizedContentAvailable.v1` is a best-effort SignalR hint for authorized readers. It says only that the client should pull after a durable cursor. The wake can be lost or duplicated, and delivery failure does not change mutation success.

## Root CLI

The CLI exposes configuration only. Use the existing repository locator options by ID or name.

PowerShell:

```powershell
grace sync roots get --repository-id $repositoryId
grace sync roots list --repository-id $repositoryId
grace sync roots add --repository-id $repositoryId --root shared --expected-version $configurationVersion --operation-id $operationId
grace sync roots remove --repository-id $repositoryId --root shared --expected-version $configurationVersion --operation-id $operationId
```

bash / zsh:

```bash
grace sync roots get --repository-id "$repository_id"
grace sync roots list --repository-id "$repository_id"
grace sync roots add --repository-id "$repository_id" --root shared --expected-version "$configuration_version" --operation-id "$operation_id"
grace sync roots remove --repository-id "$repository_id" --root shared --expected-version "$configuration_version" --operation-id "$operation_id"
```

`grace synchronize` is an alias for `grace sync`. Root commands support the standard human and `cli-json-v1` output modes. No other `sync` commands are active in Product V1.

## HTTP, SDK, and generated clients

The remote contract has 15 HTTP operations under `/sync`:

- Root configuration: get, list, add, and remove.
- Bootstrap: start and continue.
- Ordered state: get deltas, operation receipts, current items, namespace slots, and status.
- Mutation: prepare content and submit a mutation.
- Immutable reads: prepare a one-use read grant and redeem it.

`Grace.SDK.SynchronizedContent` is the handwritten .NET facade. The static OpenAPI source is `src/OpenAPI/SynchronizedContent.Components.OpenAPI.yaml` and `src/OpenAPI/SynchronizedContent.Paths.OpenAPI.yaml`. The standard generator produces TypeScript, Python, and Rust raw clients behind their existing facade boundary.

## Server configuration

Grace Server requires `grace__synchronizedcontent__token_secret`. The value is a base64-encoded key containing at least 32 bytes. It protects opaque cursor, page, and read-grant tokens and must be stable across server instances that serve the same deployment.

PowerShell:

```powershell
$bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$env:grace__synchronizedcontent__token_secret = [Convert]::ToBase64String($bytes)
```

bash / zsh:

```bash
export grace__synchronizedcontent__token_secret="$(openssl rand -base64 32)"
```

The Aspire local topology generates this value for the development run and provisions the required Session-consistent Cosmos resources. Azure and externally configured modes require the operator-supplied secret. Storage placement and partition keys are internal implementation details, not public client contracts.

## Deferred local behavior

Local synchronization arrives in a later issue. Until then:

- Watch does not subscribe to or apply synchronized deltas.
- Working Directory Update never publishes synchronized content into configured roots.
- No local database records synchronized cursors, baselines, or item state.
- No foreground or background command copies files into or out of synchronized roots.
- Root configuration remains remote repository state, not per-working-copy configuration.
