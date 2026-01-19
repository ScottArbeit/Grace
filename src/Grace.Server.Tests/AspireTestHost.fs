namespace Grace.Server.Tests

open Aspire.Hosting
open Aspire.Hosting.ApplicationModel
open Aspire.Hosting.Testing
open Aspire.Hosting.Azure
open Azure.Messaging.ServiceBus
open Grace.Shared
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Security
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Types
open Grace.Shared.Utilities

type TestHostState =
    {
        App: DistributedApplication
        Client: HttpClient
        GraceServerBaseAddress: string
        ServiceBusConnectionString: string
        ServiceBusTopic: string
        ServiceBusServerSubscription: string
        ServiceBusTestSubscription: string
    }

module AspireTestHost =

    let private graceServerResourceName = "grace-server"
    let private azuriteResourceName = "azurite"
    let private serviceBusSqlResourceName = "servicebus-sql"
    let private serviceBusEmulatorResourceName = "servicebus-emulator"
    let private isCi =
        match Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), Environment.GetEnvironmentVariable("CI") with
        | value, _ when not (String.IsNullOrWhiteSpace value) -> true
        | _, value when not (String.IsNullOrWhiteSpace value) -> true
        | _ -> false

    let private getTimeout (local: TimeSpan) (ci: TimeSpan) = if isCi then ci else local

    let private defaultWaitTimeout = getTimeout (TimeSpan.FromMinutes(5.0)) (TimeSpan.FromMinutes(10.0))

    let private getResource (app: DistributedApplication) (resourceName: string) =
        let model = app.Services.GetRequiredService<DistributedApplicationModel>()

        model.Resources
        |> Seq.tryFind (fun resource -> resource.Name = resourceName)
        |> Option.defaultWith (fun () -> failwith $"Resource '{resourceName}' not found in distributed application model.")

    let private tryFindResourceName<'T when 'T :> IResource> (app: DistributedApplication) =
        let model = app.Services.GetRequiredService<DistributedApplicationModel>()

        model.Resources
        |> Seq.tryFind (fun resource -> resource :? 'T)
        |> Option.map (fun resource -> resource.Name)

    let private tryFindResourceByName (app: DistributedApplication) (resourceName: string) =
        let model = app.Services.GetRequiredService<DistributedApplicationModel>()

        model.Resources
        |> Seq.tryFind (fun resource -> resource.Name = resourceName)

    let private getEndpointName (app: DistributedApplication) (resourceName: string) =
        let resource = getResource app resourceName
        let resourceWithEndpoints = resource :?> IResourceWithEndpoints
        let endpoints = resourceWithEndpoints.GetEndpoints() |> Seq.toList

        let byScheme scheme =
            endpoints
            |> List.tryFind (fun endpoint -> endpoint.EndpointAnnotation.UriScheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))

        match byScheme "http"
              |> Option.orElseWith (fun () -> byScheme "https")
            with
        | Some endpoint -> endpoint.EndpointAnnotation.Name
        | None ->
            match endpoints with
            | first :: _ -> first.EndpointAnnotation.Name
            | [] -> failwith $"No endpoints found for resource '{resourceName}'."

    let private getEnvironmentVariablesAsync (app: DistributedApplication) (resourceName: string) =
        task {
            let resource = getResource app resourceName
            let loggerFactory = app.Services.GetRequiredService<ILoggerFactory>()
            let logger = loggerFactory.CreateLogger("AspireTestHost.ExecutionConfiguration")
            let executionContext = DistributedApplicationExecutionContext(DistributedApplicationOperation.Run)

            let! configuration =
                ExecutionConfigurationBuilder
                    .Create(resource)
                    .WithEnvironmentVariablesConfig()
                    .BuildAsync(executionContext, logger, CancellationToken.None)

            if not (isNull configuration.Exception) then raise configuration.Exception

            let env = configuration.EnvironmentVariables

            return
                env
                |> Seq.map (fun kvp -> kvp.Key, string kvp.Value)
                |> Map.ofSeq
        }

    let private describeResourceState (notificationService: ResourceNotificationService) (resourceName: string) =
        let mutable resourceEvent = Unchecked.defaultof<ResourceEvent>

        if notificationService.TryGetCurrentState(resourceName, &resourceEvent) then
            let snapshot = resourceEvent.Snapshot

            let healthReports =
                snapshot.HealthReports
                |> Seq.map (fun report ->
                    let status = if report.Status.HasValue then report.Status.Value.ToString() else "Unknown"

                    let description = if String.IsNullOrWhiteSpace report.Description then "" else report.Description

                    let exceptionText =
                        if String.IsNullOrWhiteSpace report.ExceptionText then
                            ""
                        else
                            report.ExceptionText

                    $"{report.Name}={status}: {description} {exceptionText}"
                        .Trim())
                |> String.concat "; "

            $"State={snapshot.State}; Health={snapshot.HealthStatus}; ExitCode={snapshot.ExitCode}; HealthReports=[{healthReports}]"
        else
            "State unavailable."

    let private getResourceLogsAsync (app: DistributedApplication) (resourceName: string) =
        task {
            let loggerService = app.Services.GetRequiredService<ResourceLoggerService>()
            let lines = ResizeArray<string>()
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds(10.0))
            let logs = loggerService.GetAllAsync(resourceName)
            let enumerator = logs.GetAsyncEnumerator(cts.Token)

            try
                let mutable keepGoing = true

                while keepGoing do
                    let! hasNext = enumerator.MoveNextAsync().AsTask()

                    if hasNext then
                        let batch = enumerator.Current

                        for line in batch do
                            lines.Add(line.Content)
                    else
                        keepGoing <- false
            finally
                enumerator
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()

            return lines |> Seq.toList
        }

    let private waitForResourceHealthyAsync
        (notificationService: ResourceNotificationService)
        (app: DistributedApplication)
        (resourceName: string)
        (ct: CancellationToken)
        =
        task {
            try
                let! _ = notificationService.WaitForResourceHealthyAsync(resourceName, ct)
                ()
            with
            | ex ->
                let details = describeResourceState notificationService resourceName
                let! logLines = getResourceLogsAsync app resourceName

                let lastLines =
                    logLines
                    |> List.rev
                    |> List.truncate 50
                    |> List.rev
                    |> String.concat Environment.NewLine

                let logDetails =
                    if String.IsNullOrWhiteSpace lastLines then
                        "No logs captured."
                    else
                        $"Last logs:{Environment.NewLine}{lastLines}"

                raise (Exception($"{resourceName} failed to start. {details}{Environment.NewLine}{logDetails}", ex))
        }

    let private requireEnv (resourceName: string) (key: string) (env: Map<string, string>) =
        env
        |> Map.tryFind key
        |> Option.defaultWith (fun () -> failwith $"Missing env var '{key}' for resource '{resourceName}'.")

    let private redactServiceBusConnectionString (connectionString: string) =
        if String.IsNullOrWhiteSpace connectionString then
            connectionString
        else
            connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun part ->
                if part.StartsWith("SharedAccessKey=", StringComparison.OrdinalIgnoreCase) then
                    "SharedAccessKey=***"
                else
                    part)
            |> String.concat ";"

    let private redactStorageConnectionString (connectionString: string) =
        if String.IsNullOrWhiteSpace connectionString then
            connectionString
        else
            connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun part ->
                if part.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase) then
                    "AccountKey=***"
                else
                    part)
            |> String.concat ";"

    let private redactCosmosConnectionString (connectionString: string) =
        if String.IsNullOrWhiteSpace connectionString then
            connectionString
        else
            connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun part ->
                if part.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase) then
                    "AccountKey=***"
                else
                    part)
            |> String.concat ";"

    let private formatEnvDiagnostics (env: Map<string, string>) =
        let get key =
            match env |> Map.tryFind key with
            | Some value when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> "<missing>"

        [
            Constants.EnvironmentVariables.GraceLogDirectory
            Constants.EnvironmentVariables.OrleansClusterId
            Constants.EnvironmentVariables.OrleansServiceId
            Constants.EnvironmentVariables.DiffContainerName
            Constants.EnvironmentVariables.DirectoryVersionContainerName
            Constants.EnvironmentVariables.ZipFileContainerName
            Constants.EnvironmentVariables.AzureCosmosDBConnectionString
            Constants.EnvironmentVariables.AzureStorageConnectionString
            Constants.EnvironmentVariables.AzureServiceBusConnectionString
        ]
        |> List.map (fun key ->
            if key.Equals(Constants.EnvironmentVariables.AzureCosmosDBConnectionString, StringComparison.OrdinalIgnoreCase) then
                let value =
                    env
                    |> Map.tryFind key
                    |> Option.defaultValue String.Empty

                $"{key}={redactCosmosConnectionString value}"
            else if key.Equals(Constants.EnvironmentVariables.AzureStorageConnectionString, StringComparison.OrdinalIgnoreCase) then
                let value =
                    env
                    |> Map.tryFind key
                    |> Option.defaultValue String.Empty

                $"{key}={redactStorageConnectionString value}"
            else if key.Equals(Constants.EnvironmentVariables.AzureServiceBusConnectionString, StringComparison.OrdinalIgnoreCase) then
                let value =
                    env
                    |> Map.tryFind key
                    |> Option.defaultValue String.Empty

                $"{key}={redactServiceBusConnectionString value}"
            else
                $"{key}={get key}")
        |> String.concat "; "

    let private tryGetLatestLogTail (logDirectory: string) =
        try
            if Directory.Exists(logDirectory) then
                let latest =
                    Directory.EnumerateFiles(logDirectory, "*.log")
                    |> Seq.sortByDescending (fun path -> File.GetLastWriteTimeUtc(path))
                    |> Seq.tryHead

                match latest with
                | None -> None
                | Some path ->
                    let lines = File.ReadAllLines(path)

                    let tail =
                        lines
                        |> Seq.skip (max 0 (lines.Length - 50))
                        |> String.concat Environment.NewLine

                    Some($"Latest Grace.Server log: {path}{Environment.NewLine}{tail}")
            else
                None
        with
        | _ -> None

    let private waitForServiceBusReadyAsync (state: TestHostState) =
        task {
            let sw = Stopwatch.StartNew()
            let timeout = getTimeout (TimeSpan.FromSeconds(60.0)) (TimeSpan.FromMinutes(3.0))
            let mutable lastError = String.Empty
            let mutable ready = false

            while not ready do
                if sw.Elapsed >= timeout then
                    raise (TimeoutException($"Timed out waiting for Service Bus emulator. Last error: {lastError}"))

                try
                    let client = ServiceBusClient(state.ServiceBusConnectionString)
                    use _client = client

                    let receiver =
                        client.CreateReceiver(
                            state.ServiceBusTopic,
                            state.ServiceBusTestSubscription,
                            ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock)
                        )

                    use _receiver = receiver

                    let! _ = receiver.PeekMessageAsync()
                    ready <- true
                with
                | ex ->
                    lastError <- ex.Message
                    do! Task.Delay(TimeSpan.FromSeconds(1.0))
        }

    let private waitForCosmosReadyAsync (connectionString: string) (databaseName: string) (containerName: string) =
        task {
            if String.IsNullOrWhiteSpace connectionString then
                return ()
            else if String.IsNullOrWhiteSpace databaseName
                    || String.IsNullOrWhiteSpace containerName then
                return ()
            else
                let handler =
                    new SocketsHttpHandler(
                        SslOptions =
                            new SslClientAuthenticationOptions(TargetHost = "localhost", RemoteCertificateValidationCallback = (fun _ __ ___ ____ -> true))
                    )

                let options = CosmosClientOptions(ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = false)
                options.RequestTimeout <- TimeSpan.FromSeconds(10.0)
                options.HttpClientFactory <- (fun () -> new HttpClient(handler, disposeHandler = true))

                use client = new CosmosClient(connectionString, options)
                let sw = Stopwatch.StartNew()
                let timeout = getTimeout (TimeSpan.FromMinutes(3.0)) (TimeSpan.FromMinutes(6.0))
                let perCallTimeout = TimeSpan.FromSeconds(10.0)
                let mutable lastError = String.Empty
                let mutable attempt = 0
                let mutable ready = false

                while not ready do
                    if sw.Elapsed >= timeout then
                        let diagnostics =
                            $"Timed out waiting for Cosmos emulator. Attempts={attempt}. LastError={lastError}. Database={databaseName}. Container={containerName}. ConnectionString={redactCosmosConnectionString connectionString}"

                        raise (TimeoutException(diagnostics))

                    attempt <- attempt + 1

                    try
                        let! _ =
                            client
                                .ReadAccountAsync()
                                .WaitAsync(perCallTimeout)

                        ready <- true
                    with
                    | ex ->
                    lastError <- ex.Message
                    do! Task.Delay(TimeSpan.FromSeconds(1.0))
        }

    let private waitForGraceServerHttpReadyAsync (client: HttpClient) (ct: CancellationToken) =
        task {
            let perRequestTimeout = getTimeout (TimeSpan.FromSeconds(10.0)) (TimeSpan.FromSeconds(20.0))
            let sw = Stopwatch.StartNew()
            let mutable attempt = 0
            let mutable lastError = String.Empty

            Console.WriteLine($"Waiting for Grace.Server HTTP readiness at {client.BaseAddress}...")

            let rec loop () =
                task {
                    if ct.IsCancellationRequested then
                        raise (TimeoutException($"Timed out waiting for Grace.Server HTTP readiness. Last error: {lastError}"))

                    attempt <- attempt + 1

                    try
                        use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        linkedCts.CancelAfter(perRequestTimeout)

                        use! response = client.GetAsync("/healthz", linkedCts.Token)

                        if response.IsSuccessStatusCode then
                            Console.WriteLine($"Grace.Server HTTP readiness confirmed after {sw.Elapsed.TotalSeconds:n1}s (attempt {attempt}).")
                        else
                            lastError <- $"Status {(int response.StatusCode)} {response.StatusCode}"
                            do! Task.Delay(TimeSpan.FromSeconds(1.0), ct)
                            return! loop ()
                    with
                    | ex ->
                        lastError <- ex.Message
                        do! Task.Delay(TimeSpan.FromSeconds(1.0), ct)
                        return! loop ()
                }

            do! loop ()
        }

    let startAsync () =
        task {
            Environment.SetEnvironmentVariable("GRACE_TESTING", "1")
            Environment.SetEnvironmentVariable("GRACE_TEST_CLEANUP", "1")
            Environment.SetEnvironmentVariable("ASPIRE_RESOURCE_MODE", "Local")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcAuthority, "https://auth.grace.test")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcAudience, "https://api.grace.test")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcCliClientId, "grace-cli-test-client")
            let! builder = DistributedApplicationTestingBuilder.CreateAsync<Projects.Grace_Aspire_AppHost>()
            let! app = builder.BuildAsync()
            do! app.StartAsync()

            let notificationService = app.Services.GetRequiredService<ResourceNotificationService>()
            let model = app.Services.GetRequiredService<DistributedApplicationModel>()

            for resource in model.Resources do
                Console.WriteLine($"Resource: {resource.Name} ({resource.GetType().Name})")

                match resource with
                | :? IResourceWithEndpoints as resourceWithEndpoints ->
                    for endpoint in resourceWithEndpoints.GetEndpoints() do
                        let endpointName = endpoint.EndpointAnnotation.Name
                        let endpointUri = app.GetEndpoint(resource.Name, endpointName)
                        Console.WriteLine($"  Endpoint: {resource.Name}/{endpointName} -> {endpointUri}")
                | _ -> ()

            use cts = new CancellationTokenSource(defaultWaitTimeout)

            match tryFindResourceByName app azuriteResourceName with
            | Some _ ->
                Console.WriteLine($"Azurite resource detected: {azuriteResourceName}")
                do! waitForResourceHealthyAsync notificationService app azuriteResourceName cts.Token
            | None -> Console.WriteLine("Azurite resource not found in model.")

            match tryFindResourceByName app serviceBusSqlResourceName with
            | Some _ ->
                Console.WriteLine($"Service Bus SQL resource detected: {serviceBusSqlResourceName}")
                do! waitForResourceHealthyAsync notificationService app serviceBusSqlResourceName cts.Token
            | None -> Console.WriteLine("Service Bus SQL resource not found in model.")

            match tryFindResourceName<AzureCosmosDBEmulatorResource> app with
            | Some name ->
                Console.WriteLine($"Cosmos emulator resource detected: {name}")
                do! waitForResourceHealthyAsync notificationService app name cts.Token
            | None -> Console.WriteLine("Cosmos emulator resource not found in model.")

            do! waitForResourceHealthyAsync notificationService app serviceBusEmulatorResourceName cts.Token
            let! env = getEnvironmentVariablesAsync app graceServerResourceName

            match env
                  |> Map.tryFind Constants.EnvironmentVariables.GraceLogDirectory
                with
            | Some value when not (String.IsNullOrWhiteSpace value) -> Console.WriteLine($"GraceLogDirectory: {value}")
            | _ -> Console.WriteLine("GraceLogDirectory: <missing>")

            match env
                  |> Map.tryFind Constants.EnvironmentVariables.DebugEnvironment
                with
            | Some value when not (String.IsNullOrWhiteSpace value) -> Console.WriteLine($"DebugEnvironment: {value}")
            | _ -> Console.WriteLine("DebugEnvironment: <missing>")

            match env
                  |> Map.tryFind Constants.EnvironmentVariables.AzureStorageConnectionString
                with
            | Some value when not (String.IsNullOrWhiteSpace value) -> Console.WriteLine($"AzureStorageConnectionString: {redactStorageConnectionString value}")
            | _ -> Console.WriteLine("AzureStorageConnectionString: <missing>")

            match env
                  |> Map.tryFind Constants.EnvironmentVariables.AzureServiceBusConnectionString
                with
            | Some value when not (String.IsNullOrWhiteSpace value) ->
                Console.WriteLine($"AzureServiceBusConnectionString: {redactServiceBusConnectionString value}")
            | _ -> Console.WriteLine("AzureServiceBusConnectionString: <missing>")

            match env
                  |> Map.tryFind Constants.EnvironmentVariables.AzureCosmosDBConnectionString
                with
            | Some value when not (String.IsNullOrWhiteSpace value) -> Console.WriteLine($"AzureCosmosDBConnectionString: {redactCosmosConnectionString value}")
            | _ -> Console.WriteLine("AzureCosmosDBConnectionString: <missing>")

            let cosmosConnectionString =
                env
                |> Map.tryFind Constants.EnvironmentVariables.AzureCosmosDBConnectionString
                |> Option.defaultValue String.Empty

            let cosmosDatabaseName =
                env
                |> Map.tryFind Constants.EnvironmentVariables.AzureCosmosDBDatabaseName
                |> Option.defaultValue String.Empty

            let cosmosContainerName =
                env
                |> Map.tryFind Constants.EnvironmentVariables.AzureCosmosDBContainerName
                |> Option.defaultValue String.Empty

            do! waitForCosmosReadyAsync cosmosConnectionString cosmosDatabaseName cosmosContainerName

            try
                do! waitForResourceHealthyAsync notificationService app graceServerResourceName cts.Token
            with
            | ex ->
                let details = describeResourceState notificationService graceServerResourceName
                let! logLines = getResourceLogsAsync app graceServerResourceName
                let envDetails = formatEnvDiagnostics env

                let graceLogDetails =
                    env
                    |> Map.tryFind Constants.EnvironmentVariables.GraceLogDirectory
                    |> Option.bind tryGetLatestLogTail
                    |> Option.defaultValue "No Grace.Server log file captured."

                let lastLines =
                    logLines
                    |> List.rev
                    |> List.truncate 20
                    |> List.rev
                    |> String.concat Environment.NewLine

                let logDetails =
                    if String.IsNullOrWhiteSpace lastLines then
                        "No grace-server logs captured."
                    else
                        $"Last grace-server logs:{Environment.NewLine}{lastLines}"

                raise (
                    Exception(
                        $"Grace-server failed to start. {details}{Environment.NewLine}Env: {envDetails}{Environment.NewLine}{logDetails}{Environment.NewLine}{graceLogDetails}",
                        ex
                    )
                )

            let endpointName = getEndpointName app graceServerResourceName
            let endpointUri = app.GetEndpoint(graceServerResourceName, endpointName)
            let client = app.CreateHttpClient(graceServerResourceName, endpointName)
            client.Timeout <- getTimeout (TimeSpan.FromSeconds(100.0)) (TimeSpan.FromMinutes(5.0))

            do! waitForGraceServerHttpReadyAsync client cts.Token

            let diagnosticsPath = Path.Combine(Path.GetTempPath(), "grace-server-tests.host.log")

            let tryGetConnValue (prefix: string) (value: string) =
                value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryPick (fun segment ->
                    if segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                        Some(segment.Substring(prefix.Length))
                    else
                        None)
                |> Option.defaultValue "<missing>"

            let cosmosConn =
                env
                |> Map.tryFind Constants.EnvironmentVariables.AzureCosmosDBConnectionString
                |> Option.defaultValue String.Empty

            let cosmosEndpoint = tryGetConnValue "AccountEndpoint=" cosmosConn
            let cosmosConnRedacted = redactCosmosConnectionString cosmosConn

            let serviceBusConn =
                env
                |> Map.tryFind Constants.EnvironmentVariables.AzureServiceBusConnectionString
                |> Option.defaultValue String.Empty

            let serviceBusEndpoint = tryGetConnValue "Endpoint=" serviceBusConn

            let diagnosticsLine =
                $"GraceServer endpoint={endpointUri}; EndpointName={endpointName}; UsingBase={client.BaseAddress}; CosmosEndpoint={cosmosEndpoint}; CosmosConn={cosmosConnRedacted}; ServiceBusEndpoint={serviceBusEndpoint}"

            File.AppendAllText(diagnosticsPath, diagnosticsLine + Environment.NewLine)

            let serviceBusConnectionString = requireEnv graceServerResourceName Constants.EnvironmentVariables.AzureServiceBusConnectionString env

            let serviceBusTopic = requireEnv graceServerResourceName Constants.EnvironmentVariables.AzureServiceBusTopic env

            let serviceBusSubscription = requireEnv graceServerResourceName Constants.EnvironmentVariables.AzureServiceBusSubscription env

            let serviceBusTestSubscription = $"{serviceBusSubscription}-tests"

            let baseAddress = endpointUri.ToString().TrimEnd('/')

            let state =
                {
                    App = app
                    Client = client
                    GraceServerBaseAddress = baseAddress
                    ServiceBusConnectionString = serviceBusConnectionString
                    ServiceBusTopic = serviceBusTopic
                    ServiceBusServerSubscription = serviceBusSubscription
                    ServiceBusTestSubscription = serviceBusTestSubscription
                }

            do! waitForServiceBusReadyAsync state

            return state
        }

    let stopAsync (app: DistributedApplication option) =
        task {
            match app with
            | None -> ()
            | Some appHost ->
                Console.WriteLine("Stopping Aspire host...")

                let awaitWithTimeout (label: string) (timeout: TimeSpan) (work: Task) =
                    task {
                        let! completed = Task.WhenAny(work, Task.Delay(timeout))

                        if completed <> work then
                            Console.WriteLine($"{label} timed out after {timeout.TotalSeconds}s; continuing shutdown.")
                            return false
                        else
                            do! work
                            return true
                    }

                let! _ = awaitWithTimeout "Aspire host stop" (TimeSpan.FromSeconds(5.0)) (appHost.StopAsync())
                let! _ = awaitWithTimeout "Aspire host dispose" (TimeSpan.FromSeconds(5.0)) (appHost.DisposeAsync().AsTask())
                Console.WriteLine("Aspire host shutdown sequence completed.")
        }

    let private createServiceBusReceiver (state: TestHostState) =
        let options = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete)
        let client = ServiceBusClient(state.ServiceBusConnectionString)
        let receiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, options)
        client, receiver

    let drainServiceBusAsync (state: TestHostState) =
        task {
            let client, receiver = createServiceBusReceiver state
            use _client = client
            use _receiver = receiver

            let mutable drainedCount = 0
            let mutable keepGoing = true

            while keepGoing do
                let! message = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1.0))

                if isNull message then keepGoing <- false else drainedCount <- drainedCount + 1

            return drainedCount
        }

    let waitForOwnerCreatedEventAsync (state: TestHostState) (ownerId: string) =
        task {
            let client, receiver = createServiceBusReceiver state
            use _client = client
            use _receiver = receiver

            let sw = Stopwatch.StartNew()
            let timeout = TimeSpan.FromSeconds(30.0)
            let lastBodies = ResizeArray<string>()
            let maxBodies = 5

            let mutable parsedOwnerId = Guid.Empty
            let hasParsedOwnerId = Guid.TryParse(ownerId, &parsedOwnerId)
            let mutable found: ServiceBusReceivedMessage option = None

            while found.IsNone do
                let remaining = timeout - sw.Elapsed

                if remaining <= TimeSpan.Zero then
                    let diagnostics =
                        StringBuilder()
                            .AppendLine("Timed out waiting for Owner Created event on Service Bus test subscription.")
                            .AppendLine($"GraceServerBaseAddress: {state.GraceServerBaseAddress}")
                            .AppendLine($"ServiceBusTopic: {state.ServiceBusTopic}")
                            .AppendLine($"ServiceBusSubscription: {state.ServiceBusServerSubscription}")
                            .AppendLine($"ServiceBusTestSubscription: {state.ServiceBusTestSubscription}")
                            .AppendLine($"ServiceBusConnectionString: {redactServiceBusConnectionString state.ServiceBusConnectionString}")
                            .AppendLine($"LastMessageBodies({lastBodies.Count}):")
                            .AppendLine(String.Join(Environment.NewLine, lastBodies))
                            .ToString()

                    raise (TimeoutException(diagnostics))

                let waitTime =
                    if remaining < TimeSpan.FromSeconds(2.0) then
                        remaining
                    else
                        TimeSpan.FromSeconds(2.0)

                let! message = receiver.ReceiveMessageAsync(waitTime)

                if not (isNull message) then
                    let body = message.Body.ToString()

                    if lastBodies.Count >= maxBodies then lastBodies.RemoveAt(0)

                    lastBodies.Add(body)

                    let tryMatchOwnerCreated (ownerEvent: Owner.OwnerEvent) =
                        match ownerEvent.Event with
                        | Owner.OwnerEventType.Created (createdOwnerId, _) when hasParsedOwnerId && createdOwnerId = parsedOwnerId -> true
                        | _ -> false

                    let matchesOwnerCreated =
                        try
                            match JsonSerializer.Deserialize<Events.GraceEvent>(body, Constants.JsonSerializerOptions) with
                            | Events.GraceEvent.OwnerEvent ownerEvent -> tryMatchOwnerCreated ownerEvent
                            | _ -> false
                        with
                        | _ ->
                            try
                                let ownerEvent = JsonSerializer.Deserialize<Owner.OwnerEvent>(body, Constants.JsonSerializerOptions)
                                tryMatchOwnerCreated ownerEvent
                            with
                            | _ -> false

                    if matchesOwnerCreated then found <- Some message

            return found.Value
        }
