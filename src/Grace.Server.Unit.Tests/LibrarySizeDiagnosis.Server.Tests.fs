namespace Grace.Server.Unit.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Grace.Actors
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Library
open Grace.Server.Library
open NodaTime
open NUnit.Framework
open Giraffe
open Grace.Server.Security
open Grace.Types.Authorization
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System.Security.Claims
open System.Text.Json

/// Uses the nondeprecated NUnit task assertion overload.
module private LibraryDiagnosticAssertions =
    /// Requires a specific failure before a task can publish its result.
    let throws<'T when 'T :> exn> (action: unit -> Task) = Assert.ThrowsAsync<'T>(Func<Task>(action))

/// Exercises exact historical membership and publication guards without an external service substitute.
type LibrarySizeDiagnosisTests() =
    let now = Instant.FromUtc(2026, 9, 9, 0, 0)
    let repository = Guid.NewGuid()

    /// Builds a valid declaration of arbitrary length without pretending to possess that many payload bytes.
    let location (label: string) size =
        let hash = ContentAddress.computeBlake3Hex (Text.Encoding.UTF8.GetBytes label)
        let blocks = [ ContentBlock.Create(hash, 0L, size) ]
        let address = ContentAddress.computeManifestAddress "test" hash size blocks

        {
            SchemaVersion = 1
            AuthorizedScope = "Library/test"
            Content = { ContentVersionId = LibraryDecision.contentVersionId hash; Blake3Hash = hash; Sha256Hash = hash; Size = size; CreatedAt = now }
            Manifest = FileManifest.Create(address, "test", hash, size, "default", blocks)
        }

    /// Supplies a typed accepted record with the declaration owned by the selected operation.
    let change cursor (content: LibraryContentVersionDto option) =
        let item: LibraryItemDto =
            {
                ItemId = Guid.NewGuid()
                ItemKind = "file"
                LastChangeCursor = "signed"
                Namespace = None
                Content = content
                ContentRevision = Some "signed"
                Tombstone = None
            }

        {
            SchemaVersion = 1
            Cursor = cursor
            RequestHash = "test"
            CorrelationId = "test"
            Change =
                {
                    OperationId = Guid.NewGuid()
                    ChangeKind = "updateContent"
                    AcceptedAt = now
                    AcceptedBy = "test"
                    LibraryCatalogVersion = Guid.NewGuid()
                    Item = item
                    Conflict = None
                }
            PriorNamespace = None
            PriorContentVersionId = None
            ConsumedNamespaceVersion = None
            ConsumedContentVersionId = None
            ConsumedContentRevision = None
            ConsumedSlotVersion = None
            AddedItemRecord = false
            AddedSlotRecord = false
        }

    /// Delivers independently supplied pages and immutable point reads through the production accumulator.
    let collect boundary rows locations token =
        LibraryQueries.enumerateDiagnosticContentWith
            boundary
            (fun position _ ->
                Task.FromResult(
                    rows
                    |> Array.filter (fun row -> row.Cursor > position)
                    |> Array.truncate 2
                ))
            (fun id _ ->
                Task.FromResult(
                    locations
                    |> List.tryFind (fun location -> location.Content.ContentVersionId = id)
                ))
            token

    /// Verifies historical deduplication and distinct immutable identities at the production boundary.
    [<Test>]
    member _.``history counts superseded and repeated declarations once``() =
        task {
            let a, b = location "A" 10L, location "B" 20L

            let rows =
                [|
                    change 1L (Some a.Content)
                    change 2L (Some b.Content)
                    change 3L (Some b.Content)
                    change 4L (Some a.Content)
                |]

            let! bytes, count = collect 4L rows [ a; b ] CancellationToken.None
            Assert.That(bytes, Is.EqualTo 30L)
            Assert.That(count, Is.EqualTo 2L)
        }

    /// Makes gaps and absent mappings distinguishable from successful empty history.
    [<Test>]
    member _.``missing prefix or immutable mapping never returns a partial total``() =
        let a = location "A" 10L

        LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () ->
            collect
                3L
                [|
                    change 1L (Some a.Content)
                    change 3L (Some a.Content)
                |]
                [ a ]
                CancellationToken.None
            :> Task)
        |> ignore

        LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () -> collect 1L [| change 1L (Some a.Content) |] [] CancellationToken.None :> Task)
        |> ignore

        LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () -> collect 2L [| change 1L (Some a.Content) |] [ a ] CancellationToken.None :> Task)
        |> ignore

    /// Separates a complete empty prefix from missing file declarations, over-boundary rows and provider errors.
    [<Test>]
    member _.``empty prefix is zero while malformed rows and provider failure have no quantity``() =
        task {
            let! bytes, count =
                LibraryQueries.enumerateDiagnosticContentWith
                    0L
                    (fun _ _ -> failwith "zero must not query history")
                    (fun _ _ -> failwith "zero must not query content")
                    CancellationToken.None

            Assert.That(bytes, Is.Zero)
            Assert.That(count, Is.Zero)
            let a = location "A" 10L

            LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () -> collect 1L [| change 1L None |] [] CancellationToken.None :> Task)
            |> ignore

            LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () ->
                collect
                    1L
                    [|
                        change 1L (Some a.Content)
                        change 2L (Some a.Content)
                    |]
                    [ a ]
                    CancellationToken.None
                :> Task)
            |> ignore

            LibraryDiagnosticAssertions.throws<IOException> (fun () ->
                LibraryQueries.enumerateDiagnosticContentWith
                    1L
                    (fun _ _ -> Task.FromException<LibraryAcceptedChangeRecord array>(IOException "provider unavailable"))
                    (fun _ _ -> Task.FromResult None)
                    CancellationToken.None
                :> Task)
            |> ignore
        }

    /// Requires descriptor agreement, complete manifest ranges and reconstructed manifest identity.
    [<Test>]
    member _.``conflicting descriptor and manifest contracts fail``() =
        let a = location "A" 10L

        let invalid =
            [
                { a with Content = { a.Content with Size = 11L } }
                { a with Manifest = { a.Manifest with ManifestAddress = String.replicate 64 "0" } }
                { a with Manifest = { a.Manifest with Size = 11L } }
                { a with
                    Manifest =
                        { a.Manifest with
                            Blocks =
                                Collections.Generic.List<ContentBlock>(
                                    [
                                        ContentBlock.Create(a.Content.Blake3Hash, 1L, 10L)
                                    ]
                                )
                        }
                }
            ]

        for value in invalid do
            Assert.Throws<InvalidDataException>(
                Action (fun () ->
                    LibraryQueries.validateDiagnosticLocation a.Content value
                    |> ignore)
            )
            |> ignore

    /// Preserves exact Int64 arithmetic and rejects overflow before a successful quantity escapes.
    [<Test>]
    member _.``large declarations remain exact and checked sum overflow fails``() =
        task {
            let a = location "A" Int64.MaxValue
            let! bytes, count = collect 1L [| change 1L (Some a.Content) |] [ a ] CancellationToken.None
            Assert.That(bytes, Is.EqualTo Int64.MaxValue)
            Assert.That(count, Is.EqualTo 1L)
            let b = location "B" 1L

            LibraryDiagnosticAssertions.throws<OverflowException> (fun () ->
                collect
                    2L
                    [|
                        change 1L (Some a.Content)
                        change 2L (Some b.Content)
                    |]
                    [ a; b ]
                    CancellationToken.None
                :> Task)
            |> ignore
        }

    /// Cancels after the first accumulated mapping and starts a later attempt from the beginning.
    [<Test>]
    member _.``partial cancellation publishes nothing and a fresh retry rereads``() =
        task {
            let a, b = location "A" 10L, location "B" 20L

            let rows =
                [|
                    change 1L (Some a.Content)
                    change 2L (Some b.Content)
                |]

            use cancellation = new CancellationTokenSource()
            let mutable reads = 0

            LibraryDiagnosticAssertions.throws<OperationCanceledException> (fun () ->
                LibraryQueries.enumerateDiagnosticContentWith
                    2L
                    (fun _ _ -> Task.FromResult rows)
                    (fun _ _ ->
                        reads <- reads + 1
                        cancellation.Cancel()
                        Task.FromResult(Some a))
                    cancellation.Token
                :> Task)
            |> ignore

            Assert.That(reads, Is.EqualTo 1)
            let! bytes, _ = collect 2L rows [ a; b ] CancellationToken.None
            Assert.That(bytes, Is.EqualTo 30L)

            LibraryDiagnosticAssertions.throws<OperationCanceledException> (fun () -> collect 2L rows [ a; b ] cancellation.Token :> Task)
            |> ignore
        }

    /// Exercises the final repository and epoch checks after a complete candidate has already accumulated.
    [<Test>]
    member _.``publication rejects scope loss epoch change and cursor regression while allowing tail advancement``() =
        task {
            let epoch = Guid.NewGuid()

            let result =
                {
                    Scope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = repository }
                    DeclaredLogicalBytes = 30L
                    DistinctManifestCount = 2L
                    Epoch = epoch
                    CommittedCursor = 205L
                    EnumerationStartedAt = now
                    EnumerationFinishedAt = now
                }

            let run check boundary = diagnoseContentSizeWith check (fun _ -> Task.FromResult result) (fun _ -> Task.FromResult boundary) CancellationToken.None

            LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () -> run (fun _ -> Task.FromResult(())) (Guid.NewGuid(), 205L) :> Task)
            |> ignore

            LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () -> run (fun _ -> Task.FromResult(())) (epoch, 204L) :> Task)
            |> ignore

            let mutable checks = 0

            LibraryDiagnosticAssertions.throws<InvalidDataException> (fun () ->
                run
                    (fun _ ->
                        checks <- checks + 1

                        if checks = 2 then
                            Task.FromException<unit>(InvalidDataException "scope lost")
                        else
                            Task.FromResult(()))
                    (epoch, 205L)
                :> Task)
            |> ignore

            Assert.That(checks, Is.EqualTo 2)
            let! accepted = run (fun _ -> Task.FromResult(())) (epoch, 206L)
            Assert.That(accepted.CommittedCursor, Is.EqualTo 205L)
            Assert.That(accepted.DeclaredLogicalBytes, Is.EqualTo 30L)
        }

    /// Exercises the actual final permission handler and serializer after a completed diagnostic has accumulated.
    [<Test>]
    member _.``final permission revocation and cancellation prevent disclosure and authorized JSON stays exact``() =
        task {
            let previousEnvironment = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment)
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")
            let previousLogger = Grace.Server.ApplicationContext.loggerFactory
            Grace.Server.ApplicationContext.loggerFactory <- Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance

            try
                use activity = new Diagnostics.Activity("library-size-handler")
                activity.Start() |> ignore
                let principal = { PrincipalType = PrincipalType.User; PrincipalId = "library-diagnostic-test" }
                let mutable revoked = false

                let evaluator =
                    GracePermissionEvaluator(
                        (fun (scope, _) ->
                            Task.FromResult(
                                if scope = Scope.System && not revoked then
                                    [
                                        { Principal = principal; Scope = scope; RoleId = "SystemAdmin"; Source = "test"; SourceDetail = None; CreatedAt = now }
                                    ]
                                else
                                    []
                            )),
                        (fun _ -> Task.FromResult [])
                    )
                    :> IGracePermissionEvaluator

                use services =
                    ServiceCollection()
                        .AddGiraffe()
                        .AddSingleton<Json.ISerializer>(Json.Serializer(Constants.JsonSerializerOptions))
                        .AddSingleton<IGracePermissionEvaluator>(evaluator)
                        .BuildServiceProvider()

                let result =
                    {
                        Scope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = repository }
                        DeclaredLogicalBytes = 9007199254740993L
                        DistinctManifestCount = 2L
                        Epoch = Guid.NewGuid()
                        CommittedCursor = Int64.MaxValue
                        EnumerationStartedAt =
                            NodaTime
                                .Text
                                .InstantPattern
                                .ExtendedIso
                                .Parse(
                                    "2026-09-09T00:00:00.123456789Z"
                                )
                                .Value
                        EnumerationFinishedAt =
                            NodaTime
                                .Text
                                .InstantPattern
                                .ExtendedIso
                                .Parse(
                                    "2026-09-09T00:00:00.123456790Z"
                                )
                                .Value
                    }

                let invoke token =
                    task {
                        let context = DefaultHttpContext()
                        context.Items[ Constants.CorrelationId ] <- "library-size-handler-test"
                        context.RequestServices <- services
                        context.RequestAborted <- token

                        context.User <-
                            ClaimsPrincipal(
                                ClaimsIdentity(
                                    [
                                        Claim(PrincipalMapper.GraceUserIdClaim, principal.PrincipalId)
                                    ],
                                    "test"
                                )
                            )

                        use body = new MemoryStream()
                        context.Response.Body <- body
                        let! _ = publishContentSize result (fun context -> Task.FromResult(Some context)) context
                        return context.Response.StatusCode, Text.Encoding.UTF8.GetString(body.ToArray())
                    }

                let! status, body = invoke CancellationToken.None
                Assert.That(status, Is.EqualTo 200)
                use json = JsonDocument.Parse body
                let value = json.RootElement.GetProperty("ReturnValue")

                Assert.That(
                    value
                        .GetProperty("DeclaredLogicalBytes")
                        .GetInt64(),
                    Is.EqualTo 9007199254740993L
                )

                Assert.That(value.GetProperty("CommittedCursor").GetInt64(), Is.EqualTo Int64.MaxValue)

                Assert.That(
                    value
                        .GetProperty("EnumerationStartedAt")
                        .GetString(),
                    Is.EqualTo "2026-09-09T00:00:00.123456789Z"
                )

                TestContext.Progress.WriteLine(
                    "LIBRARY-SIZE-HANDLER-JSON-SYNTHETIC-INT64 "
                    + body
                )

                revoked <- true
                let! denied, deniedBody = invoke CancellationToken.None
                Assert.That(denied, Is.EqualTo 403)
                Assert.That(deniedBody, Does.Not.Contain "DeclaredLogicalBytes")
                revoked <- false
                use cancellation = new CancellationTokenSource()
                cancellation.Cancel()

                LibraryDiagnosticAssertions.throws<OperationCanceledException> (fun () -> invoke cancellation.Token :> Task)
                |> ignore
            finally
                Grace.Server.ApplicationContext.loggerFactory <- previousLogger
                Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, previousEnvironment)
        }
