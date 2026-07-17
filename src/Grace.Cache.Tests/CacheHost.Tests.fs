namespace Grace.Cache.Tests

open System
open System.IO
open Grace.Cache
open Grace.Types.CacheRegistration
open NUnit.Framework

/// Verifies the cache tracer exposes only its fixed safe route inventory.
[<TestFixture>]
type CacheHostTests() =

    /// Verifies that the F# cache host exposes only safe status routes before later runtime capabilities exist.
    [<Test>]
    member _.RouteInventoryContainsOnlyScaffoldRoutes() =
        Assert.That(
            CacheHost.routeInventory,
            Is.EquivalentTo(
                [
                    "/healthz"
                    "/status"
                    "/control/status"
                ]
            )
        )

    /// Verifies the host uses the #600 registration contract interval for automatic rotation rather than a local timer default.
    [<Test>]
    member _.HostUsesTheFourHourRegistrationRotationInterval() =
        Assert.That(CacheHost.keyRotationInterval, Is.EqualTo(RegistrationLifetime.KeyRotationInterval.ToTimeSpan()))

    /// Verifies registration refresh has an independent one-hour schedule that runs before the two-hour active lifetime expires.
    [<Test>]
    member _.HostUsesTheOneHourRegistrationRefreshInterval() =
        Assert.That(CacheHost.registrationRefreshInterval, Is.EqualTo(RegistrationLifetime.RefreshAfter.ToTimeSpan()))
        Assert.That(CacheHost.registrationRefreshInterval, Is.LessThan(RegistrationLifetime.ActiveLifetime.ToTimeSpan()))
        Assert.That(CacheHost.registrationRefreshInterval, Is.Not.EqualTo(CacheHost.keyRotationInterval))

    /// Verifies the pre-artifact scaffold never represents itself as ready to serve selected cache materialization work.
    [<Test>]
    member _.ScaffoldDoesNotPublishArtifactServingReadiness() = Assert.That(CacheHost.artifactServingAvailable, Is.False)

    /// Verifies the Unix local-control policy admits the service account and root while rejecting unrelated callers.
    [<Test>]
    member _.UnixLocalControlAuthorizesOnlyServiceAccountOrRoot() =
        Assert.That(CacheLocalControl.isUnixCallerAuthorized 1001u 1001u, Is.True)
        Assert.That(CacheLocalControl.isUnixCallerAuthorized 1001u 0u, Is.True)
        Assert.That(CacheLocalControl.isUnixCallerAuthorized 1001u 1002u, Is.False)

    /// Verifies ambiguous enrollment evidence keeps only the approved fields and blocks automatic recovery.
    [<Test>]
    member _.UnknownEnrollmentRecoveryPreservesEvidenceAndBlocksAutomaticWork() =
        let root = Path.Combine(Path.GetTempPath(), $"grace-cache-recovery-{Guid.NewGuid():N}")
        let configurationPath = Path.Combine(root, "cache.runtime.json")
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath
        let repositoryScopes = [| Guid.NewGuid(), Guid.NewGuid() |]
        let recovery = CacheEnrollmentRecovery.prepare "https://cache.example.test" "https://server.example.test/grace/" repositoryScopes "opaque-key-reference"

        try
            match CacheEnrollmentRecovery.write recoveryPath (CacheEnrollmentRecovery.unknown recovery) with
            | Error error -> Assert.Fail(error)
            | Ok () ->
                match CacheEnrollmentRecovery.tryRead recoveryPath with
                | Error error -> Assert.Fail(error)
                | Ok None -> Assert.Fail("Expected durable enrollment recovery evidence.")
                | Ok (Some persisted) ->
                    Assert.That(CacheEnrollmentRecovery.isUnknown persisted, Is.True)
                    Assert.That(persisted.Endpoint, Is.EqualTo(recovery.Endpoint))
                    Assert.That(persisted.ServerUri, Is.EqualTo(recovery.ServerUri))
                    Assert.That(persisted.RepositoryScopes = repositoryScopes, Is.True)
                    Assert.That(persisted.IdentityKeyName, Is.EqualTo(recovery.IdentityKeyName))
                    Assert.That(persisted.CacheId, Is.EqualTo(None))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

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
            |> CacheEnrollmentRecovery.accept cacheId

        try
            match CacheEnrollmentRecovery.write recoveryPath recovery with
            | Error error -> Assert.Fail(error)
            | Ok () ->
                let previous = Environment.GetEnvironmentVariable("GRACE_SERVER_URI")
                Environment.SetEnvironmentVariable("GRACE_SERVER_URI", "https://changed-server.example.test/")

                try
                    match CacheRuntimeControl.finalizeAcceptedEnrollment configurationPath recovery cacheId with
                    | Error error -> Assert.Fail(error)
                    | Ok _ ->
                        match CacheMachineConfiguration.tryRead configurationPath with
                        | Error error -> Assert.Fail(error)
                        | Ok configuration -> Assert.That(configuration.ServerUri, Is.EqualTo("https://original-server.example.test/grace/"))

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
        let root = Path.Combine(Path.GetTempPath(), $"grace-cache-status-{Guid.NewGuid():N}")
        let configurationPath = Path.Combine(root, "cache.runtime.json")

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.NewGuid()
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/grace/"
                IdentityKeyName = "old-key"
                PendingKeyTransition = Some { CurrentKeyName = "old-key"; ReplacementKeyName = "replacement-key" }
            }

        try
            match CacheMachineConfiguration.write configurationPath configuration with
            | Error error -> Assert.Fail(error)
            | Ok () ->
                match CacheRuntimeControl.readStatus configurationPath with
                | Error error -> Assert.Fail(error)
                | Ok status -> Assert.That(status.Lifecycle, Is.EqualTo("registered"))

                match CacheMachineConfiguration.tryRead configurationPath with
                | Error error -> Assert.Fail(error)
                | Ok persisted -> Assert.That(persisted.PendingKeyTransition, Is.EqualTo(configuration.PendingKeyTransition))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    /// Verifies startup exceptions become a stable cache process failure without exposing bind or certificate details.
    [<Test>]
    member _.StartupExceptionBecomesRedactedProcessFailure() =
        let secret = "kestrel-bind-certificate-secret"

        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Enrollment must not run for startup."
                RotateNow = fun () -> failwith "Rotation must not run for startup."
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
    [<TestCase("ftp://cache.example.test/")>]
    member _.CacheEndpointRejectsNonOriginInputs(endpoint) =
        CacheMachineConfiguration.validateEndpoint endpoint false
        |> Result.isError
        |> Assert.That

    /// Verifies an active host owns protected machine-local rotation and executes exactly one request through the shared runtime boundary.
    [<Test>]
    member _.LocalRotationControlUsesTheActiveProcess() =
        let mutable rotations = 0
        let expected = CacheRuntimeStatus.registered (Guid.Parse "11111111-1111-1111-1111-111111111111") "https"

        match
            CacheLocalControl.startWith (fun () ->
                rotations <- rotations + 1
                Ok expected)
            with
        | Error error -> Assert.Fail(error)
        | Ok server ->
            use server = server

            match CacheLocalControl.requestRotation () with
            | Error error -> Assert.Fail(error)
            | Ok actual ->
                Assert.That(actual, Is.EqualTo(expected))
                Assert.That(rotations, Is.EqualTo(1))

    /// Verifies redacted status retains a stable cache identity and transport state without exposing the Grace Server URI.
    [<Test>]
    member _.StatusRedactsServerConfiguration() =
        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/private"
                IdentityKeyName = "Grace.Cache.Identity.test"
                PendingKeyTransition = None
            }

        let status = CacheMachineConfiguration.toStatus configuration

        Assert.That(status.CacheId, Is.EqualTo(Some "11111111-1111-1111-1111-111111111111"))
        Assert.That(status.Transport, Is.EqualTo(Some "https"))
        Assert.That(status.ToString(), Does.Not.Contain(configuration.ServerUri))
        Assert.That(status.ToString(), Does.Not.Contain(configuration.IdentityKeyName))

    /// Verifies a restart before remote acceptance removes the pending replacement and retains the currently accepted key.
    [<Test>]
    member _.PendingKeyBeforeRemoteAcceptanceRollsBackOnRecovery() =
        let writes = ResizeArray<CacheMachineConfiguration>()
        let deletes = ResizeArray<string>()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                IdentityKeyName = "old"
                PendingKeyTransition = Some { CurrentKeyName = "old"; ReplacementKeyName = "replacement" }
            }

        let effects: PendingKeyTransitionEffects =
            {
                WriteConfiguration =
                    fun value ->
                        writes.Add value
                        Ok()
                DeleteKey =
                    fun value ->
                        deletes.Add value
                        Ok()
                ProbeAcceptedKey = fun _ keyName -> Ok(keyName = "old")
            }

        match PendingKeyTransition.reconcile effects configuration with
        | Error error -> Assert.Fail(error)
        | Ok recovered ->
            Assert.That(recovered.IdentityKeyName, Is.EqualTo("old"))
            Assert.That(recovered.PendingKeyTransition, Is.EqualTo(None))
            Assert.That(deletes, Is.EquivalentTo([ "replacement" ]))
            Assert.That(writes.Count, Is.EqualTo(1))

    /// Verifies a restart after server acceptance finalizes the local replacement before retiring the old key.
    [<Test>]
    member _.PendingAcceptedKeyFinalizesBeforeOldKeyCleanup() =
        let writes = ResizeArray<CacheMachineConfiguration>()
        let deletes = ResizeArray<string>()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                IdentityKeyName = "old"
                PendingKeyTransition = Some { CurrentKeyName = "old"; ReplacementKeyName = "replacement" }
            }

        let effects: PendingKeyTransitionEffects =
            {
                WriteConfiguration =
                    fun value ->
                        writes.Add value
                        Ok()
                DeleteKey =
                    fun value ->
                        deletes.Add value
                        Ok()
                ProbeAcceptedKey = fun _ keyName -> Ok(keyName = "replacement")
            }

        match PendingKeyTransition.reconcile effects configuration with
        | Error error -> Assert.Fail(error)
        | Ok recovered ->
            Assert.That(recovered.IdentityKeyName, Is.EqualTo("replacement"))
            Assert.That(recovered.PendingKeyTransition, Is.EqualTo(None))
            Assert.That(deletes, Is.EquivalentTo([ "old" ]))
            Assert.That(writes.Count, Is.EqualTo(2))
            Assert.That(writes[0].IdentityKeyName, Is.EqualTo("replacement"))
            Assert.That(writes[0].PendingKeyTransition, Is.Not.EqualTo(None))

    /// Verifies a cleanup failure retains the durable pending transition and blocks a normal-work conclusion.
    [<Test>]
    member _.PendingAcceptedKeyCleanupFailureStaysUnresolved() =
        let writes = ResizeArray<CacheMachineConfiguration>()

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                IdentityKeyName = "replacement"
                PendingKeyTransition = Some { CurrentKeyName = "old"; ReplacementKeyName = "replacement" }
            }

        let effects: PendingKeyTransitionEffects =
            {
                WriteConfiguration =
                    fun value ->
                        writes.Add value
                        Ok()
                DeleteKey = fun _ -> Error "cleanup failed"
                ProbeAcceptedKey = fun _ keyName -> Ok(keyName = "replacement")
            }

        PendingKeyTransition.reconcile effects configuration
        |> Result.isError
        |> Assert.That

        Assert.That(writes.Count, Is.EqualTo(1))
        Assert.That(writes[0].PendingKeyTransition, Is.Not.EqualTo(None))

    /// Verifies a completed transition has no further recovery probes, cleanup, or writes on restart.
    [<Test>]
    member _.CompletedKeyTransitionDoesNotRunRecoveryAgain() =
        let mutable effectsCalled = false

        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test"
                IdentityKeyName = "replacement"
                PendingKeyTransition = None
            }

        let effects: PendingKeyTransitionEffects =
            {
                WriteConfiguration =
                    fun _ ->
                        effectsCalled <- true
                        Ok()
                DeleteKey =
                    fun _ ->
                        effectsCalled <- true
                        Ok()
                ProbeAcceptedKey =
                    fun _ _ ->
                        effectsCalled <- true
                        Ok true
            }

        match PendingKeyTransition.reconcile effects configuration with
        | Error error -> Assert.Fail(error)
        | Ok recovered -> Assert.That(recovered, Is.EqualTo(configuration))

        Assert.That(effectsCalled, Is.False)

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
