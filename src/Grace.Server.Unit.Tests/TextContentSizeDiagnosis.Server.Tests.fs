namespace Grace.Server.Unit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Grace.Server
open Grace.Server.TextContentSizeDiagnosis
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.TextContent
open Grace.Types.Usage
open Grace.Types.WorkItem
open NUnit.Framework

/// Exercises retained-event declaration meaning and bounded failures without a provider or HTTP host.
[<Parallelizable(ParallelScope.All)>]
type TextContentSizeDiagnosisTests() =
    let scope: UsageFactScope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = Guid.NewGuid() }
    let workItemId = Guid.NewGuid()

    /// Creates immutable references with the actual current TextContent producer helper.
    let description correlation text = TextContentStorage.createDescription scope.RepositoryId workItemId correlation text

    /// Uses the current event and metadata records for every retained source fixture.
    let event value : WorkItemEvent =
        {
            Event = value
            Metadata =
                {
                    Timestamp = getCurrentInstant ()
                    CorrelationId = "text-size-test"
                    Principal = "test"
                    ClientType = None
                    Properties = Dictionary<string, string>()
                }
        }

    /// Binds Created to the requested repository while allowing its supported null description.
    let created description = event (Created(workItemId, 1L, scope.OwnerId, scope.OrganizationId, scope.RepositoryId, "fixture", description))

    /// Round-trips a complete persisted query row through the production decoder.
    let row events =
        use document = JsonDocument.Parse(serialize {| State = events |})
        decodeDocument document.RootElement

    /// Supplies finite pages to the real aggregation boundary with a fresh attempt for each call.
    let enumerate (pages: WorkItemEvent array array array) =
        let mutable index = 0

        enumerateWith
            (fun _ ->
                let page = pages[index]
                index <- index + 1
                Task.FromResult(page, index < pages.Length))
            scope
            CancellationToken.None

    /// Requires the intended failure type rather than accepting an unrelated exception as proof of no quantity.
    let expectFailure (expected: Type) (operation: unit -> Task<TextContentSizeDiagnostic>) =
        task {
            let! outcome =
                task {
                    try
                        let! result = operation ()
                        return Choice1Of2 result
                    with
                    | error -> return Choice2Of2 error
                }

            match outcome with
            | Choice1Of2 result -> Assert.Fail($"Unexpected successful quantity: {result.DeclaredTextContentUtf8Bytes}.")
            | Choice2Of2 error -> Assert.That(expected.IsInstanceOfType error, Is.True, error.ToString())
        }

    /// Counts retained prior text after a clear, deduplicates repeated observations and keeps identical text at different IDs distinct.
    [<Test>]
    member _.``retained events count superseded and cleared text with identity deduplication``() =
        task {
            let first = description "first" "é🙂"
            let second = description "second" "four"
            let identicalText = description "other-operation" "é🙂"
            let cleared = { DescriptionId = Guid.NewGuid(); TextContent = None }

            let history =
                row [| created (Some first)
                       event (DescriptionSet second)
                       event (DescriptionCleared cleared) |]

            let current =
                history
                |> Array.fold (fun prior next -> WorkItemState.UpdateState next prior) WorkItemState.Default

            Assert.That(current.Description.Value.TextContent.IsNone, Is.True)

            let! result =
                enumerate [| [| history |]
                             [|
                                 history
                                 row [| created (Some identicalText) |]
                             |] |]

            Assert.That(result.DeclaredTextContentUtf8Bytes, Is.EqualTo 16L)
            Assert.That(result.DistinctTextContentCount, Is.EqualTo 3L)
            Assert.That(result.Scope, Is.EqualTo scope)
            Assert.That(result.EnumerationFinishedAt, Is.GreaterThanOrEqualTo result.EnumerationStartedAt)
        }

    /// Distinguishes exhausted empty storage and actual Created-with-null-description records from missing Created data.
    [<Test>]
    member _.``exhausted empty source and explicit null description produce known zero``() =
        task {
            let! empty = enumerate [| [||] |]
            let! noDescription = enumerate [| [| row [| created None |] |] |]
            Assert.That(empty.DeclaredTextContentUtf8Bytes, Is.Zero)
            Assert.That(empty.DistinctTextContentCount, Is.Zero)
            Assert.That(noDescription.DeclaredTextContentUtf8Bytes, Is.Zero)
            Assert.That(noDescription.DistinctTextContentCount, Is.Zero)
            do! expectFailure typeof<InvalidDataException> (fun () -> enumerate [| [| [||] |] |])
        }

    /// Rejects one immutable ID carrying conflicting size or hash declarations across pages.
    [<TestCase("length")>]
    [<TestCase("hash")>]
    member _.``conflicting retained identity returns no quantity``(field: string) =
        let first = description "identity" "é🙂"
        let original = first.TextContent.Value

        let changed =
            if field = "length" then
                { original with Utf8ByteLength = 7L }
            else
                { original with Blake3Hash = String.replicate 64 "a" }

        let second = { first with TextContent = Some changed }

        expectFailure typeof<InvalidDataException> (fun () ->
            enumerate [| [| row [| created (Some first) |] |]
                         [| row [| created (Some second) |] |] |])

    /// Requires all three Created scope identities to agree with the resolved repository authority.
    [<TestCase("owner")>]
    [<TestCase("organization")>]
    [<TestCase("repository")>]
    member _.``created scope mismatch returns no quantity``(field: string) =
        let other = Guid.NewGuid()

        let changed =
            event (
                Created(
                    workItemId,
                    1L,
                    (if field = "owner" then other else scope.OwnerId),
                    (if field = "organization" then other else scope.OrganizationId),
                    (if field = "repository" then other else scope.RepositoryId),
                    "foreign",
                    None
                )
            )

        expectFailure typeof<InvalidDataException> (fun () -> enumerate [| [| row [| changed |] |] |])

    /// Requires one leading Created event even when the remaining text references would otherwise be valid.
    [<TestCase("missing")>]
    [<TestCase("duplicate")>]
    [<TestCase("late")>]
    member _.``created authority must occur once at the beginning``(kind: string) =
        let set = event (DescriptionSet(description "set" "value"))

        let events =
            match kind with
            | "missing" -> [| set |]
            | "duplicate" -> [| created None; created None |]
            | _ -> [| set; created None |]

        expectFailure typeof<InvalidDataException> (fun () -> enumerate [| [| row events |] |])

    /// Rejects missing wire fields before defaults can erase a retained declaration.
    [<TestCase("description")>]
    [<TestCase("DescriptionId")>]
    [<TestCase("TextContent")>]
    [<TestCase("TextContentId")>]
    [<TestCase("Blake3Hash")>]
    [<TestCase("Utf8ByteLength")>]
    member _.``missing retained declaration field is not known zero``(field: string) =
        let node =
            JsonNode.Parse(
                serialize
                    {|
                        State =
                            [|
                                created (Some(description "required" "value"))
                            |]
                    |}
            )

        let state = node["State"]
        let first = state[0]
        let event = first["Event"]
        let created = event["created"]
        let description = created["description"]

        let target =
            match field with
            | "description" -> created
            | "DescriptionId"
            | "TextContent" -> description
            | _ -> description["TextContent"]

        Assert.That(target.AsObject().Remove field, Is.True)
        use changed = JsonDocument.Parse(node.ToJsonString())

        Assert.Throws<InvalidDataException>(Action(fun () -> decodeDocument changed.RootElement |> ignore))
        |> ignore

    /// Keeps malformed persisted identifiers classified as source failures rather than request-JSON failures.
    [<Test>]
    member _.``invalid persisted guid is a source decoding failure``() =
        let node = JsonNode.Parse(serialize {| State = [| created None |] |})
        let state = node["State"]
        let first = state[0]
        let event = first["Event"]
        let created = event["created"]
        created["workItemId"] <- JsonValue.Create "not-a-guid"
        use changed = JsonDocument.Parse(node.ToJsonString())

        Assert.Throws<InvalidDataException>(Action(fun () -> decodeDocument changed.RootElement |> ignore))
        |> ignore

    /// Validates the minimum declared facts required by the current immutable text producer.
    [<TestCase("zero")>]
    [<TestCase("negative")>]
    [<TestCase("empty-id")>]
    [<TestCase("invalid-hash")>]
    member _.``invalid content declaration returns no quantity``(kind: string) =
        let original = description "invalid" "value"
        let content = original.TextContent.Value

        let changed =
            match kind with
            | "zero" -> { content with Utf8ByteLength = 0L }
            | "negative" -> { content with Utf8ByteLength = -1L }
            | "empty-id" -> { content with TextContentId = Guid.Empty }
            | _ -> { content with Blake3Hash = "invalid" }

        expectFailure typeof<InvalidDataException> (fun () ->
            enumerate [| [|
                             row [| created (Some { original with TextContent = Some changed }) |]
                         |] |])

    /// Prevents a continuation at the page limit from masquerading as a completed sample.
    [<Test>]
    member _.``page cap aborts before a thirty third provider read``() =
        task {
            let mutable reads = 0

            do!
                expectFailure typeof<InvalidDataException> (fun () ->
                    enumerateWith
                        (fun _ ->
                            reads <- reads + 1
                            Task.FromResult([||], true))
                        scope
                        CancellationToken.None)

            Assert.That(reads, Is.EqualTo 32)
            let! retry = enumerate [| [||] |]
            Assert.That(retry.DeclaredTextContentUtf8Bytes, Is.Zero)
        }

    /// Applies the document cap to observed documents even when they contain no text.
    [<Test>]
    member _.``document cap rejects an oversized final page``() =
        expectFailure typeof<InvalidDataException> (fun () -> enumerate [| Array.create 10001 (row [| created None |]) |])

    /// Applies the reference cap before deduplication so repetition cannot evade bounded work.
    [<Test>]
    member _.``reference cap counts repeated declarations``() =
        let value = description "repeated" "x"
        let history = Array.append [| created (Some value) |] (Array.create 100000 (event (DescriptionSet value)))
        expectFailure typeof<InvalidDataException> (fun () -> enumerate [| [| history |] |])

    /// Discards earlier-page quantities when the next provider read fails.
    [<Test>]
    member _.``provider failure after a valid page returns no quantity``() =
        let mutable reads = 0

        expectFailure typeof<IOException> (fun () ->
            enumerateWith
                (fun _ ->
                    reads <- reads + 1

                    if reads = 1 then
                        Task.FromResult(
                            [|
                                row [| created (Some(description "first-page" "sixsix")) |]
                            |],
                            true
                        )
                    else
                        Task.FromException<WorkItemEvent array array * bool>(IOException "provider failed"))
                scope
                CancellationToken.None)

    /// Checks cancellation both before the first read and before a completed final page can be returned.
    [<Test>]
    member _.``cancelled attempt cannot publish even after a final page``() =
        task {
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()
            let mutable reads = 0

            do!
                expectFailure typeof<OperationCanceledException> (fun () ->
                    enumerateWith
                        (fun _ ->
                            reads <- reads + 1
                            Task.FromResult([||], false))
                        scope
                        cancellation.Token)

            Assert.That(reads, Is.Zero)
            use duringRead = new CancellationTokenSource()

            do!
                expectFailure typeof<OperationCanceledException> (fun () ->
                    enumerateWith
                        (fun _ ->
                            duringRead.Cancel()
                            Task.FromResult([||], false))
                        scope
                        duringRead.Token)
        }

    /// Exercises checked accumulation independently of realistic producer size limits.
    [<Test>]
    member _.``sum overflow cannot wrap into a quantity``() =
        let original = description "large" "x"
        let large = { original with TextContent = Some { original.TextContent.Value with Utf8ByteLength = Int64.MaxValue } }

        expectFailure typeof<OverflowException> (fun () ->
            enumerate [| [|
                             [|
                                 created (Some large)
                                 event (DescriptionSet(description "one-more" "x"))
                             |]
                         |] |])

    /// Verifies the existing repository parameter shape accepts only explicit complete identifiers.
    [<Test>]
    member _.``scope parameters reject names and empty identifiers``() =
        let parameters =
            GetRepositoryParameters(OwnerId = string scope.OwnerId, OrganizationId = string scope.OrganizationId, RepositoryId = string scope.RepositoryId)

        match validateParameters parameters with
        | Ok actual -> Assert.That(actual, Is.EqualTo scope)
        | Error message -> Assert.Fail message

        parameters.RepositoryName <- "ignored-name"
        Assert.That(validateParameters parameters |> Result.isError, Is.True)
        parameters.RepositoryName <- ""
        parameters.OwnerId <- string Guid.Empty
        Assert.That(validateParameters parameters |> Result.isError, Is.True)

    /// Ensures zero is explicit on the successful wire and the field names cannot be confused with DirectoryVersion bytes.
    [<Test>]
    member _.``success wire uses explicit TextContent quantities including zero``() =
        task {
            let! result = enumerate [| [||] |]
            use wire = JsonDocument.Parse(serialize (GraceReturnValue.Create result "wire"))
            let value = wire.RootElement.GetProperty "ReturnValue"

            Assert.That(
                value
                    .GetProperty("DeclaredTextContentUtf8Bytes")
                    .GetInt64(),
                Is.Zero
            )

            Assert.That(
                value
                    .GetProperty("DistinctTextContentCount")
                    .GetInt64(),
                Is.Zero
            )

            let mutable other = Unchecked.defaultof<JsonElement>
            Assert.That(value.TryGetProperty("DeclaredLogicalBytes", &other), Is.False)
        }
