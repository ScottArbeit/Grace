namespace Grace.Server.Unit.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Grace.Server.TextContentSizeObservation
open Grace.Types.UsageObservation
open NodaTime
open NUnit.Framework
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

/// Checks source and repository effect ordering without replacing SQL concurrency evidence with mocks.
type TextContentSizeObservationTests() =
    let reading =
        {
            ObservationId = Guid.NewGuid()
            Scope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = Guid.NewGuid() }
            DeclaredTextContentUtf8Bytes = 37L
            DistinctTextContentCount = 2L
            EnumerationStartedAt = Instant.FromUtc(2026, 9, 7, 0, 0)
            EnumerationFinishedAt = Instant.FromUtc(2026, 9, 7, 0, 1)
        }

    /// Missing SQL is a request-time unavailable response, while malformed scope still fails before SQL access.
    [<TestCase(true, false, 503)>]
    [<TestCase(false, false, 503)>]
    [<TestCase(true, true, 400)>]
    member _.``handler reports unavailable configuration without observation or rollback promise``(capture: bool, invalid: bool, status: int) =
        task {
            let context = DefaultHttpContext()
            context.Request.ContentType <- "application/json"
            let scope = reading.Scope

            let body =
                if invalid then
                    "null"
                else
                    $"{{\"OwnerId\":\"{scope.OwnerId}\",\"OrganizationId\":\"{scope.OrganizationId}\",\"RepositoryId\":\"{scope.RepositoryId}\"}}"

            context.Request.Body <- new MemoryStream(Text.Encoding.UTF8.GetBytes body)
            context.Request.QueryString <- QueryString($"?OwnerId={scope.OwnerId}&OrganizationId={scope.OrganizationId}&RepositoryId={scope.RepositoryId}")
            context.Response.Body <- new MemoryStream()

            use services =
                ServiceCollection()
                    .AddGiraffe()
                    .AddSingleton<Json.ISerializer>(Json.Serializer(Grace.Shared.Constants.JsonSerializerOptions))
                    .AddSingleton<IConfiguration>(ConfigurationBuilder().Build())
                    .BuildServiceProvider()

            context.RequestServices <- services
            /// Completes the Giraffe handler without installing a server host.
            let next: HttpFunc = fun value -> Task.FromResult(Some value)

            let handler =
                if capture then
                    Capture(string reading.ObservationId)
                else
                    Read(string reading.ObservationId)

            let! _ = handler next context
            Assert.That(context.Response.StatusCode, Is.EqualTo status)
            context.Response.Body.Position <- 0L
            use reader = new StreamReader(context.Response.Body)
            let! response = reader.ReadToEndAsync()
            Assert.That(response, Does.Not.Contain "DeclaredTextContentUtf8Bytes")
            if status = 503 then Assert.That(response, Does.Contain "uncertain commit")
        }

    /// Uses an existing observation even when the current repository and source are unavailable or deleted.
    [<Test>]
    member _.``stored retry and scope conflict never access source``() =
        task {
            let calls = ResizeArray<string>()

            /// Records forbidden repository access on a stored retry.
            let check _ =
                calls.Add "repository"
                Task.FromResult(())

            /// Records forbidden source enumeration on a stored retry.
            let collect _ =
                calls.Add "source"
                Task.FromResult reading

            /// Records forbidden SQL acceptance on a stored retry.
            let accept _ _ =
                calls.Add "accept"
                Task.FromResult(Ok reading)

            let! existing = captureWith (fun _ -> Task.FromResult(Ok(Some reading))) check collect accept CancellationToken.None
            Assert.That((existing = Ok reading), Is.True)
            let! conflict = captureWith (fun _ -> Task.FromResult(Error "conflict")) check collect accept CancellationToken.None
            Assert.That((conflict = Error "conflict"), Is.True)
            Assert.That(calls, Is.Empty)
        }

    /// Requires both repository checks before accepting and always returns the SQL winner instead of the local reading.
    [<Test>]
    member _.``new capture checks scope around source and returns accepted winner``() =
        task {
            let effects = ResizeArray<string>()
            let winner = { reading with DeclaredTextContentUtf8Bytes = 1L; EnumerationFinishedAt = reading.EnumerationStartedAt }

            let! result =
                captureWith
                    (fun _ ->
                        effects.Add "lookup"
                        Task.FromResult(Ok None))
                    (fun _ ->
                        effects.Add "repository"
                        Task.FromResult(()))
                    (fun _ ->
                        effects.Add "source"
                        Task.FromResult reading)
                    (fun candidate _ ->
                        effects.Add "accept"
                        Assert.That(candidate, Is.EqualTo reading)
                        Task.FromResult(Ok winner))
                    CancellationToken.None

            Assert.That((result = Ok winner), Is.True)

            Assert.That(
                effects,
                Is.EqualTo(
                    box [ "lookup"
                          "repository"
                          "source"
                          "repository"
                          "accept" ]
                )
            )
        }

    /// A failed lookup, repository check or collection prevents acceptance and never manufactures zero.
    [<TestCase("lookup")>]
    [<TestCase("before-source")>]
    [<TestCase("source")>]
    [<TestCase("after-source")>]
    member _.``failure before acceptance leaves no accepted reading``(failure: string) =
        task {
            let mutable checks = 0
            let mutable accepted = false
            let mutable threw = false

            try
                let! _ =
                    captureWith
                        (fun _ ->
                            if failure = "lookup" then
                                raise (InvalidOperationException "SQL unavailable")
                            else
                                Task.FromResult(Ok None))
                        (fun _ ->
                            checks <- checks + 1

                            if (failure = "before-source" && checks = 1)
                               || (failure = "after-source" && checks = 2) then
                                raise (InvalidDataException "repository deleted")

                            Task.FromResult(()))
                        (fun _ ->
                            if failure = "source" then
                                raise (InvalidDataException "incomplete source")
                            else
                                Task.FromResult reading)
                        (fun _ _ ->
                            accepted <- true
                            Task.FromResult(Ok reading))
                        CancellationToken.None

                ()
            with
            | _ -> threw <- true

            Assert.That(threw, Is.True)
            Assert.That(accepted, Is.False)
        }

    /// Cancellation before access or after enumeration prevents the next effect and propagates the request token.
    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.``cancellation prevents acceptance``(beforeLookup: bool) =
        task {
            use cancellation = new CancellationTokenSource()
            if beforeLookup then cancellation.Cancel()
            let mutable accepted = false
            let mutable lookups = 0
            let mutable cancelled = false

            try
                let! _ =
                    captureWith
                        (fun token ->
                            Assert.That(token, Is.EqualTo cancellation.Token)
                            lookups <- lookups + 1
                            Task.FromResult(Ok None))
                        (fun _ -> Task.FromResult(()))
                        (fun _ ->
                            cancellation.Cancel()
                            Task.FromResult reading)
                        (fun _ _ ->
                            accepted <- true
                            Task.FromResult(Ok reading))
                        cancellation.Token

                ()
            with
            | :? OperationCanceledException -> cancelled <- true

            Assert.That(cancelled, Is.True)
            Assert.That(accepted, Is.False)
            Assert.That(lookups, Is.EqualTo(if beforeLookup then 0 else 1))
        }
