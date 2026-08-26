# Grace Cache local artifact path

## Purpose

Grace Cache supports one explicit Cache-required `grace connect` path on Linux x64. An authenticated user can reuse a
verified local `DirectoryVersionZip` or fill one Server-approved miss, then apply the verified ZIP through the same
Working Directory Update transaction used by Direct Connect. Direct remains the default.

## Specification state

- Issue #965 and PR #969 delivered the local artifact lifecycle.
- Issues #970 and #972, merged by PRs #971 and #973, delivered the loopback F# Cache host and verified read path.
- Issue #999, merged by PR #1001, delivered the Server-approved Cache HTTP miss-to-hit path.
- Issue #1031, merged by PR #1032 as `05e7dd6c5fab8c2f613d9bb97c8d1395606be0c5`, delivered Cache-required Connect
  through the shared WDU path.
- Issue #597 has no remaining Tier 2 implementation child. Only current-state documentation reconciliation remains;
  the deferred capabilities below remain deferred.

## Cache-required miss-to-hit outcome

An authenticated user runs `grace connect` for one immutable root. When the exact `DirectoryVersionZip` is absent
locally, one Server-approved retrieval commits it through the existing `Absent -> Staging -> Complete` lifecycle.
Cache serves the artifact only after the commit, and `grace connect` produces the same working directory it produces
through today's direct download.

### Capability inventory

Required for this increment:

- `grace connect` is the only supported fill initiator.
- Grace Cache has no product-level client-count limit. Concurrent clients may read hits and request misses subject to
  finite runtime resources and explicit backpressure behavior.
- One exact `DirectoryVersionZip` is the only artifact kind.
- Existing verified local hits continue to use the loopback Cache host.
- A miss must be approved by Grace Server for the resolved immutable root and exact artifact tuple.
- Grace Cache owns network retrieval, byte validation, staging, commit, cleanup, and serve-after-commit.
- `grace connect` never uploads artifact bytes or supplies Cache with an arbitrary source URL.
- One in-memory Cache process key and one short-lived artifact permit bind the fill to the intended running Cache.
- The Cache path is explicitly selected and required; Cache failure never falls back to Direct download.
- Cache must validate and commit exact bytes before serving them.

Deferred:

- operator seeding and local fill commands;
- CachePreferred or any other automatic Direct fallback;
- background prefetch, scheduling, retention, and cleanup;
- recursive metadata and complete-root publication;
- reusable transfer-policy modes beyond this explicit connect selection, plus Watch and Grace.Operations integration;
- automatic retry other than bounded typed Cache-backpressure retry, reconciliation, and generalized recovery; and
- persistent Cache identity, enrollment, assignment, liveness, key rotation, revocation, platform parity, HA/DR, and
  hostile-root defense.

### Cache-required connect selection

The explicit syntax is:

```powershell
grace connect <repository> --cache-required
```

`--cache-required` is a value-less per-invocation option. When it is absent, `grace connect` uses the unchanged Direct
path. Grace configuration and environment variables cannot select Cache implicitly. There is no materialization-mode
enum and no `CachePreferred` value.

The selected Cache operation resolves its loopback base URI in this order:

1. `--cache-uri <absolute-loopback-http-uri>` on the current command.
1. `GRACE_CACHE_URI` in the current CLI process environment.
1. No value. The command fails configuration validation before Server preparation, Cache fill, extraction, or local
   mutation.

`--cache-uri` without `--cache-required` is invalid. The URI is operational process input, not repository state, so it
is never stored in `.grace/graceconfig.json`. The accepted URI is absolute HTTP with host `127.0.0.1`, `localhost`, or
`[::1]`, an explicit port, and no user information, query, or fragment. Its path is empty or `/`. The local launcher or
operator supplies the actual `grace-cache` HTTP endpoint through `GRACE_CACHE_URI`; `--cache-uri` is the explicit
one-command override.

Every Cache failure remains visible and terminal except typed `CacheFillCapacityExceeded`, which retains the existing
bounded 60-second retry. There is no Direct fallback.

### WDU integration boundary

GC-CAL-04 starts from the WDU Connect result on `main`. Cache selection changes only the ZIP source. The independently
verified Cache `GET` stream enters `ConnectZipStaging.prepare`, and its prepared content enters the existing
`WorkingDirectoryUpdate.Connect.run` call exactly once. Cache-required Connect does not add an extraction writer,
Cache-specific working-directory mutation, WDU state, or retry state. Direct retrieval keeps its existing Blob stream
and follows the same staging and WDU path.

