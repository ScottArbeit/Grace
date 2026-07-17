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
