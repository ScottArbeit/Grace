namespace Grace.Server.Tests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Operations.Data
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.PersonalAccessToken
open Grace.Types.UsageObservation
open Microsoft.Data.SqlClient
open NodaTime.Text
open NUnit.Framework

/// Exercises the registered owner GET, real assignments, historical SQL and complete HTTP envelopes.
[<NonParallelizable>]
type OwnerObservationReadHttpTests() =
    /// Builds the exact historical request without entity resolution.
    let route (reading: DirectoryVersionSizeObservation) =
        $"/owner/usage/directory-version-observations/{reading.ObservationId}?OwnerId={reading.Scope.OwnerId}&OrganizationId={reading.Scope.OrganizationId}&RepositoryId={reading.Scope.RepositoryId}"

    /// Seeds a valid retained SQL observation independently of the read implementation.
    let seed quantity repository =
        task {
            let reading: DirectoryVersionSizeObservation =
                {
                    ObservationId = Guid.NewGuid()
                    Scope = { OwnerId = Guid.Parse ownerId; OrganizationId = Guid.Parse organizationId; RepositoryId = repository }
                    DeclaredLogicalBytes = quantity
                    DistinctContentCount = quantity
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

            let! accepted = DirectoryVersionSizeObservations.accept HostState.Value.OperationsSqlConnectionString reading CancellationToken.None
            Assert.That(accepted, Is.EqualTo(Ok reading: Result<DirectoryVersionSizeObservation, string>))
            return reading
        }

    /// Changes actual persisted assignments through the public authorization route.
    let role change principal scopeKind roleId =
        task {
            let parameters =
                Parameters.Access.GrantRoleParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = repositoryIds[0],
                    PrincipalType = "User",
                    PrincipalId = principal,
                    ScopeKind = scopeKind,
                    RoleId = roleId,
                    Source = "owner-observation-test"
                )

            use! response = Client.PostAsync($"/authorize/{change}-role", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)
        }

    /// Creates a caller whose assignments are evaluated by the hosted server.
    let client (user: string) =
        let result = new HttpClient(BaseAddress = Client.BaseAddress)
        result.DefaultRequestHeaders.Add("x-grace-user-id", user)
        result

    /// Runs the existing Node package against the hosted route, keeping its PAT out of arguments and logs.
    let nodeFacade (liveUrl: string) (token: string) (fixturePath: string) build =
        task {
            let package = IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "sdk", "typescript", "grace"))
            let start = Diagnostics.ProcessStartInfo()
            start.WorkingDirectory <- package
            start.UseShellExecute <- false
            start.CreateNoWindow <- true
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true

            if build then
                start.FileName <- "pwsh"
                start.ArgumentList.Add "-NoProfile"
                start.ArgumentList.Add "-Command"
                start.ArgumentList.Add "& npm ci --ignore-scripts; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & npm run build; exit $LASTEXITCODE"
            else
                start.FileName <- "node"
                start.ArgumentList.Add "--test"
                start.ArgumentList.Add "--test-name-pattern=owner observation live hosted"
                start.ArgumentList.Add "test/facade.test.mjs"
                start.Environment[ "GRACE_OWNER_OBSERVATION_LIVE_URL" ] <- liveUrl
                start.Environment[ "GRACE_OWNER_OBSERVATION_TOKEN" ] <- token
                start.Environment[ "GRACE_OWNER_OBSERVATION_FIXTURE" ] <- fixturePath

            use child = Diagnostics.Process.Start start
            let stdout = child.StandardOutput.ReadToEndAsync()
            let stderr = child.StandardError.ReadToEndAsync()

            try
                do!
                    child
                        .WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromMinutes 2.)
            with
            | error ->
                if not child.HasExited then child.Kill(true)
                return raise error

            let! output = stdout
            let! error = stderr
            /// Redacts the test PAT even if a child tool unexpectedly includes it in diagnostics.
            let redact (text: string) = if String.IsNullOrEmpty token then text else text.Replace(token, "[redacted]")
            Assert.That(child.ExitCode, Is.Zero, redact (output + error))
            if not build then TestContext.Out.WriteLine(redact output)
        }

    /// Real HTTP, F# SDK and Node boundaries preserve zero, high-bit quantities and full nanoseconds.
    [<Test>]
    member _.``registered HTTP and SDK preserve exact retained values``() =
        task {
            let principal = string (Guid.NewGuid())
            do! role "grant" principal "owner" "OwnerAdmin"
            use caller = client principal
            let tokenParameters = Parameters.Auth.CreatePersonalAccessTokenParameters(TokenName = $"owner-observation-{Guid.NewGuid():N}")
            use! tokenResponse = caller.PostAsync("/authenticate/token/create", createJsonContent tokenParameters)
            tokenResponse.EnsureSuccessStatusCode() |> ignore
            let! createdToken = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> tokenResponse
            let oldUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
            Grace.SDK.Auth.setTokenProvider (fun () -> Task.FromResult(Some createdToken.ReturnValue.Token))
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, Client.BaseAddress.ToString().TrimEnd('/'))

            try
                do! nodeFacade "" "" "" true

                let mutable quantities =
                    [
                        0L
                        9007199254740993L
                        Int64.MaxValue
                    ]

                while not quantities.IsEmpty do
                    let quantity = quantities.Head
                    quantities <- quantities.Tail
                    let! reading = seed quantity (Guid.NewGuid())
                    use! response = caller.GetAsync(route reading)
                    let! body = response.Content.ReadAsStringAsync()
                    Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)
                    Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo "application/json")
                    use document = JsonDocument.Parse body
                    let value = document.RootElement.GetProperty("ReturnValue")

                    Assert.That(
                        value
                            .GetProperty("DeclaredLogicalBytes")
                            .GetString(),
                        Is.EqualTo(string quantity)
                    )

                    Assert.That(
                        value
                            .GetProperty("DistinctContentCount")
                            .GetString(),
                        Is.EqualTo(string quantity)
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

                    let marker =
                        "OWNER_DIRECTORY_VERSION_OBSERVATION_JSON_BASE64:"
                        + Convert.ToBase64String(Text.Encoding.UTF8.GetBytes body)

                    TestContext.Out.WriteLine marker
                    TestContext.Error.WriteLine marker
                    let fixturePath = IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, $"owner-observation-{reading.ObservationId}.json")
                    IO.File.WriteAllText(fixturePath, body)
                    do! nodeFacade (Uri(Client.BaseAddress, route reading).ToString()) createdToken.ReturnValue.Token fixturePath false

                    let parameters =
                        Parameters.Repository.GetRepositoryParameters(
                            OwnerId = string reading.Scope.OwnerId,
                            OrganizationId = string reading.Scope.OrganizationId,
                            RepositoryId = string reading.Scope.RepositoryId
                        )

                    let! sdkResult = Grace.SDK.Owner.GetDirectoryVersionObservation(reading.ObservationId, parameters)

                    match sdkResult with
                    | Ok result -> Assert.That(result.ReturnValue, Is.EqualTo reading)
                    | Error error -> Assert.Fail error.Error

                    use! admin =
                        Client.GetAsync(
                            $"/admin/directory-version-size/observations/{reading.ObservationId}?OwnerId={reading.Scope.OwnerId}&OrganizationId={reading.Scope.OrganizationId}&RepositoryId={reading.Scope.RepositoryId}"
                        )

                    let! adminBody = admin.Content.ReadAsStringAsync()
                    use adminJson = JsonDocument.Parse adminBody

                    Assert.That(
                        adminJson
                            .RootElement
                            .GetProperty(
                                "ReturnValue"
                            )
                            .GetProperty(
                            "DeclaredLogicalBytes"
                        )
                            .ValueKind,
                        Is.EqualTo JsonValueKind.Number
                    )
            finally
                Grace.SDK.Auth.clearTokenProvider ()
                Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, oldUri)
        }

    /// Actual registered routing authenticates malformed requests and preserves role inheritance.
    [<Test>]
    member _.``registered route rejects selectors and unauthorized roles``() =
        task {
            use anonymous = new HttpClient(BaseAddress = Client.BaseAddress)
            use! unauthenticated = anonymous.GetAsync("/owner/usage/directory-version-observations/bad?OwnerId=bad")
            Assert.That(unauthenticated.StatusCode, Is.EqualTo HttpStatusCode.Unauthorized)
            let! reading = seed 37L (Guid.NewGuid())

            let mutable cases =
                [
                    "OwnerAdmin", "owner", HttpStatusCode.OK
                    "SystemAdmin", "system", HttpStatusCode.OK
                    "SystemOperator", "system", HttpStatusCode.OK
                    "OwnerReader", "owner", HttpStatusCode.Forbidden
                    "RepositoryAdmin", "repository", HttpStatusCode.Forbidden
                ]

            while not cases.IsEmpty do
                let roleId, scope, status = cases.Head
                cases <- cases.Tail
                let user = string (Guid.NewGuid())
                do! role "grant" user scope roleId
                use caller = client user
                use! response = caller.GetAsync(route reading)
                Assert.That(response.StatusCode, Is.EqualTo status)

            let mutable selectors =
                [
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                ]

            while not selectors.IsEmpty do
                let selector = selectors.Head
                selectors <- selectors.Tail

                let original =
                    if selector = "OwnerId" then reading.Scope.OwnerId
                    elif selector = "OrganizationId" then reading.Scope.OrganizationId
                    else reading.Scope.RepositoryId

                let mutable values = [ ""; "bad"; string Guid.Empty ]

                while not values.IsEmpty do
                    let value = values.Head
                    values <- values.Tail

                    use! invalid =
                        Client.GetAsync(
                            (route reading)
                                .Replace($"{selector}={original}", $"{selector}={value}")
                        )

                    Assert.That(invalid.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)

                use! conflict =
                    Client.GetAsync(
                        (route reading)
                            .Replace($"{selector}={original}", $"{selector}={Guid.NewGuid()}")
                    )

                let! error = conflict.Content.ReadAsStringAsync()
                Assert.That(conflict.StatusCode, Is.EqualTo HttpStatusCode.NotFound, error)
                Assert.That(error, Does.Not.Contain "DeclaredLogicalBytes")
                Assert.That(error, Does.Not.Contain(string reading.ObservationId))

            let mutable names =
                [
                    "OwnerName"
                    "OrganizationName"
                    "RepositoryName"
                ]

            while not names.IsEmpty do
                let name = names.Head
                names <- names.Tail
                use! invalid = Client.GetAsync(route reading + $"&{name}=name")
                Assert.That(invalid.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)

            use! missing = Client.GetAsync(route { reading with ObservationId = Guid.NewGuid() })
            Assert.That(missing.StatusCode, Is.EqualTo HttpStatusCode.NotFound)
        }

    /// A deleted repository does not rewrite or hide its retained declarations from its recorded owner.
    [<Test>]
    member _.``recorded scope survives repository deletion``() =
        task {
            let repository = Guid.NewGuid()

            let create =
                Parameters.Repository.CreateRepositoryParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = string repository,
                    RepositoryName = $"OwnerObservation{repository:N}"
                )

            use! created = Client.PostAsync("/repository/create", createJsonContent create)
            created.EnsureSuccessStatusCode() |> ignore
            let! reading = seed 37L repository

            let delete =
                Parameters.Repository.DeleteRepositoryParameters(
                    OwnerId = ownerId,
                    OrganizationId = organizationId,
                    RepositoryId = string repository,
                    Force = true,
                    DeleteReason = "owner observation history test"
                )

            use! deleted = Client.PostAsync("/repository/delete", createJsonContent delete)
            deleted.EnsureSuccessStatusCode() |> ignore
            let otherOwner = Guid.NewGuid()
            let otherPrincipal = string (Guid.NewGuid())

            use! otherCreated =
                Client.PostAsync(
                    "/owner/create",
                    createJsonContent (Parameters.Owner.CreateOwnerParameters(OwnerId = string otherOwner, OwnerName = $"OtherOwner{otherOwner:N}"))
                )

            otherCreated.EnsureSuccessStatusCode() |> ignore

            let otherGrant =
                Parameters.Access.GrantRoleParameters(
                    OwnerId = string otherOwner,
                    ScopeKind = "owner",
                    PrincipalType = "User",
                    PrincipalId = otherPrincipal,
                    RoleId = "OwnerAdmin",
                    Source = "owner-observation-test"
                )

            use! otherGranted = Client.PostAsync("/authorize/grant-role", createJsonContent otherGrant)
            otherGranted.EnsureSuccessStatusCode() |> ignore
            use otherCaller = client otherPrincipal
            use! otherDenied = otherCaller.GetAsync(route reading)
            Assert.That(otherDenied.StatusCode, Is.EqualTo HttpStatusCode.Forbidden)
            let! otherBody = otherDenied.Content.ReadAsStringAsync()
            Assert.That(otherBody, Does.Not.Contain "DeclaredLogicalBytes")
            let user = string (Guid.NewGuid())
            do! role "grant" user "owner" "OwnerAdmin"
            use caller = client user
            use! response = caller.GetAsync(route reading)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

            Assert.That(
                (deserialize<GraceReturnValue<DirectoryVersionSizeObservation>> body)
                    .ReturnValue,
                Is.EqualTo reading
            )

            do! role "revoke" user "owner" "OwnerAdmin"
            use! denied = caller.GetAsync(route reading)
            Assert.That(denied.StatusCode, Is.EqualTo HttpStatusCode.Forbidden)

            let! stored =
                DirectoryVersionSizeObservations.lookup HostState.Value.OperationsSqlConnectionString reading.ObservationId reading.Scope CancellationToken.None

            Assert.That(stored, Is.EqualTo(Ok(Some reading): Result<DirectoryVersionSizeObservation option, string>))
        }

    /// SQL blocking locates revocation, timeout and disconnect while the real read is awaiting SQL.
    [<TestCase("revoke")>]
    [<TestCase("cancel")>]
    [<TestCase("timeout")>]
    member _.``blocked real SQL read never discloses after revocation or failure``(failure: string) =
        task {
            let! reading = seed 37L (Guid.NewGuid())
            let user = string (Guid.NewGuid())
            do! role "grant" user "owner" "OwnerAdmin"
            use caller = client user
            caller.Timeout <- TimeSpan.FromSeconds 45.
            use cancellation = new CancellationTokenSource()
            use gate = new SqlConnection(HostState.Value.OperationsSqlConnectionString)
            do! gate.OpenAsync()
            use transaction = gate.BeginTransaction()

            use lockCommand =
                new SqlCommand("SELECT ObservationId FROM ops.DirectoryVersionSizeObservation WITH(XLOCK,HOLDLOCK) WHERE ObservationId=@id", gate, transaction)

            lockCommand.Parameters.AddWithValue("@id", reading.ObservationId)
            |> ignore

            let! _ = lockCommand.ExecuteScalarAsync()
            let pending = caller.GetAsync(route reading, cancellation.Token)
            use monitor = new SqlConnection(HostState.Value.OperationsSqlConnectionString)
            do! monitor.OpenAsync()
            use blocked = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@session", monitor)

            blocked.Parameters.AddWithValue("@session", gate.ServerProcessId)
            |> ignore

            let deadline = DateTime.UtcNow.AddSeconds 10.
            let mutable isBlocked = false

            while not isBlocked && DateTime.UtcNow < deadline do
                let! count = blocked.ExecuteScalarAsync()
                isBlocked <- unbox<int> count > 0
                if not isBlocked then do! Task.Delay 50

            Assert.That(isBlocked, Is.True, "The actual owner GET must reach its blocked SQL read before revocation.")

            if failure = "revoke" then
                do! role "revoke" user "owner" "OwnerAdmin"
                do! transaction.CommitAsync()
                use! response = pending
                let! body = response.Content.ReadAsStringAsync()
                Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.Forbidden, body)
                Assert.That(body, Does.Not.Contain "DeclaredLogicalBytes")
                do! role "grant" user "owner" "OwnerAdmin"
            elif failure = "cancel" then
                cancellation.Cancel()
                let mutable cancelled = false

                try
                    use! unexpected = pending
                    Assert.Fail "A disconnected client must not receive an observation."
                with
                | :? OperationCanceledException -> cancelled <- true

                Assert.That(cancelled, Is.True)
                do! transaction.CommitAsync()
            else
                use! response = pending
                let! body = response.Content.ReadAsStringAsync()
                Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.ServiceUnavailable, body)
                Assert.That(body, Does.Not.Contain "DeclaredLogicalBytes")
                do! transaction.CommitAsync()

            use! retry = caller.GetAsync(route reading)
            let! retryBody = retry.Content.ReadAsStringAsync()

            Assert.That(
                (deserialize<GraceReturnValue<DirectoryVersionSizeObservation>> retryBody)
                    .ReturnValue,
                Is.EqualTo reading
            )
        }