### Fill admission

Cache generates one ephemeral P-256 key pair at process startup and keeps the private key only in memory.
`grace connect` reads the public key from the configured loopback Cache and includes it in its authenticated Grace
Server request. Grace Server issues a short-lived fill permit bound to the authenticated user, Cache public-key
thumbprint, repository, immutable root, and exact artifact tuple.

When Cache misses locally, it signs the exact permit-redemption request with its ephemeral private key. Grace Server
validates the permit, Cache signature, current user access, root, and artifact tuple before returning a source. Cache
restart creates a new process key, so permits bound to the previous process can no longer be redeemed.

This increment adds no persistent Cache key, enrollment record, assignment, liveness status, key rotation, or
revocation lifecycle. A later capability that selects or administers shared Cache instances must design those
lifecycles separately.

### Primary invariant and current-state checks

Grace Cache may serve a `DirectoryVersionZip` only when its SQLite `Complete` row identifies the immutable repository
and directory version and the final file independently matches that row's exact lowercase SHA-256 and size. SQLite plus
the managed filesystem is the source for local serve eligibility. Grace Server is the source for current user access,
immutable-root membership, the ZIP descriptor, and source issuance.

Preparation checks access and immutable-root membership before issuing a permit. Redemption repeats those checks
immediately before issuing the source; a stale preparation result cannot bypass a later access change. Cache checks for
an existing hit before redemption and reclassifies SQLite plus filesystem state inside the publication section after
download. A stale pre-download miss therefore cannot overwrite a concurrent exact commit or win over a conflicting
tuple.

Failed Server revalidation issues no source. Failed download or integrity validation removes staged bytes and leaves
the artifact absent. Publication failure retains only state handled by the existing restart classification. Typed
capacity backpressure creates no local artifact state and is the only automatically retried result.

#### Permit freshness and replay

The fill permit is stateless and valid for exactly 60 seconds according to the Grace Server clock. Cache signs a
deterministic redemption envelope containing the permit digest, HTTP method, normalized Server route, and exact
artifact identity. Cache clock synchronization is not required.

Grace Server checks expiry when it admits the redemption request and rechecks current user access, repository,
immutable root, Cache public-key binding, and artifact tuple on every redemption. An admitted request may finish ZIP
preparation and source issuance after permit expiry.

An exact replay within the permit lifetime is accepted risk. It can request only the same immutable artifact through
the same running Cache, and Server revalidation still applies. Cache checks for an exact local hit before redemption,
so a replay after commit reports that the artifact is already complete without obtaining another source. This
increment adds no single-use nonce, consumed-permit state, or distributed replay cache.

### Server artifact descriptor and fill source

Grace Server derives the `DirectoryVersionZip` identity from the repository and immutable directory version. After
closing a newly generated ZIP, the Server computes its lowercase SHA-256 and exact byte size. It uploads the ZIP with
that immutable descriptor as Blob metadata in the same upload operation. The Azure .NET Blob client supports metadata
through [`BlobUploadOptions`](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.blobs.models.blobuploadoptions.metadata?view=azure-dotnet).

Successful permit redemption returns the exact artifact descriptor, a 15-minute read-only Blob SAS, and its expiry.
The response descriptor must match the permit. Cache does not log or persist the SAS, downloads the source once, and
independently verifies the exact size and SHA-256 before commit. `grace connect` never receives the SAS.

A development Blob without the required descriptor metadata is unavailable and may be regenerated. Grace has no
production Cache data, so this increment adds no compatibility or migration path for older ZIP Blobs.

### Grace Server preparation and redemption operations

The existing Direct `directory/getZipFile` operation remains unchanged. This increment adds two narrow Cache-specific
Server operations rather than restoring the historical generic Materialization Plan API.

The authenticated preparation operation accepts repository ID, immutable directory-version ID, and the ephemeral
Cache public key. It verifies current user access, ensures the ZIP and descriptor exist, and returns the exact descriptor
plus a 60-second fill permit. It returns no source URI.

The redemption operation accepts the opaque permit and Cache-signed redemption envelope. It validates their exact
binding, rechecks current user access and immutable root, obtains a fresh 15-minute read-only Blob SAS, and returns the
descriptor, SAS, and expiry to Cache.

Target selectors, general execution modes, artifact collections, and the superseded integration-branch Materialization
Plan design remain outside this increment. A later plan API may compose the same narrow preparation service.

