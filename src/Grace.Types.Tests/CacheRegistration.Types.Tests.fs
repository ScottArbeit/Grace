namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.CacheRegistration
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Security.Cryptography

/// Covers deterministic administrator-owned Cache enrollment and runtime proof behavior.
[<Parallelizable(ParallelScope.All)>]
type CacheRegistrationLifecycleTests() =

    let cacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
    let ownerId = Guid.Parse "22222222-2222-2222-2222-222222222222"
    let organizationId = Guid.Parse "33333333-3333-3333-3333-333333333333"
    let repositoryId = Guid.Parse "44444444-4444-4444-4444-444444444444"
    let otherRepositoryId = Guid.Parse "55555555-5555-5555-5555-555555555555"
    let now = Instant.FromUtc(2026, 7, 13, 10, 0)

    /// Builds a syntactically valid public key shape for pure lifecycle tests.
    let publicKey () = CacheIdentityPublicKey.Create("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")

    /// Builds one administrator enrollment request with explicit durable repository scope.
    let enrollment (repositories: Guid seq) =
        {
            Class = nameof CacheEnrollmentRequest
            DisplayName = "Seattle cache"
            BoundaryKind = CacheBoundaryKind.Organization
            OwnerId = ownerId
            OrganizationId = Some organizationId
            RepositoryScopes =
                List<CacheRepositoryScope>(
                    repositories
                    |> Seq.map (fun repositoryId -> CacheRepositoryScope.Create(organizationId, repositoryId))
                )
            PublicKey = publicKey ()
            Endpoint = "https://cache.example.test"
            Health = CacheHealthStatus.Healthy
            SoftwareVersion = "1.0.0"
            ProtocolVersion = "v1"
            PrefetchSupported = true
        }

    /// Enrolls one Cache in the empty singleton state.
    let enrolled () = Lifecycle.enroll CacheRegistrationState.Empty cacheId (enrollment [ repositoryId ]) "admin-user" now

    [<Test>]
    member _.``administrator enrollment stores CacheId boundary audit identity and explicit repositories``() =
        let state, result = enrolled ()
        let registration = state.Registrations[0]
        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.Enrolled)
        Assert.That(registration.CacheId, Is.EqualTo cacheId)
        Assert.That(registration.DisplayName, Is.EqualTo "Seattle cache")
        Assert.That(registration.OwnerId, Is.EqualTo ownerId)
        Assert.That(registration.OrganizationId, Is.EqualTo(Some organizationId))
        Assert.That(registration.RepositoryScopes, Has.Length.EqualTo 1)
        Assert.That(registration.RepositoryScopes[0].RepositoryId, Is.EqualTo repositoryId)
        Assert.That(registration.EnrolledBy, Is.EqualTo "admin-user")
        Assert.That(registration.PrefetchSupported, Is.True)
        Assert.That(registration.RotationDueAt, Is.EqualTo(now.Plus RegistrationLifetime.KeyRotationInterval))

    [<Test>]
    member _.``refresh updates only allowed operational facts and preserves administrator owned identity state``() =
        let state, _ = enrolled ()

        let request =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://cache-two.example.test"
                Health = CacheHealthStatus.Unhealthy
                SoftwareVersion = "1.1.0"
                ProtocolVersion = "v2"
                PrefetchSupported = false
                ObservedAt = now.Plus(Duration.FromHours 1)
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let refreshedAt =
            now
                .Plus(Duration.FromHours 1)
                .Plus(Duration.FromMinutes 1L)

        let next, result = Lifecycle.refresh state request refreshedAt
        let registration = next.Registrations[0]
        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.Refreshed)
        Assert.That(registration.Endpoint, Is.EqualTo "https://cache-two.example.test")
        Assert.That(registration.Health, Is.EqualTo CacheHealthStatus.Unhealthy)
        Assert.That(registration.SoftwareVersion, Is.EqualTo "1.1.0")
        Assert.That(registration.RepositoryScopes, Has.Length.EqualTo 1)
        Assert.That(registration.RepositoryScopes[0].RepositoryId, Is.EqualTo repositoryId)
        Assert.That(registration.DisplayName, Is.EqualTo "Seattle cache")
        Assert.That(registration.PublicKey, Is.EqualTo state.Registrations[0].PublicKey)
        Assert.That(registration.EnrolledBy, Is.EqualTo "admin-user")

    [<Test>]
    member _.``revoked and expired registrations are never selected or refreshed``() =
        let state, _ = enrolled ()
        let revoked, revokeResult = Lifecycle.revoke state cacheId (now.Plus(Duration.FromMinutes 1L))

        let refresh =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                Health = CacheHealthStatus.Healthy
                SoftwareVersion = "1.0.0"
                ProtocolVersion = "v1"
                PrefetchSupported = true
                ObservedAt = now.Plus(Duration.FromHours 2)
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let afterRefresh, refreshResult = Lifecycle.refresh revoked refresh (now.Plus(Duration.FromHours 2))
        let eligible = Lifecycle.selectEligible afterRefresh (CacheRegistrationSelectionQuery.Create(Some repositoryId, false)) (now.Plus(Duration.FromHours 2))
        Assert.That(revokeResult.Status, Is.EqualTo CacheRegistrationRefreshStatus.Revoked)
        Assert.That(refreshResult.Status, Is.EqualTo CacheRegistrationRefreshStatus.Revoked)
        Assert.That(eligible, Is.Empty)

    [<Test>]
    member _.``selection uses exact repository identity and current health without mode or read-through capability``() =
        let state, _ = enrolled ()
        let exact = Lifecycle.selectEligible state (CacheRegistrationSelectionQuery.Create(Some repositoryId, false)) (now.Plus(Duration.FromMinutes 30L))
        let wrong = Lifecycle.selectEligible state (CacheRegistrationSelectionQuery.Create(Some otherRepositoryId, false)) (now.Plus(Duration.FromMinutes 30L))
        let prefetch = Lifecycle.selectEligible state (CacheRegistrationSelectionQuery.Create(Some repositoryId, true)) (now.Plus(Duration.FromMinutes 30L))
        Assert.That(exact, Has.Length.EqualTo 1)
        Assert.That(wrong, Is.Empty)
        Assert.That(prefetch, Has.Length.EqualTo 1)

    [<Test>]
    member _.``enrollment validation rejects empty duplicate and cross-kind boundary shapes before mutation``() =
        let duplicate =
            { enrollment [ repositoryId
                           repositoryId ] with
                BoundaryKind = CacheBoundaryKind.Owner
                OrganizationId = Some organizationId
            }

        match Lifecycle.validateEnrollmentRequest duplicate with
        | Ok () -> Assert.Fail("Expected malformed administrator enrollment to be rejected.")
        | Error errors ->
            Assert.That(errors, Does.Contain "Owner boundary must not include OrganizationId.")
            Assert.That(errors, Does.Contain "RepositoryScopes must not include duplicate repositories.")

    [<Test>]
    member _.``cache proof binds CacheId operation request digest timestamp and current P-256 key``() =
        use privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = privateKey.ExportParameters false
        let key = CacheIdentityPublicKey.Create(ArtifactGrant.Base64Url.encode parameters.Q.X, ArtifactGrant.Base64Url.encode parameters.Q.Y)
        let digest = "canonical-request-digest"
        let proof = CacheRegistrationProof.createProof privateKey cacheId CacheRegistrationProof.RefreshOperation digest now
        Assert.That(CacheRegistrationProof.validate now key cacheId CacheRegistrationProof.RefreshOperation digest proof, Is.True)
        Assert.That(CacheRegistrationProof.validate now key cacheId CacheRegistrationProof.RotateKeyOperation digest proof, Is.False)

        Assert.That(
            CacheRegistrationProof.validate (now.Plus(Duration.FromSeconds 31L)) key cacheId CacheRegistrationProof.RefreshOperation digest proof,
            Is.False
        )
