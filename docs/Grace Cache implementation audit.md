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
| Database ownership | Canonical database path, sidecar process lock, lease lifetime, WAL, foreign keys, bounded busy handling, and one-operation serialization |
| Artifact state | Exact immutable tuple with `Absent -> Staging -> Complete` only |
| Publication | Same-root staging, exact streaming SHA-256 and size verification, deterministic opaque final path, and no replacement |
| Restart behavior | Verified `Staging` plus final file completes; incomplete staging cleans to `Absent`; `Complete` disagreement fails closed |
| Proof | Sixteen injected before/after crash cases, integrity mismatch, tuple conflicts, traversal-shaped identity, unknown residue, disagreement, and child-process locking |

## Deferred capability

GC-CAL-02 adds one C# ASP.NET Core host and one localhost-only
`GET /directory-version-zips/{directoryVersionId}` route. Its separate storage reader opens the existing SQLite
database in immutable read-only mode, queries only an exact `Complete` tuple, derives the opaque final path, and verifies
the stored size and lowercase SHA-256 before serving `application/zip`. Startup requires existing database, managed
root, artifact directory, and schema. It does not open the writer store, acquire its process lock, create sidecars or
directories, classify residue, clean up, or mutate.

Identity, enrollment, liveness, grants, network fill, recursive metadata, complete-root publication, scheduling,
retry queues, reconciliation, and generalized recovery remain deferred.