### Cache hit and fill sequence

The loopback Cache host exposes the only serving operation:

```text
GET /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}
```

Cache derives its internal artifact identity from repository ID, immutable directory-version ID, and the fixed
`DirectoryVersionZip` kind. It reads the stored SHA-256 and size from the matching `Complete` row and independently
verifies the final file before serving it. The caller supplies no internal identity text, digest, or size.

On a miss, `grace connect` sends only the opaque fill permit to a separate loopback `POST` operation. The fill operation
accepts no source URL, raw path, bytes, digest, size, or caller-authored artifact identity. Cache redeems the permit,
downloads and commits the returned source, and reports that the commit completed without returning artifact bytes.

`grace connect` then repeats the exact `GET`. Only that independent post-commit read can complete the user operation.
A successful fill followed by a failed read is a Cache failure and never falls back to Direct download.

### Client scale, fill coordination, and backpressure

Verified immutable hits may serve concurrently without a product-level client-count limit. Fill work uses an
in-memory coordinator keyed by repository, immutable directory version, and the fixed `DirectoryVersionZip` kind.

Concurrent misses for the same artifact share one leader. Followers do not start another download and independently
repeat the exact serving `GET` after the leader finishes. Cancelling one follower does not cancel work still required
by another follower.

Different-artifact fills use bounded runtime capacity. When that capacity is full, Cache returns typed backpressure
instead of creating an unbounded queue. `grace connect` may retry only this Cache backpressure result and must obtain a
fresh permit for the retry. It never falls back to Direct.

The retry budget is 60 seconds from the first backpressure response, measured by a monotonic client clock. Retry delays
use bounded exponential backoff with a two-second maximum delay. Command cancellation stops immediately. Exhausting
the budget returns a distinct `CacheFillCapacityExceeded` terminal result.

Source rejection, integrity mismatch, commit failure, post-commit read failure, and every Cache result other than typed
backpressure are terminal and are never retried automatically.

The coordinator, waiters, and capacity accounting are process-local. This increment adds no durable queue, waiter
records, retry scheduler, or client registry. Process loss fails current requests; later client retry begins from the
store's restart classification.

Once Cache begins permit redemption for a fill leader, the coordinator owns that fill through a terminal result.
Disconnecting the initiating client or cancelling any follower only detaches that client. Even when all clients detach,
the accepted immutable fill continues and may serve a later request.

Permit expiry or user-access changes after Server admission do not interrupt the admitted fill. Cache shutdown or
process loss may interrupt it; restart uses the existing storage classification and never resumes the network stream.
This process-owned completion rule adds no durable fill job or restart queue.

Network download and hashing must not hold the current global store-operation lock. The storage boundary may serialize
short publication transactions internally, but concurrent clients must not inherit the current whole-stream lock as a
fixed client limit.

#### Fill effect and restart sequence

One admitted fill leader owns this effect order:

1. Redeem the permit and receive the Server-owned descriptor and source.
2. Reserve a same-root staging path without holding the store-operation lock.
3. Stream the source to that path while hashing and counting bytes, still outside the store-operation lock.
4. Reject and remove the staged bytes unless size and lowercase SHA-256 exactly match the descriptor.
5. Enter the short serialized publication section and reclassify the artifact.
6. If another leader already committed the same tuple, remove this staged file and report success. If the stored tuple
   conflicts, remove the staged file and fail closed.
7. Insert `Staging`, atomically move the verified file to its deterministic final path, and transition the row to
   `Complete` in the existing order.
8. Leave the publication section, remove the coordinator entry, and release distinct-fill capacity.

Restart retains the existing storage classifications. Staged bytes with no row are residue and are removed. `Staging`
without a verified final file returns to `Absent`. `Staging` with the exact verified final file advances to `Complete`.
An exact `Complete` row remains a hit; any disagreement fails closed. Network retrieval is never resumed after restart.

A disposable F# executable test exercised this design against a real SQLite database and scratch filesystem. It passed
happy-path commit, 200 concurrent same-artifact callers with one source request, follower cancellation, conflicting
tuples, bounded-capacity retry, two concurrent downloads with serialized publication, integrity rejection, every
restart boundary above, and 500 concurrent verified reads. The result supports moving network download outside the
global operation lock while keeping publication serialized.

### Public operations and wire contracts

Grace Cache adds these loopback operations:

```text
GET  /fill-public-key
GET  /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}
POST /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}/fill
```

