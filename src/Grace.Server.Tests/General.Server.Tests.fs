namespace Grace.Server.Tests

open CliWrap
open CliWrap.Buffered
open CliWrap.EventStream
open Grace.Shared
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Mvc.Testing
open NUnit.Framework
open System
open System.Net.Http.Json
open System.Reflection.Metadata
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Azure.Core
open System.Net.Http
open System.IO
open System.Text
open System.Diagnostics

module Common =
    let okResult: Result<unit, string> = Result.Ok()
    let errorResult: Result<unit, string> = Result.Error "error"

module Services =
    let daprDirectory = @"C:\dapr"
    let daprExecutablePath = Path.Combine(daprDirectory, "dapr.exe")
    let dockerExecutablePath = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe"
    let graceServerPath = Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..", "Grace.Server")

    let graceServerAppId = "grace-server-integration-test"
    let daprSchedulerContainerName = "dapr-scheduler-integration-test"
    let daprPlacementContainerName = "dapr-placement-integration-test"
    let zipkinContainerName = "zipkin-integration-test"
    let daprAppPort = "5002"
    let daprHttpPort = "3551"
    let daprGrpcPort = "50051"
    let daprPlacementPort = "6055"
    let daprSchedulerPort = "6065"
    let daprSchedulerMetricsPort = "9070"
    let zipkinPort = "9412"

    let numberOfRepositories = 3
    let ownerId = $"{Guid.NewGuid()}"
    let organizationId = $"{Guid.NewGuid()}"
    let repositoryIds = Array.init numberOfRepositories (fun _ -> $"{Guid.NewGuid()}")

    let rnd = Random.Shared
    let Client = new HttpClient(handler = socketsHttpHandler, disposeHandler = false, BaseAddress = new Uri($"http://127.0.0.1:{daprAppPort}"))

    let logToTestConsole (message: string) = TestContext.Progress.WriteLine(message)

//let clientOptions = WebApplicationFactoryClientOptions(
//    BaseAddress = new Uri($"{serverUri}:{daprAppPort}"),
//    MaxAutomaticRedirections = 3)

//let public Factory =
//    try
//        logToTestConsole $"Creating WebApplicationFactory with serverUri: {serverUri} and daprAppPort: {daprAppPort}.")
//        let factory =
//            (new WebApplicationFactory<Grace.Server.Application.Startup>())
//                .WithWebHostBuilder(fun builder ->
//                    builder
//                        //.CaptureStartupErrors(true)
//                        .UseEnvironment("Development")
//                        .UseKestrel()
//                    |> ignore)

//        logToTestConsole $"WebApplicationFactory created successfully. {factory.Server.BaseAddress}")
//        factory
//    with ex ->
//        logToTestConsole $"Error creating WebApplicationFactory: {ex.Message}{Environment.NewLine}Stack trace:{Environment.NewLine}{ex.StackTrace}")
//        new WebApplicationFactory<Grace.Server.Application.Startup>()

//let public Client = Factory.CreateClient(clientOptions)

open Services
open FSharpPlus
open FSharp.Control
open Grace.Types.Types

