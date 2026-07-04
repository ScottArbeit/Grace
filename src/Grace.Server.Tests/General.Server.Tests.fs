namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Types.Common
open Grace.Types
open FSharp.Control
open Grace.Shared.Validation.Errors

/// Groups shared helpers for common.
module Common =
    let okResult: Result<unit, TestError> = Result.Ok()
    let errorResult: Result<unit, TestError> = Result.Error TestError.TestFailed

    /// Tries to resolve get guid property without failing the caller.
    let tryGetGuidProperty (value: obj) =
        let mutable parsed = Guid.Empty

        match value with
        | :? Guid as guid -> Some guid
        | :? string as text when Guid.TryParse(text, &parsed) -> Some parsed
        | :? JsonElement as element ->
            if element.ValueKind = JsonValueKind.String then
                let text = element.GetString()
                if Guid.TryParse(text, &parsed) then Some parsed else None
            else
                None
        | _ -> None

    /// Requires guid property and fails the test when missing.
    let requireGuidProperty (name: string) (value: obj) =
        match tryGetGuidProperty value with
        | Some guid -> guid
        | None -> failwith $"Property '{name}' was not a GUID value."

/// Groups shared helpers for services.
module Services =
    [<Literal>]
    let numberOfRepositories = 3

    let rnd = Random.Shared

    let mutable App: Aspire.Hosting.DistributedApplication option = None
    let mutable Client: HttpClient = Unchecked.defaultof<HttpClient>

    let mutable ownerId = String.Empty
    let mutable organizationId = String.Empty
    let mutable repositoryIds: string [] = Array.empty
    let mutable repositoryDefaultBranchIds: string [] = Array.empty

    let mutable serviceBusConnectionString = String.Empty
    let mutable serviceBusTopic = String.Empty
    let mutable serviceBusServerSubscription = String.Empty
    let mutable serviceBusTestSubscription = String.Empty
    let mutable operationalFactsTopic = String.Empty
    let mutable operationsSqlConnectionString = String.Empty
    let mutable graceServerBaseAddress = String.Empty
    let mutable testUserId = String.Empty
    let mutable testUserClaims: string list = []

    /// Defines log to test console behavior for the surrounding tests used by the server integration general scenario.
    let logToTestConsole (message: string) =
        TestContext.Progress.WriteLine(message)
        TestContext.Progress.Flush()
        Console.Error.WriteLine(message)
        Console.Error.Flush()

    /// Gets approximate test count from the running test server.
    let getApproximateTestCount () =
        Assembly.GetExecutingAssembly().GetTypes()
        |> Seq.collect (fun testType ->
            testType.GetMethods(
                BindingFlags.Instance
                ||| BindingFlags.Static
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
            ))
        |> Seq.filter (fun methodInfo ->
            methodInfo
                .GetCustomAttributes(
                    typeof<TestAttribute>,
                    true
                )
                .Length > 0)
        |> Seq.length

open Services

/// Defines the setup and teardown for all tests in the Grace.Server.Tests namespace.
[<SetUpFixture>]
type Setup() =

    /// Resets shared test state before each integration test.
    [<OneTimeSetUp>]
    member public _.Setup() =
        task {
            let approximateTestCount = getApproximateTestCount ()

            logToTestConsole "Grace.Server.Tests progress: setup starting."
            logToTestConsole $"Grace.Server.Tests progress: approximately {approximateTestCount} tests will start after setup."
            logToTestConsole "Starting Aspire test host..."

            testUserId <- $"{Guid.NewGuid()}"
            let! hostState = AspireTestHost.startAsync testUserId
            logToTestConsole "Grace.Server.Tests progress: Aspire setup ready."

            App <- Some hostState.App
            Client <- hostState.Client
            graceServerBaseAddress <- hostState.GraceServerBaseAddress
            serviceBusConnectionString <- hostState.ServiceBusConnectionString
            serviceBusTopic <- hostState.ServiceBusTopic
            serviceBusServerSubscription <- hostState.ServiceBusServerSubscription
            serviceBusTestSubscription <- hostState.ServiceBusTestSubscription
            operationalFactsTopic <- hostState.OperationalFactsTopic
            operationsSqlConnectionString <- hostState.OperationsSqlConnectionString
            testUserClaims <- [ "engineering"; "contributors" ]

            Client.DefaultRequestHeaders.Add("x-grace-user-id", testUserId)

            logToTestConsole "Grace.Server.Tests progress: shared fixture data setup starting."
            logToTestConsole $"Grace.Server base address: {graceServerBaseAddress}"

            logToTestConsole
                $"Service Bus topic: {serviceBusTopic}; subscription: {serviceBusServerSubscription}; test subscription: {serviceBusTestSubscription}"

            logToTestConsole $"Operational facts topic: {operationalFactsTopic}"

            let! drained = AspireTestHost.drainServiceBusAsync hostState

            if drained > 0 then
                logToTestConsole $"Drained {drained} message(s) from Service Bus test subscription before tests."

            let correlationId = generateCorrelationId ()

            ownerId <- $"{Guid.NewGuid()}"
            let ownerName = $"TestOwner{rnd.Next(65535):X4}"

            let ownerParameters = Parameters.Owner.CreateOwnerParameters()
            ownerParameters.OwnerId <- ownerId
            ownerParameters.OwnerName <- ownerName
            ownerParameters.CorrelationId <- correlationId

            let! ownerResponse = Client.PostAsync("/owner/create", createJsonContent ownerParameters)

            if ownerResponse.IsSuccessStatusCode then
                logToTestConsole $"Owner {ownerParameters.OwnerName} created successfully."
            else
                let! content = ownerResponse.Content.ReadAsStringAsync()
                Assert.That(content.Length, Is.GreaterThan(0))
                let error = deserialize<GraceError> content
                logToTestConsole $"StatusCode: {ownerResponse.StatusCode}; Content: {error}"

            ownerResponse.EnsureSuccessStatusCode() |> ignore

            let getOwnerParameters = Parameters.Owner.GetOwnerParameters()
            getOwnerParameters.OwnerId <- ownerId
            getOwnerParameters.CorrelationId <- correlationId

            let! getOwnerResponse = Client.PostAsync("/owner/get", createJsonContent getOwnerParameters)

            getOwnerResponse.EnsureSuccessStatusCode()
            |> ignore

            let! getOwnerReturnValue = deserializeContent<GraceReturnValue<Owner.OwnerDto>> getOwnerResponse
            Assert.That(getOwnerReturnValue.ReturnValue.OwnerId, Is.EqualTo(Guid.Parse(ownerId)))
            Assert.That(getOwnerReturnValue.ReturnValue.OwnerName, Is.EqualTo(ownerName))

            let! _ = AspireTestHost.waitForOwnerCreatedEventAsync hostState ownerId
            logToTestConsole $"Owner Created event observed on Service Bus test subscription for {ownerId}."

            organizationId <- $"{Guid.NewGuid()}"
            repositoryIds <- Array.init numberOfRepositories (fun _ -> $"{Guid.NewGuid()}")
            repositoryDefaultBranchIds <- Array.zeroCreate numberOfRepositories

            let organizationParameters = Parameters.Organization.CreateOrganizationParameters()
            organizationParameters.OwnerId <- ownerId
            organizationParameters.OrganizationId <- organizationId
            organizationParameters.OrganizationName <- $"TestOrganization{rnd.Next(65535):X4}"
            organizationParameters.CorrelationId <- correlationId

            let! organizationResponse = Client.PostAsync("/organization/create", createJsonContent organizationParameters)

            if organizationResponse.IsSuccessStatusCode then
                logToTestConsole $"Organization {organizationParameters.OrganizationName} created successfully."
            else
                let! content = organizationResponse.Content.ReadAsStringAsync()
                Assert.That(content.Length, Is.GreaterThan(0))
                let error = deserialize<GraceError> content
                logToTestConsole $"StatusCode: {organizationResponse.StatusCode}; Content: {error}"

            organizationResponse.EnsureSuccessStatusCode()
            |> ignore

            do!
                Parallel.ForEachAsync(
                    Array.indexed repositoryIds,
                    Constants.ParallelOptions,
                    (fun repositoryInfo ct ->
                        /// Defines repository index behavior for the surrounding tests used by the server integration general scenario.
                        let repositoryIndex, repositoryId = repositoryInfo

                        ValueTask(
                            task {
                                let repositoryParameters = Parameters.Repository.CreateRepositoryParameters()
                                repositoryParameters.OwnerId <- ownerId
                                repositoryParameters.OrganizationId <- organizationId
                                repositoryParameters.RepositoryId <- repositoryId
                                repositoryParameters.RepositoryName <- $"TestRepository{rnd.Next():X8}"
                                repositoryParameters.CorrelationId <- correlationId

                                let! response = Client.PostAsync("/repository/create", createJsonContent repositoryParameters)

                                if response.IsSuccessStatusCode then
                                    logToTestConsole $"Repository {repositoryParameters.RepositoryName} created successfully."
                                else
                                    let! content = response.Content.ReadAsStringAsync()
                                    Assert.That(content.Length, Is.GreaterThan(0))
                                    let error = deserialize<GraceError> content
                                    logToTestConsole $"StatusCode: {response.StatusCode}; Content: {error}"

                                response.EnsureSuccessStatusCode() |> ignore

                                let! returnValue = deserializeContent<GraceReturnValue<string>> response
                                let branchId = Common.requireGuidProperty (nameof BranchId) returnValue.Properties[nameof BranchId]
                                repositoryDefaultBranchIds[repositoryIndex] <- $"{branchId}"
                            }
                        ))
                )

            logToTestConsole $"Grace.Server.Tests progress: shared fixture data ready; approximately {approximateTestCount} tests starting."
        }

    /// Verifies the teardown scenario.
    [<OneTimeTearDown>]
    member public _.Teardown() =
        task {
            logToTestConsole "Grace.Server.Tests progress: tests concluded; teardown starting."

            let correlationId = generateCorrelationId ()
            /// Defines log cleanup failure behavior for the surrounding tests used by the server integration general scenario.
            let logCleanupFailure (label: string) (detail: string) = logToTestConsole $"Cleanup {label} failed: {detail}"

            let cleanupEnabled =
                /// Determines whether truthy environment value for test-host decisions.
                let isTruthyEnvironmentValue (value: string) =
                    value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

                match Environment.GetEnvironmentVariable("GRACE_TEST_SERVER_CLEANUP") with
                | value when not (String.IsNullOrWhiteSpace value) -> isTruthyEnvironmentValue value
                | _ ->
                    match Environment.GetEnvironmentVariable("GRACE_TEST_CLEANUP") with
                    | value when not (String.IsNullOrWhiteSpace value) -> isTruthyEnvironmentValue value
                    | _ -> false

            /// Tries to resolve post without failing the caller.
            let tryPost (label: string) (path: string) (content: HttpContent) =
                task {
                    try
                        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
                        let! response = Client.PostAsync(path, content, cts.Token)

                        if response.IsSuccessStatusCode then
                            ()
                        else
                            let! body = response.Content.ReadAsStringAsync()
                            logCleanupFailure label $"StatusCode: {response.StatusCode}; Content: {body}"
                    with
                    | ex -> logCleanupFailure label ex.Message
                }

            if cleanupEnabled then
                try
                    if not <| String.IsNullOrWhiteSpace ownerId then
                        if repositoryIds.Length > 0 then
                            for repositoryId in repositoryIds do
                                let repositoryDeleteParameters = Parameters.Repository.DeleteRepositoryParameters()

                                repositoryDeleteParameters.OwnerId <- ownerId
                                repositoryDeleteParameters.OrganizationId <- organizationId
                                repositoryDeleteParameters.RepositoryId <- repositoryId
                                repositoryDeleteParameters.DeleteReason <- "Deleting test repository"
                                repositoryDeleteParameters.CorrelationId <- correlationId
                                repositoryDeleteParameters.Force <- true

                                do! tryPost $"repository {repositoryId}" "/repository/delete" (createJsonContent repositoryDeleteParameters)

                        if not <| String.IsNullOrWhiteSpace organizationId then
                            let organizationDeleteParameters = Parameters.Organization.DeleteOrganizationParameters()

                            organizationDeleteParameters.OwnerId <- ownerId
                            organizationDeleteParameters.OrganizationId <- organizationId
                            organizationDeleteParameters.DeleteReason <- "Deleting test organization"
                            organizationDeleteParameters.CorrelationId <- correlationId
                            organizationDeleteParameters.Force <- true

                            do! tryPost $"organization {organizationId}" "/organization/delete" (createJsonContent organizationDeleteParameters)

                        let ownerDeleteParameters = Parameters.Owner.DeleteOwnerParameters()
                        ownerDeleteParameters.OwnerId <- ownerId
                        ownerDeleteParameters.DeleteReason <- "Deleting test owner"
                        ownerDeleteParameters.CorrelationId <- generateCorrelationId ()
                        ownerDeleteParameters.Force <- true

                        do! tryPost $"owner {ownerId}" "/owner/delete" (createJsonContent ownerDeleteParameters)
                with
                | ex -> logCleanupFailure "cleanup" ex.Message
            else
                logToTestConsole "Skipping server-side cleanup (set GRACE_TEST_SERVER_CLEANUP=1 to enable)."

            if not (isNull Client) then Client.Dispose()

            try
                do! AspireTestHost.stopAsync App
            with
            | ex -> logCleanupFailure "apphost stop" ex.Message

            logToTestConsole "Grace.Server.Tests progress: teardown concluded."

        }

/// Captures general values used by the test suite.
[<Parallelizable(ParallelScope.All)>]
type General() =

    /// Verifies the root path returns value scenario.
    [<Test>]
    member public _.RootPathReturnsValue() =
        task {
            let correlationId = generateCorrelationId ()
            use request = new HttpRequestMessage(HttpMethod.Get, "/")
            request.Headers.Add(Constants.CorrelationIdHeaderKey, correlationId)

            let! response = Services.Client.SendAsync(request)
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}")
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("text/html"))
            Assert.That(response.Headers.Contains(Constants.CorrelationIdHeaderKey), Is.True)

            Assert.That(
                response.Headers.GetValues(Constants.CorrelationIdHeaderKey)
                |> Seq.head,
                Is.EqualTo(correlationId)
            )

            Assert.That(content, Does.Contain("Grace"))
        }

    /// Verifies the metrics requires authentication scenario.
    [<Test>]
    member public _.MetricsRequiresAuthentication() =
        task {
            use client = new HttpClient()
            client.BaseAddress <- Services.Client.BaseAddress

            let! response = client.GetAsync("/metrics")
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }

    /// Verifies the metrics requires system admin and returns prometheus text scenario.
    [<Test>]
    member public _.MetricsRequiresSystemAdminAndReturnsPrometheusText() =
        task {
            use nonAdminClient = new HttpClient()
            nonAdminClient.BaseAddress <- Services.Client.BaseAddress
            nonAdminClient.DefaultRequestHeaders.Add("x-grace-user-id", $"{Guid.NewGuid()}")

            let! denied = nonAdminClient.GetAsync("/metrics")
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
            let! deniedBody = denied.Content.ReadAsStringAsync()
            Assert.That(deniedBody, Does.Contain("SystemAdmin"))

            let! response = Services.Client.GetAsync("/metrics")
            let! content = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("text/plain"))
            Assert.That(content, Does.Contain("# HELP"))
            Assert.That(content, Does.Contain("# TYPE"))
        }
