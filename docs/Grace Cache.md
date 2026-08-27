# Grace Cache local artifact path

## Purpose

Grace Cache provides a loopback-only read path for immutable Grace artifacts. Cache-required `grace connect` uses it to retrieve one Server-approved `DirectoryVersionZip` without making Cache an independent access-control or retention source.

The current Product V1 contract supports one authenticated CLI user, one Grace Server process, and one Cache process on the same machine. Direct Connect remains unchanged.

## Cache-required Connect

`grace connect --cache-required` requires an explicit loopback HTTP endpoint from `--cache-uri` or `GRACE_CACHE_URI`. It does not fall back to Direct retrieval.

The request sequence is:

1. The CLI reads the Cache process public fill key.
1. The authenticated CLI asks Grace Server to prepare the exact root `DirectoryVersionZip`.
1. Server checks current repository access, confirms or creates the ZIP, reads its Blob metadata, and returns the exact artifact, a five-minute read grant, and a separate 60-second fill permit.
1. The CLI sends `Authorization: Bearer <CacheArtifactGrant>` with its first Cache GET.
1. Cache validates the grant locally before checking whether the artifact exists.
1. On a miss, the CLI sends only the separate fill permit to the Cache fill route.
1. Cache redeems and validates that permit before checking for an existing hit or starting fill work.
1. After fill, the CLI repeats the GET with the same read grant while it remains valid. It obtains a replacement preparation if the grant has expired before the retry begins.
1. The CLI stages the complete ZIP and verifies BLAKE3 before ZIP extraction, staging validation, or Working Directory Update.

An admitted HTTP stream may finish after grant expiration. Every later request is admitted independently.

## Artifact and grant contract

The exact immutable generation is:

```text
DirectoryVersionZipCacheArtifact
  RepositoryId
  DirectoryVersionId
  Blake3Hash
```

The compact ES256 grant has fixed issuer `Grace.Server.CacheArtifactGrant.v1`, audience `Grace.Cache.Artifact.v1`, a five-minute lifetime, and claims for the GET method, exact Cache route, artifact kind, repository, directory version, and BLAKE3 value.

Grace Server creates one ephemeral P-256 signing key per process. Cache fetches the public validation key from:

```text
GET /cache/artifact-grant-validation-key
```

Cache retains that public key only in memory. A known-key request makes no Server call. An unknown key ID allows one refresh, then fails closed.

The artifact route remains:

```text
GET /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}
Authorization: Bearer <CacheArtifactGrant>
```

The fill route remains separate:

```text
POST /repositories/{repositoryId}/directory-version-zips/{directoryVersionId}/fill
```

A read grant cannot authorize fill, and a fill permit cannot authorize a read.

## Integrity and storage

BLAKE3 is the only recomputed artifact-integrity hash in Cache and the CLI. SHA-256 is computed once while producing a ZIP because Blob compliance metadata requires it. Size is metadata, not identity or an integrity check.

ZIP production writes:

- `zip_sha256hash`
- `zip_blake3hash`
- `zip_size`

The ZIP producer computes both hashes in one pass, resets the same temporary file stream, and uploads from that stream.

Cache identifies a stored generation by repository ID, directory-version ID, and BLAKE3. Its development-only SQLite schema stores only the exact `Absent -> Staging -> Complete` lifecycle. No migration is provided; an older development database requires an explicit local reset.

Cache stages downloaded bytes on the managed filesystem, verifies BLAKE3, publishes the final file, records `Complete`, and verifies the final file again before reporting success. A different or corrupted generation is never served.

Network download and hashing do not hold the serialized publication section. Exact concurrent fills coalesce, while distinct fills are limited by `GRACE_CACHE_MAX_CONCURRENT_FILLS`, which defaults to four. The CLI retries only `CacheFillCapacityExceeded`, using fresh preparations and a monotonic 60-second budget.

## Failure behavior

| Failure | Result |
| --- | --- |
| Missing or malformed grant | `401` before local lookup |
| Invalid signature, algorithm, key, issuer, audience, or time bounds | `401` before local lookup |
| Valid grant bound to another method, route, artifact, repository, directory version, or BLAKE3 value | `403` before local lookup |
| Validation key unavailable | `503` before local lookup |
| Valid exact grant with no matching generation | `404` |
| Invalid or rejected fill permit | Existing generic fill failure before a hit shortcut |
| Downloaded bytes do not match BLAKE3 | No `Complete` state or successful fill |
| CLI BLAKE3 mismatch or invalid ZIP | No Working Directory Update and no Direct fallback |

## Working Directory Update boundary

Cache retrieval does not change Working Directory Update contracts. The verified, resettable staged ZIP stream enters `ConnectZipStaging.prepare`; only a successfully prepared ZIP reaches the existing Working Directory Update transaction. Cache-specific failure cannot mutate the working directory.

## Supported world and deferred work

Current support is Linux x64 Cache-required Connect with a loopback Cache. Grace Cache has no product-level client-count limit for verified hits.

The following remain outside the current contract:

- persisted Cache-required configuration;
- `grace cache status` or disable commands;
- Cache-preferred or Direct fallback behavior;
- FileVersion, ContentBlock, Watch, branch-switch, or batch retrieval;
- non-loopback or shared Cache operation;
- durable signing keys, enrollment, holder binding, replay tracking, or overlapping key rotation;
- retirement delivery, cleanup scheduling, prefetch, HA/DR, or generalized recovery.

Bearer grants may be replayed for the same immutable artifact during their five-minute lifetime. Server restart replaces the ephemeral key and may require a new preparation. Older unmatched generations may remain until later retirement work, but they cannot satisfy a grant for a different BLAKE3 value.
