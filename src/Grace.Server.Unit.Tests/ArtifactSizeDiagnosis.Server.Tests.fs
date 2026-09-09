namespace Grace.Server.Unit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Grace.Server.Artifact
open Grace.Actors.Services
open Grace.Shared.Utilities
open Grace.Shared.Parameters.Repository
open Grace.Types.Artifact
open Grace.Types.Usage
open Grace.Types.Common
open NUnit.Framework

/// Checks retained Artifact declaration projection, complete enumeration and repository revalidation without hosting.
[<Parallelizable(ParallelScope.All)>]
type ArtifactSizeDiagnosisTests() =
    let scope: UsageFactScope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = Guid.NewGuid() }

    let metadata =
        { ArtifactMetadata.Default with
            ArtifactId = Guid.NewGuid()
            OwnerId = scope.OwnerId
            OrganizationId = scope.OrganizationId
            RepositoryId = scope.RepositoryId
            BlobPath = "grace-artifacts/test"
            Size = 13L
            CreatedAt = getCurrentInstant ()
            CreatedBy = "test"
        }

    /// Uses the real compiled producer conversion for persisted lifecycle snapshots.
    let snapshot name value =
        let eventMetadata: EventMetadata =
            {
                Timestamp = getCurrentInstant ()
                CorrelationId = "artifact-diagnostic"
                Principal = "test"
                ClientType = None
                Properties = Dictionary<string, string>()
            }

        ArtifactEvent.FromMetadata(name, value, eventMetadata)

    let created = snapshot ArtifactEventNames.Created metadata

    /// Decodes the exact current serializer output before exercising the production enumeration.
    let row (events: ArtifactEvent array) =
        use json = JsonDocument.Parse(serialize {| State = events |})
        decodeArtifactSizeDocument json.RootElement

    /// Supplies complete pages afresh on each enumeration attempt.
    let enumerate (pages: ArtifactEvent array array array) =
        let mutable index = 0

        task {
            let! total, count, started, finished =
                enumerateArtifactSizeWith
                    (fun _ ->
                        let page = pages[index]
                        index <- index + 1
                        Task.FromResult(page, index < pages.Length))
                    scope
                    CancellationToken.None

            return
                {
                    Scope = scope
                    DeclaredArtifactBytes = total
                    DistinctArtifactCount = count
                    EnumerationStartedAt = started
                    EnumerationFinishedAt = finished
                }
        }

    /// Requires the intended error and rejects any partial diagnostic result.
    let failure (expected: Type) (run: unit -> Task<'a>) =
        task {
            let! outcome =
                task {
                    try
                        let! result = run ()
                        return Choice1Of2 result
                    with
                    | error -> return Choice2Of2 error
                }

            match outcome with
            | Choice1Of2 result -> Assert.Fail($"Unexpected successful result {result}.")
            | Choice2Of2 error -> Assert.That(expected.IsInstanceOfType error, Is.True, error.ToString())
        }

    /// Counts the latest complete snapshot once through retained deletion and restoration, including zero and unattached identities.
    [<Test>]
    member _.``full snapshots deduplicate while zero and unattached identities count``() =
        task {
            let deleted = { metadata with DeletedAt = Some(getCurrentInstant ()); BlobDeleted = true; WorkItemLinkRemoved = true }

            let history =
                row [| created
                       snapshot ArtifactEventNames.LogicalDeleted deleted
                       snapshot ArtifactEventNames.BlobDeleted deleted
                       snapshot ArtifactEventNames.WorkItemLinkRemoved deleted |]

            let zero = row [| snapshot ArtifactEventNames.Created { metadata with ArtifactId = Guid.NewGuid(); Size = 0L } |]
            let twin = row [| snapshot ArtifactEventNames.Created { metadata with ArtifactId = Guid.NewGuid() } |]

            let! result =
                enumerate [| [| history; zero |]
                             [| history; twin; row [||] |] |]

            Assert.That(result.DeclaredArtifactBytes, Is.EqualTo 26L)
            Assert.That(result.DistinctArtifactCount, Is.EqualTo 3L)
            Assert.That(result.Scope, Is.EqualTo scope)
            Assert.That(result.EnumerationFinishedAt, Is.GreaterThanOrEqualTo result.EnumerationStartedAt)

            Assert.That(
                (projectArtifactSize scope history)
                    .Value
                    .BlobDeleted,
                Is.True
            )

            let restored =
                row [| created
                       snapshot ArtifactEventNames.LogicalDeleted deleted
                       snapshot ArtifactEventNames.Undeleted metadata |]

            Assert.That((projectArtifactSize scope restored).Value, Is.EqualTo metadata)

            let changed =
                row [| created
                       { created with Event = ArtifactEventNames.Undeleted; Size = 7L } |]

            let! latest = enumerate [| [| changed |] |]
            Assert.That(latest.DeclaredArtifactBytes, Is.EqualTo 7L, "Projection uses the last full snapshot, not a sum of events.")
        }

    /// Distinguishes explicit Size zero, an empty shell and an absent or malformed State field.
    [<Test>]
    member _.``explicit zero survives serializer and empty source is known zero``() =
        task {
            let zero = snapshot ArtifactEventNames.Created { metadata with Size = 0L }
            let wire = serialize {| State = [| zero |] |}
            Assert.That(wire, Does.Contain "\"Size\": 0")

            Assert.That(
                ((row [| zero |]).[0].ToMetadata())
                    .WorkItemId
                    .IsNone,
                Is.True
            )

            let! result = enumerate [| [| row [| zero |] |] |]
            Assert.That(result.DeclaredArtifactBytes, Is.Zero)
            Assert.That(result.DistinctArtifactCount, Is.EqualTo 1L)
            let! empty = enumerate [| [| row [||] |] |]
            Assert.That(empty.DeclaredArtifactBytes, Is.Zero)
            Assert.That(empty.DistinctArtifactCount, Is.Zero)
        }

    /// Rejects missing fields before serializer defaults can turn absent declarations into zero.
    [<TestCase("Size")>]
    [<TestCase("ArtifactId")>]
    [<TestCase("OwnerId")>]
    [<TestCase("OrganizationId")>]
    [<TestCase("RepositoryId")>]
    [<TestCase("CreatedAtUnixTimeTicks")>]
    [<TestCase("BlobPath")>]
    [<TestCase("Event")>]
    member _.``missing required snapshot fields fail``(field: string) =
        let node = JsonNode.Parse(serialize {| State = [| created |] |})

        node.["State"].[0].AsObject().Remove(field)
        |> ignore

        use json = JsonDocument.Parse(node.ToJsonString())

        Assert.Throws<InvalidDataException>(
            Action (fun () ->
                decodeArtifactSizeDocument json.RootElement
                |> ignore)
        )
        |> ignore

    /// Refuses absent State and malformed state arrays before typed decoding.
    [<TestCase("{}")>]
    [<TestCase("{\"State\":null}")>]
    [<TestCase("{\"State\":{}}")>]
    [<TestCase("{\"State\":[null]}")>]
    member _.``malformed state cannot become known zero``(wire: string) =
        use json = JsonDocument.Parse wire

        Assert.Throws<InvalidDataException>(
            Action (fun () ->
                decodeArtifactSizeDocument json.RootElement
                |> ignore)
        )
        |> ignore

    /// Accepts current empty optional metadata and rejects invalid required scalar fields.
    [<Test>]
    member _.``required scalar validation does not demand optional metadata``() =
        let optional = JsonNode.Parse(serialize {| State = [| created |] |})

        optional.["State"].[0].["Sha256"] <- JsonValue.Create ""
        optional.["State"].[0].["WorkItemId"] <- JsonValue.Create(string Guid.Empty)

        use optionalJson = JsonDocument.Parse(optional.ToJsonString())

        Assert.That(
            (decodeArtifactSizeDocument optionalJson.RootElement)
                .Length,
            Is.EqualTo 1
        )

        for field, value in
            [
                "Size", "-1"
                "Size", "1.5"
                "Size", "9223372036854775808"
                "Size", "\"0\""
                "ArtifactId", "\"00000000-0000-0000-0000-000000000000\""
                "OwnerId", "\"bad\""
                "BlobPath", "\"\""
            ] do
            let node = JsonNode.Parse(serialize {| State = [| created |] |})
            node.["State"].[0].[field] <- JsonNode.Parse value
            use json = JsonDocument.Parse(node.ToJsonString())

            Assert.Throws<InvalidDataException>(
                Action (fun () ->
                    decodeArtifactSizeDocument json.RootElement
                    |> ignore)
            )
            |> ignore

    /// Rejects inconsistent stream identity, scope, immutable path/time and event ordering before publishing any sum.
    [<TestCase("artifact")>]
    [<TestCase("owner")>]
    [<TestCase("organization")>]
    [<TestCase("repository")>]
    [<TestCase("path")>]
    [<TestCase("time")>]
    [<TestCase("event")>]
    [<TestCase("second-created")>]
    [<TestCase("no-created")>]
    member _.``stream conflicts yield no quantity``(kind: string) =
        let next = { created with Event = ArtifactEventNames.LogicalDeleted }

        let changed =
            match kind with
            | "artifact" -> { next with ArtifactId = Guid.NewGuid() }
            | "owner" -> { next with OwnerId = Guid.NewGuid() }
            | "organization" -> { next with OrganizationId = Guid.NewGuid() }
            | "repository" -> { next with RepositoryId = Guid.NewGuid() }
            | "path" -> { next with BlobPath = "other" }
            | "time" -> { next with CreatedAtUnixTimeTicks = next.CreatedAtUnixTimeTicks + 1L }
            | "event" -> { next with Event = "unsupported" }
            | "second-created" -> created
            | _ -> next

        let events = if kind = "no-created" then [| changed |] else [| created; changed |]
        failure typeof<InvalidDataException> (fun () -> enumerate [| [| row events |] |])

    /// Classifies timestamps outside the existing Instant range as malformed source rather than provider unavailability.
    [<Test>]
    member _.``out of range snapshot creation time is invalid source``() =
        failure typeof<InvalidDataException> (fun () ->
            enumerate [| [|
                             row [| { created with CreatedAtUnixTimeTicks = Int64.MaxValue } |]
                         |] |])

    /// Conflicting repeated declarations and overflow cannot publish a partial sum.
    [<TestCase("size")>]
    [<TestCase("path")>]
    [<TestCase("time")>]
    [<TestCase("overflow")>]
    member _.``duplicate conflicts and checked overflow fail``(kind: string) =
        let first, second =
            match kind with
            | "size" -> created, { created with Size = 14L }
            | "path" -> created, { created with BlobPath = "other" }
            | "time" -> created, { created with CreatedAtUnixTimeTicks = created.CreatedAtUnixTimeTicks + 1L }
            | _ -> { created with Size = Int64.MaxValue }, { created with ArtifactId = Guid.NewGuid(); Size = 1L }

        failure
            (if kind = "overflow" then
                 typeof<OverflowException>
             else
                 typeof<InvalidDataException>)
            (fun () -> enumerate [| [| row [| first |]; row [| second |] |] |])

    /// Complete traversal succeeds both at and beyond each former cap without truncating the projected quantity.
    [<TestCase("pages", false)>]
    [<TestCase("pages", true)>]
    [<TestCase("documents", false)>]
    [<TestCase("documents", true)>]
    [<TestCase("events", false)>]
    [<TestCase("events", true)>]
    member _.``enumeration completes beyond former bounds``(kind: string, exceeded: bool) =
        task {
            let extra = if exceeded then 1 else 0
            let finalArtifact = { created with ArtifactId = Guid.NewGuid() }

            let pages =
                match kind with
                | "pages" -> Array.append (Array.create (31 + extra) [| [| created |] |]) [| [| [| finalArtifact |] |] |]
                | "documents" ->
                    [|
                        Array.append (Array.create (9999 + extra) [| created |]) [| [| finalArtifact |] |]
                    |]
                | _ ->
                    [|
                        [|
                            Array.concat [| [| created |]
                                            Array.create (99998 + extra) { created with Event = ArtifactEventNames.LogicalDeleted }
                                            [|
                                                { created with Event = ArtifactEventNames.Undeleted; Size = 14L }
                                            |] |]
                        |]
                    |]

            let! result = enumerate pages
            Assert.That(result.DistinctArtifactCount, Is.EqualTo(if kind = "events" then 1L else 2L))
            Assert.That(result.DeclaredArtifactBytes, Is.EqualTo(if kind = "events" then 14L else 26L))
        }

    /// Unsupported providers fail before any Cosmos container is requested.
    [<TestCase("MongoDB")>]
    [<TestCase("Unknown")>]
    member _.``unsupported provider never acquires Cosmos``(name: string) =
        task {
            let provider = if name = "MongoDB" then MongoDB else Unknown
            let mutable acquired = false

            do!
                failure typeof<NotSupportedException> (fun () ->
                    readArtifactSizeWith
                        provider
                        (fun () ->
                            acquired <- true
                            Unchecked.defaultof<Microsoft.Azure.Cosmos.Container>)
                        scope
                        CancellationToken.None)

            Assert.That(acquired, Is.False)
        }

    /// Caller cancellation prevents even acquiring the selected Cosmos source.
    [<Test>]
    member _.``cancelled provider read never acquires Cosmos``() =
        task {
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()
            let mutable acquired = false

            do!
                failure typeof<OperationCanceledException> (fun () ->
                    readArtifactSizeWith
                        AzureCosmosDb
                        (fun () ->
                            acquired <- true
                            Unchecked.defaultof<Microsoft.Azure.Cosmos.Container>)
                        scope
                        cancellation.Token)

            Assert.That(acquired, Is.False)
        }

    /// Discards quantities on provider faults and cancellation before, during or after page consumption.
    [<TestCase("before")>]
    [<TestCase("after-page")>]
    [<TestCase("stalled")>]
    [<TestCase("source")>]
    member _.``source failures and cancellation never return partial results``(kind: string) =
        task {
            use token = new CancellationTokenSource()
            if kind = "before" then token.Cancel()
            if kind = "stalled" then token.CancelAfter(20)

            /// Injects failure or cancellation at the selected page boundary.
            let readPage (ct: CancellationToken) =
                task {
                    if kind = "stalled" then do! Task.Delay(Timeout.Infinite, ct)
                    if kind = "source" then raise (IOException "removed source")
                    if kind = "after-page" then token.Cancel()
                    return [| row [| created |] |], false
                }

            return!
                failure
                    (if kind = "source" then
                         typeof<IOException>
                     else
                         typeof<OperationCanceledException>)
                    (fun () -> enumerateArtifactSizeWith readPage scope token.Token)
        }

    /// Verifies both repository checks and suppresses an already collected quantity when final scope or cancellation changes.
    [<TestCase("complete")>]
    [<TestCase("before")>]
    [<TestCase("after")>]
    [<TestCase("cancel")>]
    member _.``repository is revalidated before publication``(kind: string) =
        task {
            use token = new CancellationTokenSource()
            let calls = ResizeArray<string>()

            /// Changes repository availability at the selected pre/post read boundary.
            let check _ =
                task {
                    calls.Add "check"

                    if kind = "before"
                       || (kind = "after" && calls.Count = 3) then
                        raise (InvalidDataException "scope changed")

                    if kind = "cancel" && calls.Count = 3 then token.Cancel()
                }

            /// Marks collection between the two repository checks.
            let collect _ =
                task {
                    calls.Add "collect"
                    return! enumerate [| [| row [| created |] |] |]
                }

            if kind = "complete" then
                let! result = diagnoseArtifactSizeWith check collect token.Token
                Assert.That(result.DeclaredArtifactBytes, Is.EqualTo 13L)
            else
                do!
                    failure
                        (if kind = "cancel" then
                             typeof<OperationCanceledException>
                         else
                             typeof<InvalidDataException>)
                        (fun () -> diagnoseArtifactSizeWith check collect token.Token)

            Assert.That(calls.ToArray(), Is.EqualTo(box (if kind = "before" then [| "check" |] else [| "check"; "collect"; "check" |])))
        }

    /// Reuses explicit three-ID selectors and rejects every name selector and empty identifier.
    [<Test>]
    member _.``request scope requires three explicit IDs``() =
        let parameters =
            GetRepositoryParameters(OwnerId = string scope.OwnerId, OrganizationId = string scope.OrganizationId, RepositoryId = string scope.RepositoryId)

        Assert.That(validateArtifactSizeDiagnosticParameters parameters = Ok scope, Is.True)
        parameters.OwnerName <- "name"
        Assert.That(Result.isError (validateArtifactSizeDiagnosticParameters parameters), Is.True)
        parameters.OwnerName <- ""
        parameters.OrganizationName <- "name"
        Assert.That(Result.isError (validateArtifactSizeDiagnosticParameters parameters), Is.True)
        parameters.OrganizationName <- ""
        parameters.RepositoryName <- "name"
        Assert.That(Result.isError (validateArtifactSizeDiagnosticParameters parameters), Is.True)
        parameters.RepositoryName <- ""
        parameters.RepositoryId <- string Guid.Empty
        Assert.That(Result.isError (validateArtifactSizeDiagnosticParameters parameters), Is.True)