The public-key response is a P-256 JSON Web Key containing only `kty`, `crv`, `x`, and `y`. Its thumbprint is the
base64url RFC 7638 SHA-256 thumbprint. The fill request contains only the opaque permit. `204 No Content` means the
artifact is complete; the operation never returns ZIP bytes or a source URI.

Grace Server adds authenticated `POST /cache/prepareDirectoryVersionZip` and permit-authenticated
`POST /cache/redeemDirectoryVersionZipFill`. Preparation accepts repository ID, directory-version ID, and the Cache
public JWK. It returns the descriptor, opaque permit, permit expiry, and the exact redemption bytes Cache must sign.
Redemption accepts the permit and a base64url P-256 ECDSA signature over those bytes using SHA-256 and IEEE P1363
`r || s` encoding. Returning the Server-produced bytes avoids independent JSON normalization rules.

The Cache host returns typed problem details. `404` means the artifact is not a verified local hit. `429` with code
`CacheFillCapacityExceeded` is the only automatically retryable fill result and may include `Retry-After`. Malformed
input returns `400`; tuple conflict returns `409`; failed permit redemption, source, integrity, publication, and
post-fill verification return terminal problem codes without exposing the SAS or managed filesystem paths.

Distinct-fill capacity is a positive process setting named `GRACE_CACHE_MAX_CONCURRENT_FILLS`, defaulting to four.
It limits active network fills, not connected clients, hit readers, or same-artifact followers. Invalid configuration
fails host startup.

### Contract propagation map

| Surface | Implemented result |
| --- | --- |
| Shared contracts | Provide the artifact descriptor, public-key, preparation, permit, redemption, and typed Cache problem shapes. |
| Persistence | Reuses the existing artifact row and state machine; adds no permit, waiter, client, or fill-job state. |
| Grace Cache HTTP | Uses the identity-only verified `GET`, public-key `GET`, and permit-only fill `POST`. |
| Grace Server HTTP | Provides narrow ZIP preparation and permit-redemption operations; leaves Direct `getZipFile` unchanged. |
| ZIP Blob | Stores lowercase SHA-256 and exact size as immutable metadata during upload. |
| `grace connect` | Adds `--cache-required`, optional `--cache-uri`, `GET -> prepare -> fill -> GET`, and 60-second typed retry before shared ZIP staging and WDU. |
| Grace SDK | Provides typed calls for the two Server operations; exposes neither SAS nor a caller-selected source to CLI code. |
| OpenAPI and generated artifacts | Include the Server contracts and routes; Cache remains a local host contract. |
| Documentation | Records Cache selection, routes, terminal failures, and the shared staging and WDU delivery boundary. |
| Tests | Cover crypto binding, access revalidation, descriptor metadata, concurrency, retry, restart, CLI selection, and Cache stream orchestration. |

CLI/API compatibility, migration, and legacy Cache data handling are N/A because the Cache-required mode and its
contracts are new and Grace has no production Cache data.

### Implementation stop conditions

Stop and return to design if implementation requires a persistent Cache identity, durable permit or replay state,
durable fill jobs, a general Materialization Plan, a new artifact kind, a change to Direct connect behavior, or a
second durable lifecycle. Also stop if exact publication cannot remain within the existing
`Absent -> Staging -> Complete` model, or if Server cannot recheck current access immediately before source issuance.
Those changes would add a product or state-machine decision that this specification has not accepted.

### Decision ledger

