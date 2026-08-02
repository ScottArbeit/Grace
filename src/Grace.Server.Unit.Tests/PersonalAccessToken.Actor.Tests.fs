namespace Grace.Server.Tests

open Grace.Actors
open Grace.Actors.Interfaces
open Grace.Types.PersonalAccessToken
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open Orleans.Runtime
open System
open System.Threading.Tasks

/// Records attempted PAT writes while deterministically rejecting persistence.
type private RejectingPersonalAccessTokenState() =
    let mutable state = PersonalAccessToken.PersonalAccessTokenState.Empty
    let mutable writeCount = 0

    member _.WriteCount = writeCount

    interface IPersistentState<PersonalAccessToken.PersonalAccessTokenState> with
        member _.State
            with get () = state
            and set value = state <- value

        member _.Etag = null

        member _.RecordExists = false

        member _.ReadStateAsync() = Task.CompletedTask

        member _.WriteStateAsync() =
            writeCount <- writeCount + 1
            Task.FromException(InvalidOperationException("simulated PAT persistence failure"))

        member _.ClearStateAsync() = Task.CompletedTask

/// Covers PAT actor durability behavior without Aspire.
[<NonParallelizable>]
type PersonalAccessTokenActorTests() =

    /// Verifies a failed durable write cannot create an in-memory duplicate on retry.
    [<Test>]
    member _.CreateFailureDoesNotRetainTokenName() =
        task {
            let persistentState = RejectingPersonalAccessTokenState()
            let actor = PersonalAccessToken.PersonalAccessTokenActor(persistentState, NullLoggerFactory.Instance)
            let personalAccessTokenActor = actor :> IPersonalAccessTokenActor
            let now = Instant.FromUtc(2026, 8, 1, 12, 0)

            let invoke attempt =
                task {
                    try
                        let! result = personalAccessTokenActor.CreateToken "local-dev" [] [] None now $"correlation-{attempt}"
                        return Choice1Of2 result
                    with
                    | ex -> return Choice2Of2 ex
                }

            let! firstFailure = invoke 1
            let! secondFailure = invoke 2
            let failures = [ firstFailure; secondFailure ]

            Assert.That(persistentState.WriteCount, Is.EqualTo(2), "Each request should reach durable persistence.")

            for failure in failures do
                match failure with
                | Choice1Of2 (Ok _) -> Assert.Fail("A PAT must not be returned after persistence fails.")
                | Choice1Of2 (Error error) -> Assert.Fail($"Failed persistence became a domain error: {error.Error}")
                | Choice2Of2 ex -> Assert.That(ex.Message, Is.EqualTo("simulated PAT persistence failure"))
        }
