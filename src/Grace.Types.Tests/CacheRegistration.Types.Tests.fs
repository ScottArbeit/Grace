namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.ArtifactGrant
open Grace.Types.CacheRegistration
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json

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
            AllowHttpEndpoint = false
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
        Assert.That(registration.Health, Is.EqualTo CacheHealthStatus.Unhealthy)
        Assert.That(registration.PrefetchSupported, Is.True)
        Assert.That(registration.RotationDueAt, Is.EqualTo(now.Plus RegistrationLifetime.KeyRotationInterval))

    /// Verifies administrator enrollment request JSON retains the server-owned PublicKey field and never borrows registration state fields.
    [<Test>]
    member _.``enrollment request JSON uses PublicKey rather than registration key fields``() =
        use document = JsonDocument.Parse(JsonSerializer.Serialize(enrollment [ repositoryId ], Constants.JsonSerializerOptions))
        let mutable ignoredProperty = Unchecked.defaultof<JsonElement>

        Assert.That(document.RootElement.TryGetProperty("PublicKey", &ignoredProperty), Is.True)
        Assert.That(document.RootElement.TryGetProperty("Health", &ignoredProperty), Is.False)
        Assert.That(document.RootElement.TryGetProperty("ActivePublicKey", &ignoredProperty), Is.False)
        Assert.That(document.RootElement.TryGetProperty("CandidatePublicKey", &ignoredProperty), Is.False)

    /// Verifies lifecycle responses preserve retry-after values through the JSON contract used by generated clients.
    [<Test>]
    member _.``registration result JSON round trips RetryAfterSeconds``() =
        let result: CacheRegistrationResult =
            {
                Class = nameof CacheRegistrationResult
                Status = CacheRegistrationRefreshStatus.RotationRetryAfter
                Registration = None
                Message = "Retry later."
                RetryAfterSeconds = Some 42
            }

        let json = JsonSerializer.Serialize(result, Constants.JsonSerializerOptions)
        let roundTripped = JsonSerializer.Deserialize<CacheRegistrationResult>(json, Constants.JsonSerializerOptions)

        Assert.That(roundTripped.RetryAfterSeconds, Is.EqualTo(Some 42))

    /// Verifies HTTP enrollment remains an administrator-selected exception stored with the exact origin.
    [<Test>]
    member _.``enrollment accepts only coherent explicit HTTP endpoint approval``() =
        let httpEnrollment = { enrollment [ repositoryId ] with Endpoint = "http://cache.example.test:8080"; AllowHttpEndpoint = true }

        let state, result = Lifecycle.enroll CacheRegistrationState.Empty cacheId httpEnrollment "admin-user" now
        let registration = state.Registrations[0]

        Assert.That(
            Lifecycle.validateEnrollmentRequest httpEnrollment
            |> Result.isOk,
            Is.True
        )

        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.Enrolled)
        Assert.That(registration.Endpoint, Is.EqualTo httpEnrollment.Endpoint)
        Assert.That(registration.AllowHttpEndpoint, Is.True)

        let missingApproval = { httpEnrollment with AllowHttpEndpoint = false }
        let incoherentApproval = { enrollment [ repositoryId ] with AllowHttpEndpoint = true }

        match Lifecycle.validateEnrollmentRequest missingApproval with
        | Ok () -> Assert.Fail("Expected HTTP enrollment without administrator approval to fail.")
        | Error errors -> Assert.That(errors, Does.Contain "Endpoint must be an absolute HTTPS URI unless AllowHttpEndpoint is explicitly selected.")

        match Lifecycle.validateEnrollmentRequest incoherentApproval with
        | Ok () -> Assert.Fail("Expected HTTP approval on an HTTPS endpoint to fail.")
        | Error errors -> Assert.That(errors, Does.Contain "AllowHttpEndpoint may be selected only for an HTTP Endpoint.")

    /// Verifies cache-authenticated refresh cannot substitute the scheme, host, port, or path of the stored endpoint.
    [<Test>]
    member _.``refresh rejects every endpoint substitution before durable mutation``() =
        let httpEnrollment = { enrollment [ repositoryId ] with Endpoint = "http://cache.example.test:8080"; AllowHttpEndpoint = true }

        let state, _ = Lifecycle.enroll CacheRegistrationState.Empty cacheId httpEnrollment "admin-user" now

        let substitutions =
            [
                "https://cache.example.test:8080/artifacts"
                "http://other-cache.example.test:8080"
                "http://cache.example.test:8081"
                "http://cache.example.test:8080/other-artifacts"
            ]

        for endpoint in substitutions do
            let request =
                {
                    Class = nameof CacheRegistrationRefreshRequest
                    CacheId = cacheId
                    Endpoint = endpoint
                    Health = CacheHealthStatus.Healthy
                    SoftwareVersion = "1.1.0"
                    ProtocolVersion = "v2"
                    PrefetchSupported = false
                    ObservedAt = now.Plus(Duration.FromMinutes 90L)
                    Proof = Unchecked.defaultof<SignedCacheRequestProof>
                }

            let next, result = Lifecycle.refresh state request (now.Plus(Duration.FromMinutes 90L))
            Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.EndpointMismatch)
            Assert.That(next, Is.EqualTo state)

    /// Verifies every public registration boundary rejects endpoint paths, credentials, queries, and fragments before actor mutation.
    [<TestCase("https://cache.example.test/cache")>]
    [<TestCase("https://cache.example.test/?preview=true")>]
    [<TestCase("https://cache.example.test/#fragment")>]
    [<TestCase("https://operator@cache.example.test")>]
    member _.``registration validators reject non-origin endpoints`` endpoint =
        let enrollmentRequest = { enrollment [ repositoryId ] with Endpoint = endpoint }

        let refreshRequest: CacheRegistrationRefreshRequest =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = endpoint
                Health = CacheHealthStatus.Healthy
                SoftwareVersion = "1.0.0"
                ProtocolVersion = "v1"
                PrefetchSupported = true
                ObservedAt = now.Plus(Duration.FromMinutes 90L)
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        for validation in
            [
                Lifecycle.validateEnrollmentRequest enrollmentRequest
                Lifecycle.validateRefreshRequest refreshRequest
            ] do
            match validation with
            | Ok () -> Assert.Fail("A path-qualified Cache endpoint must be rejected before lifecycle mutation.")
            | Error errors -> Assert.That(errors, Does.Contain "Endpoint must be an absolute HTTP or HTTPS origin with path '/'.")

    /// Verifies candidate proof cannot promote or extend a registration through an endpoint other than its exact stored endpoint.
    [<Test>]
    member _.``candidate promotion rejects endpoint substitution before durable mutation``() =
        let state, _ = enrolled ()
        let registration = { state.Registrations[0] with CandidatePublicKey = Some(publicKey ()) }
        let pendingState = { state with Registrations = [| registration |] }

        let request =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://other-cache.example.test"
                Health = CacheHealthStatus.Healthy
                SoftwareVersion = "1.1.0"
                ProtocolVersion = "v2"
                PrefetchSupported = false
                ObservedAt = now.Plus(Duration.FromMinutes 90L)
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let next, result = Lifecycle.promoteCandidate pendingState request (now.Plus(Duration.FromMinutes 90L))

        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.EndpointMismatch)
        Assert.That(next, Is.EqualTo pendingState)

    /// Verifies a response lost after candidate promotion can still prove the selected candidate through a normal refresh throttle result.
    [<Test>]
    member _.``promoted candidate remains identifiable through refresh throttling``() =
        let state, _ = enrolled ()
        use candidateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let candidateParameters = candidateKey.ExportParameters(false)
        let candidate = CacheIdentityPublicKey.Create(Base64Url.encode candidateParameters.Q.X, Base64Url.encode candidateParameters.Q.Y)

        let candidateRequest =
            {
                Class = nameof CacheKeyCandidateRequest
                CacheId = cacheId
                CandidatePublicKey = candidate
                RotationIntervalMinutes = RegistrationLifetime.DefaultRotationIntervalMinutes
                IsStartup = true
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let pendingState, _ = Lifecycle.submitCandidate state candidateRequest (now.Plus(Duration.FromMinutes 90L))

        let refreshRequest =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                Health = CacheHealthStatus.Unhealthy
                SoftwareVersion = "1.1.0"
                ProtocolVersion = "v2"
                PrefetchSupported = false
                ObservedAt = now.Plus(Duration.FromMinutes 90L)
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let promotedState, promotion = Lifecycle.promoteCandidate pendingState refreshRequest (now.Plus(Duration.FromMinutes 90L))
        let promoted = promotedState.Registrations[0]

        let throttledRequest = { refreshRequest with ObservedAt = now.Plus(Duration.FromMinutes 91L) }

        let _, throttled = Lifecycle.refresh promotedState throttledRequest (now.Plus(Duration.FromMinutes 91L))

        Assert.That(promotion.Status, Is.EqualTo CacheRegistrationRefreshStatus.Refreshed)
        Assert.That(throttled.Status, Is.EqualTo CacheRegistrationRefreshStatus.RefreshNotDue)
        Assert.That(promoted.ActivePublicKey, Is.EqualTo(candidate))
        Assert.That(promoted.LastRotatedAt, Is.EqualTo(Some(now.Plus(Duration.FromMinutes 90L))))

        Assert.That(
            promoted.RotationDueAt,
            Is.EqualTo(
                now.Plus(
                    Duration.FromMinutes(
                        float RegistrationLifetime.DefaultRotationIntervalMinutes
                        + 90.0
                    )
                )
            )
        )

        Assert.That(
            throttled.Registration
            |> Option.map (fun registration -> registration.ActivePublicKey),
            Is.EqualTo(Some candidate)
        )

        Assert.That(
            throttled.Registration
            |> Option.bind (fun registration -> registration.CandidatePublicKey),
            Is.EqualTo(None)
        )

    /// Verifies an active-key duplicate cannot become a no-op candidate or advance the durable rotation schedule.
    [<Test>]
    member _.``active key cannot be submitted as a candidate``() =
        let state, _ = enrolled ()
        let registration = state.Registrations[0]

        let request =
            {
                Class = nameof CacheKeyCandidateRequest
                CacheId = cacheId
                CandidatePublicKey = registration.ActivePublicKey
                RotationIntervalMinutes = RegistrationLifetime.DefaultRotationIntervalMinutes
                IsStartup = false
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let next, result = Lifecycle.submitCandidate state request (now.Plus(Duration.FromMinutes 90L))
        let retained = next.Registrations[0]

        Assert.That(result.Status, Is.EqualTo(CacheRegistrationRefreshStatus.NotFound))
        Assert.That(result.Message, Is.EqualTo("Cache identity candidate must differ from the active identity key."))
        Assert.That(next, Is.EqualTo(state))
        Assert.That(retained.CandidatePublicKey, Is.EqualTo(None))
        Assert.That(retained.LastRotatedAt, Is.EqualTo(registration.LastRotatedAt))
        Assert.That(retained.RotationDueAt, Is.EqualTo(registration.RotationDueAt))

    [<Test>]
    member _.``refresh updates only allowed operational facts and preserves administrator owned endpoint identity``() =
        let state, _ = enrolled ()

        let request =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
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
        Assert.That(registration.Endpoint, Is.EqualTo "https://cache.example.test")
        Assert.That(registration.Health, Is.EqualTo CacheHealthStatus.Unhealthy)
        Assert.That(registration.SoftwareVersion, Is.EqualTo "1.1.0")
        Assert.That(registration.RepositoryScopes, Has.Length.EqualTo 1)
        Assert.That(registration.RepositoryScopes[0].RepositoryId, Is.EqualTo repositoryId)
        Assert.That(registration.DisplayName, Is.EqualTo "Seattle cache")
        Assert.That(registration.ActivePublicKey, Is.EqualTo state.Registrations[0].ActivePublicKey)
        Assert.That(registration.EnrolledBy, Is.EqualTo "admin-user")

    /// Verifies an early unhealthy report immediately removes a Cache from selection without extending any other operational fact.
    [<Test>]
    member _.``early unhealthy refresh persists only the health downgrade and excludes later selection``() =
        let enrolledState, _ = enrolled ()

        let state =
            { enrolledState with
                Registrations =
                    [|
                        { enrolledState.Registrations[0] with Health = CacheHealthStatus.Healthy }
                    |]
            }

        let earlyUnhealthy =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                Health = CacheHealthStatus.Unhealthy
                SoftwareVersion = "ignored"
                ProtocolVersion = "ignored"
                PrefetchSupported = false
                ObservedAt = now.Plus(Duration.FromMinutes 1L)
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let afterDowngrade, downgradeResult = Lifecycle.refresh state earlyUnhealthy (now.Plus(Duration.FromMinutes 1L))
        let downgraded = afterDowngrade.Registrations[0]

        Assert.That(downgradeResult.Status, Is.EqualTo CacheRegistrationRefreshStatus.Refreshed)
        Assert.That(downgraded.Health, Is.EqualTo CacheHealthStatus.Unhealthy)
        Assert.That(downgraded.Endpoint, Is.EqualTo state.Registrations[0].Endpoint)
        Assert.That(downgraded.SoftwareVersion, Is.EqualTo state.Registrations[0].SoftwareVersion)
        Assert.That(downgraded.ProtocolVersion, Is.EqualTo state.Registrations[0].ProtocolVersion)
        Assert.That(downgraded.PrefetchSupported, Is.EqualTo state.Registrations[0].PrefetchSupported)
        Assert.That(downgraded.LastRefreshedAt, Is.EqualTo state.Registrations[0].LastRefreshedAt)
        Assert.That(downgraded.RefreshAfter, Is.EqualTo state.Registrations[0].RefreshAfter)
        Assert.That(downgraded.ExpiresAt, Is.EqualTo state.Registrations[0].ExpiresAt)

        let eligible =
            Lifecycle.selectEligible afterDowngrade (CacheRegistrationSelectionQuery.Create(Some repositoryId, false)) (now.Plus(Duration.FromMinutes 2L))

        Assert.That(eligible, Is.Empty)

    /// Verifies a ready Cache may publish Healthy before its normal refresh is due without extending registration lifetime or rotation facts.
    [<Test>]
    member _.``early healthy refresh publishes readiness without renewing lifetime``() =
        let state, _ = enrolled ()
        let original = state.Registrations[0]

        let earlyHealthy =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                Health = CacheHealthStatus.Healthy
                SoftwareVersion = "ignored"
                ProtocolVersion = "ignored"
                PrefetchSupported = false
                ObservedAt = original.LastRefreshedAt
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let readyAt = now.Plus(Duration.FromMinutes 1L)
        let readyState, readyResult = Lifecycle.refresh state earlyHealthy readyAt
        let ready = readyState.Registrations[0]

        Assert.That(readyResult.Status, Is.EqualTo CacheRegistrationRefreshStatus.Refreshed)
        Assert.That(ready.Health, Is.EqualTo CacheHealthStatus.Healthy)
        Assert.That(ready.LastRefreshedAt, Is.EqualTo original.LastRefreshedAt)
        Assert.That(ready.RefreshAfter, Is.EqualTo original.RefreshAfter)
        Assert.That(ready.ExpiresAt, Is.EqualTo original.ExpiresAt)
        Assert.That(ready.LastRotatedAt, Is.EqualTo original.LastRotatedAt)
        Assert.That(ready.RotationDueAt, Is.EqualTo original.RotationDueAt)

        let eligible = Lifecycle.selectEligible readyState (CacheRegistrationSelectionQuery.Create(Some repositoryId, false)) readyAt

        Assert.That(eligible, Has.Length.EqualTo 1)

        let unchangedState, unchangedResult =
            Lifecycle.refresh readyState { earlyHealthy with ObservedAt = original.LastRefreshedAt } (readyAt.Plus(Duration.FromMinutes 1L))

        Assert.That(unchangedResult.Status, Is.EqualTo CacheRegistrationRefreshStatus.RefreshNotDue)
        Assert.That(unchangedState, Is.EqualTo readyState)

    /// Verifies durable renewal is ordered only by Grace Server time when proof timestamps are inside the existing tolerance.
    [<Test>]
    member _.``due refresh accepts behind equal and ahead proof observations using server lifetime order``() =
        let state, _ = enrolled ()
        let original = state.Registrations[0]

        for observedAt in
            [
                original.LastRefreshedAt.Minus(Duration.FromSeconds 30L)
                original.LastRefreshedAt
                original.LastRefreshedAt.Plus(Duration.FromSeconds 30L)
            ] do
            let request =
                {
                    Class = nameof CacheRegistrationRefreshRequest
                    CacheId = cacheId
                    Endpoint = original.Endpoint
                    Health = CacheHealthStatus.Healthy
                    SoftwareVersion = "1.1.0"
                    ProtocolVersion = "v2"
                    PrefetchSupported = false
                    ObservedAt = observedAt
                    Proof = Unchecked.defaultof<SignedCacheRequestProof>
                }

            let next, result = Lifecycle.refresh state request original.RefreshAfter
            let renewed = next.Registrations[0]

            Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.Refreshed)
            Assert.That(renewed.LastRefreshedAt, Is.EqualTo original.RefreshAfter)
            Assert.That(renewed.RefreshAfter, Is.EqualTo(original.RefreshAfter.Plus RegistrationLifetime.RefreshAfter))
            Assert.That(renewed.ExpiresAt, Is.EqualTo(original.RefreshAfter.Plus RegistrationLifetime.ActiveLifetime))
            Assert.That(renewed.LastRotatedAt, Is.EqualTo original.LastRotatedAt)
            Assert.That(renewed.RotationDueAt, Is.EqualTo original.RotationDueAt)

    /// Verifies malformed replacement scopes leave the durable lifecycle state unchanged.
    [<Test>]
    member _.``malformed repository replacement is rejected without lifecycle mutation``() =
        let state, _ = enrolled ()

        let duplicateScopes =
            [|
                CacheRepositoryScope.Create(organizationId, repositoryId)
                CacheRepositoryScope.Create(organizationId, repositoryId)
            |]

        let next, result = Lifecycle.updateAssignments state cacheId duplicateScopes

        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.NotFound)
        Assert.That(next, Is.EqualTo state)

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
        let enrolledState, _ = enrolled ()

        let state =
            { enrolledState with
                Registrations =
                    [|
                        { enrolledState.Registrations[0] with Health = CacheHealthStatus.Healthy }
                    |]
            }

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

    /// Verifies only the named durable Cache health cases can pass refresh validation after enrollment has fixed its initial health.
    [<Test>]
    member _.``refresh accepts named health values and rejects undefined numeric values``() =
        let refresh health =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                Health = health
                SoftwareVersion = "1.0.0"
                ProtocolVersion = "v1"
                PrefetchSupported = true
                ObservedAt = now.Plus(Duration.FromHours 1)
                Proof =
                    SignedCacheRequestProof.Create(
                        CacheRequestProofPayload.Create(cacheId, CacheRegistrationProof.RefreshOperation, "validation-fixture", now),
                        "validation-fixture"
                    )
            }

        for health in
            [
                CacheHealthStatus.Healthy
                CacheHealthStatus.Unhealthy
            ] do
            Assert.That(
                Lifecycle.validateRefreshRequest (refresh health)
                |> Result.isOk,
                Is.True
            )

        for health in
            [
                enum<CacheHealthStatus> 0
                enum<CacheHealthStatus> 999
            ] do
            match Lifecycle.validateRefreshRequest (refresh health) with
            | Ok () -> Assert.Fail($"Undefined refresh health value {int health} was accepted.")
            | Error errors -> Assert.That(errors, Does.Contain "Health must be Healthy or Unhealthy.")

    [<Test>]
    member _.``cache proof binds CacheId operation request digest timestamp and current P-256 key``() =
        use privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = privateKey.ExportParameters false
        let key = CacheIdentityPublicKey.Create(ArtifactGrant.Base64Url.encode parameters.Q.X, ArtifactGrant.Base64Url.encode parameters.Q.Y)
        let digest = "canonical-request-digest"
        let proof = CacheRegistrationProof.createProof privateKey cacheId CacheRegistrationProof.RefreshOperation digest now
        Assert.That(CacheRegistrationProof.validate now key cacheId CacheRegistrationProof.RefreshOperation digest proof, Is.True)
        Assert.That(CacheRegistrationProof.validate now key cacheId CacheRegistrationProof.SubmitCandidateOperation digest proof, Is.False)

        Assert.That(
            CacheRegistrationProof.validate (now.Plus(Duration.FromSeconds 31L)) key cacheId CacheRegistrationProof.RefreshOperation digest proof,
            Is.False
        )

    /// Verifies only a valid candidate proof outside the existing timestamp tolerance receives the retry-safe stale classification.
    [<Test>]
    member _.``candidate proof classifies only stale timestamp after all identity bindings validate``() =
        use activeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        use otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let activeParameters = activeKey.ExportParameters false
        let otherParameters = otherKey.ExportParameters false

        let activePublicKey =
            CacheIdentityPublicKey.Create(ArtifactGrant.Base64Url.encode activeParameters.Q.X, ArtifactGrant.Base64Url.encode activeParameters.Q.Y)

        let otherPublicKey =
            CacheIdentityPublicKey.Create(ArtifactGrant.Base64Url.encode otherParameters.Q.X, ArtifactGrant.Base64Url.encode otherParameters.Q.Y)

        let digest = "candidate-request-digest"

        let staleProof =
            CacheRegistrationProof.createProof activeKey cacheId CacheRegistrationProof.SubmitCandidateOperation digest (now.Minus(Duration.FromSeconds 31L))

        let classify publicKey operation requestDigest proof = CacheRegistrationProof.classify now publicKey cacheId operation requestDigest proof

        let isStale =
            function
            | Error CacheProofValidationFailure.TimestampOutsideTolerance -> true
            | Ok ()
            | Error _ -> false

        Assert.That(
            classify activePublicKey CacheRegistrationProof.SubmitCandidateOperation digest staleProof
            |> isStale,
            Is.True
        )

        let invalidSignature = { staleProof with Signature = "invalid" }

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    classify activePublicKey CacheRegistrationProof.SubmitCandidateOperation digest invalidSignature
                    |> isStale,
                    Is.False
                )

                Assert.That(
                    classify otherPublicKey CacheRegistrationProof.SubmitCandidateOperation digest staleProof
                    |> isStale,
                    Is.False
                )

                Assert.That(
                    classify activePublicKey CacheRegistrationProof.RefreshOperation digest staleProof
                    |> isStale,
                    Is.False
                )

                Assert.That(
                    classify activePublicKey CacheRegistrationProof.SubmitCandidateOperation "wrong-request-digest" staleProof
                    |> isStale,
                    Is.False
                ))
        )

    /// Verifies malformed external proof timestamps remain ordinary admission failures at inclusive skew boundaries.
    [<Test>]
    member _.``cache proof timestamp admission is inclusive and overflow safe``() =
        use privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = privateKey.ExportParameters false
        let key = CacheIdentityPublicKey.Create(ArtifactGrant.Base64Url.encode parameters.Q.X, ArtifactGrant.Base64Url.encode parameters.Q.Y)
        let digest = "canonical-request-digest"

        let validateAt operation timestamp =
            CacheRegistrationProof.createProof privateKey cacheId operation digest timestamp
            |> CacheRegistrationProof.validate now key cacheId operation digest

        Assert.Multiple(
            Action (fun () ->
                for operation in
                    [
                        CacheRegistrationProof.RefreshOperation
                        CacheRegistrationProof.SubmitCandidateOperation
                    ] do
                    Assert.That(validateAt operation (now.Minus(Duration.FromSeconds 30L)), Is.True)
                    Assert.That(validateAt operation (now.Plus(Duration.FromSeconds 30L)), Is.True)

                    Assert.That(
                        validateAt
                            operation
                            (now
                                .Minus(Duration.FromSeconds 30L)
                                .Minus(Duration.FromMilliseconds 1L)),
                        Is.False
                    )

                    Assert.That(
                        validateAt
                            operation
                            (now
                                .Plus(Duration.FromSeconds 30L)
                                .Plus(Duration.FromMilliseconds 1L)),
                        Is.False
                    )

                    Assert.DoesNotThrow(Action(fun () -> validateAt operation Instant.MinValue |> ignore))
                    Assert.That(validateAt operation Instant.MinValue, Is.False)
                    Assert.DoesNotThrow(Action(fun () -> validateAt operation Instant.MaxValue |> ignore))
                    Assert.That(validateAt operation Instant.MaxValue, Is.False))
        )
