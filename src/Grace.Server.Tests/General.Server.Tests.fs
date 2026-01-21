namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Types.Types
open Grace.Types
open FSharp.Control
open Grace.Shared.Validation.Errors

module Common =
    let okResult: Result<unit, TestError> = Result.Ok()
    let errorResult: Result<unit, TestError> = Result.Error TestError.TestFailed

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

    let requireGuidProperty (name: string) (value: obj) =
        match tryGetGuidProperty value with
        | Some guid -> guid
        | None -> failwith $"Property '{name}' was not a GUID value."

module Services =
    [<Literal>]
    let numberOfRepositories = 3

    let rnd = Random.Shared

    let mutable App: Aspire.Hosting.DistributedApplication option = None
    let mutable Client: HttpClient = Unchecked.defaultof<HttpClient>

    let mutable ownerId = String.Empty
    let mutable organizationId = String.Empty
    let mutable repositoryIds: string [] = Array.empty

    let mutable serviceBusConnectionString = String.Empty
    let mutable serviceBusTopic = String.Empty
    let mutable serviceBusServerSubscription = String.Empty
    let mutable serviceBusTestSubscription = String.Empty
    let mutable graceServerBaseAddress = String.Empty
    let mutable testUserId = String.Empty
    let mutable testUserClaims: string list = []

    let logToTestConsole (message: string) = Console.WriteLine(message)

open Services

/// Defines the setup and teardown for all tests in the Grace.Server.Tests namespace.
[<SetUpFixture>]
type Setup() =

    [<OneTimeSetUp>]
    member public _.Setup() =
        task {
            logToTestConsole "Starting Aspire test host..."

            testUserId <- $"{Guid.NewGuid()}"
            let! hostState = AspireTestHost.startAsync testUserId

            App <- Some hostState.App
            Client <- hostState.Client
            graceServerBaseAddress <- hostState.GraceServerBaseAddress
            serviceBusConnectionString <- hostState.ServiceBusConnectionString
            serviceBusTopic <- hostState.ServiceBusTopic
            serviceBusServerSubscription <- hostState.ServiceBusServerSubscription
            serviceBusTestSubscription <- hostState.ServiceBusTestSubscription
            testUserClaims <- [ "engineering"; "contributors" ]

            Client.DefaultRequestHeaders.Add("x-grace-user-id", testUserId)

            logToTestConsole $"Grace.Server base address: {graceServerBaseAddress}"

            logToTestConsole
                $"Service Bus topic: {serviceBusTopic}; subscription: {serviceBusServerSubscription}; test subscription: {serviceBusTestSubscription}"

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
                    repositoryIds,
                    Constants.ParallelOptions,
                    (fun repositoryId ct ->
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
                            }
                        ))
                )
        }

    [<OneTimeTearDown>]
    member public _.Teardown() =
        task {
            let correlationId = generateCorrelationId ()
            let logCleanupFailure (label: string) (detail: string) = logToTestConsole $"Cleanup {label} failed: {detail}"

            let cleanupEnabled =
                match Environment.GetEnvironmentVariable("GRACE_TEST_CLEANUP") with
                | null -> false
                | value ->
                    value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

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
                logToTestConsole "Skipping server-side cleanup (set GRACE_TEST_CLEANUP=1 to enable)."

            if not (isNull Client) then Client.Dispose()

            try
                do! AspireTestHost.stopAsync App
            with
            | ex -> logCleanupFailure "apphost stop" ex.Message

        }

[<Parallelizable(ParallelScope.All)>]
type General() =

    [<Test>]
    member public _.RootPathReturnsValue() =
        task {
            let! response = Services.Client.GetAsync("/")
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}")
            Assert.That(content, Does.Contain("Grace"))
        }

    [<Test>]
    member public _.MetricsRequiresAuthentication() =
        task {
            use client = new HttpClient()
            client.BaseAddress <- Services.Client.BaseAddress

            let! response = client.GetAsync("/metrics")
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }
