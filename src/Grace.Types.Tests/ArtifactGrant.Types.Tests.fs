namespace Grace.Types.Tests

module GrantCrypto = Grace.Shared.ArtifactGrant

open Grace.Shared.ArtifactGrant
open Grace.Types.ArtifactGrant
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json

/// Covers signed artifact grant validation and fail-closed cache behavior.
[<Parallelizable(ParallelScope.All)>]
type ArtifactGrantValidationTests() =

    let now = Instant.FromUtc(2026, 7, 9, 12, 0)
    let cacheId = "cache-service-client"
    let targetRoot = DirectoryVersionId.Parse "11111111-1111-1111-1111-111111111111"
    let otherRoot = DirectoryVersionId.Parse "22222222-2222-2222-2222-222222222222"
    let artifactIdentity = "GraceZipFiles/11111111-1111-1111-1111-111111111111.zip"
    let otherArtifactIdentity = "11111111-1111-1111-1111-111111111111.msgpack"

    /// Matches the complete actor-published public validation-key window.
    let canonicalValidationKeyLifetime = ArtifactGrantContract.SigningKeyActiveLifetime.Plus ArtifactGrantContract.MaximumAcceptedGrantTtl

    /// Creates a deterministic validation request for the cache-mode artifact test surface.
    let validationRequest () = ArtifactGrantValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

    /// Creates one P-256 signing key and matching public validation key.
    let signingKey keyId createdAt expiresAt =
        let key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        key, GrantCrypto.exportValidationKey keyId createdAt expiresAt key

    /// Builds and signs one artifact grant for the supplied execution mode.
    let signedGrantForMode keyId key executionMode issuedAt ttl artifacts =
        let header = ArtifactGrantHeader.Create keyId

        let payload =
            ArtifactGrantPayload.Create(
                "user-11111111-1111-1111-1111-111111111111",
                "holder-thumbprint",
                cacheId,
                targetRoot,
                executionMode,
                artifacts,
                issuedAt,
                ttl
            )

        GrantCrypto.sign key header payload

    /// Builds and signs one valid cache-mode artifact grant.
    let signedGrant keyId key issuedAt ttl artifacts = signedGrantForMode keyId key MaterializationExecutionMode.CacheRequired issuedAt ttl artifacts

    /// Creates a validation-key set with a deterministic publication time.
    let keySet keys = ArtifactGrantValidationKeySet.Create(now, keys)

    /// Asserts that grant validation succeeded.
    let assertOk result =
        match result with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected grant validation to succeed, got {ArtifactGrantValidationError.toMessage error}.")

    /// Asserts that grant validation failed with the expected fail-closed reason.
    let assertError (expected: ArtifactGrantValidationError) result =
        match result with
        | Ok () -> Assert.Fail("Expected grant validation to fail.")
        | Error actual -> if actual <> expected then Assert.Fail($"Expected {expected}, got {actual}.")

    [<Test>]
    member _.``valid grant is accepted for intended cache target root execution mode and artifact``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let result = validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) grant

        assertOk result

    [<Test>]
    member _.``direct mode does not require an artifact grant``() =
        let request = ArtifactGrantValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity)

        let mutable refreshCalled = false

        let result =
            validateRequiredForCacheMode
                now
                (fun _ ->
                    refreshCalled <- true
                    RefreshSkipped)
                (keySet [])
                request
                None

        assertOk result
        Assert.That(refreshCalled, Is.False)

    [<Test>]
    member _.``low-level signed grant validator rejects direct mode payloads``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrantForMode "key-1" key MaterializationExecutionMode.Direct now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]
        let request = ArtifactGrantValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity)

        validateWithKeySet now (keySet [ validationKey ]) request grant
        |> assertError WrongExecutionMode

    [<Test>]
    member _.``unsigned and malformed grants fail closed``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let missingKey = { grant with Header = { grant.Header with KeyId = String.Empty } }
        let unsigned = { grant with Signature = String.Empty }
        let unsupported = { grant with Header = { grant.Header with Algorithm = "HS256" } }

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) missingKey
        |> assertError MissingKeyId

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) unsigned
        |> assertError MissingSignature

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) unsupported
        |> assertError (UnsupportedAlgorithm "HS256")

    [<Test>]
    member _.``wrong cache root execution mode artifact and signature are rejected``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        let wrongKey, _ = signingKey "wrong" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        use wrongKey = wrongKey
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let wrongCache = ArtifactGrantValidationRequest.Create("other-cache", targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

        let wrongRoot = ArtifactGrantValidationRequest.Create(cacheId, otherRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

        let wrongMode = ArtifactGrantValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.CachePreferred, artifactIdentity)

        let wrongArtifact = ArtifactGrantValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.CacheRequired, otherArtifactIdentity)

        let wrongSignature = signedGrant "key-1" wrongKey now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        validateWithKeySet now (keySet [ validationKey ]) wrongCache grant
        |> assertError WrongCacheService

        validateWithKeySet now (keySet [ validationKey ]) wrongRoot grant
        |> assertError WrongTargetRoot

        validateWithKeySet now (keySet [ validationKey ]) wrongMode grant
        |> assertError WrongExecutionMode

        validateWithKeySet now (keySet [ validationKey ]) wrongArtifact grant
        |> assertError WrongArtifact

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) wrongSignature
        |> assertError InvalidSignature

    [<Test>]
    member _.``expired grant excessive ttl and expired key are rejected``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key

        let expiredGrant = signedGrant "key-1" key (now.Minus(Duration.FromMinutes 10L)) ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let longTtlGrant = signedGrant "key-1" key now (ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus(Duration.FromTicks 1L)) [ artifactIdentity ]

        let boundaryExpiresAt = now.Minus ArtifactGrantContract.MaximumProofClockSkew

        let boundaryKey: ArtifactGrantValidationKey =
            { validationKey with
                CreatedAt = boundaryExpiresAt.Minus canonicalValidationKeyLifetime
                NotBefore = boundaryExpiresAt.Minus canonicalValidationKeyLifetime
                ExpiresAt = boundaryExpiresAt
            }

        let insideBoundaryKey: ArtifactGrantValidationKey =
            { boundaryKey with
                CreatedAt = boundaryKey.CreatedAt.Plus(Duration.FromTicks 1L)
                NotBefore = boundaryKey.NotBefore.Plus(Duration.FromTicks 1L)
                ExpiresAt = boundaryKey.ExpiresAt.Plus(Duration.FromTicks 1L)
            }

        let expiredKey: ArtifactGrantValidationKey =
            { boundaryKey with
                CreatedAt = boundaryKey.CreatedAt.Minus(Duration.FromTicks 1L)
                NotBefore = boundaryKey.NotBefore.Minus(Duration.FromTicks 1L)
                ExpiresAt = boundaryKey.ExpiresAt.Minus(Duration.FromTicks 1L)
            }

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) expiredGrant
        |> assertError ExpiredGrant

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) longTtlGrant
        |> assertError GrantTtlTooLong

        validateWithKeySet now (keySet [ expiredKey ]) (validationRequest ()) longTtlGrant
        |> assertError GrantTtlTooLong

        let validGrant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        validateWithKeySet now (keySet [ insideBoundaryKey ]) (validationRequest ()) validGrant
        |> assertOk

        validateWithKeySet now (keySet [ boundaryKey ]) (validationRequest ()) validGrant
        |> assertOk

        validateWithKeySet now (keySet [ expiredKey ]) (validationRequest ()) validGrant
        |> assertError ExpiredValidationKey

    [<Test>]
    member _.``grant and validation key clock tolerance is symmetric without extending declared grant ttl``() =
        let tolerance = ArtifactGrantContract.MaximumProofClockSkew
        let precisionStep = Duration.FromMilliseconds 1L
        let keyNotBefore = now.Plus tolerance
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key

        let insideFutureGrant =
            signedGrant "key-1" key (now.Plus(tolerance.Minus precisionStep)) ArtifactGrantContract.MaximumAcceptedGrantTtl [ artifactIdentity ]

        let futureGrant = signedGrant "key-1" key (now.Plus tolerance) ArtifactGrantContract.MaximumAcceptedGrantTtl [ artifactIdentity ]

        validateWithKeySet
            now
            (keySet [ { validationKey with
                          CreatedAt = validationKey.CreatedAt.Minus precisionStep
                          NotBefore = validationKey.NotBefore.Minus precisionStep
                          ExpiresAt = validationKey.ExpiresAt.Minus precisionStep
                      } ])
            (validationRequest ())
            insideFutureGrant
        |> assertOk

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) futureGrant
        |> assertOk

        let tooFutureKey =
            { validationKey with
                CreatedAt = validationKey.CreatedAt.Plus precisionStep
                NotBefore = validationKey.NotBefore.Plus precisionStep
                ExpiresAt = validationKey.ExpiresAt.Plus precisionStep
            }

        validateWithKeySet now (keySet [ tooFutureKey ]) (validationRequest ()) futureGrant
        |> assertError ValidationKeyNotYetValid

        let tooFutureGrant = signedGrant "key-1" key (now.Plus(tolerance.Plus precisionStep)) ArtifactGrantContract.MaximumAcceptedGrantTtl [ artifactIdentity ]

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) tooFutureGrant
        |> assertError GrantNotYetValid

        let expiredAtBoundary =
            signedGrant
                "key-1"
                key
                (now.Minus(ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus tolerance))
                ArtifactGrantContract.MaximumAcceptedGrantTtl
                [ artifactIdentity ]

        let expiredJustInside =
            signedGrant
                "key-1"
                key
                (now.Minus(ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus(tolerance.Minus precisionStep)))
                ArtifactGrantContract.MaximumAcceptedGrantTtl
                [ artifactIdentity ]

        validateWithKeySet
            now
            (keySet [ { validationKey with
                          CreatedAt = now.Minus(Duration.FromHours 1)
                          NotBefore = now.Minus(Duration.FromHours 1)
                          ExpiresAt =
                              now
                                  .Minus(Duration.FromHours 1)
                                  .Plus canonicalValidationKeyLifetime
                      } ])
            (validationRequest ())
            expiredJustInside
        |> assertOk

        validateWithKeySet
            now
            (keySet [ { validationKey with
                          CreatedAt = now.Minus(Duration.FromHours 1)
                          NotBefore = now.Minus(Duration.FromHours 1)
                          ExpiresAt =
                              now
                                  .Minus(Duration.FromHours 1)
                                  .Plus canonicalValidationKeyLifetime
                      } ])
            (validationRequest ())
            expiredAtBoundary
        |> assertOk

        let overlong = signedGrant "key-1" key now (ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus precisionStep) [ artifactIdentity ]

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) overlong
        |> assertError GrantTtlTooLong

    [<Test>]
    member _.``signed grant start must equal issuance while ttl boundaries remain independent of clock tolerance``() =
        let precisionStep = Duration.FromMilliseconds 1L
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let valid = signedGrant "key-1" key now ArtifactGrantContract.MaximumAcceptedGrantTtl [ artifactIdentity ]

        let resign payload = GrantCrypto.sign key valid.Header payload
        let earlier = resign { valid.Payload with NotBefore = valid.Payload.IssuedAt.Minus precisionStep }
        let later = resign { valid.Payload with NotBefore = valid.Payload.IssuedAt.Plus precisionStep }

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) valid
        |> assertOk

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) earlier
        |> assertError GrantTtlTooLong

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) later
        |> assertError GrantTtlTooLong

        let overlong = resign { valid.Payload with ExpiresAt = valid.Payload.ExpiresAt.Plus precisionStep }

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) overlong
        |> assertError GrantTtlTooLong

    [<Test>]
    member _.``signed grant lifetime must be positive before clock tolerance admission``() =
        let precisionStep = Duration.FromMilliseconds 1L
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key

        let maximum = signedGrant "key-1" key now ArtifactGrantContract.MaximumAcceptedGrantTtl [ artifactIdentity ]

        /// Re-signs the canonical grant with one adversarial declared expiry.
        let resign expiresAt = GrantCrypto.sign key maximum.Header { maximum.Payload with ExpiresAt = expiresAt }

        let positive = resign (maximum.Payload.IssuedAt.Plus precisionStep)
        let zero = resign maximum.Payload.IssuedAt
        let negative = resign (maximum.Payload.IssuedAt.Minus precisionStep)
        let overMaximum = resign (maximum.Payload.IssuedAt.Plus(ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus precisionStep))

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) positive
        |> assertOk

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) maximum
        |> assertOk

        for invalid in [ zero; negative; overMaximum ] do
            validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) invalid
            |> assertError GrantTtlTooLong

    [<Test>]
    member _.``validation key publications require the exact canonical public lifetime``() =
        let precisionStep = Duration.FromMilliseconds 1L
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) grant
        |> assertOk

        let malformedKeys =
            [
                { validationKey with ExpiresAt = validationKey.ExpiresAt.Minus precisionStep }
                { validationKey with ExpiresAt = validationKey.ExpiresAt.Plus precisionStep }
                { validationKey with ExpiresAt = validationKey.NotBefore }
                { validationKey with ExpiresAt = validationKey.NotBefore.Minus precisionStep }
                { validationKey with NotBefore = Instant.MinValue; CreatedAt = Instant.MinValue; ExpiresAt = Instant.MaxValue }
            ]

        malformedKeys
        |> List.iter (fun malformed ->
            validateWithKeySet now (keySet [ malformed ]) (validationRequest ()) grant
            |> assertError InvalidValidationKeySet)

    [<Test>]
    member _.``mixed malformed validation key publication fails before lookup and refresh``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "published-key" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let unknownGrant = signedGrant "unknown-key" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let malformedKey = { validationKey with KeyId = "malformed-key"; ExpiresAt = validationKey.ExpiresAt.Plus(Duration.FromMilliseconds 1L) }

        let mutable refreshCount = 0

        validateWithRefresh
            now
            (fun _ ->
                refreshCount <- refreshCount + 1
                RefreshSkipped)
            (keySet [ validationKey; malformedKey ])
            (validationRequest ())
            unknownGrant
        |> assertError InvalidValidationKeySet

        Assert.That(refreshCount, Is.Zero)

    [<Test>]
    member _.``malformed validation key sets fail closed with their typed contract``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]
        let nullKey = Unchecked.defaultof<ArtifactGrantValidationKey>
        let nullKeySet = Unchecked.defaultof<ArtifactGrantValidationKeySet>
        let nullKeys = { keySet [] with Keys = null }
        let malformedClass = { validationKey with Class = null }
        let malformedCoordinates = { validationKey with PublicKeyX = "not-base64url" }
        let missingCoordinate = { validationKey with PublicKeyY = null }

        let nonImportablePoint =
            { validationKey with
                PublicKeyX = GrantCrypto.Base64Url.encode (Array.zeroCreate 32)
                PublicKeyY = GrantCrypto.Base64Url.encode (Array.zeroCreate 32)
            }

        let malformedTimes = { validationKey with ExpiresAt = validationKey.NotBefore }

        [
            nullKeySet
            nullKeys
            keySet [ nullKey ]
            keySet [ malformedClass ]
            keySet [ malformedCoordinates ]
            keySet [ missingCoordinate ]
            keySet [ nonImportablePoint ]
            keySet [ malformedTimes ]
            keySet [ validationKey; validationKey ]
        ]
        |> List.iter (fun malformed ->
            validateWithKeySet now malformed (validationRequest ()) grant
            |> assertError InvalidValidationKeySet)

    [<Test>]
    member _.``grant validation entry points are total over null and malformed runtime shapes``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]
        let keys = keySet [ validationKey ]
        let nullGrant = Unchecked.defaultof<SignedArtifactGrant>
        let nullRequest = Unchecked.defaultof<ArtifactGrantValidationRequest>

        validateWithKeySet now keys (validationRequest ()) nullGrant
        |> assertError MissingGrant

        validateWithKeySet now keys nullRequest grant
        |> assertError InvalidClass

        validateWithKeySet now keys (validationRequest ()) { grant with Header = Unchecked.defaultof<ArtifactGrantHeader> }
        |> assertError MissingHeader

        validateWithKeySet now keys (validationRequest ()) { grant with Payload = Unchecked.defaultof<ArtifactGrantPayload> }
        |> assertError MissingPayload

        let malformedArtifacts = { grant with Payload = { grant.Payload with ArtifactIdentities = null } }

        validateWithKeySet now keys (validationRequest ()) malformedArtifacts
        |> assertError InvalidArtifactBinding

        let malformedArtifactEntry = { grant with Payload = { grant.Payload with ArtifactIdentities = List<string>([ null ]) } }

        validateWithKeySet now keys (validationRequest ()) malformedArtifactEntry
        |> assertError InvalidArtifactBinding

        validateWithKeySet now keys (validationRequest ()) { grant with Payload = { grant.Payload with Issuer = null } }
        |> assertError InvalidIssuer

        validateWithKeySet now keys (validationRequest ()) { grant with Payload = { grant.Payload with RequesterPrincipalId = null } }
        |> assertError ArtifactGrantValidationError.InvalidRequesterPrincipal

        validateWithKeySet now keys (validationRequest ()) { grant with Payload = { grant.Payload with HolderKeyThumbprint = null } }
        |> assertError MissingHolderKeyBinding

        validateRequiredForCacheMode now (fun _ -> RefreshSkipped) keys nullRequest (Some grant)
        |> assertError InvalidClass

        validateRequiredForCacheMode now (fun _ -> RefreshSkipped) keys (validationRequest ()) (Some nullGrant)
        |> assertError MissingGrant

    [<Test>]
    member _.``current and overlap keys validate until old validation key expiry``() =
        let oldExpiresAt = now.Plus(Duration.FromMinutes 1L)
        let oldNotBefore = oldExpiresAt.Minus canonicalValidationKeyLifetime
        let oldKey, oldValidationKey = signingKey "old-key" oldNotBefore oldExpiresAt
        let newKey, newValidationKey = signingKey "new-key" now (now.Plus canonicalValidationKeyLifetime)
        use oldKey = oldKey
        use newKey = newKey

        let oldGrant = signedGrant "old-key" oldKey now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]
        let newGrant = signedGrant "new-key" newKey now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let keys =
            keySet [ oldValidationKey
                     newValidationKey ]

        validateWithKeySet now keys (validationRequest ()) oldGrant
        |> assertOk

        validateWithKeySet now keys (validationRequest ()) newGrant
        |> assertOk

        let afterOldKeyExpiry = now.Plus(Duration.FromMinutes 2L)

        validateWithKeySet afterOldKeyExpiry keys (validationRequest ()) oldGrant
        |> assertError ExpiredValidationKey

    [<Test>]
    member _.``unknown key refresh is attempted once and then fails closed when still unknown``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, _ = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]
        let mutable refreshCount = 0

        let result =
            validateWithRefresh
                now
                (fun _ ->
                    refreshCount <- refreshCount + 1
                    RefreshAttempted(keySet []))
                (keySet [])
                (validationRequest ())
                grant

        result |> assertError (UnknownKeyId "key-1")
        Assert.That(refreshCount, Is.EqualTo 1)

    [<Test>]
    member _.``unknown key refresh can validate when refreshed publication contains the key``() =
        let keyNotBefore = now.Minus(Duration.FromHours 1)
        let key, validationKey = signingKey "key-1" keyNotBefore (keyNotBefore.Plus canonicalValidationKeyLifetime)
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]
        let mutable refreshCount = 0

        let result =
            validateWithRefresh
                now
                (fun _ ->
                    refreshCount <- refreshCount + 1
                    RefreshAttempted(keySet [ validationKey ]))
                (keySet [])
                (validationRequest ())
                grant

        assertOk result
        Assert.That(refreshCount, Is.EqualTo 1)

