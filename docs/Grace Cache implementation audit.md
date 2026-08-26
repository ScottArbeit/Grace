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

## GC-CAL-02 loopback read delivery

GC-CAL-02 added one F# ASP.NET Core host and one localhost-only exact-tuple read route. Its separate storage reader
opened the existing SQLite database in immutable read-only mode, queried only an exact `Complete` tuple, derived the
opaque final path, and verified the stored size and lowercase SHA-256 before serving `application/zip`. Startup required
the existing database, managed root, artifact directory, and schema. The reader did not open the writer store, acquire
its process lock, create sidecars or directories, classify residue, clean up, or mutate.

That calibration route required caller-supplied `canonicalIdentity`, `sha256`, and `size` query values and exposed no
fill route. Its writer held the global operation gate across staging, streaming, hashing, and publication. Issue #999
replaced those temporary boundaries with the delivered Product V1 HTTP path below.

## Delivered miss-to-hit implementation

Issue #999 replaced the exact-tuple read route with an identity-only repository and immutable directory-version route.
`grace connect` first asks Cache for a verified hit. On a miss, it obtains a Server-signed
60-second permit bound to the authenticated user, artifact, and an ephemeral Cache process key. Cache redeems that
permit for the Server-owned descriptor and a 15-minute read-only Blob SAS, retrieves and validates the bytes, commits
them, and returns no ZIP bytes from the fill operation. Connect repeats the independent Cache `GET`; there is no Direct
fallback.

The fill coordinator is process-local and keyed by the immutable artifact. Same-artifact callers share one leader.
Different artifacts use bounded active-fill capacity and typed backpressure without an unbounded queue. Connect retries
only that backpressure for a 60-second monotonic budget. Once redemption starts, client cancellation detaches the
waiter but does not cancel the shared immutable fill.

### Algorithm test result

Before Issue #999, a disposable F# executable test used a real scratch filesystem and SQLite database to test the
effect order. All nine cases passed:

- happy-path commit;
- 200 concurrent same-artifact callers with one source request;
- follower cancellation without leader cancellation;
- conflicting immutable tuples;
- bounded distinct-fill capacity and later retry;
- two overlapping downloads with serialized publication;
- size or SHA-256 rejection;
- restart after staged bytes, `Staging`, final move, and `Complete`; and
- 500 concurrent verified hits.

Issue #999 implemented that effect order. Network streaming and hashing run outside the global store-operation gate.
Only a short publication section reclassifies the artifact, inserts `Staging`, moves the verified same-root file, and
transitions to `Complete`. Restart classifies SQLite plus filesystem state and never resumes a network stream.

### Implemented seams

| Area | Implemented result |
| --- | --- |
| `Grace.Cache.Storage` | Separates network staging from short exact publication and retains existing restart classifications. |
| `Grace.Cache` | Provides ephemeral P-256 identity, identity-only read, permit-only fill, coalescing, bounded capacity, and typed errors. |
| `Grace.Server` | Provides narrow ZIP preparation and signed-permit redemption while leaving Direct `getZipFile` unchanged. |
| Directory-version ZIP producer | Writes lowercase SHA-256 and exact size as Blob metadata with the ZIP. |
| `Grace.SDK` and shared contracts | Provide preparation and redemption DTOs and typed calls without exposing the SAS to CLI code. |
| `Grace.CLI` | Provides explicit Cache-required Connect selection, Cache stream orchestration, and bounded typed backpressure retry. |
| Tests and generated surfaces | Cover bindings, access revalidation, effects, restart, concurrency, generated API artifacts, CLI selection, and Cache stream orchestration. |

Persistent identity, enrollment, liveness, assignment, revocation, recursive metadata, complete-root publication,
prefetch, scheduling, durable retry queues, reconciliation, and generalized recovery remain deferred.

## GC-CAL-03 delivered HTTP tracer

