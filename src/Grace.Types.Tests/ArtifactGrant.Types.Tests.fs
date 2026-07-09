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
    let cacheServicePrincipalId = "cache-service-client"
    let targetRoot = DirectoryVersionId.Parse "11111111-1111-1111-1111-111111111111"
    let otherRoot = DirectoryVersionId.Parse "22222222-2222-2222-2222-222222222222"
    let artifactIdentity = "GraceZipFiles/11111111-1111-1111-1111-111111111111.zip"
    let otherArtifactIdentity = "11111111-1111-1111-1111-111111111111.msgpack"

    /// Creates a deterministic validation request for the cache-mode artifact test surface.
    let validationRequest () =
        ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

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
                cacheServicePrincipalId,
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
        let key, validationKey = signingKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
        use key = key
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let result = validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) grant

        assertOk result

    [<Test>]
    member _.``direct mode does not require an artifact grant``() =
        let request = ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity)

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
        let key, validationKey = signingKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
        use key = key
        let grant = signedGrantForMode "key-1" key MaterializationExecutionMode.Direct now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]
        let request = ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity)

        validateWithKeySet now (keySet [ validationKey ]) request grant
        |> assertError WrongExecutionMode

    [<Test>]
    member _.``unsigned and malformed grants fail closed``() =
        let key, validationKey = signingKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
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
        let key, validationKey = signingKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
        let wrongKey, _ = signingKey "wrong" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
        use key = key
        use wrongKey = wrongKey
        let grant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let wrongCache = ArtifactGrantValidationRequest.Create("other-cache", targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

        let wrongRoot = ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, otherRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

        let wrongMode =
            ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.CachePreferred, artifactIdentity)

        let wrongArtifact =
            ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.CacheRequired, otherArtifactIdentity)

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
        let key, validationKey = signingKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
        use key = key

        let expiredGrant = signedGrant "key-1" key (now.Minus(Duration.FromMinutes 10L)) ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        let longTtlGrant = signedGrant "key-1" key now (ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus(Duration.FromTicks 1L)) [ artifactIdentity ]

        let expiredKey: ArtifactGrantValidationKey = { validationKey with ExpiresAt = now }

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) expiredGrant
        |> assertError ExpiredGrant

        validateWithKeySet now (keySet [ validationKey ]) (validationRequest ()) longTtlGrant
        |> assertError GrantTtlTooLong

        validateWithKeySet now (keySet [ expiredKey ]) (validationRequest ()) longTtlGrant
        |> assertError GrantTtlTooLong

        let validGrant = signedGrant "key-1" key now ArtifactGrantContract.DefaultGrantTtl [ artifactIdentity ]

        validateWithKeySet now (keySet [ expiredKey ]) (validationRequest ()) validGrant
        |> assertError ExpiredValidationKey

    [<Test>]
    member _.``current and overlap keys validate until old validation key expiry``() =
        let oldKey, oldValidationKey = signingKey "old-key" (now.Minus(Duration.FromHours 2)) (now.Plus(Duration.FromMinutes 1L))
        let newKey, newValidationKey = signingKey "new-key" now (now.Plus(Duration.FromHours 2))
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
        let key, _ = signingKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
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
        let key, validationKey = signingKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1))
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
    let cacheServicePrincipalId = "cache-service-client"
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
        let validationKey = GrantCrypto.exportValidationKey "key-1" (now.Minus(Duration.FromHours 1)) (now.Plus(Duration.FromHours 1)) signingKey

        let payload =
            ArtifactGrantPayload.Create(
                requesterPrincipalId,
                GrantCrypto.holderKeyThumbprint holderPublicKey,
                cacheServicePrincipalId,
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
        ArtifactRequestValidationRequest.Create(
            cacheServicePrincipalId,
            targetRoot,
            MaterializationExecutionMode.CacheRequired,
            artifactIdentity,
            methodName,
            requestRoute
        )

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

        GrantCrypto.validateArtifactRequestWithKeySet exactAdmissionBoundary keySet (request "GET" route) (Some grant) (Some proof)
        |> assertProofOk

        GrantCrypto.validateArtifactRequestWithKeySet
            (exactAdmissionBoundary.Plus(Duration.FromTicks 1L))
            keySet
            (request "GET" route)
            (Some grant)
            (Some proof)
        |> assertProofError ExpiredProof

        let overlongProof = { proof with Payload = { proof.Payload with ExpiresAt = proof.Payload.ExpiresAt.Plus(Duration.FromTicks 1L) } }

        GrantCrypto.validateArtifactRequestWithKeySet now keySet (request "GET" route) (Some grant) (Some overlongProof)
        |> assertProofError ProofLifetimeTooLong

    [<Test>]
    member _.``direct mode bypass remains explicit and requires neither grant nor holder proof``() =
        let directRequest =
            ArtifactRequestValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.Direct, artifactIdentity, "GET", route)

        GrantCrypto.validateArtifactRequestWithKeySet now (ArtifactGrantValidationKeySet.Create(now, [])) directRequest None None
        |> assertProofOk

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