/// Covers requester and proof-of-possession binding for one cache artifact request.
[<Parallelizable(ParallelScope.All)>]
type ArtifactRequestProofValidationTests() =

    let now = Instant.FromUtc(2026, 7, 9, 12, 0)
    let cacheId = "cache-service-client"
    let requesterPrincipalId = "user-11111111-1111-1111-1111-111111111111"
    let targetRoot = DirectoryVersionId.Parse "11111111-1111-1111-1111-111111111111"
    let artifactIdentity = "GraceZipFiles/11111111-1111-1111-1111-111111111111.zip"
    let route = "/cache/artifacts/GraceZipFiles/11111111-1111-1111-1111-111111111111.zip"

    /// Creates a signing key and its canonical public holder-key contract.
    let holderKey () =
        let key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        key, GrantCrypto.exportHolderPublicKey key

    /// Creates a requester- and holder-bound grant plus its validation publication.
    let grantForHolder (holderPublicKey: ArtifactGrantHolderPublicKey) =
        let signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let keyNotBefore = now.Minus(Duration.FromHours 1)

        let validationKey =
            GrantCrypto.exportValidationKey
                "key-1"
                keyNotBefore
                (keyNotBefore
                    .Plus(ArtifactGrantContract.SigningKeyActiveLifetime)
                    .Plus ArtifactGrantContract.MaximumAcceptedGrantTtl)
                signingKey

        let payload =
            ArtifactGrantPayload.Create(
                requesterPrincipalId,
                GrantCrypto.holderKeyThumbprint holderPublicKey,
                cacheId,
                targetRoot,
                MaterializationExecutionMode.CacheRequired,
                [ artifactIdentity ],
                now,
                ArtifactGrantContract.DefaultGrantTtl
            )

        let grant = GrantCrypto.sign signingKey (ArtifactGrantHeader.Create "key-1") payload
        signingKey.Dispose()
        grant, ArtifactGrantValidationKeySet.Create(now, [ validationKey ])

    /// Creates the expected request-admission contract for one GET.
    let request methodName requestRoute =
        ArtifactRequestValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity, methodName, requestRoute)

    /// Asserts that full grant and holder-proof validation succeeded.
    let assertProofOk result =
        match result with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected artifact request proof validation to succeed, got {ArtifactGrantValidationError.toMessage error}.")

    /// Asserts that full grant and holder-proof validation failed closed.
    let assertProofError (expected: ArtifactGrantValidationError) (result: Result<unit, ArtifactGrantValidationError>) =
        match result with
        | Ok () -> Assert.Fail("Expected artifact request proof validation to fail.")
        | Error actual -> Assert.That(actual, Is.EqualTo expected)

    [<Test>]
    member _.``matching ephemeral holder proof authorizes the exact requester-bound artifact request``() =
        let holderPrivateKey, holderPublicKey = holderKey ()
        use holderPrivateKey = holderPrivateKey
        let grant, keySet = grantForHolder holderPublicKey

        let proof =
            GrantCrypto.createRequestProof
                holderPrivateKey
                holderPublicKey
                grant
                "get"
                $"  {route}?download=1#ignored  "
                artifactIdentity
                now
                ArtifactGrantContract.MaximumProofPresentationLifetime

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some proof)
        |> assertProofOk

        Assert.That(grant.Payload.RequesterPrincipalType, Is.EqualTo ArtifactGrantRequesterPrincipalType.User)
        Assert.That(grant.Payload.RequesterPrincipalId, Is.EqualTo requesterPrincipalId)

    [<Test>]
    member _.``copied grant signed by another holder key fails closed``() =
        let intendedPrivateKey, intendedPublicKey = holderKey ()
        let copiedPrivateKey, copiedPublicKey = holderKey ()
        use intendedPrivateKey = intendedPrivateKey
        use copiedPrivateKey = copiedPrivateKey
        let grant, keySet = grantForHolder intendedPublicKey

        let copiedProof =
            GrantCrypto.createRequestProof
                copiedPrivateKey
                copiedPublicKey
                grant
                "GET"
                route
                artifactIdentity
                now
                ArtifactGrantContract.MaximumProofPresentationLifetime

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some copiedProof)
        |> assertProofError HolderKeyMismatch

    [<Test>]
    member _.``proof is bound to grant digest method normalized route artifact and signature``() =
        let holderPrivateKey, holderPublicKey = holderKey ()
        use holderPrivateKey = holderPrivateKey
        let grant, keySet = grantForHolder holderPublicKey

        let proof =
            GrantCrypto.createRequestProof
                holderPrivateKey
                holderPublicKey
                grant
                "GET"
                route
                artifactIdentity
                now
                ArtifactGrantContract.MaximumProofPresentationLifetime

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "POST" route) (Some grant) (Some proof)
        |> assertProofError WrongProofMethod

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" "/cache/artifacts/other") (Some grant) (Some proof)
        |> assertProofError WrongProofRoute

        let tamperedProof = { proof with Signature = proof.Signature + "a" }

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some tamperedProof)
        |> assertProofError InvalidProofSignature

        let otherGrant = { grant with Signature = grant.Signature + "a" }

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some otherGrant) (Some proof)
        |> assertProofError InvalidSignature

    [<Test>]
    member _.``proof freshness accepts exact skew boundary and rejects overlong or stale presentations``() =
        let holderPrivateKey, holderPublicKey = holderKey ()
        use holderPrivateKey = holderPrivateKey
        let grant, keySet = grantForHolder holderPublicKey

        let proof =
            GrantCrypto.createRequestProof
                holderPrivateKey
                holderPublicKey
                grant
                "GET"
                route
                artifactIdentity
                now
                ArtifactGrantContract.MaximumProofPresentationLifetime

        let exactAdmissionBoundary = proof.Payload.ExpiresAt.Plus ArtifactGrantContract.MaximumProofClockSkew

        GrantCrypto.validateArtifactRequestWithKeySet
            (exactAdmissionBoundary.Minus(Duration.FromMilliseconds 1L))
            keySet
            (request "GET" route)
            (Some grant)
            (Some proof)
        |> assertProofOk

        GrantCrypto.validateArtifactRequestWithKeySet exactAdmissionBoundary keySet (request "GET" route) (Some grant) (Some proof)
        |> assertProofOk

        GrantCrypto.validateArtifactRequestWithKeySet
            (exactAdmissionBoundary.Plus(Duration.FromMilliseconds 1L))
            keySet
            (request "GET" route)
            (Some grant)
            (Some proof)
        |> assertProofError ExpiredProof

        let overlongProof = { proof with Payload = { proof.Payload with ExpiresAt = proof.Payload.ExpiresAt.Plus(Duration.FromTicks 1L) } }

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some overlongProof)
        |> assertProofError ProofLifetimeTooLong

        let inside = ArtifactGrantContract.MaximumProofClockSkew.Minus(Duration.FromMilliseconds 1L)

        for offset in
            [
                inside
                ArtifactGrantContract.MaximumProofClockSkew
            ] do
            let futureProof =
                GrantCrypto.createRequestProof
                    holderPrivateKey
                    holderPublicKey
                    grant
                    "GET"
                    route
                    artifactIdentity
                    (now.Plus offset)
                    ArtifactGrantContract.MaximumProofPresentationLifetime

            GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some futureProof)
            |> assertProofOk

        let outsideProof =
            GrantCrypto.createRequestProof
                holderPrivateKey
                holderPublicKey
                grant
                "GET"
                route
                artifactIdentity
                (now
                    .Plus(ArtifactGrantContract.MaximumProofClockSkew)
                    .Plus(Duration.FromMilliseconds 1L))
                ArtifactGrantContract.MaximumProofPresentationLifetime

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some outsideProof)
        |> assertProofError ProofNotYetValid

    [<Test>]
    member _.``request proof admission is total over null optional and nested envelopes``() =
        let holderPrivateKey, holderPublicKey = holderKey ()
        use holderPrivateKey = holderPrivateKey
        let grant, keySet = grantForHolder holderPublicKey

        let proof =
            GrantCrypto.createRequestProof
                holderPrivateKey
                holderPublicKey
                grant
                "GET"
                route
                artifactIdentity
                now
                ArtifactGrantContract.MaximumProofPresentationLifetime

        let nullGrant = Unchecked.defaultof<SignedArtifactGrant>
        let nullProof = Unchecked.defaultof<SignedArtifactRequestProof>
        let nullRequest = Unchecked.defaultof<ArtifactRequestValidationRequest>
        let nullKeySet = Unchecked.defaultof<ArtifactGrantValidationKeySet>

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some nullGrant) (Some proof)
        |> assertProofError MissingGrant

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some nullProof)
        |> assertProofError MissingProof

        GrantCrypto.validateArtifactRequestWithKeySet now keySet nullRequest (Some grant) (Some proof)
        |> assertProofError InvalidClass

        GrantCrypto.validateArtifactRequestWithKeySet now nullKeySet (request "GET" route) (Some grant) (Some proof)
        |> assertProofError InvalidValidationKeySet

        let missingHolder = { proof with HolderPublicKey = Unchecked.defaultof<ArtifactGrantHolderPublicKey> }

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some missingHolder)
        |> assertProofError MissingHolderPublicKey

        let missingPayload = { proof with Payload = Unchecked.defaultof<ArtifactRequestProofPayload> }

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some missingPayload)
        |> assertProofError MissingPayload

        let malformedPayloads =
            [
                { proof with Payload = { proof.Payload with GrantDigest = null } }, WrongGrantDigest
                { proof with Payload = { proof.Payload with HttpMethod = null } }, WrongProofMethod
                { proof with Payload = { proof.Payload with NormalizedRoute = null } }, WrongProofRoute
                { proof with Payload = { proof.Payload with ArtifactIdentity = null } }, WrongProofArtifact
            ]

        malformedPayloads
        |> List.iter (fun (malformedProof, expected) ->
            GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some malformedProof)
            |> assertProofError expected)

    [<Test>]
    member _.``direct mode bypass remains explicit and requires neither grant nor holder proof``() =
        let directRequest = ArtifactRequestValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity, "GET", route)

        GrantCrypto.validateArtifactRequestWithKeySet now (ArtifactGrantValidationKeySet.Create(now, [])) directRequest None None
        |> assertProofOk

        let malformedKeySet = Unchecked.defaultof<ArtifactGrantValidationKeySet>

        GrantCrypto.validateArtifactRequestWithKeySet now malformedKeySet directRequest None None
        |> assertProofOk

        GrantCrypto.validateRequiredForCacheMode
            now
            (fun _ -> RefreshSkipped)
            malformedKeySet
            (ArtifactGrantValidationRequest.Create(cacheId, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity))
            None
        |> assertProofOk

        let malformedDirectRequest = { directRequest with HttpMethod = String.Empty }

        GrantCrypto.validateArtifactRequestWithKeySet now malformedKeySet malformedDirectRequest None None
        |> assertProofError InvalidClass

        let malformedGrantRequest = ArtifactGrantValidationRequest.Create(String.Empty, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity)

        GrantCrypto.validateRequiredForCacheMode now (fun _ -> RefreshSkipped) malformedKeySet malformedGrantRequest None
        |> assertProofError InvalidClass

    [<Test>]
    member _.``signed protocol timestamps normalize to Unix milliseconds``() =
        let subMillisecond = now.Plus(Duration.FromTicks 9876L)
        let holderPrivateKey, holderPublicKey = holderKey ()
        use holderPrivateKey = holderPrivateKey

        let payload =
            ArtifactGrantPayload.Create(
                requesterPrincipalId,
                GrantCrypto.holderKeyThumbprint holderPublicKey,
                cacheId,
                targetRoot,
                MaterializationExecutionMode.CacheRequired,
                [ artifactIdentity ],
                subMillisecond,
                ArtifactGrantContract.DefaultGrantTtl
            )

        let proof =
            ArtifactRequestProofPayload.Create("digest", "GET", route, artifactIdentity, subMillisecond, ArtifactGrantContract.MaximumProofPresentationLifetime)

        Assert.That(payload.IssuedAt.ToUnixTimeTicks() % TimeSpan.TicksPerMillisecond, Is.EqualTo 0L)
        Assert.That(payload.NotBefore, Is.EqualTo payload.IssuedAt)
        Assert.That(payload.ExpiresAt.ToUnixTimeTicks() % TimeSpan.TicksPerMillisecond, Is.EqualTo 0L)
        Assert.That(proof.IssuedAt.ToUnixTimeTicks() % TimeSpan.TicksPerMillisecond, Is.EqualTo 0L)
        Assert.That(proof.ExpiresAt.ToUnixTimeTicks() % TimeSpan.TicksPerMillisecond, Is.EqualTo 0L)

    [<Test>]
    member _.``public JSON preserves user and holder proof contracts without private key material``() =
        let holderPrivateKey, holderPublicKey = holderKey ()
        use holderPrivateKey = holderPrivateKey
        let grant, _ = grantForHolder holderPublicKey

        let proof =
            GrantCrypto.createRequestProof
                holderPrivateKey
                holderPublicKey
                grant
                "GET"
                route
                artifactIdentity
                now
                ArtifactGrantContract.MaximumProofPresentationLifetime

        let grantJson = JsonSerializer.Serialize(grant, Grace.Shared.Constants.JsonSerializerOptions)
        let proofJson = JsonSerializer.Serialize(proof, Grace.Shared.Constants.JsonSerializerOptions)

        Assert.That(grantJson, Does.Contain("\"RequesterPrincipalType\": \"user\""))
        Assert.That(grantJson, Does.Contain("\"RequesterPrincipalId\""))
        Assert.That(grantJson, Does.Contain("\"HolderKeyThumbprint\""))
        Assert.That(proofJson, Does.Contain("\"HolderPublicKey\""))
        Assert.That(proofJson, Does.Not.Contain("PrivateKey"))
