namespace Grace.Types.Tests

open Grace.Types.CacheRegistration
open Grace.Types.MaterializationPlan
open NodaTime
open NUnit.Framework

/// Covers deterministic Grace Cache registration lifecycle behavior.
[<Parallelizable(ParallelScope.All)>]
type CacheRegistrationLifecycleTests() =

    let servicePrincipalId = "cache-service-client"
    let scope = "repository:owner/org/repo"
    let secondScope = "repository:owner/org/second"
    let now = Instant.FromUtc(2026, 7, 9, 10, 0)

    /// Builds a valid Cache registration request for lifecycle tests.
    let request endpoint scopes capabilities modes = CacheRegistrationRequest.Create(endpoint, scopes, capabilities, modes)

    /// Registers one current Cache service with the deterministic lifecycle helper.
    let registerCurrent () =
        Lifecycle.register
            CacheRegistrationState.Empty
            servicePrincipalId
            (request
                "https://cache.example.test"
                [ scope ]
                [
                    Capability.ReadThrough
                    Capability.Prefetch
                ]
                [
                    MaterializationExecutionMode.CachePreferred
                    MaterializationExecutionMode.CacheRequired
                ])
            [ scope ]
            [
                Capability.ReadThrough
                Capability.Prefetch
            ]
            now

    [<Test>]
    member _.``register stores approved service boundary with two hour lifetime and one hour refresh-after``() =
        let state, result = registerCurrent ()

        Assert.That(state.Registrations, Has.Length.EqualTo 1)
        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.Refreshed)

        let registration = state.Registrations[0]

        Assert.That(registration.ServicePrincipalId, Is.EqualTo servicePrincipalId)
        Assert.That(registration.Endpoint, Is.EqualTo "https://cache.example.test")
        Assert.That(registration.ApprovedScopes, Has.Length.EqualTo 1)
        Assert.That(registration.ApprovedScopes[0], Is.EqualTo scope)

        Assert.That(
            registration.ApprovedCapabilities,
            Is.EquivalentTo [| Capability.ReadThrough
                               Capability.Prefetch |]
        )

        Assert.That(registration.ReadThroughEnabled, Is.True)
        Assert.That(registration.PrefetchEnabled, Is.True)
        Assert.That(registration.RegisteredAt, Is.EqualTo now)
        Assert.That(registration.LastRefreshedAt, Is.EqualTo now)
        Assert.That(registration.RefreshAfter, Is.EqualTo(now.Plus(Duration.FromHours 1)))
        Assert.That(registration.ExpiresAt, Is.EqualTo(now.Plus(Duration.FromHours 2)))

    [<Test>]
    member _.``duplicate register replaces the live record for the same service principal``() =
        let state, _ = registerCurrent ()

        let nextState, _ =
            Lifecycle.register
                state
                servicePrincipalId
                (request
                    "https://cache-two.example.test"
                    [ secondScope ]
                    [ Capability.ReadThrough ]
                    [
                        MaterializationExecutionMode.CacheRequired
                    ])
                [ secondScope ]
                [ Capability.ReadThrough ]
                (now.Plus(Duration.FromMinutes 5L))

        Assert.That(nextState.Registrations, Has.Length.EqualTo 1)
        Assert.That(nextState.Registrations[0].Endpoint, Is.EqualTo "https://cache-two.example.test")

        Assert.That(nextState.Registrations[0].ApprovedScopes, Has.Length.EqualTo 1)
        Assert.That(nextState.Registrations[0].ApprovedScopes[0], Is.EqualTo secondScope)

        Assert.That(nextState.Registrations[0].PrefetchEnabled, Is.False)

    [<Test>]
    member _.``refresh before refresh-after is deterministic and preserves the current registration``() =
        let state, _ = registerCurrent ()
        let nextState, result = Lifecycle.refresh state servicePrincipalId (now.Plus(Duration.FromMinutes 30L))

        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.RefreshNotDue)
        Assert.That(nextState.Registrations[0].LastRefreshedAt, Is.EqualTo now)
        Assert.That(nextState.Registrations[0].ExpiresAt, Is.EqualTo(now.Plus(Duration.FromHours 2)))

    [<Test>]
    member _.``refresh after refresh-after extends expiry without changing approved scopes or capabilities``() =
        let state, _ = registerCurrent ()
        let refreshInstant = now.Plus(Duration.FromMinutes 75L)
        let nextState, result = Lifecycle.refresh state servicePrincipalId refreshInstant

        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.Refreshed)

        let registration = nextState.Registrations[0]

        Assert.That(registration.LastRefreshedAt, Is.EqualTo refreshInstant)
        Assert.That(registration.RefreshAfter, Is.EqualTo(refreshInstant.Plus(Duration.FromHours 1)))
        Assert.That(registration.ExpiresAt, Is.EqualTo(refreshInstant.Plus(Duration.FromHours 2)))
        Assert.That(registration.ApprovedScopes, Has.Length.EqualTo 1)
        Assert.That(registration.ApprovedScopes[0], Is.EqualTo scope)

        Assert.That(
            registration.ApprovedCapabilities,
            Is.EquivalentTo [| Capability.ReadThrough
                               Capability.Prefetch |]
        )

    [<Test>]
    member _.``expired registrations are not refreshed or selected for new plans``() =
        let state, _ = registerCurrent ()
        let expiredInstant = now.Plus(Duration.FromHours 3)
        let _, result = Lifecycle.refresh state servicePrincipalId expiredInstant

        let eligible =
            Lifecycle.selectEligible
                state
                (CacheRegistrationSelectionQuery.Create(Some scope, [ Capability.ReadThrough ], Some MaterializationExecutionMode.CachePreferred, true, false))
                expiredInstant

        Assert.That(result.Status, Is.EqualTo CacheRegistrationRefreshStatus.Expired)
        Assert.That(eligible, Is.Empty)

    [<Test>]
    member _.``selection query returns only current registrations matching scope mode and capability``() =
        let state, _ = registerCurrent ()

        let matching =
            Lifecycle.selectEligible
                state
                (CacheRegistrationSelectionQuery.Create(Some scope, [ Capability.ReadThrough ], Some MaterializationExecutionMode.CacheRequired, true, false))
                (now.Plus(Duration.FromMinutes 90L))

        let wrongScope =
            Lifecycle.selectEligible
                state
                (CacheRegistrationSelectionQuery.Create(
                    Some secondScope,
                    [ Capability.ReadThrough ],
                    Some MaterializationExecutionMode.CacheRequired,
                    true,
                    false
                ))
                (now.Plus(Duration.FromMinutes 90L))

        let wrongCapability =
            Lifecycle.selectEligible
                state
                (CacheRegistrationSelectionQuery.Create(Some scope, [ "unsupported-capability" ], Some MaterializationExecutionMode.CacheRequired, false, false))
                (now.Plus(Duration.FromMinutes 90L))

        Assert.That(matching, Has.Length.EqualTo 1)
        Assert.That(wrongScope, Is.Empty)
        Assert.That(wrongCapability, Is.Empty)

    [<Test>]
    member _.``registration request validation rejects non-https endpoints``() =
        let invalid =
            request
                "http://cache.example.test"
                [ scope ]
                [ Capability.ReadThrough ]
                [
                    MaterializationExecutionMode.CachePreferred
                ]

        match Lifecycle.validateRequest invalid with
        | Ok () -> Assert.Fail("Expected validation to reject a non-HTTPS endpoint.")
        | Error errors -> Assert.That(errors, Does.Contain "Endpoint must be an absolute HTTPS URI.")