| ID | Decision | Status | Consequence |
| --- | --- | --- | --- |
| CMT-01 | `grace connect` is the sole fill initiator. | Accepted | No operator-only route, background fill, or prefetch worker enters the tracer. |
| CMT-02 | Grace Cache retrieves bytes from the network. | Accepted | Cache owns transport and commit effects; `grace connect` supplies neither bytes nor a source URL. |
| CMT-03 | Use an ephemeral Cache process key and a short-lived artifact permit. | Accepted | The Server binds and revalidates one fill without persistent Cache identity machinery. |
| CMT-04 | Return the Server-owned descriptor and a 15-minute read-only Blob SAS. | Accepted | Cache receives the source only after permit redemption and independently verifies its bytes. |
| CMT-05 | Use an explicit cache-required path with no Direct fallback. | Accepted | Direct remains the default; every selected Cache failure is visible and terminal. |
| CMT-06 | Stream outside the store lock and serialize only exact publication. | Accepted | The executable algorithm test passed concurrency, integrity, effect-boundary restart, and verified-read cases. |
| CMT-07 | Keep serving `GET` separate from the fill `POST`. | Accepted | Misses use `GET -> POST -> GET`; only the final verified read completes `grace connect`. |
| CMT-08 | Use a stateless 60-second fill permit. | Accepted | Server-clock admission and exact binding replace nonce storage; narrow replay is accepted risk. |
| CMT-09 | Coalesce identical fills and use bounded capacity plus typed retry for distinct fills. | Accepted | Client count is not fixed; no unbounded Cache queue or Direct fallback is added. |
| CMT-10 | Retry typed Cache backpressure for at most 60 seconds. | Accepted | Monotonic elapsed time bounds retry; all other failures remain terminal. |
| CMT-11 | The process coordinator owns an admitted fill through completion. | Accepted | Client cancellation detaches waiters but cannot cancel shared immutable work. |
| CMT-12 | Add narrow Cache preparation and redemption operations. | Accepted | Direct remains unchanged; the generic historical Materialization Plan API is not restored. |
| CMT-13 | Replace the exact-tuple Cache `GET` with a repository-and-directory-version route. | Accepted | Hits avoid Server artifact preparation and callers supply no storage integrity fields. |
| CMT-14 | Select Cache only with the value-less `--cache-required` connect option. | Accepted | Direct remains the default; no mode enum, Cache configuration default, or CachePreferred value is added. |
| CMT-15 | Resolve the loopback URI from `--cache-uri`, then `GRACE_CACHE_URI`; reject a missing or non-loopback URI. | Accepted | Repository configuration never stores service location, and a selected Cache path fails before effects when location is unavailable. |
| CMT-16 | Deliver WDU Connect to `main` before GC-CAL-04. | Accepted | Cache supplies prepared content to `WorkingDirectoryUpdate.run` instead of creating a second local mutation path. |

## Supported world

- One process owns one SQLite database and one managed artifact root.
- Network retrieval stages and hashes bytes outside the store-operation lock. Only the short classification and
  publication section is serialized; verified reads and distinct downloads may overlap subject to finite capacity.
- An artifact tuple contains kind, canonical identity, represented directory-version identity, lowercase SHA-256, and
  exact byte size.
- Only `DirectoryVersionZip` is accepted.
- A local reset is the explicit operator action for a `Complete` disagreement.
- `Cache__DatabasePath` and `Cache__ManagedRoot` must name pre-existing readable locations. Cache and AppHost do not
  invent or create them.

## Lifecycle

The only durable lifecycle is `Absent -> Staging -> Complete`.

1. Stage bytes below the managed root.
1. Write, close, and verify exact size and SHA-256.
1. Persist the exact `Staging` tuple and operation identity in SQLite.
1. Publish the deterministic opaque final path without replacement.
1. Persist the exact `Complete` tuple and operation identity transition.
1. Recheck final bytes and SQLite before reporting success.

The commit point is the exact `Complete` SQLite transaction after verified final-file publication. `Staging` is never a
hit. On a fresh store instance, verified `Staging` plus final bytes becomes `Complete`; other staging residue is cleaned
to `Absent`. A `Complete` row that disagrees with its final file fails closed and retains its record for local reset.

## Local read boundary

The loopback Cache exposes these Product V1 routes:

```text
GET /fill-public-key
GET /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}
POST /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}/fill
```

The ZIP `GET` derives the internal artifact identity and integrity values from its own `Complete` row, then verifies the
managed final bytes before returning `application/zip`. Missing, ineligible, conflicting, corrupt, or byte-disagreeing
state returns 404 without mutation.

The public-key `GET` returns the running Cache process's ephemeral key. The fill `POST` accepts only an opaque
Server-signed permit. Cache redeems the permit with Server, retrieves and independently validates the exact ZIP,
commits it through the local lifecycle, and returns no artifact bytes or source location.

Cache-required Connect first performs the verified ZIP `GET`. On a miss, it obtains the public key, prepares a permit
through Server, asks Cache to fill, and repeats the independent verified ZIP `GET`. The resulting stream enters
`ConnectZipStaging.prepare` and exactly one `WorkingDirectoryUpdate.Connect.run` call. Direct Connect obtains its own
Blob stream and enters the same staging and WDU path.

The host binds only to loopback. Persistent identity, enrollment, liveness, recursive metadata, complete-root
publication, prefetch, scheduling, reconciliation, and generalized recovery remain excluded.
