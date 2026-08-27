# Grace Cache implementation audit

## Delivery history

| Calibration | Delivered behavior |
| --- | --- |
| GC-CAL-01 and GC-CAL-02 | Private SQLite ownership, same-root staging, exact publication order, finite restart classification, and loopback ZIP serving |
| GC-CAL-03 | HTTP miss-to-hit tracer with a separate fill operation |
| GC-CAL-04 | Cache-required Connect, typed capacity retry, ZIP staging, and the unchanged Working Directory Update boundary |
| GC-CAL-05 | Server-signed BLAKE3 read grants, grant-before-lookup admission, exact-generation storage, and client BLAKE3 verification |

Issue #597 remains completed. Issue #1035 is the first post-Issue #597 Product V1 tracer and does not reuse legacy Cache branch ancestry.

## GC-CAL-05 implementation

Grace Server owns one ephemeral P-256 process key. Authenticated `POST /cache/prepareDirectoryVersionZip` checks current repository access, confirms or creates the root ZIP, reads `zip_blake3hash`, and returns:

- `DirectoryVersionZipCacheArtifact` with repository ID, directory-version ID, and BLAKE3;
- a compact ES256 read grant with a fixed five-minute lifetime;
- the existing separate fill permit and redemption bytes.

Anonymous `GET /cache/artifact-grant-validation-key` publishes only the fixed issuer, audience, algorithm, key ID, and public P-256 JWK.

Grace Cache validates the Bearer grant before any local state lookup. It checks algorithm, key ID, signature, issuer, audience, time bounds, method, route, artifact kind, repository, directory version, and BLAKE3. A known key stays local. An unknown key ID performs at most one refresh from the configured Server.

The fill operation validates the separate permit before an existing-hit shortcut. Read grants and fill permits remain distinct capabilities.

## Storage and producer audit

The development Cache schema version is 3. The exact stored generation includes repository ID, directory-version ID, and BLAKE3. Cache commit, reopen, staged-byte, and final-byte checks recompute only BLAKE3. SHA-256 and size are not Cache integrity inputs.

Directory-version ZIP production computes SHA-256 and BLAKE3 once over the same temporary stream, resets that stream, and reuses it for Blob upload. Blob metadata names are `zip_sha256hash`, `zip_blake3hash`, and `zip_size`.

The durable lifecycle remains `Absent -> Staging -> Complete`. No migration, retirement state, cleanup scheduler, replay store, or second lifecycle was added.

## CLI and Working Directory Update audit

Cache-required Connect reads the Cache fill key and completes Server preparation before its first artifact GET. Both the initial and post-fill GET carry a read grant. The post-fill request reuses the grant while valid and obtains a replacement after expiry.

The CLI copies the complete response to a temporary staging file, recomputes BLAKE3, and resets the stream before invoking ZIP staging. A mismatch does not invoke the ZIP consumer, Working Directory Update, or Direct fallback.

Direct Connect and Working Directory Update source contracts are unchanged.

## Contract propagation

The shared DTOs, Server SDK, OpenAPI source fragments, bundled OpenAPI 3.2 document, 3.1.2 generator projection, proof manifest, and generated SDK metadata are derived from the same contract. The Server endpoint authorization manifest lists preparation as authenticated and both validation-key publication and permit redemption as anonymous transport boundaries.

## Focused validation

The implementation includes focused tests for:

- compact ES256 issuance, exact validation, malformed artifacts, substitution, and strict expiry;
- Server process signing and fill-permit binding;
- known-key local validation and one unknown-key refresh;
- grant-required Cache reads and exact-generation serving;
- BLAKE3 commit, reopen, corruption, conflict, and injected restart boundaries;
- fill coalescing, permit-before-hit behavior, and distinct-fill capacity;
- preparation-before-GET, grant reuse, controlled-clock replacement, and BLAKE3-before-consumption;
- Cache-required Connect composition with no Direct fallback or Working Directory Update on failure.

## Residual risk and deferred work

A bearer grant may be replayed for the same immutable generation during its five-minute lifetime. A Server restart replaces the ephemeral validation key. Unmatched old generations may remain on disk until later retirement work, but exact BLAKE3 selection prevents them from satisfying a new grant.

Status commands, persisted Cache configuration, FileVersion and ContentBlock retrieval, non-loopback operation, durable keys, replay tracking, rotation, retirement delivery, prefetch, and HA/DR remain deferred.
