namespace Grace.Server.Unit.Tests

open Grace.Actors.CacheRegistrationActor
open Grace.Actors.Interfaces
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Supplies controllable durable state for Cache registration actor persistence-order tests.
type private FailingCacheRegistrationState(initial: CacheRegistrationState) =
    let mutable value = initial
    let mutable writes = 0

    /// Returns the number of attempted durable writes.
    member _.WriteCount = writes

    /// Returns the value assigned to the persistence facet before its configured write failure.
    member _.State = value

    interface IPersistentState<CacheRegistrationState> with
        member _.State
            with get () = value
            and set next = value <- next

        member _.Etag = null
        member _.RecordExists = false
        member _.ReadStateAsync() = Task.CompletedTask

        member _.WriteStateAsync() =
            writes <- writes + 1
            Task.FromException(InvalidOperationException("configured test persistence failure"))

        member _.ClearStateAsync() = Task.CompletedTask
        member this.ReadStateAsync(_: CancellationToken) = (this :> IPersistentState<_>).ReadStateAsync()
        member this.WriteStateAsync(_: CancellationToken) = (this :> IPersistentState<_>).WriteStateAsync()
        member this.ClearStateAsync(_: CancellationToken) = (this :> IPersistentState<_>).ClearStateAsync()

/// Proves Cache registration success is not returned and in-memory eligibility does not advance when persistence fails.
[<TestFixture>]
type CacheRegistrationActorTests() =

    /// Builds one valid administrator-owned registration request for actor persistence ordering proof.
    let enrollmentRequest () =
        {
            Class = nameof CacheEnrollmentRequest
            DisplayName = "actor-write-failure"
            BoundaryKind = CacheBoundaryKind.Organization
            OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            OrganizationId = Some(Guid.Parse("33333333-3333-3333-3333-333333333333"))
            RepositoryScopes =
                List<CacheRepositoryScope>(
                    [
                        CacheRepositoryScope.Create(Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"))
                    ]
                )
            PublicKey = CacheIdentityPublicKey.Create("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
            Endpoint = "https://cache.example.test"
            AllowHttpEndpoint = false
            SoftwareVersion = "1.0.0"
            ProtocolVersion = "v1"
            PrefetchSupported = false
        }

    /// Verifies a failed actor store write cannot return enrollment success or make the registration selectable in memory.
    [<Test>]
    member _.``enrollment persistence failure leaves the authoritative actor state unadvanced``() =
        task {
            let persistentState = FailingCacheRegistrationState(CacheRegistrationState.Empty)

            let actor = CacheRegistrationActor(NullLoggerFactory.Instance, persistentState :> IPersistentState<CacheRegistrationState>)

            let cacheActor = actor :> ICacheRegistrationActor
            let now = Instant.FromUtc(2026, 8, 12, 12, 0)
            let cacheId = Guid.Parse("55555555-5555-5555-5555-555555555555")

            Assert.ThrowsAsync<InvalidOperationException>(
                Func<Task>(fun () -> cacheActor.Enroll(cacheId, enrollmentRequest (), "administrator", now, "cache-write-failure") :> Task)
            )
            |> ignore

            let! retained = cacheActor.Get(cacheId, "cache-write-failure")
            let! eligible = cacheActor.SelectEligible(CacheRegistrationSelectionQuery.Current, now, "cache-write-failure")

            Assert.That(persistentState.WriteCount, Is.EqualTo(1))
            Assert.That(persistentState.State.Registrations, Has.Length.EqualTo(1))

            match retained with
            | None -> ()
            | Some registration -> Assert.Fail($"Actor retained CacheId {registration.CacheId} after its durable enrollment write failed.")

            Assert.That(eligible, Is.Empty)
        }
