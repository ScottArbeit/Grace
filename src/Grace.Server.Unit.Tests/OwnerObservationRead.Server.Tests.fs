namespace Grace.Server.Unit.Tests

open System
open System.IO
open System.Security.Claims
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Giraffe
open Grace.Server
open Grace.Server.Security
open Grace.Shared
open Grace.Types.Authorization
open Grace.Types.UsageObservation
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open NodaTime.Text
open NUnit.Framework

/// Exercises the owner read's real permission evaluator and Giraffe handler without hosting or SQL.
type OwnerObservationReadTests() =
    let originalEnvironment = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment)
    let mutable originalLogger: Microsoft.Extensions.Logging.ILoggerFactory = null

    let reading: DirectoryVersionSizeObservation =
        {
            ObservationId = Guid.NewGuid()
            Scope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = Guid.NewGuid() }
            DeclaredLogicalBytes = 9007199254740993L
            DistinctContentCount = Int64.MaxValue
            EnumerationStartedAt =
                InstantPattern
                    .ExtendedIso
                    .Parse(
                        "2026-09-07T01:02:03.123456789Z"
                    )
                    .Value
            EnumerationFinishedAt =
                InstantPattern
                    .ExtendedIso
                    .Parse(
                        "2026-09-07T01:02:04.987654321Z"
                    )
                    .Value
        }

    let principal = { PrincipalType = PrincipalType.User; PrincipalId = "owner-observation-test" }

    /// Executes authentication, explicit parsing, permission checks and local HTTP serialization.
    let invoke authenticated role query id scenario =
        task {
            use activity = new System.Diagnostics.Activity("owner-observation-test")
            activity.Start() |> ignore
            let mutable revoked = false
            let mutable sqlCalls = 0
            let mutable evaluations = 0
            let context = DefaultHttpContext()
            use cancellation = new CancellationTokenSource()
            context.RequestAborted <- cancellation.Token
            context.Request.QueryString <- QueryString query

            context.Response.Body <-
                if scenario = "publication-failure" then
                    new MemoryStream(Array.empty<byte>, false)
                else
                    new MemoryStream()

            if authenticated then
                context.User <-
                    ClaimsPrincipal(
                        ClaimsIdentity(
                            [
                                Claim(PrincipalMapper.GraceUserIdClaim, principal.PrincipalId)
                            ],
                            "test"
                        )
                    )

            /// Supplies fresh assignments to the production evaluator on each check.
            let assignments (scope, _) =
                task {
                    if scope = Scope.System then evaluations <- evaluations + 1

                    if scenario = "before-lookup-cancel"
                       || (scenario = "pre-publication-cancel"
                           && sqlCalls > 0) then
                        cancellation.Cancel()

                    if scenario = "first-evaluator-failure"
                       || (scenario = "second-evaluator-failure"
                           && sqlCalls > 0) then
                        failwith "private evaluator details"

                    let roleScope =
                        if role = "SystemAdmin" || role = "SystemOperator" then
                            Scope.System
                        elif role = "RepositoryAdmin" then
                            Scope.Repository(reading.Scope.OwnerId, reading.Scope.OrganizationId, reading.Scope.RepositoryId)
                        elif role = "OtherOwner" then
                            Scope.Owner(Guid.NewGuid())
                        else
                            Scope.Owner reading.Scope.OwnerId

                    return
                        if not revoked && scope = roleScope && role <> "None" then
                            [
                                {
                                    Principal = principal
                                    Scope = scope
                                    RoleId = if role = "OtherOwner" then "OwnerAdmin" else role
                                    Source = "test"
                                    SourceDetail = None
                                    CreatedAt = reading.EnumerationStartedAt
                                }
                            ]
                        else
                            []
                }

            let evaluator = GracePermissionEvaluator(assignments, (fun _ -> Task.FromResult [])) :> IGracePermissionEvaluator

            use services =
                ServiceCollection()
                    .AddGiraffe()
                    .AddSingleton<Json.ISerializer>(Json.Serializer(Constants.JsonSerializerOptions))
                    .AddSingleton<IGracePermissionEvaluator>(evaluator)
                    .BuildServiceProvider()

            context.RequestServices <- services

            /// Models a single awaited immutable read and changes permission only at its return boundary.
            let lookup _ _ _ (_: CancellationToken) =
                task {
                    sqlCalls <- sqlCalls + 1
                    do! Task.Yield()
                    if scenario = "lookup-failure" then failwith "private stored values"

                    if scenario = "revoke"
                       || scenario = "missing-revoke"
                       || scenario = "conflict-revoke" then
                        revoked <- true

                    if scenario = "cancel" then cancellation.Cancel()

                    return
                        match scenario with
                        | "missing"
                        | "missing-revoke" -> Ok None
                        | "conflict"
                        | "conflict-revoke" -> Error "private stored scope"
                        | "wrong-id" -> Ok(Some { reading with ObservationId = Guid.NewGuid() })
                        | "wrong-owner" -> Ok(Some { reading with Scope = { reading.Scope with OwnerId = Guid.NewGuid() } })
                        | "wrong-organization" -> Ok(Some { reading with Scope = { reading.Scope with OrganizationId = Guid.NewGuid() } })
                        | "wrong-repository" -> Ok(Some { reading with Scope = { reading.Scope with RepositoryId = Guid.NewGuid() } })
                        | _ -> Ok(Some reading)
                }

            let mutable publicationFailed = false

            try
                let! _ = OwnerObservationRead.handleWith lookup id (fun ctx -> Task.FromResult(Some ctx)) context
                ()
            with
            | :? NotSupportedException when scenario = "publication-failure" -> publicationFailed <- true

            if scenario = "publication-failure" then Assert.That(publicationFailed, Is.True)

            if context.Response.StatusCode = 200 then
                Assert.That(context.Response.ContentType, Is.EqualTo "application/json; charset=utf-8")

            context.Response.Body.Position <- 0L
            use reader = new StreamReader(context.Response.Body)
            let! body = reader.ReadToEndAsync()
            return context.Response.StatusCode, body, sqlCalls, evaluations
        }

    /// Builds only the recorded three-ID query; no live repository exists in this test.
    let query = $"?OwnerId={reading.Scope.OwnerId}&OrganizationId={reading.Scope.OrganizationId}&RepositoryId={reading.Scope.RepositoryId}"

    /// Configures inert local endpoints before the existing authorization logger initializes.
    [<OneTimeSetUp>]
    member _.SetUp() =
        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")
        originalLogger <- ApplicationContext.loggerFactory
        ApplicationContext.loggerFactory <- Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance

    /// Restores process configuration after the fixture's isolated permission checks.
    [<OneTimeTearDown>]
    member _.TearDown() =
        ApplicationContext.loggerFactory <- originalLogger
        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, originalEnvironment)

    /// Authentication takes precedence over malformed inputs and all permission/storage work.
    [<Test>]
    member _.``unauthenticated malformed request has no resolver effects``() =
        task {
            let! status, _, calls, evaluations = invoke false "OwnerAdmin" "?OwnerId=bad" "bad" "success"
            Assert.That(status, Is.EqualTo 401)
            Assert.That(calls, Is.Zero)
            Assert.That(evaluations, Is.Zero)
        }

    /// Every explicit identifier and every name selector is rejected before SQL.
    [<Test>]
    member _.``explicit selector failures never read SQL``() =
        task {
            let invalid =
                [
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                ]
                |> List.collect (fun name ->
                    [ ""; "bad"; string Guid.Empty ]
                    |> List.map (fun value ->
                        query.Replace(
                            $"{name}="
                            + (if name = "OwnerId" then string reading.Scope.OwnerId
                               elif name = "OrganizationId" then string reading.Scope.OrganizationId
                               else string reading.Scope.RepositoryId),
                            $"{name}={value}"
                        )))

            let names =
                [
                    "OwnerName"
                    "OrganizationName"
                    "RepositoryName"
                ]
                |> List.map (fun name -> query + $"&{name}=named")

            let cases =
                (invalid @ names @ [ "" ])
                |> List.map (fun q -> q, string reading.ObservationId)

            let mutable remaining =
                cases
                @ [
                    query, "bad"
                    query, string Guid.Empty
                ]

            while not remaining.IsEmpty do
                let q, id = remaining.Head
                remaining <- remaining.Tail
                let! status, _, calls, evaluations = invoke true "OwnerAdmin" q id "success"
                Assert.That(status, Is.EqualTo 400, q)
                Assert.That(calls, Is.Zero)
                Assert.That(evaluations, Is.Zero)
        }

    /// Existing owner and system roles decide access; repository or unrelated roles cannot disclose a row.
    [<TestCase("OwnerAdmin", 200)>]
    [<TestCase("SystemAdmin", 200)>]
    [<TestCase("SystemOperator", 200)>]
    [<TestCase("OwnerReader", 403)>]
    [<TestCase("RepositoryAdmin", 403)>]
    [<TestCase("OtherOwner", 403)>]
    [<TestCase("None", 403)>]
    member _.``existing roles and exact wire``(role: string, expected: int) =
        task {
            let! status, body, calls, evaluations = invoke true role query (string reading.ObservationId) "success"
            Assert.That(status, Is.EqualTo expected, body)
            Assert.That(calls, Is.EqualTo(if expected = 200 then 1 else 0))
            Assert.That(evaluations, Is.EqualTo(if expected = 200 then 2 else 1))

            if expected = 200 then
                use document = JsonDocument.Parse body
                let value = document.RootElement.GetProperty("ReturnValue")

                Assert.That(
                    value
                        .GetProperty("DeclaredLogicalBytes")
                        .GetString(),
                    Is.EqualTo "9007199254740993"
                )

                Assert.That(
                    value
                        .GetProperty("DistinctContentCount")
                        .GetString(),
                    Is.EqualTo "9223372036854775807"
                )

                Assert.That(
                    value
                        .GetProperty("EnumerationStartedAt")
                        .GetString(),
                    Is.EqualTo "2026-09-07T01:02:03.123456789Z"
                )

                Assert.That(
                    value
                        .GetProperty("EnumerationFinishedAt")
                        .GetString(),
                    Is.EqualTo "2026-09-07T01:02:04.987654321Z"
                )

                use shared = JsonDocument.Parse(JsonSerializer.Serialize(reading, Constants.JsonSerializerOptions))

                Assert.That(
                    shared
                        .RootElement
                        .GetProperty(
                            "DeclaredLogicalBytes"
                        )
                        .ValueKind,
                    Is.EqualTo JsonValueKind.Number
                )
            else
                Assert.That(body, Does.Not.Contain "DeclaredLogicalBytes")
        }

    /// Failed effects, stale permission and substituted rows never publish stored fields; a retry starts fresh.
    [<TestCase("revoke", 403, 2)>]
    [<TestCase("missing-revoke", 403, 2)>]
    [<TestCase("conflict-revoke", 403, 2)>]
    [<TestCase("first-evaluator-failure", 403, 1)>]
    [<TestCase("second-evaluator-failure", 403, 2)>]
    [<TestCase("lookup-failure", 503, 1)>]
    [<TestCase("cancel", 503, 2)>]
    [<TestCase("before-lookup-cancel", 503, 1)>]
    [<TestCase("pre-publication-cancel", 503, 2)>]
    [<TestCase("publication-failure", 503, 2)>]
    [<TestCase("missing", 404, 2)>]
    [<TestCase("conflict", 404, 2)>]
    [<TestCase("wrong-id", 404, 2)>]
    [<TestCase("wrong-owner", 404, 2)>]
    [<TestCase("wrong-organization", 404, 2)>]
    [<TestCase("wrong-repository", 404, 2)>]
    member _.``failures and stale permission disclose no observation``(scenario: string, expected: int, checks: int) =
        task {
            let! status, body, calls, evaluations = invoke true "OwnerAdmin" query (string reading.ObservationId) scenario
            Assert.That(status, Is.EqualTo expected, body)
            Assert.That(evaluations, Is.EqualTo checks)
            Assert.That(body, Does.Not.Contain "DeclaredLogicalBytes")
            Assert.That(body, Does.Not.Contain "private stored")
            Assert.That(body, Does.Not.Contain(string reading.ObservationId))

            Assert.That(
                calls,
                Is.EqualTo(
                    if scenario = "first-evaluator-failure"
                       || scenario = "before-lookup-cancel" then
                        0
                    else
                        1
                )
            )

            let! retryStatus, _, retryCalls, retryChecks = invoke true "OwnerAdmin" query (string reading.ObservationId) "success"
            Assert.That((retryStatus, retryCalls, retryChecks), Is.EqualTo((200, 1, 2)))
        }
