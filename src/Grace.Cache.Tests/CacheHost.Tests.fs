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
                CandidateKeyState =
                    Some
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
            Assert.That(configuration.CandidateKeyState, Is.Not.EqualTo(None))

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
                CandidateKeyState = None
            }

        let status = CacheMachineConfiguration.toStatus configuration

        Assert.That(status.CacheId, Is.EqualTo(Some "11111111-1111-1111-1111-111111111111"))
        Assert.That(status.Transport, Is.EqualTo(Some "https"))
        Assert.That(status.ToString(), Does.Not.Contain(configuration.ServerUri))
        Assert.That(status.ToString(), Does.Not.Contain(configuration.ActiveKeyName))

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
                CandidateKeyState =
                    Some { ActiveKeyName = "old"; ActivePublicKey = testPublicKey; CandidateKeyName = "replacement"; CandidatePublicKey = testPublicKey }
            }

        let effects: CandidateKeyStateEffects =
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

        match configuration.CandidateKeyState with
        | None -> Assert.Fail("The test requires a persisted candidate.")
        | Some candidate ->
            match CandidateKeyState.acceptReplacement effects configuration candidate with
            | Error error -> Assert.Fail(error)
            | Ok promoted ->
                Assert.That(promoted.ActiveKeyName, Is.EqualTo("replacement"))
                Assert.That(promoted.CandidateKeyState, Is.EqualTo(None))
                Assert.That(deletes, Is.EquivalentTo([ "old" ]))
                Assert.That(writes.Count, Is.EqualTo(2))
                Assert.That(writes[0].ActiveKeyName, Is.EqualTo("replacement"))
                Assert.That(writes[0].ActivePublicKey, Is.EqualTo(testPublicKey))
                Assert.That(writes[0].CandidateKeyState, Is.Not.EqualTo(None))

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