/// Defines the setup and teardown for all tests in the Grace.Server.Tests namespace.
[<SetUpFixture>]
type Setup() =

    /// Makes sure that the Dapr and Zipkin containers are stopped and deleted before and after the test run.
    member private this.DeleteContainers() =
        task {
            try
                let sbOutput = StringBuilder()
                let sbError = StringBuilder()

                // Kill any existing Dapr and dotnet processes.
                Process.GetProcesses()
                |> Seq.iter (fun p ->
                    match p.ProcessName with
                    | "dapr"
                    | "daprd"
                    | "dotnet" -> if p.Id <> Process.GetCurrentProcess().Id then p.Kill()
                    | _ -> ())

                // Stop the Dapr scheduler container.
                let daprSchedulerDockerArguments = [| "rm"; daprSchedulerContainerName; "--force" |]

                let! daprSchedulerResult =
                    Cli
                        .Wrap(dockerExecutablePath)
                        .WithArguments(daprSchedulerDockerArguments)
                        .WithValidation(CommandResultValidation.None)
                        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sbOutput))
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sbError))
                        .ExecuteBufferedAsync()

                if daprSchedulerResult.ExitCode = 0 then
                    logToTestConsole $"Dapr scheduler container stopped successfully. Output: {sbOutput}"
                else
                    let msg = $"Dapr scheduler container failed to stop. Exit code: {daprSchedulerResult.ExitCode}. Output: {sbOutput}. Error: {sbError}."
                    logToTestConsole msg

                sbOutput.Clear() |> ignore
                sbError.Clear() |> ignore

                // Stop the Dapr placement container.
                let daprPlacementDockerArguments = [| "rm"; daprPlacementContainerName; "--force" |]

                let! daprPlacementResult =
                    Cli
                        .Wrap(dockerExecutablePath)
                        .WithArguments(daprPlacementDockerArguments)
                        .WithValidation(CommandResultValidation.None)
                        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sbOutput))
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sbError))
                        .ExecuteBufferedAsync()

                if daprPlacementResult.ExitCode = 0 then
                    logToTestConsole $"Dapr placement container stopped successfully. Output: {sbOutput}"
                else
                    let msg = $"Dapr placement container failed to stop. Exit code: {daprPlacementResult.ExitCode}. Output: {sbOutput}. Error: {sbError}."
                    logToTestConsole msg

                sbOutput.Clear() |> ignore
                sbError.Clear() |> ignore

                // Stop the Zipkin container.
                let zipKinDockerArguments = [| "rm"; zipkinContainerName; "--force" |]

                let! zipkinResult =
                    Cli
                        .Wrap(dockerExecutablePath)
                        .WithArguments(zipKinDockerArguments)
                        .WithValidation(CommandResultValidation.None)
                        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sbOutput))
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sbError))
                        .ExecuteBufferedAsync()

                if zipkinResult.ExitCode = 0 then
                    logToTestConsole $"Zipkin container stopped successfully. Output: {sbOutput}"
                else
                    let msg = $"Zipkin container failed to stop. Exit code: {zipkinResult.ExitCode}. Output: {sbOutput}. Error: {sbError}."
                    logToTestConsole msg
            with ex ->
                let msg =
                    $"Exception in DeleteContainers().{Environment.NewLine}Message: {ex.Message}{Environment.NewLine}Stack trace:{Environment.NewLine}{ex.StackTrace}"

                logToTestConsole msg
        }


    [<OneTimeSetUp>]
    member public this.Setup() =
        task {
            let sbOutput = StringBuilder()
            let sbError = StringBuilder()

            let homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            let daprComponentsPath = Path.Combine(homeDirectory, ".dapr", "components-integration")
            let daprConfigFilePath = Path.Combine(daprComponentsPath, "dapr-config.yaml")

            logToTestConsole $"Current process id: {Environment.ProcessId}. Process start time: {Process.GetCurrentProcess().StartTime}."
            logToTestConsole $"Current directory: {Environment.CurrentDirectory}"
            let correlationId = generateCorrelationId ()

            // Delete any existing integration test containers.
            do! this.DeleteContainers()

            let daprSchedulerDockerRunArguments =
                [| "run"
                   "-d"
                   "--name"
                   daprSchedulerContainerName
                   "--restart"
                   "always"
                   "-p"
                   $"{daprSchedulerPort}:50006"
                   "-p"
                   $"{daprSchedulerMetricsPort}:9090"
                   "daprio/dapr:1.14.1"
                   "./scheduler"
                   "--etcd-data-dir"
                   "/var/lock/dapr/scheduler" |]

            // Start the Dapr scheduler container.
            let! daprSchedulerResult =
                Cli
                    .Wrap(dockerExecutablePath)
                    .WithArguments(daprSchedulerDockerRunArguments)
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sbOutput))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sbError))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync()

            if daprSchedulerResult.ExitCode = 0 then
                logToTestConsole $"Dapr scheduler container started successfully. Output: {sbOutput}"
            else
                let msg = $"Dapr scheduler container failed to start. Exit code: {daprSchedulerResult.ExitCode}; Output: {sbOutput}; Error: {sbError}"
                logToTestConsole msg

            sbOutput.Clear() |> ignore
            sbError.Clear() |> ignore

            let daprPlacementDockerRunArguments =
                [| "run"
                   "-d"
                   "--name"
                   daprPlacementContainerName
                   "--restart"
                   "always"
                   "-p"
                   $"{daprPlacementPort}:50005"
                   "daprio/dapr:1.14.1"
                   "./placement"
                   "--log-level"
                   "debug"
                   "--enable-metrics"
                   "--replicationFactor"
                   "100"
                   "--max-api-level"
                   "10"
                   "--min-api-level"
                   "0"
                   "--metrics-port"
                   "9090" |]

            // Start the Dapr placement container.
            let! daprPlacementResult =
                Cli
                    .Wrap(dockerExecutablePath)
                    .WithArguments(daprPlacementDockerRunArguments)
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sbOutput))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sbError))
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteAsync()

            if daprPlacementResult.ExitCode = 0 then
                logToTestConsole $"Dapr placement container started successfully. Output: {sbOutput}"
            else
                let msg = $"Dapr placement container failed to start. Exit code: {daprPlacementResult.ExitCode}; Output: {sbOutput}; Error: {sbError}"
                logToTestConsole msg

            sbOutput.Clear() |> ignore
            sbError.Clear() |> ignore

            let zipkinArguments =
                [| "run"
                   "-d"
                   "-p"
                   $"{zipkinPort}:9411"
                   "--name"
                   zipkinContainerName
                   "openzipkin/zipkin:latest" |]

            let! zipkinResult =
                Cli
                    .Wrap(dockerExecutablePath)
                    .WithArguments(zipkinArguments)
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sbOutput))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sbError))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync()

            if zipkinResult.ExitCode = 0 then
                logToTestConsole $"Zipkin container started successfully. Output: {sbOutput}"
            else
                let msg = $"Zipkin container failed to start. Exit code: {zipkinResult.ExitCode}; Output: {sbOutput}; Error: {sbError}"
                logToTestConsole msg

            sbOutput.Clear() |> ignore
            sbError.Clear() |> ignore

            let applicationInsightsConnectionString = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.ApplicationInsightsConnectionString
            let azureStorageKey = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.AzureStorageKey
            let azureStorageConnectionString = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.AzureStorageConnectionString
            let azureCosmosDbConnectionString = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.AzureCosmosDBConnectionString
            let cosmosDatabaseName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.CosmosDatabaseName
            let cosmosContainerName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.CosmosContainerName

            let daprEnvironmentVariables =
                dict
                    [ "ASPNETCORE_ENVIRONMENT", "Development"
                      "ASPNETCORE_HTTP_PORT", daprAppPort
                      "ASPNETCORE_URLS", $"http://*:{daprAppPort}"
                      "DAPR_APP_PORT", daprAppPort
                      "DAPR_HTTP_PORT", daprHttpPort
                      "DAPR_GRPC_PORT", daprGrpcPort
                      "DAPR_SERVER_URI", "http://127.0.0.1"
                      "DAPR_APP_ID", graceServerAppId
                      "DAPR_PLACEMENT_HOST_ADDRESS", $"127.0.0.1:{daprPlacementPort}"
                      "DAPR_RESOURCES_PATH", daprComponentsPath
                      "DAPR_CONFIG", daprConfigFilePath
                      "application_insights_connection_string", applicationInsightsConnectionString
                      "azurestoragekey", azureStorageKey
                      "azurestorageconnectionstring", azureStorageConnectionString
                      "azurecosmosdbconnectionstring", azureCosmosDbConnectionString
                      "cosmosdatabasename", cosmosDatabaseName
                      "cosmoscontainername", cosmosContainerName ]
                |> Dict.toIReadOnlyDictionary

            let daprRuntimeArguments =
                [| "run"
                   "--app-id"
                   graceServerAppId
                   "--app-port"
                   daprAppPort
                   "--dapr-http-port"
                   daprHttpPort
                   "--dapr-grpc-port"
                   daprGrpcPort
                   "--log-level"
                   "debug"
                   "--enable-api-logging"
                   "--placement-host-address"
                   $"127.0.0.1:{daprPlacementPort}"
                   "--scheduler-host-address"
                   $"127.0.0.1:{daprSchedulerPort}"
                   "--resources-path"
                   $"{daprComponentsPath}"
                   "--config"
                   $"{daprConfigFilePath}"
                   "--"
                   "dotnet"
                   "run"
                   "-c"
                   "Debug"
                   "--no-build"
                   "--project"
                   "Grace.Server.fsproj" |]

            // Start the Dapr sidecar process.
            let startDaprCommand =
                Cli
                    .Wrap(daprExecutablePath)
                    .WithArguments(daprRuntimeArguments)
                    .WithWorkingDirectory(graceServerPath)
                    .WithEnvironmentVariables(daprEnvironmentVariables)
                    .WithValidation(CommandResultValidation.None)
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sbOutput))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(sbError))

            startDaprCommand.ListenAsync()
            |> TaskSeq.iter (fun ev ->
                match ev with
                | :? StartedCommandEvent -> logToTestConsole $"{ev}"
                | :? StandardOutputCommandEvent -> logToTestConsole $"{ev}"
                | :? StandardErrorCommandEvent -> logToTestConsole $"{ev}"
                | :? ExitedCommandEvent -> logToTestConsole $"Dapr process exited with code {(ev :?> ExitedCommandEvent).ExitCode}."
                | _ -> ())
            :> Task
            |> ignore

            // Give time for Dapr to warm up.
            logToTestConsole "Waiting for Dapr to warm up..."

            while not
                  <| (sbOutput.ToString().Contains("dapr initialized")
                      && (sbOutput.ToString().Contains("Placement order received: unlock"))) do
                do! Task.Delay(250)

            logToTestConsole "Warm up complete."

            try
                // Create the owner we'll use for this test run.
                let ownerParameters = Parameters.Owner.CreateOwnerParameters()
                ownerParameters.OwnerId <- ownerId
                ownerParameters.OwnerName <- $"TestOwner{rnd.Next(65535):X4}"
                ownerParameters.CorrelationId <- correlationId
                let! response = Client.PostAsync("/owner/create", createJsonContent ownerParameters)

                if response.IsSuccessStatusCode then
                    logToTestConsole $"Owner {ownerParameters.OwnerName} created successfully."
                else
                    let! content = response.Content.ReadAsStringAsync()
                    Assert.That(content.Length, Is.GreaterThan(0))
                    let error = deserialize<GraceError> content
                    logToTestConsole $"StatusCode: {response.StatusCode}; Content: {error}"

                response.EnsureSuccessStatusCode() |> ignore

                // Create the organization we'll use for this test run.
                let organizationParameters = Parameters.Organization.CreateOrganizationParameters()
                organizationParameters.OwnerId <- ownerId
                organizationParameters.OrganizationId <- organizationId
                organizationParameters.OrganizationName <- $"TestOrganization{rnd.Next(65535):X4}"
                organizationParameters.CorrelationId <- correlationId
                let! response = Client.PostAsync("/organization/create", createJsonContent organizationParameters)

                if response.IsSuccessStatusCode then
                    logToTestConsole $"Organization {organizationParameters.OrganizationName} created successfully."
                else
                    let! content = response.Content.ReadAsStringAsync()
                    Assert.That(content.Length, Is.GreaterThan(0))
                    let error = deserialize<GraceError> content
                    logToTestConsole $"StatusCode: {response.StatusCode}; Content: {error}"

                response.EnsureSuccessStatusCode() |> ignore

                // Create the repositories we'll use for this test run.
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
            with ex ->
                let msg =
                    $"Exception in Setup().{Environment.NewLine}Message: {ex.Message}{Environment.NewLine}Stack trace:{Environment.NewLine}{ex.StackTrace}"

                logToTestConsole msg
                exit -1
        }

    [<OneTimeTearDown>]
    member public this.Teardown() =
        task {
            let correlationId = generateCorrelationId ()

            try
                // Delete the repositories we created for this test run.
                do!
                    Parallel.ForEachAsync(
                        repositoryIds,
                        Constants.ParallelOptions,
                        (fun repositoryId ct ->
                            ValueTask(
                                task {
                                    let repositoryDeleteParameters = Parameters.Repository.DeleteRepositoryParameters()
                                    repositoryDeleteParameters.OwnerId <- ownerId
                                    repositoryDeleteParameters.OrganizationId <- organizationId
                                    repositoryDeleteParameters.RepositoryId <- repositoryId
                                    repositoryDeleteParameters.DeleteReason <- "Deleting test repository"
                                    repositoryDeleteParameters.CorrelationId <- correlationId
                                    repositoryDeleteParameters.Force <- true

                                    let! response = Client.PostAsync("/repository/delete", createJsonContent repositoryDeleteParameters)

                                    if not <| response.IsSuccessStatusCode then
                                        let! content = response.Content.ReadAsStringAsync()
                                        Assert.That(content.Length, Is.GreaterThan(0))
                                        let error = deserialize<GraceError> content
                                        logToTestConsole $"StatusCode: {response.StatusCode}; Content: {error}"

                                    response.EnsureSuccessStatusCode() |> ignore
                                }
                            ))
                    )

                // Delete the organization we created for this test run.
                let organizationDeleteParameters = Parameters.Organization.DeleteOrganizationParameters()
                organizationDeleteParameters.OwnerId <- ownerId
                organizationDeleteParameters.OrganizationId <- organizationId
                organizationDeleteParameters.DeleteReason <- "Deleting test organization"
                organizationDeleteParameters.CorrelationId <- correlationId
                organizationDeleteParameters.Force <- true
                let! response = Client.PostAsync("/organization/delete", createJsonContent organizationDeleteParameters)

                if not <| response.IsSuccessStatusCode then
                    let! content = response.Content.ReadAsStringAsync()
                    Assert.That(content.Length, Is.GreaterThan(0))
                    let error = deserialize<GraceError> content
                    logToTestConsole $"StatusCode: {response.StatusCode}; Content: {error}"

                // Delete the owner we created for this test run.
                let ownerDeleteParameters = Parameters.Owner.DeleteOwnerParameters()
                ownerDeleteParameters.OwnerId <- ownerId
                ownerDeleteParameters.DeleteReason <- "Deleting test owner"
                ownerDeleteParameters.CorrelationId <- generateCorrelationId ()
                ownerDeleteParameters.Force <- true
                let! response = Client.PostAsync("/owner/delete", createJsonContent ownerDeleteParameters)

                if not <| response.IsSuccessStatusCode then
                    let! content = response.Content.ReadAsStringAsync()
                    Assert.That(content.Length, Is.GreaterThan(0))
                    let error = deserialize<GraceError> content
                    logToTestConsole $"StatusCode: {response.StatusCode}; Content: {error}"

                response.EnsureSuccessStatusCode() |> ignore

                // Delete all of the integration test containers.
                do! this.DeleteContainers()
            with ex ->
                let msg =
                    $"Exception in Teardown().{Environment.NewLine}Message: {ex.Message}{Environment.NewLine}Stack trace:{Environment.NewLine}{ex.StackTrace}"

                logToTestConsole msg
        }

[<Parallelizable(ParallelScope.All)>]
type General() =

    [<Test>]
    member public this.RootPathReturnsValue() =
        task {
            let! response = Services.Client.GetAsync("/")
            let! content = response.Content.ReadAsStringAsync()
            Console.WriteLine($"{content}")
            Assert.That(content, Does.Contain("Grace"))
        }
