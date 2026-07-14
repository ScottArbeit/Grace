namespace Grace.Cache.Tests

open System
open System.IO
open Grace.Cache
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

    /// Verifies redacted status retains a stable cache identity and transport state without exposing the Grace Server URI.
    [<Test>]
    member _.StatusRedactsServerConfiguration() =
        let configuration: CacheMachineConfiguration =
            {
                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                ServerUri = "https://server.example.test/private"
            }

        let status = CacheMachineConfiguration.toStatus configuration

        Assert.That(status.CacheId, Is.EqualTo(Some "11111111-1111-1111-1111-111111111111"))
        Assert.That(status.Transport, Is.EqualTo(Some "https"))
        Assert.That(status.ToString(), Does.Not.Contain(configuration.ServerUri))

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