Issue #999, merged by PR #1001, delivered the Server-approved HTTP miss-to-hit path on `main`. The current Cache host
now supports identity-only verified reads, ephemeral process-key publication, permit-only fill, Server redemption,
independent integrity validation, bounded distinct fills, same-artifact coalescing, and a separate post-commit `GET`.
At the GC-CAL-03 checkpoint, Direct `directory/getZipFile` behavior and `grace connect` remained unchanged.

## GC-CAL-04 delivery result

Issue #1031, merged by PR #1032 as `05e7dd6c5fab8c2f613d9bb97c8d1395606be0c5`, completed the Cache-required Connect
contract with these fixed integration choices:

- Syntax: `grace connect <repository> --cache-required`; absence means Direct.
- Selection: per invocation only. No configuration or environment value silently selects Cache, and no
  `CachePreferred` mode exists.
- URI precedence: `--cache-uri`, then `GRACE_CACHE_URI`, otherwise fail before effects. The accepted value is an
  absolute loopback HTTP URI with an explicit port and is never persisted in repository configuration.
- Failure: every Cache failure is terminal except the accepted 60-second retry for typed capacity backpressure. There
  is no Direct fallback.
- Cache sequence: exact verified `GET`; on `404`, public-key `GET`, authenticated Server preparation, permit-only fill
  `POST`, and an independent verified `GET`.
- Retry: only HTTP `429` with problem code `CacheFillCapacityExceeded` retries. Each retry obtains a fresh Server permit
  and uses a monotonic 60-second budget with bounded exponential delay capped at two seconds.
- Local update: both Direct and Cache ZIP streams enter `ConnectZipStaging.prepare`; prepared content then enters the
  existing `WorkingDirectoryUpdate.Connect.run` call exactly once.
- Unchanged surfaces: Direct retrieval, WDU contracts and algorithms, Cache and Server routes, SDK and shared contracts,
  OpenAPI and generated artifacts, persisted configuration, and local status shapes do not change in GC-CAL-04.

GC-CAL-04 changed only the Connect Cache HTTP client, the two CLI options and URI validation, Cache-required source
selection and orchestration, typed capacity retry, focused CLI tests, and these two Cache documents. CachePreferred,
repository-persisted Cache location, durable retry, and any second working-directory writer remain outside this slice.

### Review and validation

- R1 reviewed candidate `6ea3489e3df354d9262b26f34433c662f1829dcb` and accepted three repairs: bound Server
  preparation by the remaining 60-second retry budget, detach promptly when the caller cancels an in-flight
  preparation, and add deterministic Cache-to-staging-to-WDU composition coverage.
- The consolidated repair produced approved head `ef47dbe2d9cff7cda9d555e6f0fa9ca69ba2dedc`. R2 returned `VERIFIED` with all
  three items closed, no direct repair regression, and no scope escape.
- GitHub Validate run `32934622196` passed on the approved head. The approved head and merge commit have the same tree,
  `61bab6337f400bbeb06c71abf9ebe4eba6c04ca1`.
- Focused tests covered deadline crossing, prompt cancellation, real ZIP staging and local WDU application, and
  invalid-ZIP cleanup with no WDU entry.
- A live external Server plus loopback Cache pair was not run. Deterministic tests cover the Server-to-Cache seams and
  Cache-to-staging-to-WDU composition, including real local ZIP staging and WDU behavior.

### Issue #597 completion verdict

No next Tier 2 child is selected. The accepted Product V1 outcome is implemented, reviewed, validated, and present on
`main`. Issue #597 now needs only the current-state reconciliation in this document and `docs/Grace Cache.md` before it
can close. Future Cache capabilities require fresh design and issue-readiness work against then-current evidence.

Historical Cache issues, branches, pull requests, worktrees, and dirty state remain preserved. Persistent identity,
enrollment, liveness, assignment, revocation, recursive metadata, complete-root publication, prefetch, scheduling,
retention, cleanup, Watch or Operations integration, durable retry queues, reconciliation, generalized recovery,
platform parity, HA/DR, and hostile-root defense remain deferred.
