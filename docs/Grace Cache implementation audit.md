# Grace Cache implementation audit

## GC-CAL-01 clean-main translation

Issue [#965](https://github.com/ScottArbeit/Grace/issues/965) translates the proven GC-CAL-00 one-ZIP lifecycle into
the internal `Grace.Cache.Storage` library on clean `main`. The selected base is
`3346f8244babbd222ec1f8f5f591eafe31c45190`; the prior Cache pull request is source-level salvage only and does not
provide ancestry or host topology.

## Implemented storage boundary

| Area | Result |
| --- | --- |
| Project shape | F# `Microsoft.NET.Sdk` library compiling only `CacheStore.fs` and `CacheArtifactStore.fs` |
| Database ownership | Stable database path, sidecar process lock, lease lifetime, WAL, foreign keys, bounded busy handling, and one-operation serialization |
| Artifact state | Exact immutable tuple with `Absent -> Staging -> Complete` only |
| Publication | Same-root staging, exact streaming SHA-256 and size verification, deterministic opaque final path, and no replacement |
| Restart behavior | Verified `Staging` plus final file completes; incomplete staging cleans to `Absent`; `Complete` disagreement fails closed |
| Validation | Sixteen injected before/after crash cases, integrity mismatch, tuple conflicts, traversal-shaped identity, unknown residue, disagreement, and child-process locking |

## Implemented loopback read

GC-CAL-02 added one F# ASP.NET Core host and one localhost-only
`GET /directory-version-zips/{directoryVersionId}` route. Its separate storage reader opens the existing SQLite
database in immutable read-only mode, queries only an exact `Complete` tuple, derives the opaque final path, and verifies
the stored size and lowercase SHA-256 before serving `application/zip`. Startup requires existing database, managed
root, artifact directory, and schema. It does not open the writer store, acquire its process lock, create sidecars or
directories, classify residue, clean up, or mutate.

The implemented route still requires caller-supplied `canonicalIdentity`, `sha256`, and `size` query values. The host
has no fill route. Current `CacheArtifactStore.commit` also holds the global operation gate across staging, streaming,
hashing, and publication, so it is not the target implementation for many-client network fill.

## Design-ready miss-to-hit increment

The accepted Product V1 increment replaces the exact-tuple read route with an identity-only repository and immutable
directory-version route. `grace connect` first asks Cache for a verified hit. On a miss, it obtains a Server-signed
60-second permit bound to the authenticated user, artifact, and an ephemeral Cache process key. Cache redeems that
permit for the Server-owned descriptor and a 15-minute read-only Blob SAS, retrieves and validates the bytes, commits
them, and returns no ZIP bytes from the fill operation. Connect repeats the independent Cache `GET`; there is no Direct
fallback.

The fill coordinator is process-local and keyed by the immutable artifact. Same-artifact callers share one leader.
Different artifacts use bounded active-fill capacity and typed backpressure without an unbounded queue. Connect retries
only that backpressure for a 60-second monotonic budget. Once redemption starts, client cancellation detaches the
waiter but does not cancel the shared immutable fill.

### Algorithm test result

A disposable F# executable test used a real scratch filesystem and SQLite database to test the proposed effect order.
All nine cases passed:

- happy-path commit;
- 200 concurrent same-artifact callers with one source request;
- follower cancellation without leader cancellation;
- conflicting immutable tuples;
- bounded distinct-fill capacity and later retry;
- two overlapping downloads with serialized publication;
- size or SHA-256 rejection;
- restart after staged bytes, `Staging`, final move, and `Complete`; and
- 500 concurrent verified hits.

The result supports removing network streaming and hashing from the global store-operation gate. The implementation
should retain only a short serialized publication section that reclassifies the artifact, inserts `Staging`, moves the
verified same-root file, and transitions to `Complete`. Restart continues to classify SQLite plus filesystem state and
never resumes a network stream.

### Planned implementation seams

| Area | Planned change |
| --- | --- |
| `Grace.Cache.Storage` | Split download/staging from a short exact publication operation; retain existing restart classifications. |
| `Grace.Cache` | Add ephemeral P-256 identity, identity-only read, permit-only fill, coalescing, bounded capacity, and typed errors. |
| `Grace.Server` | Add narrow ZIP preparation and signed-permit redemption while leaving Direct `getZipFile` unchanged. |
| Directory-version ZIP producer | Calculate lowercase SHA-256 and exact size and upload both as Blob metadata with the ZIP. |
| `Grace.SDK` and shared contracts | Add preparation/redemption DTOs and typed calls without exposing the SAS to CLI code. |
| `Grace.CLI` | Add explicit Cache-required connect flow and the bounded 60-second backpressure retry. |
| Tests and generated surfaces | Cover bindings, access revalidation, effects, restart, concurrency, parity, and regenerate affected API artifacts. |

Persistent identity, enrollment, liveness, assignment, revocation, recursive metadata, complete-root publication,
prefetch, scheduling, durable retry queues, reconciliation, and generalized recovery remain deferred.

## GC-CAL-03 delivered HTTP tracer

Issue #999, merged by PR #1001, delivered the Server-approved HTTP miss-to-hit path on `main`. The current Cache host
now supports identity-only verified reads, ephemeral process-key publication, permit-only fill, Server redemption,
independent integrity validation, bounded distinct fills, same-artifact coalescing, and a separate post-commit `GET`.
Direct `directory/getZipFile` behavior and `grace connect` remain unchanged.

## GC-CAL-04 readiness result

The Cache-required Connect contract is Plan-ready, with these fixed integration choices:

- Syntax: `grace connect <repository> --cache-required`; absence means Direct.
- Selection: per invocation only. No configuration or environment value silently selects Cache, and no
  `CachePreferred` mode exists.
- URI precedence: `--cache-uri`, then `GRACE_CACHE_URI`, otherwise fail before effects. The accepted value is an
  absolute loopback HTTP URI with an explicit port and is never persisted in repository configuration.
- Failure: every Cache failure is terminal except the accepted 60-second retry for typed capacity backpressure. There
  is no Direct fallback.
- Local update: Cache-produced prepared content must enter the shared WDU transaction. GC-CAL-04 must not retain or
  duplicate `Connect.extractZipEntries` as a mutation path.
- Delivery: merge the WDU Connect path through Issue #835 to `main`, then run a fresh Issue #597 checkpoint and create
  one GC-CAL-04 mainline child from that current revision.

The current source still retrieves a Direct Blob SAS and mutates working and object files inside
`Connect.extractZipEntries`. Issue #845 owns replacing that behavior with `WorkingDirectoryUpdate.run`, but the WDU
epic currently selects Issue #960 as its only next child. Therefore GC-CAL-04 is not issue-ready today. Creating it now
would either freeze a stale implementation seam or permit concurrent edits to `Connect.CLI.fs`.

The post-WDU child should own only the Connect Cache HTTP client, the two new CLI options and URI validation, the
Cache-required source sequence, typed capacity retry, and the adapter from verified Cache ZIP bytes into WDU prepared
content. Direct behavior, WDU state and algorithms, CachePreferred, repository-persisted Cache location, and any second
working-directory writer remain outside that child.
