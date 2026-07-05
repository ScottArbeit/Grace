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
open NUnit.Framework
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Types
open Grace.Shared.Utilities

/// Captures test host state values used by the test suite.
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

/// Groups shared helpers for aspire test host.
module AspireTestHost =
    do AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> Environment.ExitCode <- 0)

    let private graceServerResourceName = "grace-server"
    let private azuriteResourceName = "azurite"
    let private sharedStateLock = new SemaphoreSlim(1, 1)
    let mutable private sharedState: TestHostState option = None
    let mutable private sharedBootstrapUserId: string option = None

    /// Gets service bus sql resource name from the running test server.
    let private getServiceBusSqlResourceName () =
        match Environment.GetEnvironmentVariable("GRACE_TEST_RUN_ID") with
        | value when not (String.IsNullOrWhiteSpace value) -> $"servicebus-sql-{value}"
        | _ -> "servicebus-sql"

    /// Gets service bus emulator resource name from the running test server.
    let private getServiceBusEmulatorResourceName () =
        match Environment.GetEnvironmentVariable("GRACE_TEST_RUN_ID") with
        | value when not (String.IsNullOrWhiteSpace value) -> $"servicebus-emulator-{value}"
        | _ -> "servicebus-emulator"

    let private isCi =
        match Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), Environment.GetEnvironmentVariable("CI") with
        | value, _ when not (String.IsNullOrWhiteSpace value) -> true
        | _, value when not (String.IsNullOrWhiteSpace value) -> true
        | _ -> false

    /// Gets timeout from the running test server.
    let private getTimeout (local: TimeSpan) (ci: TimeSpan) = if isCi then ci else local

    let private defaultWaitTimeout = getTimeout (TimeSpan.FromMinutes(5.0)) (TimeSpan.FromMinutes(3.0))

    /// Defines log progress behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private logProgress (message: string) =
        let progressMessage = $"Grace.Server.Tests progress: {message}"

        TestContext.Progress.WriteLine(progressMessage)
        TestContext.Progress.Flush()
        Console.Error.WriteLine(progressMessage)
        Console.Error.Flush()

    /// Normalizes a deliberate Grace.Server restart context for diagnostic progress messages.
    let private normalizeRestartContext (restartContext: string) =
        if String.IsNullOrWhiteSpace restartContext then
            "unspecified test operation"
        else
            restartContext.Trim()

    /// Resolves the deliberate restart context when the wait is for the Grace.Server Aspire resource.
    let private tryGetGraceServerRestartContext (resourceName: string) (restartContext: string option) =
        match restartContext with
        | Some context when resourceName.Equals(graceServerResourceName, StringComparison.OrdinalIgnoreCase) -> Some(normalizeRestartContext context)
        | _ -> None

    /// Formats the progress message emitted before waiting for an Aspire resource to become healthy.
    let private formatResourceHealthWaitStartProgress (resourceName: string) (restartContext: string option) =
        match tryGetGraceServerRestartContext resourceName restartContext with
        | Some context -> $"intentional Grace.Server restart '{context}': waiting for resource '{resourceName}' to become healthy."
        | None -> $"waiting for resource '{resourceName}' to become healthy."

    /// Formats the progress message emitted after an Aspire resource reports healthy.
    let private formatResourceHealthWaitHealthyProgress (resourceName: string) (restartContext: string option) =
        match tryGetGraceServerRestartContext resourceName restartContext with
        | Some context -> $"resource '{resourceName}' recovered after intentional Grace.Server restart '{context}'."
        | None -> $"resource '{resourceName}' is healthy."

    /// Defines ensure bootstrap compatible behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private ensureBootstrapCompatible (bootstrapUserId: string) =
        match sharedBootstrapUserId with
        | None -> ()
        | Some existing when String.IsNullOrWhiteSpace bootstrapUserId ->
            if not (String.IsNullOrWhiteSpace existing) then
                Console.WriteLine($"Aspire test host already started with bootstrap user '{existing}'. Ignoring empty bootstrap request.")
        | Some existing when existing.Equals(bootstrapUserId, StringComparison.OrdinalIgnoreCase) -> ()
        | Some existing ->
            raise (
                InvalidOperationException(
                    $"Aspire test host already started with bootstrap user '{existing}'. Requested '{bootstrapUserId}' cannot be applied without restarting the host."
                )
            )

    /// Gets resource from the running test server.
    let private getResource (app: DistributedApplication) (resourceName: string) =
        let model = app.Services.GetRequiredService<DistributedApplicationModel>()

        model.Resources
        |> Seq.tryFind (fun resource -> resource.Name = resourceName)
        |> Option.defaultWith (fun () -> failwith $"Resource '{resourceName}' not found in distributed application model.")

    /// Tries to resolve find resource name without failing the caller.
    let private tryFindResourceName<'T when 'T :> IResource> (app: DistributedApplication) =
        let model = app.Services.GetRequiredService<DistributedApplicationModel>()

        model.Resources
        |> Seq.tryFind (fun resource -> resource :? 'T)
        |> Option.map (fun resource -> resource.Name)

    /// Tries to resolve find resource by name without failing the caller.
    let private tryFindResourceByName (app: DistributedApplication) (resourceName: string) =
        let model = app.Services.GetRequiredService<DistributedApplicationModel>()

        model.Resources
        |> Seq.tryFind (fun resource -> resource.Name = resourceName)

    /// Gets endpoint name from the running test server.
    let private getEndpointName (app: DistributedApplication) (resourceName: string) =
        let resource = getResource app resourceName
        let resourceWithEndpoints = resource :?> IResourceWithEndpoints
        let endpoints = resourceWithEndpoints.GetEndpoints() |> Seq.toList

        /// Defines by scheme behavior for the surrounding tests used by the server integration aspire Test Host scenario.
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

    /// Gets environment variables from the running test server.
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

    /// Defines describe resource state behavior for the surrounding tests used by the server integration aspire Test Host scenario.
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

    /// Gets resource logs from the running test server.
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

    /// Captures process result values used by the test suite.
    type private ProcessResult = { ExitCode: int option; StdOut: string; StdErr: string; TimedOut: bool; Error: string option }

    /// Runs process with the configured test context.
    let private runProcessAsync (fileName: string) (arguments: string) (timeout: TimeSpan) =
        task {
            try
                let startInfo = ProcessStartInfo(fileName, arguments)
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true

                use proc = new Process()
                proc.StartInfo <- startInfo

                if not (proc.Start()) then
                    return { ExitCode = None; StdOut = ""; StdErr = ""; TimedOut = false; Error = Some "Failed to start process." }
                else
                    let waitTask = proc.WaitForExitAsync()
                    let! completed = Task.WhenAny(waitTask, Task.Delay(timeout))

                    if completed <> waitTask then
                        try
                            proc.Kill(true)
                        with
                        | _ -> ()

                        return { ExitCode = None; StdOut = ""; StdErr = ""; TimedOut = true; Error = None }
                    else
                        let! stdOut = proc.StandardOutput.ReadToEndAsync()
                        let! stdErr = proc.StandardError.ReadToEndAsync()

                        return { ExitCode = Some proc.ExitCode; StdOut = stdOut; StdErr = stdErr; TimedOut = false; Error = None }
            with
            | ex -> return { ExitCode = None; StdOut = ""; StdErr = ""; TimedOut = false; Error = Some ex.Message }
        }

    /// Tries to resolve get environment value without failing the caller.
    let private tryGetEnvironmentValue name =
        match Environment.GetEnvironmentVariable(name) with
        | value when String.IsNullOrWhiteSpace value -> None
        | value -> Some value

    /// Determines whether truthy environment value for test-host decisions.
    let private isTruthyEnvironmentValue (value: string) =
        value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

    /// Defines should cleanup docker behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private shouldCleanupDocker () =
        match tryGetEnvironmentValue "GRACE_TEST_DOCKER_CLEANUP" with
        | Some value -> isTruthyEnvironmentValue value
        | None ->
            match tryGetEnvironmentValue "GRACE_TEST_CLEANUP" with
            | Some value -> isTruthyEnvironmentValue value
            | None -> false

    /// Defines should skip service bus behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private shouldSkipServiceBus () =
        match Environment.GetEnvironmentVariable("GRACE_TEST_SKIP_SERVICEBUS") with
        | null -> false
        | value when
            value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            ->
            true
        | _ -> false

    /// Defines cleanup docker containers behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private cleanupDockerContainersAsync () =
        task {
            if shouldCleanupDocker () then
                let containerPrefixes =
                    [
                        "servicebus-sql"
                        "servicebus-emulator"
                        "cosmosdb-emulator"
                        "azurite"
                        "redis"
                    ]

                let! listResult = runProcessAsync "docker" "ps -a --format \"{{.Names}}\"" (TimeSpan.FromSeconds(20.0))

                if listResult.ExitCode = Some 0 then
                    let names =
                        listResult.StdOut.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.toList

                    /// Defines matches prefix behavior for the surrounding tests used by the server integration aspire Test Host scenario.
                    let matchesPrefix (name: string) =
                        containerPrefixes
                        |> List.exists (fun prefix ->
                            name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))

                    for name in names do
                        if matchesPrefix name then
                            let! result = runProcessAsync "docker" $"rm -f {name}" (TimeSpan.FromSeconds(20.0))

                            if result.ExitCode = Some 0 then
                                if not (String.IsNullOrWhiteSpace result.StdOut) then
                                    Console.WriteLine($"Docker cleanup: removed {name}.")
                            else if result.Error.IsSome then
                                Console.WriteLine($"Docker cleanup ({name}): {result.Error.Value}")
                            else if not (String.IsNullOrWhiteSpace result.StdErr) then
                                Console.WriteLine($"Docker cleanup ({name}) stderr: {result.StdErr.Trim()}")
                else if listResult.Error.IsSome then
                    Console.WriteLine($"Docker cleanup list failed: {listResult.Error.Value}")
                else if not (String.IsNullOrWhiteSpace listResult.StdErr) then
                    Console.WriteLine($"Docker cleanup list stderr: {listResult.StdErr.Trim()}")
        }

    /// Formats log tail for diagnostics.
    let private formatLogTail (label: string) (lines: string list) (maxLines: int) =
        let tail =
            lines
            |> List.rev
            |> List.truncate maxLines
            |> List.rev
            |> String.concat Environment.NewLine

        if String.IsNullOrWhiteSpace tail then
            $"{label}: <no logs captured>"
        else
            $"{label}:{Environment.NewLine}{tail}"

    /// Gets resource log snapshot from the running test server.
    let private getResourceLogSnapshotAsync (app: DistributedApplication) =
        task {
            let model = app.Services.GetRequiredService<DistributedApplicationModel>()

            let tasks =
                model.Resources
                |> Seq.map (fun resource ->
                    task {
                        let name = resource.Name

                        try
                            let! logLines = getResourceLogsAsync app name
                            return formatLogTail $"[{name}]" logLines 50
                        with
                        | ex -> return $"[{name}]: failed to capture logs ({ex.Message})"
                    })
                |> Seq.toArray

            let! snapshots = Task.WhenAll(tasks)
            return snapshots |> String.concat Environment.NewLine
        }

    /// Formats process failure for diagnostics.
    let private formatProcessFailure (label: string) (result: ProcessResult) =
        if result.TimedOut then
            $"{label} timed out."
        else
            let exitCode =
                result.ExitCode
                |> Option.map string
                |> Option.defaultValue "<unknown>"

            let details =
                [
                    if not (String.IsNullOrWhiteSpace result.StdOut) then
                        $"stdout:{Environment.NewLine}{result.StdOut.TrimEnd()}"
                    if not (String.IsNullOrWhiteSpace result.StdErr) then
                        $"stderr:{Environment.NewLine}{result.StdErr.TrimEnd()}"
                ]
                |> String.concat Environment.NewLine

            match result.Error with
            | Some errorMessage -> $"{label} failed: {errorMessage}"
            | None when not (String.IsNullOrWhiteSpace details) -> $"{label} exited with {exitCode}.{Environment.NewLine}{details}"
            | None -> $"{label} exited with {exitCode}."

    /// Tries to resolve get docker diagnostics without failing the caller.
    let private tryGetDockerDiagnosticsAsync () =
        task {
            let! psResult = runProcessAsync "docker" "ps -a --format \"{{.ID}} {{.Names}}\"" (TimeSpan.FromSeconds(10.0))

            if psResult.TimedOut
               || psResult.ExitCode <> Some 0
               || psResult.Error.IsSome then
                return formatProcessFailure "Docker ps" psResult
            else
                let lines = psResult.StdOut.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

                if lines.Length = 0 then
                    return "Docker ps: no containers."
                else
                    let containers =
                        lines
                        |> Seq.map (fun line ->
                            let parts = line.Split([| ' ' |], 2, StringSplitOptions.RemoveEmptyEntries)

                            if parts.Length = 0 then
                                None
                            else
                                let id = parts[0]
                                let name = if parts.Length > 1 then parts[1] else "<unknown>"
                                Some(id, name))
                        |> Seq.choose id
                        |> Seq.truncate 10
                        |> Seq.toArray

                    let tasks =
                        containers
                        |> Array.map (fun (id, name) ->
                            task {
                                let! logResult = runProcessAsync "docker" $"logs --tail 200 {id}" (TimeSpan.FromSeconds(15.0))

                                if logResult.TimedOut
                                   || logResult.ExitCode <> Some 0
                                   || logResult.Error.IsSome then
                                    return formatProcessFailure $"Docker logs ({name})" logResult
                                else if String.IsNullOrWhiteSpace logResult.StdOut then
                                    return $"Docker logs ({name}): <empty>"
                                else
                                    return $"Docker logs ({name}):{Environment.NewLine}{logResult.StdOut.TrimEnd()}"
                            })

                    let! logBlocks = Task.WhenAll(tasks)

                    let containersSummary = String.Join(Environment.NewLine, lines)
                    return $"Docker containers:{Environment.NewLine}{containersSummary}{Environment.NewLine}{String.Join(Environment.NewLine, logBlocks)}"
        }

    let private waitForResourceHealthyWithProgressContextAsync
        (notificationService: ResourceNotificationService)
        (app: DistributedApplication)
        (resourceName: string)
        (ct: CancellationToken)
        (restartContext: string option)
        =
        task {
            try
                logProgress (formatResourceHealthWaitStartProgress resourceName restartContext)
                let! _ = notificationService.WaitForResourceHealthyAsync(resourceName, ct)
                logProgress (formatResourceHealthWaitHealthyProgress resourceName restartContext)
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

    let private waitForResourceHealthyAsync
        (notificationService: ResourceNotificationService)
        (app: DistributedApplication)
        (resourceName: string)
        (ct: CancellationToken)
        =
        waitForResourceHealthyWithProgressContextAsync notificationService app resourceName ct None

    /// Groups shared helpers for fixture diagnostics.
    module FixtureDiagnostics =
        /// Formats the progress message emitted before waiting for an Aspire resource to become healthy.
        let formatResourceHealthWaitStartMessage resourceName restartContext = formatResourceHealthWaitStartProgress resourceName restartContext

        /// Formats the progress message emitted after an Aspire resource reports healthy.
        let formatResourceHealthWaitHealthyMessage resourceName restartContext = formatResourceHealthWaitHealthyProgress resourceName restartContext

        /// Defines redact connection string segments behavior for the surrounding tests used by the server integration aspire Test Host scenario.
        let private redactConnectionStringSegments (sensitivePrefixes: string list) (connectionString: string) =
            if String.IsNullOrWhiteSpace connectionString then
                connectionString
            else
                connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun part ->
                    match sensitivePrefixes
                          |> List.tryFind (fun prefix -> part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        with
                    | Some prefix -> $"{prefix}***"
                    | None -> part)
                |> String.concat ";"

        /// Defines redact service bus connection string behavior for the surrounding tests used by the server integration aspire Test Host scenario.
        let redactServiceBusConnectionString (connectionString: string) =
            redactConnectionStringSegments
                [
                    "SharedAccessKey="
                    "SharedAccessSignature="
                ]
                connectionString

        /// Defines redact storage connection string behavior for the surrounding tests used by the server integration aspire Test Host scenario.
        let redactStorageConnectionString (connectionString: string) =
            redactConnectionStringSegments
                [
                    "AccountKey="
                    "SharedAccessSignature="
                ]
                connectionString

        /// Defines redact cosmos connection string behavior for the surrounding tests used by the server integration aspire Test Host scenario.
        let redactCosmosConnectionString (connectionString: string) =
            redactConnectionStringSegments
                [
                    "AccountKey="
                    "SharedAccessSignature="
                ]
                connectionString

        /// Formats env diagnostics for diagnostics.
        let formatEnvDiagnostics (env: Map<string, string>) =
            /// Gets get from the running test server.
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

        /// Gets required startup keys from the running test server.
        let getRequiredStartupKeys skipServiceBus =
            [
                Constants.EnvironmentVariables.AzureCosmosDBConnectionString
                Constants.EnvironmentVariables.AzureCosmosDBDatabaseName
                Constants.EnvironmentVariables.AzureCosmosDBContainerName
                Constants.EnvironmentVariables.AzureStorageConnectionString
                if not skipServiceBus then
                    Constants.EnvironmentVariables.AzureServiceBusConnectionString
                    Constants.EnvironmentVariables.AzureServiceBusTopic
                    Constants.EnvironmentVariables.AzureServiceBusSubscription
            ]

        /// Gets missing startup keys from the running test server.
        let getMissingStartupKeys skipServiceBus (env: Map<string, string>) =
            getRequiredStartupKeys skipServiceBus
            |> List.filter (fun key ->
                env
                |> Map.tryFind key
                |> Option.forall String.IsNullOrWhiteSpace)

        let serviceBusSkipModeMessage =
            "GRACE_TEST_SKIP_SERVICEBUS=1 is unsupported for Grace.Server.Tests because the shared setup drains the Service Bus test subscription and proves the Owner Created event."

    /// Requires env and fails the test when missing.
    let private requireEnv (resourceName: string) (key: string) (env: Map<string, string>) =
        env
        |> Map.tryFind key
        |> Option.defaultWith (fun () -> failwith $"Missing env var '{key}' for resource '{resourceName}'.")

    let private redactServiceBusConnectionString = FixtureDiagnostics.redactServiceBusConnectionString
    let private redactStorageConnectionString = FixtureDiagnostics.redactStorageConnectionString
    let private redactCosmosConnectionString = FixtureDiagnostics.redactCosmosConnectionString

    /// Tries to resolve get conn value without failing the caller.
    let private tryGetConnValue (prefix: string) (value: string) =
        value.Split(';', StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun segment ->
            if segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                Some(segment.Substring(prefix.Length))
            else
                None)
        |> Option.defaultValue "<missing>"

    /// Defines replace conn value behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private replaceConnValue (prefix: string) (replacement: string) (value: string) =
        let mutable replaced = false

        let segments =
            value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun segment ->
                if segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                    replaced <- true
                    $"{prefix}{replacement}"
                else
                    segment)

        if replaced then (String.Join(";", segments)) + ";" else value

    /// Tries to resolve get cosmos endpoint URI without failing the caller.
    let private tryGetCosmosEndpointUri (connectionString: string) =
        let value = tryGetConnValue "AccountEndpoint=" connectionString
        let mutable uri = Unchecked.defaultof<Uri>

        if Uri.TryCreate(value, UriKind.Absolute, &uri) then Some uri else None

    /// Tries to resolve get endpoint name for target port without failing the caller.
    let private tryGetEndpointNameForTargetPort (app: DistributedApplication) (resourceName: string) (targetPort: int) =
        match tryFindResourceByName app resourceName with
        | Some (:? IResourceWithEndpoints as resourceWithEndpoints) ->
            resourceWithEndpoints.GetEndpoints()
            |> Seq.tryFind (fun endpoint ->
                endpoint.EndpointAnnotation.TargetPort.HasValue
                && endpoint.EndpointAnnotation.TargetPort.Value = targetPort)
            |> Option.map (fun endpoint -> endpoint.EndpointAnnotation.Name)
        | _ -> None

    /// Tries to resolve find resource name for target port without failing the caller.
    let private tryFindResourceNameForTargetPort (app: DistributedApplication) (targetPort: int) =
        let model = app.Services.GetRequiredService<DistributedApplicationModel>()

        model.Resources
        |> Seq.tryPick (fun resource ->
            match resource with
            | :? IResourceWithEndpoints as resourceWithEndpoints ->
                let hasTargetPort =
                    resourceWithEndpoints.GetEndpoints()
                    |> Seq.exists (fun endpoint ->
                        endpoint.EndpointAnnotation.TargetPort.HasValue
                        && endpoint.EndpointAnnotation.TargetPort.Value = targetPort)

                if hasTargetPort then Some resource.Name else None
            | _ -> None)

    /// Tries to resolve get runtime endpoint without failing the caller.
    let private tryGetRuntimeEndpoint (app: DistributedApplication) (resourceName: string) (targetPort: int) =
        try
            let endpointName =
                tryGetEndpointNameForTargetPort app resourceName targetPort
                |> Option.defaultWith (fun () -> getEndpointName app resourceName)

            Some(app.GetEndpoint(resourceName, endpointName))
        with
        | ex ->
            Console.WriteLine($"Unable to resolve runtime endpoint for {resourceName}: {ex.Message}")
            None

    /// Defines resolve local cosmos connection string behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private resolveLocalCosmosConnectionString (app: DistributedApplication) (cosmosResourceName: string option) (connectionString: string) =
        match cosmosResourceName with
        | Some resourceName ->
            match tryGetRuntimeEndpoint app resourceName 8081 with
            | Some runtimeEndpoint ->
                let endpoint = runtimeEndpoint.ToString().TrimEnd('/') + "/"
                let configuredEndpoint = tryGetConnValue "AccountEndpoint=" connectionString

                if not (endpoint.Equals(configuredEndpoint, StringComparison.OrdinalIgnoreCase)) then
                    Console.WriteLine($"Cosmos readiness endpoint adjusted from {configuredEndpoint} to {endpoint}.")

                replaceConnValue "AccountEndpoint=" endpoint connectionString
            | None -> connectionString
        | None -> connectionString

    /// Formats exception chain for diagnostics.
    let private formatExceptionChain (ex: exn) =
        let parts = ResizeArray<string>()
        let mutable current = ex

        while not (isNull current) do
            parts.Add($"{current.GetType().FullName}: {current.Message}")
            current <- current.InnerException

        parts |> String.concat " --> "

    /// Builds a deterministic permissive cosmos HTTP client for integration setup fixture for the server integration aspire Test Host assertions.
    let private createPermissiveCosmosHttpClient () =
        let handler = new HttpClientHandler()
        handler.ServerCertificateCustomValidationCallback <- HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        new HttpClient(handler, disposeHandler = true)

    /// Builds a deterministic local cosmos client options for integration setup fixture for the server integration aspire Test Host assertions.
    let private createLocalCosmosClientOptions () =
        let options = CosmosClientOptions(ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true)
        options.RequestTimeout <- TimeSpan.FromSeconds(10.0)
        options.HttpClientFactory <- (fun () -> createPermissiveCosmosHttpClient ())
        options.ServerCertificateCustomValidationCallback <- Func<X509Certificate2, X509Chain, SslPolicyErrors, bool>(fun _ _ _ -> true)
        options

    let private formatEnvDiagnostics = FixtureDiagnostics.formatEnvDiagnostics

    /// Requires startup environment and fails the test when missing.
    let private requireStartupEnvironment (resourceName: string) (skipServiceBus: bool) (env: Map<string, string>) =
        let missing = FixtureDiagnostics.getMissingStartupKeys skipServiceBus env

        if not missing.IsEmpty then
            let missingText = String.Join(", ", missing)
            let envDetails = formatEnvDiagnostics env

            failwith $"Missing required startup env var(s) for resource '{resourceName}': {missingText}. Env: {envDetails}"

    /// Tries to resolve get latest log tail without failing the caller.
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

    /// Waits for for service bus ready to become observable in the test host.
    let private waitForServiceBusReadyAsync (serviceBusEmulatorName: string) (serviceBusSqlName: string) (state: TestHostState) =
        task {
            let sw = Stopwatch.StartNew()
            let timeout = getTimeout (TimeSpan.FromSeconds(60.0)) (TimeSpan.FromMinutes(3.0))
            let mutable lastError = String.Empty
            let mutable attempt = 0
            let mutable ready = false
            let redactedConnectionString = redactServiceBusConnectionString state.ServiceBusConnectionString
            let endpoint = tryGetConnValue "Endpoint=" state.ServiceBusConnectionString

            Console.WriteLine(
                $"Service Bus readiness probe: Endpoint={endpoint}; Topic={state.ServiceBusTopic}; Subscription={state.ServiceBusTestSubscription}; Connection={redactedConnectionString}"
            )

            /// Gets resource diagnostics from the running test server.
            let getResourceDiagnosticsAsync (resourceName: string) =
                task {
                    let notificationService = state.App.Services.GetRequiredService<ResourceNotificationService>()

                    let details = describeResourceState notificationService resourceName

                    let! logDetails =
                        task {
                            try
                                let! logLines = getResourceLogsAsync state.App resourceName
                                return formatLogTail $"{resourceName} logs" logLines 50
                            with
                            | ex -> return $"{resourceName} logs: <failed to capture ({ex.Message})>"
                        }

                    return $"{resourceName}: {details}{Environment.NewLine}{logDetails}"
                }

            while not ready do
                if sw.Elapsed >= timeout then
                    let! emulatorDetails = getResourceDiagnosticsAsync serviceBusEmulatorName
                    let! sqlDetails = getResourceDiagnosticsAsync serviceBusSqlName
                    let! dockerDetails = tryGetDockerDiagnosticsAsync ()

                    raise (
                        TimeoutException(
                            $"Timed out waiting for Service Bus emulator. Attempts={attempt}; Elapsed={sw.Elapsed.TotalSeconds:n1}s; Last error: {lastError}{Environment.NewLine}{emulatorDetails}{Environment.NewLine}{sqlDetails}{Environment.NewLine}{dockerDetails}"
                        )
                    )

                attempt <- attempt + 1

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
                    logProgress $"Service Bus emulator ready after {sw.Elapsed.TotalSeconds:n1}s."
                with
                | ex ->
                    lastError <- ex.Message
                    Console.WriteLine($"Service Bus readiness attempt {attempt} failed: {lastError}")
                    do! Task.Delay(TimeSpan.FromSeconds(1.0))
        }

    let private waitForCosmosReadyAsync
        (app: DistributedApplication)
        (cosmosResourceName: string option)
        (connectionString: string)
        (databaseName: string)
        (containerName: string)
        =
        task {
            if String.IsNullOrWhiteSpace connectionString then
                return ()
            else if String.IsNullOrWhiteSpace databaseName
                    || String.IsNullOrWhiteSpace containerName then
                return ()
            else
                let isLocalCosmos =
                    connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                    || connectionString.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)

                let connectionString =
                    if isLocalCosmos then
                        resolveLocalCosmosConnectionString app cosmosResourceName connectionString
                    else
                        connectionString

                let options = createLocalCosmosClientOptions ()

                use client = new CosmosClient(connectionString, options)
                use probeClient = createPermissiveCosmosHttpClient ()
                probeClient.Timeout <- TimeSpan.FromSeconds(10.0)
                let sw = Stopwatch.StartNew()
                let timeout = getTimeout (TimeSpan.FromMinutes(3.0)) (TimeSpan.FromMinutes(3.0))
                let perCallTimeout = TimeSpan.FromSeconds(10.0)
                let mutable lastError = String.Empty
                let mutable attempt = 0
                let mutable ready = false
                let endpoint = tryGetCosmosEndpointUri connectionString

                let endpointText =
                    endpoint
                    |> Option.map string
                    |> Option.defaultValue "<missing>"

                let redactedConnectionString = redactCosmosConnectionString connectionString

                /// Gets resource diagnostics from the running test server.
                let getResourceDiagnosticsAsync () =
                    task {
                        match cosmosResourceName with
                        | Some resourceName ->
                            let notificationService = app.Services.GetRequiredService<ResourceNotificationService>()

                            let details = describeResourceState notificationService resourceName

                            let! logDetails =
                                task {
                                    try
                                        let! logLines = getResourceLogsAsync app resourceName
                                        return formatLogTail $"{resourceName} logs" logLines 80
                                    with
                                    | ex -> return $"{resourceName} logs: <failed to capture ({ex.Message})>"
                                }

                            return $"{resourceName}: {details}{Environment.NewLine}{logDetails}"
                        | None -> return "Cosmos emulator resource name unavailable."
                    }

                Console.WriteLine(
                    $"Cosmos readiness probe: Endpoint={endpointText}; Database={databaseName}; Container={containerName}; Connection={redactedConnectionString}"
                )

                while not ready do
                    if sw.Elapsed >= timeout then
                        let! resourceDetails = getResourceDiagnosticsAsync ()
                        let! dockerDetails = tryGetDockerDiagnosticsAsync ()

                        let diagnostics =
                            $"Timed out waiting for Cosmos emulator. Attempts={attempt}. Elapsed={sw.Elapsed.TotalSeconds:n1}s. LastError={lastError}. Database={databaseName}. Container={containerName}. ConnectionString={redactedConnectionString}{Environment.NewLine}{resourceDetails}{Environment.NewLine}{dockerDetails}"

                        raise (TimeoutException(diagnostics))

                    attempt <- attempt + 1

                    try
                        match endpoint with
                        | Some endpointUri when isLocalCosmos ->
                            let probeUri = Uri(endpointUri, "_explorer/emulator.pem")

                            use! response =
                                probeClient
                                    .GetAsync(probeUri)
                                    .WaitAsync(perCallTimeout)

                            ()
                        | _ -> ()

                        let! _ =
                            client
                                .ReadAccountAsync()
                                .WaitAsync(perCallTimeout)

                        if isLocalCosmos then
                            let! database =
                                client
                                    .CreateDatabaseIfNotExistsAsync(databaseName)
                                    .WaitAsync(perCallTimeout)

                            let! _ =
                                database
                                    .Database
                                    .CreateContainerIfNotExistsAsync(containerName, "/PartitionKey")
                                    .WaitAsync(perCallTimeout)

                            ()

                        ready <- true
                        logProgress $"Cosmos emulator ready after {sw.Elapsed.TotalSeconds:n1}s."
                    with
                    | ex ->
                        lastError <- formatExceptionChain ex
                        Console.WriteLine($"Cosmos readiness attempt {attempt} failed: {lastError}")
                        do! Task.Delay(TimeSpan.FromSeconds(1.0))
        }

    /// Waits for for grace server HTTP ready to become observable in the test host.
    let private waitForGraceServerHttpReadyAsync (client: HttpClient) (ct: CancellationToken) =
        task {
            let perRequestTimeout = getTimeout (TimeSpan.FromSeconds(10.0)) (TimeSpan.FromSeconds(20.0))
            let sw = Stopwatch.StartNew()
            let mutable attempt = 0
            let mutable lastError = String.Empty

            /// Defines delay behavior for the surrounding tests used by the server integration aspire Test Host scenario.
            let delayAsync () =
                task {
                    try
                        do! Task.Delay(TimeSpan.FromSeconds(1.0), ct)
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested ->
                        raise (TimeoutException($"Timed out waiting for Grace.Server HTTP readiness. Last error: {lastError}"))
                }

            Console.WriteLine($"Waiting for Grace.Server HTTP readiness at {client.BaseAddress}...")

            let mutable ready = false

            while not ready do
                if ct.IsCancellationRequested then
                    raise (TimeoutException($"Timed out waiting for Grace.Server HTTP readiness. Last error: {lastError}"))

                attempt <- attempt + 1

                try
                    use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    linkedCts.CancelAfter(perRequestTimeout)

                    use! response = client.GetAsync("/healthz", linkedCts.Token)

                    if response.IsSuccessStatusCode then
                        Console.WriteLine($"Grace.Server HTTP readiness confirmed after {sw.Elapsed.TotalSeconds:n1}s (attempt {attempt}).")
                        ready <- true
                    else
                        lastError <- $"Status {(int response.StatusCode)} {response.StatusCode}"
                        do! delayAsync ()
                with
                | :? OperationCanceledException when ct.IsCancellationRequested ->
                    raise (TimeoutException($"Timed out waiting for Grace.Server HTTP readiness. Last error: {lastError}"))
                | ex ->
                    lastError <- ex.Message
                    do! delayAsync ()
        }

    /// Defines start new host behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private startNewHostAsync (bootstrapUserId: string) =
        task {
            logProgress "Aspire setup starting."

            Environment.SetEnvironmentVariable("GRACE_TESTING", "1")
            Environment.SetEnvironmentVariable("GRACE_TEST_DOCKER_CLEANUP", "1")

            if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRACE_TEST_SERVER_CLEANUP")) then
                Environment.SetEnvironmentVariable("GRACE_TEST_SERVER_CLEANUP", "1")

            Environment.SetEnvironmentVariable("GRACE_TEST_RUN_ID", Guid.NewGuid().ToString("N"))
            Environment.SetEnvironmentVariable("ASPIRE_RESOURCE_MODE", "Local")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcAuthority, "https://auth.grace.test")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcAudience, "https://api.grace.test")
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcCliClientId, "grace-cli-test-client")
            Environment.SetEnvironmentVariable("grace__webhooks__outbound_urls__allow_unsafe_local_development", "true")

            if not <| String.IsNullOrWhiteSpace bootstrapUserId then
                Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthzBootstrapSystemAdminUsers, bootstrapUserId)

            if shouldSkipServiceBus () then
                invalidOp FixtureDiagnostics.serviceBusSkipModeMessage

            logProgress "Docker cleanup starting."
            do! cleanupDockerContainersAsync ()
            logProgress "Docker cleanup complete."
            logProgress "building Aspire AppHost."
            let! builder = DistributedApplicationTestingBuilder.CreateAsync<Projects.Grace_Aspire_AppHost>()
            let! app = builder.BuildAsync()
            logProgress "starting Aspire AppHost resources."
            do! app.StartAsync()
            logProgress "Aspire AppHost started; waiting for resources."

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

            let serviceBusSqlResourceName = getServiceBusSqlResourceName ()
            let serviceBusEmulatorResourceName = getServiceBusEmulatorResourceName ()

            if not (shouldSkipServiceBus ()) then
                match tryFindResourceByName app serviceBusSqlResourceName with
                | Some _ ->
                    Console.WriteLine($"Service Bus SQL resource detected: {serviceBusSqlResourceName}")
                    do! waitForResourceHealthyAsync notificationService app serviceBusSqlResourceName cts.Token
                | None -> Console.WriteLine("Service Bus SQL resource not found in model.")
            else
                Console.WriteLine("Skipping Service Bus SQL readiness checks (GRACE_TEST_SKIP_SERVICEBUS=1).")

            let cosmosResourceName =
                tryFindResourceName<AzureCosmosDBEmulatorResource> app
                |> Option.orElseWith (fun () -> tryFindResourceNameForTargetPort app 8081)

            match cosmosResourceName with
            | Some name ->
                Console.WriteLine($"Cosmos emulator resource detected: {name}")
                Console.WriteLine("Cosmos functional readiness probe will verify emulator access.")
            | None -> Console.WriteLine("Cosmos emulator resource not found in model.")

            if not (shouldSkipServiceBus ()) then
                do! waitForResourceHealthyAsync notificationService app serviceBusEmulatorResourceName cts.Token
            else
                Console.WriteLine("Skipping Service Bus emulator readiness checks (GRACE_TEST_SKIP_SERVICEBUS=1).")

            logProgress "container resources reported healthy; validating service readiness."

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

            requireStartupEnvironment graceServerResourceName (shouldSkipServiceBus ()) env

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

            do! waitForCosmosReadyAsync app cosmosResourceName cosmosConnectionString cosmosDatabaseName cosmosContainerName

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

            try
                do! waitForGraceServerHttpReadyAsync client cts.Token
            with
            | ex ->
                let details = describeResourceState notificationService graceServerResourceName
                let! graceResourceLogs = getResourceLogsAsync app graceServerResourceName
                let graceResourceLogDetails = formatLogTail "Grace.Server resource logs" graceResourceLogs 50
                let envDetails = formatEnvDiagnostics env

                let graceFileLog =
                    env
                    |> Map.tryFind Constants.EnvironmentVariables.GraceLogDirectory
                    |> Option.bind tryGetLatestLogTail
                    |> Option.defaultValue "No Grace.Server log file captured."

                let! aspireLogSnapshot = getResourceLogSnapshotAsync app
                let! dockerDiagnostics = tryGetDockerDiagnosticsAsync ()

                raise (
                    Exception(
                        $"Grace-server HTTP readiness failed. {details}{Environment.NewLine}Error: {ex.Message}{Environment.NewLine}Env: {envDetails}{Environment.NewLine}{graceResourceLogDetails}{Environment.NewLine}{graceFileLog}{Environment.NewLine}{aspireLogSnapshot}{Environment.NewLine}{dockerDiagnostics}",
                        ex
                    )
                )

            let diagnosticsPath = Path.Combine(Path.GetTempPath(), "grace-server-tests.host.log")

            /// Tries to resolve get conn value without failing the caller.
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

            /// Defines service bus connection string behavior for the surrounding tests used by the server integration aspire Test Host scenario.
            let serviceBusConnectionString, serviceBusTopic, serviceBusSubscription, serviceBusTestSubscription =
                if shouldSkipServiceBus () then
                    "", "", "", ""
                else
                    let connection = requireEnv graceServerResourceName Constants.EnvironmentVariables.AzureServiceBusConnectionString env

                    let topic = requireEnv graceServerResourceName Constants.EnvironmentVariables.AzureServiceBusTopic env

                    let subscription = requireEnv graceServerResourceName Constants.EnvironmentVariables.AzureServiceBusSubscription env

                    let testSubscription = $"{subscription}-tests"
                    connection, topic, subscription, testSubscription

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

            if not (shouldSkipServiceBus ()) then
                do! waitForServiceBusReadyAsync serviceBusEmulatorResourceName serviceBusSqlResourceName state
            else
                Console.WriteLine("Skipping Service Bus functional readiness checks (GRACE_TEST_SKIP_SERVICEBUS=1).")

            logProgress "containers and service readiness checks complete."

            return state
        }

    /// Defines start behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let startAsync (bootstrapUserId: string) =
        task {
            do! sharedStateLock.WaitAsync()

            try
                match sharedState with
                | Some state ->
                    ensureBootstrapCompatible bootstrapUserId
                    return state
                | None ->
                    let! state = startNewHostAsync bootstrapUserId
                    sharedBootstrapUserId <- Some bootstrapUserId
                    sharedState <- Some state
                    return state
            finally
                sharedStateLock.Release() |> ignore
        }

    /// Defines stop behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let stopAsync (app: DistributedApplication option) =
        task {
            match app with
            | None -> ()
            | Some appHost ->
                Console.WriteLine("Stopping Aspire host...")
                Console.WriteLine("Aspire host shutdown skipped to avoid test host teardown crashes.")
        }

    /// Restarts Grace.Server with a scenario label for deliberate restart diagnostics.
    let restartGraceServerAsync (state: TestHostState) (restartContext: string) =
        task {
            do! sharedStateLock.WaitAsync()

            try
                let normalizedRestartContext = normalizeRestartContext restartContext
                Console.WriteLine($"Restarting Grace.Server Aspire project resource for {normalizedRestartContext}...")
                let commandService = state.App.Services.GetRequiredService<ResourceCommandService>()
                use cts = new CancellationTokenSource(defaultWaitTimeout)

                let! result = commandService.ExecuteCommandAsync(graceServerResourceName, KnownResourceCommands.RestartCommand, cts.Token)

                if not result.Success then
                    let errorMessage =
                        if not (String.IsNullOrWhiteSpace result.Message) then result.Message
                        elif result.Canceled then "Restart command was canceled."
                        else "Restart command failed without details."

                    raise (InvalidOperationException($"Grace.Server restart failed during {normalizedRestartContext}: {errorMessage}"))

                let notificationService = state.App.Services.GetRequiredService<ResourceNotificationService>()

                do!
                    waitForResourceHealthyWithProgressContextAsync
                        notificationService
                        state.App
                        graceServerResourceName
                        cts.Token
                        (Some normalizedRestartContext)

                do! waitForGraceServerHttpReadyAsync state.Client cts.Token
                logProgress $"Grace.Server HTTP readiness recovered after intentional restart '{normalizedRestartContext}'."
                Console.WriteLine($"Grace.Server Aspire project resource restart completed for {normalizedRestartContext}.")
            finally
                sharedStateLock.Release() |> ignore
        }

    /// Builds a deterministic service bus receiver for integration setup fixture for the server integration aspire Test Host assertions.
    let private createServiceBusReceiver (state: TestHostState) =
        let options = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete)
        let client = ServiceBusClient(state.ServiceBusConnectionString)
        let receiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, options)
        client, receiver

    /// Defines drain service bus behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let drainServiceBusAsync (state: TestHostState) =
        task {
            /// Defines client behavior for the surrounding tests used by the server integration aspire Test Host scenario.
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

    /// Waits for for owner created event to become observable in the test host.
    let waitForOwnerCreatedEventAsync (state: TestHostState) (ownerId: string) =
        task {
            /// Defines client behavior for the surrounding tests used by the server integration aspire Test Host scenario.
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

                    /// Tries to resolve match owner created without failing the caller.
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

    let private serviceBusSendTimeout = TimeSpan.FromSeconds(10.0)

    /// Defines send service bus message behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let private sendServiceBusMessageAsync (state: TestHostState) (message: ServiceBusMessage) =
        task {
            use cts = new CancellationTokenSource(serviceBusSendTimeout)
            let client = ServiceBusClient(state.ServiceBusConnectionString)
            use _client = client
            let sender = client.CreateSender(state.ServiceBusTopic)

            try
                do! sender.SendMessageAsync(message, cts.Token)
            finally
                sender
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
        }

    /// Defines send grace event behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let sendGraceEventAsync (state: TestHostState) (graceEvent: Events.GraceEvent) (metadata: Grace.Types.Common.EventMetadata) =
        task {
            let payload = JsonSerializer.SerializeToUtf8Bytes(graceEvent, Constants.JsonSerializerOptions)
            let message = ServiceBusMessage(payload)
            message.ContentType <- "application/json"
            message.Subject <- "GraceEvent"
            message.CorrelationId <- metadata.CorrelationId
            message.MessageId <- $"{metadata.CorrelationId}-{Guid.NewGuid():N}"
            message.ApplicationProperties[ "graceEventType" ] <- getDiscriminatedUnionFullName graceEvent

            let metadataProperties = metadata.Properties |> Seq.toArray
            let mutable index = 0

            while index < metadataProperties.Length do
                let property = metadataProperties[index]
                message.ApplicationProperties[ property.Key ] <- property.Value
                index <- index + 1

            do! sendServiceBusMessageAsync state message
        }

    /// Defines send raw service bus message behavior for the surrounding tests used by the server integration aspire Test Host scenario.
    let sendRawServiceBusMessageAsync (state: TestHostState) (body: string) (messageId: string) (correlationId: string) =
        task {
            let message = ServiceBusMessage(BinaryData.FromString(body))
            message.ContentType <- "application/json"
            message.Subject <- "GraceEvent"
            message.CorrelationId <- correlationId
            message.MessageId <- messageId

            do! sendServiceBusMessageAsync state message
        }

    /// Tries to resolve wait for grace event without failing the caller.
    let tryWaitForGraceEventAsync (state: TestHostState) (timeout: TimeSpan) (predicate: Events.GraceEvent -> bool) =
        task {
            /// Defines client behavior for the surrounding tests used by the server integration aspire Test Host scenario.
            let client, receiver = createServiceBusReceiver state
            use _client = client
            use _receiver = receiver

            let sw = Stopwatch.StartNew()
            let lastBodies = ResizeArray<string>()
            let maxBodies = 10
            let mutable found: Events.GraceEvent option = None

            while found.IsNone && sw.Elapsed < timeout do
                let remaining = timeout - sw.Elapsed

                let waitTime =
                    if remaining < TimeSpan.FromSeconds(1.0) then
                        remaining
                    else
                        TimeSpan.FromSeconds(1.0)

                let! message = receiver.ReceiveMessageAsync(waitTime)

                if not (isNull message) then
                    let body = message.Body.ToString()

                    if lastBodies.Count >= maxBodies then lastBodies.RemoveAt(0)

                    lastBodies.Add(body)

                    try
                        let graceEvent = JsonSerializer.Deserialize<Events.GraceEvent>(body, Constants.JsonSerializerOptions)

                        if predicate graceEvent then found <- Some graceEvent
                    with
                    | ex -> Console.WriteLine($"Service Bus test subscription message was not a GraceEvent: {ex.Message}; Body={body}")

            return found
        }

    /// Waits for for grace event to become observable in the test host.
    let waitForGraceEventAsync (state: TestHostState) (timeout: TimeSpan) (description: string) (predicate: Events.GraceEvent -> bool) =
        task {
            match! tryWaitForGraceEventAsync state timeout predicate with
            | Some graceEvent -> return graceEvent
            | None ->
                return
                    raise (
                        TimeoutException(
                            $"Timed out waiting for {description} on Service Bus test subscription. Topic={state.ServiceBusTopic}; TestSubscription={state.ServiceBusTestSubscription}; Connection={redactServiceBusConnectionString state.ServiceBusConnectionString}"
                        )
                    )
        }
