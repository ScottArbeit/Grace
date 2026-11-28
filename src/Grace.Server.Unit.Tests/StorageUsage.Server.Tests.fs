namespace Grace.Server.Unit.Tests

open Giraffe
open Grace.Server
open Grace.Server.Security
open Grace.Shared
open Grace.Types.Authorization
open Grace.Types.Usage
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open NodaTime
open NUnit.Framework
open System
open System.IO
open System.Security.Claims
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Exercises `GET /usage/storage` through the real permission evaluator and Giraffe handler, without hosting.
[<NonParallelizable>]
type StorageUsageReadTests() =
    let originalEnvironment = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment)
    let mutable originalLogger: Microsoft.Extensions.Logging.ILoggerFactory = null
    let ownerId = Guid.Parse("0f8b8f7e-5d7f-4d0c-a1b4-0c5f1c4a9a01")
    let organizationId = Guid.Parse("0f8b8f7e-5d7f-4d0c-a1b4-0c5f1c4a9a02")
    let repositoryId = Guid.Parse("0f8b8f7e-5d7f-4d0c-a1b4-0c5f1c4a9a03")
    let principal = { PrincipalType = PrincipalType.User; PrincipalId = "storage-usage-test" }
    let query = $"?OwnerId={ownerId}&OrganizationId={organizationId}&RepositoryId={repositoryId}"

    /// Runs one request. `role` is the caller's only assignment; `repository` is what the repository lookup finds.
    let invoke authenticated (role: string) (queryText: string) (repository: string) (scenario: string) =
        task {
            // ASP.NET Core sets the current activity in a hosted request; the shared result helpers expect one.
            use activity = new System.Diagnostics.Activity("storage-usage-test")
            activity.Start() |> ignore
            let mutable revoked = false
            let mutable reads = 0
            let context = DefaultHttpContext()
            context.Request.QueryString <- QueryString queryText
            context.Response.Body <- new MemoryStream()

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

            /// Supplies fresh assignments on each permission check.
            let assignments (scope, _) =
                task {
                    let roleScope =
                        match role with
                        | "SystemAdmin"
                        | "SystemOperator" -> Scope.System
                        | "RepositoryAdmin" -> Scope.Repository(ownerId, organizationId, repositoryId)
                        | "OtherOwnerAdmin" -> Scope.Owner(Guid.NewGuid())
                        | _ -> Scope.Owner ownerId

                    return
                        if not revoked && scope = roleScope && role <> "None" then
                            [
                                {
                                    Principal = principal
                                    Scope = scope
                                    RoleId = if role = "OtherOwnerAdmin" then "OwnerAdmin" else role
                                    Source = "test"
                                    SourceDetail = None
                                    CreatedAt = Instant.FromUnixTimeSeconds 0L
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

            let lookup _ _ (_: CancellationToken) =
                task {
                    reads <- reads + 1

                    return
                        match repository with
                        | "exists" -> Some(ownerId, organizationId)
                        | "other-owner" -> Some(Guid.NewGuid(), organizationId)
                        | "other-organization" -> Some(ownerId, Guid.NewGuid())
                        | _ -> None
                }

            let readBytes _ _ (_: CancellationToken) =
                task {
                    reads <- reads + 1
                    if scenario = "revoke-during-read" then revoked <- true
                    if scenario = "meter-failure" then failwith "private meter details"
                    return 2765L
                }

            let! _ = StorageUsage.handleWith lookup readBytes (fun ctx -> Task.FromResult(Some ctx)) context
            context.Response.Body.Position <- 0L
            use reader = new StreamReader(context.Response.Body)
            let! body = reader.ReadToEndAsync()
            return context.Response.StatusCode, body, reads
        }

    /// Reads the error text so two responses can be compared without their correlation ids.
    let errorText (body: string) =
        use document = JsonDocument.Parse body

        document
            .RootElement
            .GetProperty("Error")
            .GetString()

    [<OneTimeSetUp>]
    member _.SetUp() =
        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")
        originalLogger <- ApplicationContext.loggerFactory
        ApplicationContext.loggerFactory <- Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance

    [<OneTimeTearDown>]
    member _.TearDown() =
        ApplicationContext.loggerFactory <- originalLogger
        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, originalEnvironment)

    /// Done-when check: without OwnerAdmin, a hidden repository and a missing one get the same answer, and nothing is read.
    [<TestCase("None")>]
    [<TestCase("OwnerReader")>]
    [<TestCase("RepositoryAdmin")>]
    [<TestCase("OtherOwnerAdmin")>]
    member _.``a caller without OwnerAdmin cannot tell a hidden repository from a missing one``(role: string) =
        task {
            let! hiddenStatus, hiddenBody, hiddenReads = invoke true role query "exists" "success"
            let! missingStatus, missingBody, missingReads = invoke true role query "missing" "success"

            Assert.That(hiddenStatus, Is.EqualTo 403)
            Assert.That(missingStatus, Is.EqualTo hiddenStatus)
            Assert.That(missingBody, Is.EqualTo hiddenBody)
            Assert.That(hiddenBody, Does.Not.Contain "2765")
            Assert.That(hiddenReads, Is.Zero)
            Assert.That(missingReads, Is.Zero)
        }

    /// OwnerAdmin and the system roles read the answer: manifest-backed bytes counted, the rest not counted yet.
    [<TestCase("OwnerAdmin")>]
    [<TestCase("SystemAdmin")>]
    [<TestCase("SystemOperator")>]
    member _.``owner admins and system roles read manifest bytes with partial coverage``(role: string) =
        task {
            let! status, body, _ = invoke true role query "exists" "success"
            Assert.That(status, Is.EqualTo 200, body)
            use document = JsonDocument.Parse body
            let usage = document.RootElement.GetProperty("ReturnValue")
            Assert.That(usage.GetProperty("Coverage").GetString(), Is.EqualTo "partial")

            Assert.That(
                usage
                    .GetProperty("Scope")
                    .GetProperty("RepositoryId")
                    .GetGuid(),
                Is.EqualTo repositoryId
            )

            let classes =
                usage
                    .GetProperty("ContentClasses")
                    .EnumerateArray()
                |> Seq.toList

            Assert.That(
                classes[0]
                    .GetProperty("ContentClass")
                    .GetString(),
                Is.EqualTo "manifestBackedFiles"
            )

            Assert.That(
                classes[0]
                    .GetProperty("RetainedLogicalBytes")
                    .GetInt64(),
                Is.EqualTo 2765L
            )

            Assert.That(
                classes.Tail
                |> List.map (fun item -> item.GetProperty("ContentClass").GetString(), item.GetProperty("Counted").GetBoolean()),
                Is.EqualTo [ "wholeFiles", false
                             "workItemText", false
                             "artifactPayloads", false ]
            )
        }

    /// An OwnerAdmin sees the same 404 for a missing repository and one recorded under another owner or organization.
    [<TestCase("missing")>]
    [<TestCase("other-owner")>]
    [<TestCase("other-organization")>]
    member _.``an owner admin gets not found for a repository outside the scope``(repository: string) =
        task {
            let! status, body, _ = invoke true "OwnerAdmin" query repository "success"
            Assert.That(status, Is.EqualTo 404)
            Assert.That(errorText body, Is.EqualTo "Repository was not found.")
        }

    /// Permission revoked while the meter was read answers 403 and discloses nothing.
    [<Test>]
    member _.``permission is checked again after the read``() =
        task {
            let! status, body, _ = invoke true "OwnerAdmin" query "exists" "revoke-during-read"
            Assert.That(status, Is.EqualTo 403)
            Assert.That(body, Does.Not.Contain "2765")
        }

    /// A meter failure is a retryable 503 without private details.
    [<Test>]
    member _.``a meter failure is a retryable unavailable answer``() =
        task {
            let! status, body, _ = invoke true "OwnerAdmin" query "exists" "meter-failure"
            Assert.That(status, Is.EqualTo 503)
            Assert.That(body, Does.Not.Contain "private")
        }

    /// Authentication comes first; malformed IDs and name selectors are rejected before any read.
    [<Test>]
    member _.``unauthenticated and malformed requests read nothing``() =
        task {
            let! anonymousStatus, _, anonymousReads = invoke false "OwnerAdmin" "?OwnerId=bad" "exists" "success"
            Assert.That(anonymousStatus, Is.EqualTo 401)
            Assert.That(anonymousReads, Is.Zero)

            let invalid =
                [
                    query.Replace($"OwnerId={ownerId}", "OwnerId=bad")
                    query.Replace($"OrganizationId={organizationId}", $"OrganizationId={Guid.Empty}")
                    query.Replace($"&RepositoryId={repositoryId}", "")
                    query + "&RepositoryName=named"
                    ""
                ]

            let! results =
                invalid
                |> List.map (fun queryText -> invoke true "OwnerAdmin" queryText "exists" "success")
                |> Task.WhenAll

            for (queryText, (status, body, reads)) in List.zip invalid (List.ofArray results) do
                Assert.That(status, Is.EqualTo 400, $"{queryText}: {body}")
                Assert.That(reads, Is.Zero)
        }
