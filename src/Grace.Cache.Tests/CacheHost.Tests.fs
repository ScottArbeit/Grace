namespace Grace.Cache.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text.Json
open Grace.Cache
open Grace.Shared
open Grace.Shared.ArtifactGrant
open Grace.Types.CacheRegistration
open NodaTime
open NUnit.Framework

/// Verifies the cache tracer exposes only its fixed safe route inventory.
[<TestFixture>]
type CacheHostTests() =

    let testPublicKey =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = key.ExportParameters(false)
        CacheIdentityPublicKey.Create(Base64Url.encode parameters.Q.X, Base64Url.encode parameters.Q.Y)

    /// Verifies that the F# cache host exposes only safe status routes before later runtime capabilities exist.
    [<Test>]
    member _.RouteInventoryContainsOnlyScaffoldRoutes() = Assert.That(CacheHost.routeInventory, Is.EquivalentTo([ "/healthz"; "/status" ]))

    /// Verifies hosted status observes the latest protected configuration lifecycle without performing reconciliation.
    [<Test>]
    member _.HostedStatusReadsCurrentProtectedConfiguration() =
        let cacheId = Guid.NewGuid()

        let mutable configuration: CacheMachineConfiguration =
            {
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "active-key"
                ActivePublicKey = testPublicKey
                RotationLifecycle = Ready
            }

        let mutable reads = 0

        let readCurrentStatus () =
            reads <- reads + 1
            Ok(CacheMachineConfiguration.toStatus configuration)

        match CacheHostStatus.read readCurrentStatus with
        | Error error -> Assert.Fail(error)
        | Ok status -> Assert.That(status.Lifecycle, Is.EqualTo("registered"))

        configuration <- { configuration with RotationLifecycle = CacheKeyRotationLifecycle.OperatorRecoveryRequired }

        match CacheHostStatus.read readCurrentStatus with
        | Error error -> Assert.Fail(error)
        | Ok status ->
            Assert.That(status.Lifecycle, Is.EqualTo("operator-recovery-required"))
            Assert.That(status.CacheId, Is.EqualTo(Some(cacheId.ToString("D"))))

        Assert.That(reads, Is.EqualTo(2))

    /// Verifies the enrolled endpoint replaces every inherited Kestrel listener address.
    [<Test>]
    member _.HostReplacesInheritedKestrelAddresses() =
        let addresses =
            ResizeArray<string>(
                [
                    "http://127.0.0.1:5000"
                    "https://127.0.0.1:5001"
                ]
            )

        CacheHostAddresses.replace (addresses :> System.Collections.Generic.ICollection<string>) "https://cache.example.test:8443"

        Assert.That(addresses, Is.EquivalentTo([ "https://cache.example.test:8443" ]))

    /// Verifies failed automatic rotation retries on the approved five-minute cadence rather than a fixed key-rotation timer.
    [<Test>]
    member _.HostRetriesFailedRotationAfterFiveMinutes() = Assert.That(CacheRefreshSchedule.retryInterval, Is.EqualTo(TimeSpan.FromMinutes 5.0))

    /// Verifies registration refresh has an independent one-hour schedule that runs before the two-hour active lifetime expires.
    [<Test>]
    member _.HostUsesTheOneHourRegistrationRefreshInterval() =
        Assert.That(CacheHost.registrationRefreshInterval, Is.EqualTo(RegistrationLifetime.RefreshAfter.ToTimeSpan()))
        Assert.That(CacheHost.registrationRefreshInterval, Is.LessThan(RegistrationLifetime.ActiveLifetime.ToTimeSpan()))

    /// Verifies the pre-artifact scaffold never represents itself as ready to serve selected cache materialization work.
    [<Test>]
    member _.ScaffoldDoesNotPublishArtifactServingReadiness() = Assert.That(CacheHost.artifactServingAvailable, Is.False)

    /// Verifies server-returned rotation deadlines schedule before, at, and after the deadline without resetting the cadence at restart.
    [<Test>]
    member _.RotationScheduleUsesServerDueTime() =
        let now = Instant.FromUtc(2026, 7, 17, 20, 0)

        Assert.That(CacheRotationSchedule.initialDelayForDue now (now.Plus(Duration.FromMinutes 10.0)), Is.EqualTo(TimeSpan.FromMinutes 10.0))
        Assert.That(CacheRotationSchedule.initialDelayForDue now now, Is.EqualTo(TimeSpan.Zero))
        Assert.That(CacheRotationSchedule.initialDelayForDue now (now - Duration.FromMinutes 1.0), Is.EqualTo(TimeSpan.Zero))

    /// Verifies known server acceptance remains known through every recovery/configuration local-write ordering.
    [<Test>]
    member _.KnownAcceptedCacheIdSurvivesRecoveryWriteFailures() =
        let recovery =
            CacheEnrollmentRecovery.prepare
                "https://cache.example.test"
                "https://server.example.test/grace/"
                [| Guid.NewGuid(), Guid.NewGuid() |]
                "opaque-key-reference"
                testPublicKey

        let cacheId = Guid.NewGuid()
        let writes = ResizeArray<CacheEnrollmentRecovery>()
        let mutable writeAttempt = 0
        let mutable configurationCacheId = Guid.Empty

        let effects: AcceptedEnrollmentFinalizationEffects<string> =
            {
                WriteRecovery =
                    fun _ accepted ->
                        writeAttempt <- writeAttempt + 1
                        writes.Add accepted

                        if writeAttempt = 1 then Error "first recovery write failed" else Ok()
                WriteConfiguration =
                    fun accepted acceptedCacheId ->
                        configurationCacheId <- acceptedCacheId
                        Assert.That(accepted.CacheId, Is.EqualTo(Some cacheId))
                        Ok "configuration"
                ClearRecovery = fun _ -> Ok()
            }

        let result = AcceptedEnrollmentFinalization.finalize effects "recovery-path" recovery cacheId

        Assert.That(result, Is.EqualTo((Ok "configuration": Result<string, string>)))
        Assert.That(configurationCacheId, Is.EqualTo(cacheId))

        Assert.That(
            writes
            |> Seq.forall (fun written -> written.CacheId = Some cacheId),
            Is.True
        )

        Assert.That(
            writes
            |> Seq.forall (fun written -> not (CacheEnrollmentRecovery.isUnknown written)),
            Is.True
        )

    /// Verifies a failed configuration write leaves accepted recovery evidence rather than converting the known outcome to unknown.
    [<Test>]
    member _.KnownAcceptedCacheIdSurvivesConfigurationWriteFailure() =
        let recovery =
            CacheEnrollmentRecovery.prepare
                "https://cache.example.test"
                "https://server.example.test/grace/"
                [| Guid.NewGuid(), Guid.NewGuid() |]
                "opaque-key-reference"
                testPublicKey

        let cacheId = Guid.NewGuid()
        let writes = ResizeArray<CacheEnrollmentRecovery>()

        let effects: AcceptedEnrollmentFinalizationEffects<string> =
            {
                WriteRecovery =
                    fun _ accepted ->
                        writes.Add accepted
                        Ok()
                WriteConfiguration = fun _ _ -> Error "configuration write failed"
                ClearRecovery = fun _ -> failwith "Recovery must remain after configuration failure."
            }

        let result = AcceptedEnrollmentFinalization.finalize effects "recovery-path" recovery cacheId

        Assert.That(result, Is.EqualTo((Error "configuration write failed": Result<string, string>)))
        Assert.That(writes, Has.Count.EqualTo(1))
        Assert.That(writes[0].CacheId, Is.EqualTo(Some cacheId))
        Assert.That(CacheEnrollmentRecovery.isUnknown writes[0], Is.False)

    /// Verifies an accepted-record write failure followed by configuration and clear failures retries only the known accepted evidence.
    [<Test>]
    member _.KnownAcceptedCacheIdRemainsKnownWhenLaterWritesFail() =
        let recovery =
            CacheEnrollmentRecovery.prepare
                "https://cache.example.test"
                "https://server.example.test/grace/"
                [| Guid.NewGuid(), Guid.NewGuid() |]
                "opaque-key-reference"
                testPublicKey

        let cacheId = Guid.NewGuid()
        let writes = ResizeArray<CacheEnrollmentRecovery>()
        let mutable attempts = 0

        let effects: AcceptedEnrollmentFinalizationEffects<string> =
            {
                WriteRecovery =
                    fun _ accepted ->
                        attempts <- attempts + 1
                        writes.Add accepted
                        if attempts = 2 then Ok() else Error "recovery write failed"
                WriteConfiguration = fun _ _ -> Ok "configuration"
                ClearRecovery = fun _ -> Error "recovery clear failed"
            }

        let result = AcceptedEnrollmentFinalization.finalize effects "recovery-path" recovery cacheId

        Assert.That(result, Is.EqualTo((Error "recovery clear failed": Result<string, string>)))
        Assert.That(writes, Has.Count.EqualTo(2))

        Assert.That(
            writes
            |> Seq.forall (fun written -> written.CacheId = Some cacheId),
            Is.True
        )

        Assert.That(
            writes
            |> Seq.forall (fun written -> not (CacheEnrollmentRecovery.isUnknown written)),
            Is.True
        )

    /// Verifies retry recovery is driven by the actual server expiry and never schedules a refresh in the fifteen-minute reserve.
    [<Test>]
    member _.RefreshRecoveryStopsAtTheExpiryReserve() =
        let expiry = Instant.FromUtc(2026, 7, 17, 20, 0)

        match CacheRefreshSchedule.nextRetryAction (expiry - Duration.FromMinutes 21.0) expiry with
        | CacheRefreshSchedule.RetryAfter delay -> Assert.That(delay, Is.EqualTo(TimeSpan.FromMinutes 5.0))
        | _ -> Assert.Fail("A retry is required while more than twenty minutes remain.")

        match CacheRefreshSchedule.nextRetryAction (expiry - Duration.FromMinutes 20.0) expiry with
        | CacheRefreshSchedule.WaitForExpiry delay -> Assert.That(delay, Is.EqualTo(TimeSpan.FromMinutes 20.0))
        | _ -> Assert.Fail("A retry cannot be scheduled when it would enter the reserve.")

        match CacheRefreshSchedule.nextRetryAction (expiry - Duration.FromMinutes 15.0) expiry with
        | CacheRefreshSchedule.WaitForExpiry delay -> Assert.That(delay, Is.EqualTo(TimeSpan.FromMinutes 15.0))
        | _ -> Assert.Fail("The fifteen-minute reserve forbids another automatic refresh.")

        match CacheRefreshSchedule.nextRetryAction expiry expiry with
        | CacheRefreshSchedule.Expired -> ()
        | _ -> Assert.Fail("Expiry requires the unhealthy terminal operation, not another refresh.")

    /// Verifies ambiguous enrollment evidence keeps only the approved fields and blocks automatic recovery.
    [<Test>]
    member _.UnknownEnrollmentRecoveryPreservesEvidenceAndBlocksAutomaticWork() =
        let configurationPath = Path.Combine(Path.GetTempPath(), $"grace-cache-recovery-{Guid.NewGuid():N}", "cache.runtime.json")
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath
        let repositoryScopes = [| Guid.NewGuid(), Guid.NewGuid() |]

        let recovery =
            CacheEnrollmentRecovery.prepare
                "https://cache.example.test"
                "https://server.example.test/grace/"
                repositoryScopes
                "opaque-key-reference"
                testPublicKey

        let persisted = CacheEnrollmentRecovery.unknown recovery

        let effects: CacheRuntimeStatusReadEffects =
            {
                ValidateStorage = fun _ -> Ok()
                ValidateFile = fun _ -> Ok()
                ReadRecovery =
                    fun path ->
                        if path = recoveryPath then
                            Ok(Some persisted)
                        else
                            Error "Unexpected recovery path."
                ReadConfiguration = fun _ -> Error "Unknown enrollment recovery must block normal configuration reads."
            }

        match CacheRuntimeControl.readStatusWith effects configurationPath with
        | Error error -> Assert.Fail(error)
        | Ok status ->
            Assert.That(status, Is.EqualTo(CacheRuntimeStatus.enrollmentRecoveryRequired))
            Assert.That(CacheEnrollmentRecovery.isUnknown persisted, Is.True)
            Assert.That(persisted.Endpoint, Is.EqualTo(recovery.Endpoint))
            Assert.That(persisted.ServerUri, Is.EqualTo(recovery.ServerUri))
            Assert.That(persisted.RepositoryScopes = repositoryScopes, Is.True)
            Assert.That(persisted.ActiveKeyName, Is.EqualTo(recovery.ActiveKeyName))
            Assert.That(persisted.CacheId, Is.EqualTo(None))

    /// Verifies every retained enrollment recovery subtype is visible through a pure redacted status read even when no final configuration exists.
    [<Test>]
    member _.RetainedEnrollmentRecoveryStatusIsPureAndRedactedWithoutFinalConfiguration() =
        let configurationPath = Path.Combine(Path.GetTempPath(), $"grace-cache-status-recovery-{Guid.NewGuid():N}", "cache.runtime.json")
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath

        let prepared =
            CacheEnrollmentRecovery.prepare
                "https://cache.example.test"
                "https://server.example.test/private"
                [| Guid.NewGuid(), Guid.NewGuid() |]
                "opaque-key-reference"
                testPublicKey

        let retainedRecoveries =
            [
                prepared
                CacheEnrollmentRecovery.accept (Guid.NewGuid()) prepared
                CacheEnrollmentRecovery.unknown prepared
            ]

        for recovery in retainedRecoveries do
            let mutable recoveryReads = 0
            let mutable configurationReads = 0

            let effects: CacheRuntimeStatusReadEffects =
                {
                    ValidateStorage = fun _ -> Ok()
                    ValidateFile = fun _ -> Ok()
                    ReadRecovery =
                        fun path ->
                            recoveryReads <- recoveryReads + 1

                            if path = recoveryPath then
                                Ok(Some recovery)
                            else
                                Error "Unexpected recovery path."
                    ReadConfiguration =
                        fun _ ->
                            configurationReads <- configurationReads + 1
                            Error "Status must not read a final configuration while enrollment recovery is retained."
                }

            match CacheRuntimeControl.readStatusWith effects configurationPath with
            | Error error -> Assert.Fail(error)
            | Ok status ->
                Assert.That(status, Is.EqualTo(CacheRuntimeStatus.enrollmentRecoveryRequired))
                Assert.That(recoveryReads, Is.EqualTo(1))
                Assert.That(configurationReads, Is.EqualTo(0))
                Assert.That(status.ToString(), Does.Not.Contain(recovery.Endpoint))
                Assert.That(status.ToString(), Does.Not.Contain(recovery.ServerUri))
                Assert.That(status.ToString(), Does.Not.Contain(recovery.ActiveKeyName))

    /// Verifies invalid rotation configuration aborts before candidate recovery can mutate retained candidate bytes or references.
    [<Test>]
    member _.InvalidRotationIntervalPrecedesEveryCandidateEffect() =
        let candidateBytes = [| 1uy; 2uy; 3uy |]
        let retainedCandidateBytes = candidateBytes
        let originalBytes = Array.copy candidateBytes
        let mutable candidateEffectRan = false

        let result =
            CacheRotationInterval.beforeSynchronization
                (fun _ -> "14")
                (fun _ ->
                    candidateEffectRan <- true
                    candidateBytes[0] <- 0uy
                    Ok())

        Assert.That(result, Is.EqualTo((Error "Grace Cache key rotation interval is invalid.": Result<unit, string>)))
        Assert.That(candidateEffectRan, Is.False)
        Assert.That(Object.ReferenceEquals(candidateBytes, retainedCandidateBytes), Is.True)
        Assert.That((candidateBytes = originalBytes), Is.True)

    /// Verifies mandatory startup rotation waits exactly for the server boundary before retrying the same durable candidate without listener or health publication.
    [<Test>]
    member _.StartupThrottleWaitsUnreadyThenRetriesTheSameCandidate() =
        let retryAfter = TimeSpan.FromSeconds 37.0
        let candidateReference = "candidate-reference"
        let attempts = ResizeArray<string>()
        let waits = ResizeArray<TimeSpan>()
        let mutable listenerStarts = 0
        let mutable healthyPublications = 0

        let effects: StartupRotationEffects =
            {
                Synchronize =
                    fun () ->
                        attempts.Add candidateReference

                        if attempts.Count = 1 then
                            Ok(RotationRetryAfter retryAfter)
                        else
                            Ok(Synchronized(CacheRuntimeStatus.registered Guid.Empty "https"))
                WaitForRetry =
                    fun delay ->
                        waits.Add delay
                        Assert.That(listenerStarts, Is.EqualTo(0))
                        Assert.That(healthyPublications, Is.EqualTo(0))
                        true
            }

        match CacheStartupRotation.synchronizeBeforeListener effects with
        | Error error -> Assert.Fail(error)
        | Ok () ->
            listenerStarts <- listenerStarts + 1
            healthyPublications <- healthyPublications + 1

        Assert.That(
            (attempts |> Seq.toList) =
                [
                    candidateReference
                    candidateReference
                ],
            Is.True
        )

        Assert.That((waits |> Seq.toList) = [ retryAfter ], Is.True)
        Assert.That(listenerStarts, Is.EqualTo(1))
        Assert.That(healthyPublications, Is.EqualTo(1))

    /// Verifies startup cancellation interrupts the throttle wait without retrying rotation or permitting listener startup.
    [<Test>]
    member _.StartupThrottleCancellationStopsBeforeRetryOrListenerStartup() =
        let retryAfter = TimeSpan.FromSeconds 59.0
        let mutable synchronizationCalls = 0
        let mutable listenerStarts = 0
        let waits = ResizeArray<TimeSpan>()

        let effects: StartupRotationEffects =
            {
                Synchronize =
                    fun () ->
                        synchronizationCalls <- synchronizationCalls + 1
                        Ok(RotationRetryAfter retryAfter)
                WaitForRetry =
                    fun delay ->
                        waits.Add delay
                        false
            }

        match CacheStartupRotation.synchronizeBeforeListener effects with
        | Ok () -> listenerStarts <- listenerStarts + 1
        | Error error -> Assert.That(error, Is.EqualTo("Grace Cache startup was cancelled."))

        Assert.That(synchronizationCalls, Is.EqualTo(1))
        Assert.That((waits |> Seq.toList) = [ retryAfter ], Is.True)
        Assert.That(listenerStarts, Is.EqualTo(0))

    /// Verifies a failed post-promotion registration read retries only scheduling and uses the later server RotationDueAt without creating another candidate.
    [<Test>]
    member _.PostPromotionSchedulingRetryDoesNotRotateAgain() =
        let now = Instant.FromUtc(2026, 7, 17, 20, 0)
        let currentDue = now.Plus(Duration.FromMinutes 43.0)

        let registration: CacheRegistration =
            {
                Class = nameof CacheRegistration
                CacheId = Guid.NewGuid()
                DisplayName = "test-cache"
                BoundaryKind = CacheBoundaryKind.Owner
                OwnerId = Guid.NewGuid()
                OrganizationId = None
                RepositoryScopes = Array.empty
                ActivePublicKey = testPublicKey
                CandidatePublicKey = None
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                Health = CacheHealthStatus.Unhealthy
                SoftwareVersion = "1.0.0"
                ProtocolVersion = "1"
                PrefetchSupported = false
                EnrolledBy = "test"
                EnrolledAt = now
                LastRefreshedAt = now
                RefreshAfter = now.Plus(Duration.FromHours 1)
                ExpiresAt = now.Plus(Duration.FromHours 2)
                RotationIntervalMinutes = RegistrationLifetime.DefaultRotationIntervalMinutes
                LastRotatedAt = Some now
                RotationDueAt = currentDue
                RevokedAt = None
            }

        let mutable synchronizationCalls = 0
        let mutable registrationReads = 0
        let scheduled = ResizeArray<TimeSpan>()

        let effects: KeyRotationSchedulingEffects =
            {
                Synchronize =
                    fun () ->
                        synchronizationCalls <- synchronizationCalls + 1
                        Ok(Synchronized(CacheRuntimeStatus.registered registration.CacheId "https"))
                ReadRegistration =
                    fun () ->
                        registrationReads <- registrationReads + 1

                        if registrationReads = 1 then
                            Error "Transient post-promotion registration read failure."
                        else
                            Ok registration
                IsOperatorRecoveryRequired = fun () -> false
                MarkUnhealthy = fun () -> Assert.Fail("A completed promotion with a scheduling read failure must not start a second rotation path.")
                Schedule = fun delay -> scheduled.Add delay
                Now = fun () -> now
            }

        let afterPromotion = CacheKeyRotationScheduling.run RotationRequired effects
        let afterSchedulingRetry = CacheKeyRotationScheduling.run afterPromotion effects

        Assert.That(afterPromotion, Is.EqualTo(RegistrationSchedulingRequired))
        Assert.That(afterSchedulingRetry, Is.EqualTo(RotationRequired))
        Assert.That(synchronizationCalls, Is.EqualTo(1))
        Assert.That(registrationReads, Is.EqualTo(2))

        Assert.That(
            (scheduled |> Seq.toList) =
                [
                    CacheRefreshSchedule.retryInterval
                    TimeSpan.FromMinutes 43.0
                ],
            Is.True
        )

    /// Verifies a retained candidate's retryable failure publishes Unhealthy and waits five minutes without beginning another candidate transition.
    [<Test>]
    member _.CandidateRetryableFailureSchedulesFiveMinuteRetryWithoutReplacement() =
        let retainedCandidate = "candidate-reference"
        let attemptedCandidates = ResizeArray<string>()
        let scheduled = ResizeArray<TimeSpan>()
        let mutable unhealthyPublications = 0

        let effects: KeyRotationSchedulingEffects =
            {
                Synchronize =
                    fun () ->
                        attemptedCandidates.Add retainedCandidate
                        Error "Grace Cache identity candidate outcome is unknown; the same candidate remains pending for retry."
                ReadRegistration = fun () -> Error "A retryable candidate failure must not read a replacement registration."
                IsOperatorRecoveryRequired = fun () -> false
                MarkUnhealthy = fun () -> unhealthyPublications <- unhealthyPublications + 1
                Schedule = fun delay -> scheduled.Add delay
                Now = fun () -> Instant.FromUtc(2026, 7, 18, 13, 0)
            }

        let phase = CacheKeyRotationScheduling.run RotationRequired effects

        Assert.That(phase, Is.EqualTo(RotationRequired))
        Assert.That((attemptedCandidates |> Seq.toList) = [ retainedCandidate ], Is.True)
        Assert.That(unhealthyPublications, Is.EqualTo(1))
        Assert.That((scheduled |> Seq.toList) = [ CacheRefreshSchedule.retryInterval ], Is.True)

    /// Verifies a terminal registration refresh stops the key-rotation registration callback instead of retaining a no-dispatch retry timer.
    [<Test>]
    member _.TerminalRegistrationRefreshStopsKeyRotationRegistrationScheduling() =
        let mutable scheduled = false

        let effects: KeyRotationSchedulingEffects =
            {
                Synchronize = fun () -> Ok(Synchronized(CacheRuntimeStatus.registered Guid.Empty "https"))
                ReadRegistration = fun () -> Error "terminal registration refresh"
                IsOperatorRecoveryRequired = fun () -> true
                MarkUnhealthy = fun () -> Assert.Fail("A terminal registration refresh must not enter the unhealthy retry path.")
                Schedule = fun _ -> scheduled <- true
                Now = fun () -> Instant.FromUnixTimeSeconds 0L
            }

        let phase = CacheKeyRotationScheduling.run RotationRequired effects
        Assert.That(phase, Is.EqualTo(AutomaticWorkStopped))
        Assert.That(scheduled, Is.False)

    /// Verifies known-CacheId recovery finalizes against the immutable original server URI even after the environment changes.
    [<Test>]
    member _.KnownEnrollmentRecoveryUsesRecordedServerUri() =
        let root = Path.Combine(Path.GetTempPath(), $"grace-cache-recovery-uri-{Guid.NewGuid():N}")
        let configurationPath = Path.Combine(root, "cache.runtime.json")
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath
        let cacheId = Guid.NewGuid()

        let recovery =
            CacheEnrollmentRecovery.prepare
                "https://cache.example.test"
                "https://original-server.example.test/grace/"
                [| Guid.NewGuid(), Guid.NewGuid() |]
                "opaque-key-reference"
                testPublicKey
            |> CacheEnrollmentRecovery.accept cacheId

        try
            Directory.CreateDirectory(root) |> ignore

            match CacheEnrollmentRecovery.write recoveryPath recovery with
            | Error error -> Assert.Fail(error)
            | Ok () ->
                let previous = Environment.GetEnvironmentVariable("GRACE_SERVER_URI")
                Environment.SetEnvironmentVariable("GRACE_SERVER_URI", "https://changed-server.example.test/")

                try
                    match CacheRuntimeControl.finalizeAcceptedEnrollment configurationPath recovery cacheId with
                    | Error error -> Assert.Fail(error)
                    | Ok _ ->
                        let configuration =
                            JsonSerializer.Deserialize<CacheMachineConfiguration>(File.ReadAllText configurationPath, Constants.JsonSerializerOptions)

                        Assert.That(configuration.ServerUri, Is.EqualTo("https://original-server.example.test/grace/"))

                        match CacheEnrollmentRecovery.tryRead recoveryPath with
                        | Error error -> Assert.Fail(error)
                        | Ok persisted -> Assert.That(persisted, Is.EqualTo(None))
                finally
                    Environment.SetEnvironmentVariable("GRACE_SERVER_URI", previous)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    /// Verifies status reads a pending transition without probing Grace Server, deleting either key, or reconciling state.
    [<Test>]
    member _.StatusReadIsPureWhileRotationIsPending() =
        let configurationPath = Path.Combine(Path.GetTempPath(), $"grace-cache-status-{Guid.NewGuid():N}", "cache.runtime.json")

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "old-key"
                ActivePublicKey = testPublicKey
                RotationLifecycle =
                    CandidatePending
                        { ActiveKeyName = "old-key"; ActivePublicKey = testPublicKey; CandidateKeyName = "replacement-key"; CandidatePublicKey = testPublicKey }
            }

        let mutable recoveryReadCount = 0
        let mutable configurationReadCount = 0

        let effects: CacheRuntimeStatusReadEffects =
            {
                ValidateStorage = fun _ -> Ok()
                ValidateFile = fun _ -> Ok()
                ReadRecovery =
                    fun _ ->
                        recoveryReadCount <- recoveryReadCount + 1
                        Ok None
                ReadConfiguration =
                    fun _ ->
                        configurationReadCount <- configurationReadCount + 1
                        Ok configuration
            }

        match CacheRuntimeControl.readStatusWith effects configurationPath with
        | Error error -> Assert.Fail(error)
        | Ok status ->
            Assert.That(status.Lifecycle, Is.EqualTo("registered"))
            Assert.That(recoveryReadCount, Is.EqualTo(1))
            Assert.That(configurationReadCount, Is.EqualTo(1))

            match configuration.RotationLifecycle with
            | CandidatePending _ -> ()
            | Ready
            | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> Assert.Fail("Status read must retain the pending candidate.")

    /// Verifies a persisted terminal lifecycle is exposed as stable redacted observation data without status-side effects.
    [<Test>]
    member _.StatusReportsOperatorRecoveryRequiredWithoutLifecycleEffects() =
        let configurationPath = Path.Combine(Path.GetTempPath(), $"grace-cache-terminal-status-{Guid.NewGuid():N}", "cache.runtime.json")
        let cacheId = Guid.NewGuid()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "active-key"
                ActivePublicKey = testPublicKey
                RotationLifecycle = CacheKeyRotationLifecycle.OperatorRecoveryRequired
            }

        let validateStorage _ = Ok()
        let validateFile _ = Ok()
        let readRecovery _ = Ok None
        let readConfiguration _ = Ok configuration

        let effects: CacheRuntimeStatusReadEffects =
            { ValidateStorage = validateStorage; ValidateFile = validateFile; ReadRecovery = readRecovery; ReadConfiguration = readConfiguration }

        match CacheRuntimeControl.readStatusWith effects configurationPath with
        | Error error -> Assert.Fail(error)
        | Ok status ->
            Assert.That(status.Lifecycle, Is.EqualTo("operator-recovery-required"))
            Assert.That(status.CacheId, Is.EqualTo(Some(cacheId.ToString("D"))))
            Assert.That(status.Transport, Is.EqualTo(Some "https"))

    /// Verifies expiry persists the existing terminal lifecycle before a status reader can observe completion.
    [<Test>]
    member _.RegistrationExpiryPersistsOperatorRecoveryBeforeStatusObservation() =
        let cacheId = Guid.NewGuid()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = cacheId
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "active-key"
                ActivePublicKey = testPublicKey
                RotationLifecycle = Ready
            }

        let writes = ResizeArray<CacheMachineConfiguration>()

        let effects: CacheRegistrationExpiryEffects =
            {
                PersistConfiguration =
                    fun persisted ->
                        writes.Add persisted
                        Ok()
                DeleteKey =
                    fun _ ->
                        Assert.Fail("Ready expiry must not delete an active key.")
                        Ok()
            }

        match CacheRegistrationExpiry.requireOperatorRecovery effects configuration with
        | Error error -> Assert.Fail(error)
        | Ok persisted ->
            Assert.That(writes, Has.Count.EqualTo(1))
            Assert.That(persisted.RotationLifecycle, Is.EqualTo(CacheKeyRotationLifecycle.OperatorRecoveryRequired))

            let status = CacheMachineConfiguration.toStatus persisted
            Assert.That(status.Lifecycle, Is.EqualTo("operator-recovery-required"))
            Assert.That(status.CacheId, Is.EqualTo(Some(cacheId.ToString("D"))))

    /// Verifies candidate expiry deletes the unaccepted key before persisting terminal recovery while its durable reference still exists.
    [<Test>]
    member _.CandidatePendingExpiryCleansCandidateBeforePersistingTerminalRecovery() =
        let operations = ResizeArray<string>()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "active"
                ActivePublicKey = testPublicKey
                RotationLifecycle =
                    CandidatePending
                        { ActiveKeyName = "active"; ActivePublicKey = testPublicKey; CandidateKeyName = "candidate"; CandidatePublicKey = testPublicKey }
            }

        let effects: CacheRegistrationExpiryEffects =
            {
                DeleteKey =
                    fun candidate ->
                        operations.Add $"delete:{candidate}"
                        Ok()
                PersistConfiguration =
                    fun persisted ->
                        operations.Add $"persist:{persisted.RotationLifecycle}"
                        Ok()
            }

        match CacheRegistrationExpiry.requireOperatorRecovery effects configuration with
        | Error error -> Assert.Fail(error)
        | Ok terminal ->
            Assert.That(terminal.RotationLifecycle, Is.EqualTo(CacheKeyRotationLifecycle.OperatorRecoveryRequired))

            Assert.That(
                operations.ToArray() =
                    [|
                        "delete:candidate"
                        "persist:OperatorRecoveryRequired"
                    |],
                Is.True
            )

    /// Verifies failed pending-candidate cleanup retains durable recovery evidence and does not erase the reference before a later recovery attempt.
    [<Test>]
    member _.CandidatePendingExpiryCleanupFailureRetainsDurableCandidateReference() =
        let mutable persisted = false

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "active"
                ActivePublicKey = testPublicKey
                RotationLifecycle =
                    CandidatePending
                        { ActiveKeyName = "active"; ActivePublicKey = testPublicKey; CandidateKeyName = "candidate"; CandidatePublicKey = testPublicKey }
            }

        let effects: CacheRegistrationExpiryEffects =
            {
                DeleteKey = fun _ -> Error "cleanup failed"
                PersistConfiguration =
                    fun _ ->
                        persisted <- true
                        Ok()
            }

        match CacheRegistrationExpiry.requireOperatorRecovery effects configuration with
        | Ok _ -> Assert.Fail("Terminal recovery must not be persisted before candidate cleanup succeeds.")
        | Error _ ->
            Assert.That(persisted, Is.False)

            match configuration.RotationLifecycle with
            | CandidatePending pending -> Assert.That(pending.CandidateKeyName, Is.EqualTo("candidate"))
            | Ready
            | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> Assert.Fail("Cleanup failure must retain the pending durable reference.")

    /// Verifies terminal lifecycle state blocks refresh dispatch and definitive server results persist operator recovery immediately.
    [<Test>]
    member _.RegistrationRefreshStopsForTerminalLifecycleAndDefinitiveResults() =
        let configuration lifecycle =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "active"
                ActivePublicKey = testPublicKey
                RotationLifecycle = lifecycle
            }

        let mutable sends = 0
        let mutable terminalWrites = 0

        let effects result : CacheRegistrationRefreshEffects =
            {
                Send =
                    fun () ->
                        sends <- sends + 1
                        Ok result
                RequireOperatorRecovery =
                    fun _ ->
                        terminalWrites <- terminalWrites + 1
                        Ok()
            }

        let terminalConfiguration = configuration CacheKeyRotationLifecycle.OperatorRecoveryRequired
        let revoked = CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Revoked, None, "revoked")
        let notFound = CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "not found")

        let isRecoveryRequired =
            function
            | Ok RegistrationRecoveryRequired -> true
            | Ok (RegistrationRefreshed _)
            | Ok RegistrationRetryableFailure
            | Error _ -> false

        Assert.That(
            CacheRegistrationRefresh.run (effects revoked) terminalConfiguration
            |> isRecoveryRequired,
            Is.True
        )

        Assert.That(sends, Is.Zero)
        Assert.That(terminalWrites, Is.Zero)

        Assert.That(
            CacheRegistrationRefresh.run (effects revoked) (configuration Ready)
            |> isRecoveryRequired,
            Is.True
        )

        Assert.That(
            CacheRegistrationRefresh.run (effects notFound) (configuration Ready)
            |> isRecoveryRequired,
            Is.True
        )

        Assert.That(sends, Is.EqualTo(2))
        Assert.That(terminalWrites, Is.EqualTo(2))

    /// Verifies transient refresh failure remains on the established retry path and does not persist terminal recovery.
    [<Test>]
    member _.TransientRegistrationRefreshRetainsRetrySchedule() =
        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                ActiveKeyName = "active"
                ActivePublicKey = testPublicKey
                RotationLifecycle = Ready
            }

        let mutable sends = 0
        let mutable terminalWrites = 0

        let effects: CacheRegistrationRefreshEffects =
            {
                Send =
                    fun () ->
                        sends <- sends + 1
                        Error "transient"
                RequireOperatorRecovery =
                    fun _ ->
                        terminalWrites <- terminalWrites + 1
                        Ok()
            }

        match CacheRegistrationRefresh.run effects configuration with
        | Ok RegistrationRetryableFailure -> ()
        | Ok RegistrationRecoveryRequired
        | Ok (RegistrationRefreshed _)
        | Error _ -> Assert.Fail("A transient refresh failure must retain the normal retry path.")

        Assert.That(sends, Is.EqualTo(1))
        Assert.That(terminalWrites, Is.Zero)

        Assert.That(
            CacheRefreshSchedule.nextRetryAction (Instant.FromUnixTimeSeconds 0L) (Instant.FromUnixTimeSeconds 3600L),
            Is.EqualTo(CacheRefreshSchedule.RetryAfter CacheRefreshSchedule.retryInterval)
        )

    /// Verifies startup exceptions become a stable cache process failure without exposing bind or certificate details.
    [<Test>]
    member _.StartupExceptionBecomesRedactedProcessFailure() =
        let secret = "kestrel-bind-certificate-secret"

        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Enrollment must not run for startup."
                Status = fun () -> failwith "Status must not run for startup."
                Run =
                    fun () ->
                        CacheHostStartup.start (fun () -> raise (InvalidOperationException(secret)))
                        |> Result.bind (fun () -> Error "Cache host unexpectedly started.")
            }

        let result = CacheProcessCommand.execute effects [| "--run" |]

        Assert.That(result.ExitCode, Is.EqualTo(1))
        Assert.That(result.Payload, Does.Contain("Cache startup failed."))
        Assert.That(result.Payload, Does.Not.Contain(secret))

    /// Verifies that a missing required process setting rejects startup without exposing the supplied configuration value.
    [<Test>]
    member _.MissingInstanceNameIsRejected() =
        let result = CacheHostSettings.fromEnvironment (fun _ -> null)

        match result with
        | Error message -> Assert.That(message, Does.Contain("GRACE_CACHE_INSTANCE_NAME"))
        | Ok _ -> Assert.Fail("A missing cache instance marker must abort startup.")

    /// Verifies that whitespace-only process input aborts before the cache host can listen.
    [<Test>]
    member _.WhitespaceInstanceNameIsRejected() =
        let result = CacheHostSettings.fromEnvironment (fun _ -> "   ")

        match result with
        | Error message -> Assert.That(message, Does.Contain("GRACE_CACHE_INSTANCE_NAME"))
        | Ok _ -> Assert.Fail("A whitespace-only cache instance marker must abort startup.")

    /// Verifies the cache configuration rejects HTTP unless the administrator explicitly selected the HTTP exception.
    [<Test>]
    member _.HttpEndpointRequiresExplicitException() =
        CacheMachineConfiguration.validateEndpoint "http://cache.example.test:8080" false
        |> Result.map ignore
        |> Result.isError
        |> Assert.That

    /// Verifies the cache configuration accepts only an explicitly approved exact HTTP endpoint.
    [<Test>]
    member _.ExplicitHttpEndpointIsAccepted() =
        CacheMachineConfiguration.validateEndpoint "http://cache.example.test:8080" true
        |> Result.isOk
        |> Assert.That

    /// Verifies a cache listening address is an origin and cannot carry a Kestrel-incompatible path, query, or fragment.
    [<TestCase("https://cache.example.test/cache")>]
    [<TestCase("https://cache.example.test/?preview=true")>]
    [<TestCase("https://cache.example.test/#fragment")>]
    [<TestCase("https://operator:secret@cache.example.test/")>]
    [<TestCase("ftp://cache.example.test/")>]
    member _.CacheEndpointRejectsNonOriginInputs(endpoint) =
        CacheMachineConfiguration.validateEndpoint endpoint false
        |> Result.isError
        |> Assert.That

    /// Verifies redacted status retains a stable cache identity and transport state without exposing the Grace Server URI.
    [<Test>]
    member _.StatusRedactsServerConfiguration() =
        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/private"
                ActiveKeyName = "Grace.Cache.Identity.test"
                ActivePublicKey = testPublicKey
                RotationLifecycle = Ready
            }

        let status = CacheMachineConfiguration.toStatus configuration

        Assert.That(status.CacheId, Is.EqualTo(Some "11111111-1111-1111-1111-111111111111"))
        Assert.That(status.Transport, Is.EqualTo(Some "https"))
        Assert.That(status.ToString(), Does.Not.Contain(configuration.ServerUri))
        Assert.That(status.ToString(), Does.Not.Contain(configuration.ActiveKeyName))

    /// Verifies Linux alone selects protected PKCS#8 custody while Windows and macOS retain platform X.509 stores.
    [<Test>]
    member _.KeyCustodySelectsLinuxFilesOnly() =
        Assert.That(CacheIdentityKeyCustody.selectProvider true, Is.EqualTo(LinuxProtectedPkcs8File))
        Assert.That(CacheIdentityKeyCustody.selectProvider false, Is.EqualTo(PlatformX509Store))

    /// Verifies a Linux key reference remains opaque, rejects path-like substitutions, and cannot leak through status.
    [<Test>]
    member _.LinuxKeyReferenceRejectsSubstitutionAndStatusRedactsIt() =
        let keyReference = "linux-11111111111111111111111111111111"
        let keyPath = CacheMachineConfiguration.tryGetLinuxIdentityKeyPath keyReference

        Assert.That(keyPath, Is.Not.EqualTo(None))
        Assert.That(CacheMachineConfiguration.tryGetLinuxIdentityKeyPath "linux-../identity.pk8", Is.EqualTo(None))
        Assert.That(CacheMachineConfiguration.tryGetLinuxIdentityKeyPath "linux-11111111-1111-1111-1111-111111111111", Is.EqualTo(None))

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/private"
                ActiveKeyName = keyReference
                ActivePublicKey = testPublicKey
                RotationLifecycle = Ready
            }

        let rendered =
            CacheMachineConfiguration.toStatus configuration
            |> string

        Assert.That(rendered, Does.Not.Contain(keyReference))

        keyPath
        |> Option.iter (fun path -> Assert.That(rendered, Does.Not.Contain(path)))

    /// Verifies a proof created after a probe lasting longer than thirty seconds is accepted at the existing tolerance boundary.
    [<Test>]
    member _.CandidateSubmissionProofUsesFreshPostProbeTimestamp() =
        use activeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = activeKey.ExportParameters(false)
        let activePublicKey = CacheIdentityPublicKey.Create(Base64Url.encode parameters.Q.X, Base64Url.encode parameters.Q.Y)
        let cacheId = Guid.NewGuid()
        let probeStarted = Instant.FromUtc(2026, 7, 17, 20, 0)
        let dispatchTime = probeStarted.Plus(Duration.FromSeconds 31L)

        let unsignedRequest: CacheKeyCandidateRequest =
            {
                Class = nameof CacheKeyCandidateRequest
                CacheId = cacheId
                CandidatePublicKey = testPublicKey
                RotationIntervalMinutes = 240
                IsStartup = false
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let signedRequest = CacheCandidateSubmissionProof.create activeKey unsignedRequest dispatchTime
        let digest = CacheRegistrationProof.candidateRequestDigest unsignedRequest

        Assert.That(signedRequest.Proof.Payload.IssuedAt, Is.EqualTo(dispatchTime))

        Assert.That(
            CacheRegistrationProof.validate
                (dispatchTime.Plus(Duration.FromSeconds 30L))
                activePublicKey
                cacheId
                CacheRegistrationProof.SubmitCandidateOperation
                digest
                signedRequest.Proof,
            Is.True
        )

    /// Verifies Linux syscall custody checks are intentionally host-dependent and cannot be claimed as a Windows filesystem proof.
    [<Test>]
    member _.LinuxFileCustodySyscallsAreHostDependent() =
        if OperatingSystem.IsLinux() then
            CacheMachineConfiguration.validateProvisionedIdentityKeyFile "/definitely-missing-grace-cache-key"
            |> Result.isError
            |> Assert.That
        else
            Assert.That(CacheIdentityKeyCustody.selectProvider false, Is.EqualTo(PlatformX509Store))

    /// Verifies a restart sees the same durable candidate and does not retire the still-usable active key before promotion.
    [<Test>]
    member _.CandidateRestartRetainsSameCandidateBeforeCleanup() =
        let writes = ResizeArray<CacheMachineConfiguration>()
        let deletes = ResizeArray<string>()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                ActiveKeyName = "linux-11111111111111111111111111111111"
                ActivePublicKey = testPublicKey
                RotationLifecycle = Ready
            }

        let effects: CacheKeyRotationLifecycleEffects =
            {
                WriteConfiguration =
                    fun value ->
                        writes.Add value
                        Ok()
                DeleteKey =
                    fun value ->
                        deletes.Add value
                        Ok()
            }

        match CacheKeyRotationLifecycle.beginTransition effects configuration "linux-22222222222222222222222222222222" testPublicKey with
        | Error error -> Assert.Fail(error)
        | Ok persisted ->
            let restartCandidate =
                match persisted.RotationLifecycle with
                | CandidatePending pending -> Some pending.CandidateKeyName
                | Ready
                | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> None

            Assert.That(restartCandidate, Is.EqualTo(Some "linux-22222222222222222222222222222222"))
            Assert.That(deletes, Is.Empty)
            Assert.That(writes, Has.Count.EqualTo(1))

    /// Verifies candidate promotion selects the candidate durably before retiring the former active key.
    [<Test>]
    member _.CandidatePromotionSelectsBeforeActiveKeyCleanup() =
        let writes = ResizeArray<CacheMachineConfiguration>()
        let deletes = ResizeArray<string>()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                ActiveKeyName = "old"
                ActivePublicKey = testPublicKey
                RotationLifecycle =
                    CandidatePending
                        { ActiveKeyName = "old"; ActivePublicKey = testPublicKey; CandidateKeyName = "replacement"; CandidatePublicKey = testPublicKey }
            }

        let effects: CacheKeyRotationLifecycleEffects =
            {
                WriteConfiguration =
                    fun value ->
                        writes.Add value
                        Ok()
                DeleteKey =
                    fun value ->
                        deletes.Add value
                        Ok()
            }

        match configuration.RotationLifecycle with
        | CandidatePending candidate ->
            match CacheKeyRotationLifecycle.acceptReplacement effects configuration candidate with
            | Error error -> Assert.Fail(error)
            | Ok promoted ->
                Assert.That(promoted.ActiveKeyName, Is.EqualTo("replacement"))
                Assert.That(promoted.RotationLifecycle, Is.EqualTo(Ready))
                Assert.That(deletes, Is.EquivalentTo([ "old" ]))
                Assert.That(writes.Count, Is.EqualTo(2))
                Assert.That(writes[0].ActiveKeyName, Is.EqualTo("replacement"))
                Assert.That(writes[0].ActivePublicKey, Is.EqualTo(testPublicKey))

                match writes[0].RotationLifecycle with
                | CandidatePending _ -> ()
                | Ready
                | CacheKeyRotationLifecycle.OperatorRecoveryRequired ->
                    Assert.Fail("The active key must remain paired with its pending candidate until cleanup.")
        | Ready
        | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> Assert.Fail("The test requires a persisted candidate.")

    /// Verifies final server candidate outcomes are never retried with retained local candidate state.
    [<TestCase(CacheRegistrationRefreshStatus.Expired)>]
    [<TestCase(CacheRegistrationRefreshStatus.Revoked)>]
    [<TestCase(CacheRegistrationRefreshStatus.NotFound)>]
    member _.DefinitiveCandidateOutcomesRequireOperatorRecovery(status) =
        CacheKeyRotationLifecycle.isDefinitiveRegistrationRejection status
        |> Assert.That

    /// Verifies candidate rejection clears durable pending state before deleting the rejected key, including a cleanup failure.
    [<Test>]
    member _.CandidateRejectionClearsStateBeforeKeyCleanupFailure() =
        let writes = ResizeArray<CacheMachineConfiguration>()
        let deletes = ResizeArray<string>()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                ActiveKeyName = "active"
                ActivePublicKey = testPublicKey
                RotationLifecycle =
                    CandidatePending
                        { ActiveKeyName = "active"; ActivePublicKey = testPublicKey; CandidateKeyName = "candidate"; CandidatePublicKey = testPublicKey }
            }

        let effects: CacheKeyRotationLifecycleEffects =
            {
                WriteConfiguration =
                    fun value ->
                        writes.Add value
                        Ok()
                DeleteKey =
                    fun value ->
                        deletes.Add value
                        Error "candidate-key-delete-failed"
            }

        match configuration.RotationLifecycle with
        | CandidatePending pending ->
            match CacheKeyRotationLifecycle.rejectReplacement effects configuration pending with
            | Error error -> Assert.Fail(error)
            | Ok (cleared, cleanupFailure) ->
                Assert.That(cleared.RotationLifecycle, Is.EqualTo(CacheKeyRotationLifecycle.OperatorRecoveryRequired))
                Assert.That(CacheKeyRotationLifecycle.requiresOperatorRecovery cleared, Is.True)
                Assert.That(writes, Has.Count.EqualTo(1))
                Assert.That(writes[0].RotationLifecycle, Is.EqualTo(CacheKeyRotationLifecycle.OperatorRecoveryRequired))
                Assert.That(deletes, Is.EquivalentTo([ "candidate" ]))
                Assert.That(cleanupFailure, Is.EqualTo(Some "candidate-key-delete-failed"))
                let terminalJson = JsonSerializer.Serialize(cleared, Constants.JsonSerializerOptions)
                Assert.That(terminalJson, Does.Not.Contain("CandidateKeyName"))
                Assert.That(terminalJson, Does.Not.Contain("CandidatePublicKey"))

                let restarted = JsonSerializer.Deserialize<CacheMachineConfiguration>(terminalJson, Constants.JsonSerializerOptions)
                Assert.That(restarted.RotationLifecycle, Is.EqualTo(CacheKeyRotationLifecycle.OperatorRecoveryRequired))
        | Ready
        | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> Assert.Fail("The test requires a pending candidate.")

    /// Verifies the durable rejection marker makes a restarted host return the same operator-recovery outcome before listener startup.
    [<Test>]
    member _.DefinitiveCandidateRejectionStopsStartupAfterRestart() =
        let mutable listenerStarts = 0

        let rejectedConfiguration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                ActiveKeyName = "active"
                ActivePublicKey = testPublicKey
                RotationLifecycle = CacheKeyRotationLifecycle.OperatorRecoveryRequired
            }

        let effects: StartupRotationEffects =
            {
                Synchronize =
                    fun () ->
                        if CacheKeyRotationLifecycle.requiresOperatorRecovery rejectedConfiguration then
                            Ok(OperatorRecoveryRequired "Grace Cache identity recovery requires administrator revocation and re-enrollment.")
                        else
                            Assert.Fail("A definitive rejection must prevent startup from creating another candidate.")
                            Error "unreachable"
                WaitForRetry =
                    fun _ ->
                        Assert.Fail("Terminal rejection must not schedule a startup retry.")
                        false
            }

        match CacheStartupRotation.synchronizeBeforeListener effects with
        | Ok () ->
            listenerStarts <- listenerStarts + 1
            Assert.Fail("Terminal candidate rejection must block cache startup.")
        | Error error -> Assert.That(error, Does.Contain("administrator revocation and re-enrollment"))

        Assert.That(listenerStarts, Is.Zero)

    /// Verifies a final candidate rejection stops runtime scheduling instead of retrying or creating another candidate.
    [<Test>]
    member _.DefinitiveCandidateRejectionStopsRuntimeRotation() =
        let mutable scheduled = false
        let mutable markedUnhealthy = false

        let effects: KeyRotationSchedulingEffects =
            {
                Synchronize = fun () -> Ok(OperatorRecoveryRequired "operator recovery")
                ReadRegistration = fun () -> Error "registration must not be read after terminal candidate rejection"
                IsOperatorRecoveryRequired = fun () -> true
                MarkUnhealthy = fun () -> markedUnhealthy <- true
                Schedule = fun _ -> scheduled <- true
                Now = fun () -> Instant.FromUnixTimeSeconds 0L
            }

        let phase = CacheKeyRotationScheduling.run RotationRequired effects
        Assert.That(phase, Is.EqualTo(AutomaticWorkStopped))
        Assert.That(scheduled, Is.False)
        Assert.That(markedUnhealthy, Is.False)

    /// Verifies simultaneous cache starts have exactly one operating-system guard winner and the loser can cause no later effects.
    [<Test>]
    member _.MachineGuardAllowsExactlyOneConcurrentOwner() =
        let guardName = $"Grace.Cache.Tests.{Guid.NewGuid():N}"

        match MachineInstanceGuard.tryAcquireWithName guardName with
        | Error message -> Assert.Fail(message)
        | Ok first ->
            use first = first

            match MachineInstanceGuard.tryAcquireWithName guardName with
            | Ok second ->
                use second = second
                Assert.Fail("A simultaneous cache startup must not acquire the machine guard.")
            | Error message -> Assert.That(message, Does.Contain("already active"))

    /// Verifies mutex creation failures become the stable redacted guard result without exposing the rejected guard identity.
    [<Test>]
    member _.MachineGuardRedactsMutexCreationFailure() =
        let rejectedName = "Global\\"

        match MachineInstanceGuard.tryAcquireWithName rejectedName with
        | Ok lease ->
            use lease = lease
            Assert.Fail("The invalid Windows global mutex name unexpectedly acquired the machine guard.")
        | Error message ->
            Assert.That(message, Is.EqualTo("A Grace Cache process is already active on this machine."))
            Assert.That(message, Does.Not.Contain(rejectedName))
            Assert.That(message, Does.Not.Contain("Global\\"))

    /// Verifies normal disposal releases the operating-system guard for a later cache process.
    [<Test>]
    member _.MachineGuardIsReleasedAfterDisposal() =
        let guardName = $"Grace.Cache.Tests.{Guid.NewGuid():N}"

        let first =
            match MachineInstanceGuard.tryAcquireWithName guardName with
            | Ok lease -> lease
            | Error message ->
                Assert.Fail(message)
                Unchecked.defaultof<MachineInstanceLease>

        (first :> IDisposable).Dispose()

        match MachineInstanceGuard.tryAcquireWithName guardName with
        | Error message -> Assert.Fail(message)
        | Ok second -> (second :> IDisposable).Dispose()
