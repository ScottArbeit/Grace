module Program

#nowarn "57"

open Aspire.Hosting
open Aspire.Hosting.ApplicationModel
open Aspire.Hosting.Azure
open Aspire.Hosting.Redis
open Grace.Shared
open Grace.Shared.Utilities
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

/// Holds the fixed AppHost resource names that must not depend on Azure entity configuration.
module private ResourceNames =
    [<Literal>]
    let AspireResourceModeEnvironmentVariable = "ASPIRE_RESOURCE_MODE"

    [<Literal>]
    let AspireResourceModeLocal = "Local"

    [<Literal>]
    let AspireResourceModeAzure = "Azure"

    [<Literal>]
    let GraceEventTopic = "grace-event-topic"

    [<Literal>]
    let GraceEventSubscription = "grace-event-subscription"

    [<Literal>]
    let OperationalFactsTopic = "grace-operational-facts-topic"

    [<Literal>]
    let OperationalFactsProcessorSubscription = "operational-facts-processor"

    [<Literal>]
    let OperationalFactsProcessorSubscriptionResource = "grace-operational-facts-processor-subscription"

    [<Literal>]
    let OperationalFactsProcessorSubscriptionSetting = "grace__azure_service_bus__operational_facts_processor_subscription"

    [<Literal>]
    let OperationsSqlConnectionStringSetting = "grace__operations__sql__connectionstring"

/// Resolves one AppHost setting from process, user, machine, then configuration sources.
let private resolveSetting (configuration: IConfiguration) (name: string) =
    [
        Environment.GetEnvironmentVariable(name)
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
        configuration[name]
        configuration[getConfigKey name]
    ]
    |> List.tryFind (String.IsNullOrWhiteSpace >> not)

/// Requires a DebugAzure setting before the host starts any listener or resource.
let private getRequiredSetting configuration name =
    match resolveSetting configuration name with
    | Some value -> value
    | None -> invalidOp $"Missing required setting '{name}' (or '{getConfigKey name}') for DebugAzure."

/// Rejects Azure Service Bus topic overlap so operational facts cannot enter the Grace event stream.
let private ensureDistinctServiceBusTopics (graceEventTopic: string option) (operationalFactsTopic: string option) =
    match graceEventTopic, operationalFactsTopic with
    | Some graceEvent, Some operationalFacts when
        graceEvent
            .Trim()
            .Equals(operationalFacts.Trim(), StringComparison.OrdinalIgnoreCase)
        ->
        invalidOp
            $"Service Bus topic '{Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic}' must differ from '{Constants.EnvironmentVariables.AzureServiceBusTopic}' so usage facts cannot enter the GraceEvent topic/subscriber path."
    | _ -> ()

/// Requires DebugAzure to use the durable operational-facts processor subscription resource.
let private ensureOperationalFactsProcessorSubscription subscriptionName =
    if
        not
            (
                ResourceNames.OperationalFactsProcessorSubscription.Equals(
                    subscriptionName
                    |> Option.defaultValue ""
                    |> fun value -> value.Trim(), StringComparison.Ordinal
                )
            )
    then
        invalidOp
            $"DebugAzure requires existing Azure Service Bus topic '{Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic}' to contain durable subscription '{ResourceNames.OperationalFactsProcessorSubscription}'. Set '{ResourceNames.OperationalFactsProcessorSubscriptionSetting}' to '{ResourceNames.OperationalFactsProcessorSubscription}' after creating that subscription."

/// Formats a local SQL endpoint for SqlClient while preserving the allocated port.
let BuildSqlTcpDataSource (host: obj, port: obj) =
    let requireValue (parameterName: string) (value: string) =
        if String.IsNullOrWhiteSpace value then
            raise (ArgumentException($"SQL {parameterName} is required.", parameterName))
        else
            value.Trim()

    let hostValue = requireValue "host" (Convert.ToString host)
    let portValue = requireValue "port" (Convert.ToString port)

    let normalizedHost =
        if hostValue.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) then
            hostValue[4..]
        else
            hostValue

    let normalizedHost =
        if
            normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        then
            IPAddress.Loopback.ToString()
        else
            normalizedHost

    $"tcp:{normalizedHost},{portValue}"

/// Forwards a non-empty optional setting without logging the setting value.
let private addOptionalEnvironment (resource: IResourceBuilder<ProjectResource>) configuration name (forwardedKeys: IList<string>) =
    match resolveSetting configuration name with
    | Some value ->
        resource.WithEnvironment(name, value) |> ignore
        forwardedKeys.Add(name)
    | None -> ()

/// Writes only the names of settings forwarded to a child project.
let private logForwardedSettings label (forwardedKeys: IList<string>) =
    let names =
        if forwardedKeys.Count = 0 then
            "none detected"
        else
            String.Join(", ", forwardedKeys)

    Console.WriteLine($"{label}: {names}.")

/// Removes prior fixed-name Service Bus test containers without treating cleanup failure as startup failure.
let private cleanupDockerContainers containerNames =
    for name in containerNames do
        try
            let startInfo = ProcessStartInfo("docker", $"rm -f {name}")
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true

            use dockerProcess = Process.Start(startInfo)

            if
                not (isNull dockerProcess)
                && not (dockerProcess.WaitForExit 5000)
            then
                try
                    dockerProcess.Kill true
                with
                | _ -> ()
        with
        | _ -> ()

/// Allocates one ephemeral loopback port for isolated non-fixed AppHost test runs.
let private getAvailableTcpPort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// Builds the Service Bus emulator JSON while retaining the two topic/subscription contracts.
let private createServiceBusConfiguration configFilePath (configuration: IConfiguration) =
    let topicName =
        resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusTopic
        |> Option.defaultValue Constants.GraceEventStreamTopic

    let operationalFactsTopicName =
        resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
        |> Option.defaultValue Constants.GraceOperationalFactsTopic

    ensureDistinctServiceBusTopics (Some topicName) (Some operationalFactsTopicName)

    let subscriptionName =
        resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusSubscription
        |> Option.defaultValue "grace-server"

    let property (name: string) (value: JsonNode) = name, value
    let stringValue (value: string) = JsonValue.Create<string>(value) :> JsonNode
    let intValue (value: int) = JsonValue.Create<int>(value) :> JsonNode
    let boolValue (value: bool) = JsonValue.Create<bool>(value) :> JsonNode

    let objectNode (properties: (string * JsonNode) list) =
        let node = JsonObject()

        properties
        |> List.iter (fun (name, value) -> node[name] <- value)

        node :> JsonNode

    let arrayNode (values: JsonNode list) =
        let node = JsonArray()
        values |> List.iter node.Add
        node :> JsonNode

    let subscription name =
        objectNode [ property "Name" (stringValue name)
                     property
                         "Properties"
                         (objectNode [ property "DeadLetteringOnMessageExpiration" (boolValue false)
                                       property "DefaultMessageTimeToLive" (stringValue "PT1H")
                                       property "LockDuration" (stringValue "PT1M")
                                       property "MaxDeliveryCount" (intValue 10)
                                       property "ForwardDeadLetteredMessagesTo" (stringValue "")
                                       property "ForwardTo" (stringValue "")
                                       property "RequiresSession" (boolValue false) ])
                     property "Rules" (arrayNode []) ]

    let topic name duplicateWindow duplicateDetection subscriptions =
        objectNode [ property "Name" (stringValue name)
                     property
                         "Properties"
                         (objectNode [ property "DefaultMessageTimeToLive" (stringValue "PT1H")
                                       property "DuplicateDetectionHistoryTimeWindow" (stringValue duplicateWindow)
                                       property "RequiresDuplicateDetection" (boolValue duplicateDetection) ])
                     property "Subscriptions" (arrayNode subscriptions) ]

    let config =
        objectNode [ property
                         "UserConfig"
                         (objectNode [ property
                                           "Namespaces"
                                           (arrayNode [ objectNode [ property "Name" (stringValue "sbemulatorns")
                                                                     property "Queues" (arrayNode [])
                                                                     property
                                                                         "Topics"
                                                                         (arrayNode [ topic
                                                                                          topicName
                                                                                          "PT20S"
                                                                                          false
                                                                                          [
                                                                                              subscription subscriptionName
                                                                                              subscription $"{subscriptionName}-tests"
                                                                                          ]
                                                                                      topic
                                                                                          operationalFactsTopicName
                                                                                          "PT5M"
                                                                                          true
                                                                                          [
                                                                                              subscription ResourceNames.OperationalFactsProcessorSubscription
                                                                                          ] ]) ] ])
                                       property "Logging" (objectNode [ property "Type" (stringValue "Console") ]) ]) ]

    let options = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = null)
    let json = config.ToJsonString(options)

    let existingJson =
        if File.Exists configFilePath then
            Some(File.ReadAllText configFilePath)
        else
            None

    if existingJson <> Some json then
        Console.WriteLine($"Creating Service Bus Emulator config at {configFilePath}:{Environment.NewLine}{json}")
        File.WriteAllText(configFilePath, json)

/// Composes Grace.Cache without a CLI dependency and isolates ordinary test hosts from fixed developer ports.
let private addCacheProject (builder: IDistributedApplicationBuilder) isTestRun useFixedTestPorts =
    let cache =
        builder
            .AddProject("grace-cache", "..\\Grace.Cache\\Grace.Cache.fsproj")
            .WithEnvironment("GRACE_CACHE_INSTANCE_NAME", "aspire-cache")

    if isTestRun && not useFixedTestPorts then
        let cacheTargetPort = getAvailableTcpPort ()

        cache
            .WithHttpEndpoint(targetPort = cacheTargetPort, name = "http")
            .WithEnvironment("ASPNETCORE_URLS", $"http://127.0.0.1:{cacheTargetPort}")
        |> ignore
    else
        cache
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithHttpEndpoint(targetPort = 8080, name = "http")
        |> ignore

/// Builds and runs the existing AppHost resource graph through the F# composition entry point.
[<EntryPoint>]
let main args =
    try
        let environmentName =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            |> Option.ofObj
            |> Option.defaultValue "Development"

        let configuration =
            ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional = true, reloadOnChange = true)
                .AddJsonFile($"appsettings.{environmentName}.json", optional = true, reloadOnChange = true)
                .AddUserSecrets(Assembly.GetExecutingAssembly(), optional = true)
                .AddEnvironmentVariables()
                .Build()

        let builder = DistributedApplication.CreateBuilder(args)

        let resourceMode =
            Environment.GetEnvironmentVariable(ResourceNames.AspireResourceModeEnvironmentVariable)
            |> Option.ofObj
            |> Option.orElseWith (fun () ->
                configuration[$"grace:{ResourceNames.AspireResourceModeEnvironmentVariable}"]
                |> Option.ofObj)
            |> Option.defaultValue ResourceNames.AspireResourceModeLocal

        let isRunMode = builder.ExecutionContext.IsRunMode
        let isPublishMode = builder.ExecutionContext.IsPublishMode
        Console.WriteLine($"Aspire execution context: Run={isRunMode}; Publish={isPublishMode}.")

        let isAzureDebugRun =
            isRunMode
            && resourceMode.Equals(ResourceNames.AspireResourceModeAzure, StringComparison.OrdinalIgnoreCase)

        let isTruthy value =
            not (String.IsNullOrWhiteSpace value)
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase))

        let isTestRun =
            Environment.GetEnvironmentVariable("GRACE_TESTING")
            |> isTruthy

        let runSuffix =
            if isTestRun then
                Environment.GetEnvironmentVariable("GRACE_TEST_RUN_ID")
                |> Option.ofObj
                |> Option.defaultWith (fun () -> Guid.NewGuid().ToString "N")
                |> Some
            else
                None

        let skipServiceBus =
            isTestRun
            && (Environment.GetEnvironmentVariable("GRACE_TEST_SKIP_SERVICEBUS")
                |> isTruthy)

        let useFixedTestPorts =
            isTestRun
            && (Environment.GetEnvironmentVariable("GRACE_TEST_FIXED_PORTS")
                |> isTruthy)

        let pubSubSystem = if skipServiceBus then "UnknownPubSubProvider" else "AzureServiceBus"

        if isTestRun then
            cleanupDockerContainers [ "servicebus-sql"
                                      "servicebus-emulator" ]

        let redisContainerName =
            runSuffix
            |> Option.map (fun suffix -> $"redis-{suffix}")
            |> Option.defaultValue "redis"

        let redis =
            builder
                .AddContainer("redis", "redis", "latest")
                .WithContainerName(redisContainerName)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEndpoint(targetPort = 6379, port = 6379)

        if isTestRun then
            redis.WithLifetime(ContainerLifetime.Session)
            |> ignore

        addCacheProject builder isTestRun useFixedTestPorts

        if not isPublishMode then
            let otlpEndpoint =
                configuration["grace:otlp_endpoint"]
                |> Option.ofObj
                |> Option.defaultValue "http://localhost:18889"

            let stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grace", "aspire")
            let logDirectory = Path.Combine(stateRoot, "logs")
            Directory.CreateDirectory stateRoot |> ignore
            Directory.CreateDirectory logDirectory |> ignore

            let mutable orleansClusterId =
                configuration[getConfigKey Constants.EnvironmentVariables.OrleansClusterId]
                |> Option.ofObj
                |> Option.defaultValue "local"

            let mutable orleansServiceId =
                configuration[getConfigKey Constants.EnvironmentVariables.OrleansServiceId]
                |> Option.ofObj
                |> Option.defaultValue "grace-dev"

            match isTestRun, runSuffix with
            | true, Some suffix ->
                orleansClusterId <- $"{orleansClusterId}-test-{suffix}"
                orleansServiceId <- $"{orleansServiceId}-test-{suffix}"
            | _ -> ()

            Console.WriteLine($"Using Orleans ClusterId='{orleansClusterId}' and ServiceId='{orleansServiceId}'.")

            let graceServer =
                builder
                    .AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithParentRelationship(redis.Resource)
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(
                        Constants.EnvironmentVariables.ApplicationInsightsConnectionString,
                        configuration[getConfigKey Constants.EnvironmentVariables.ApplicationInsightsConnectionString]
                        |> Option.ofObj
                        |> Option.defaultValue ""
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(Constants.EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(Constants.EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(Constants.EnvironmentVariables.RedisHost, "127.0.0.1")
                    .WithEnvironment(Constants.EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(Constants.EnvironmentVariables.OrleansClusterId, orleansClusterId)
                    .WithEnvironment(Constants.EnvironmentVariables.OrleansServiceId, orleansServiceId)
                    .WithEnvironment(Constants.EnvironmentVariables.GracePubSubSystem, pubSubSystem)
                    .AsHttp2Service()
                    .WithOtlpExporter()

            let forwardedAuthKeys = List<string>()
            addOptionalEnvironment graceServer configuration Constants.EnvironmentVariables.GraceAuthOidcAuthority forwardedAuthKeys
            addOptionalEnvironment graceServer configuration Constants.EnvironmentVariables.GraceAuthOidcAudience forwardedAuthKeys
            logForwardedSettings "Grace.Server auth settings" forwardedAuthKeys

            if isTestRun && not useFixedTestPorts then
                let graceTargetPort = getAvailableTcpPort ()

                graceServer
                    .WithHttpEndpoint(targetPort = graceTargetPort, name = "http")
                    .WithEnvironment("ASPNETCORE_URLS", $"http://127.0.0.1:{graceTargetPort}")
                |> ignore
            else
                graceServer
                    .WithEnvironment("ASPNETCORE_URLS", "https://+:5001;http://+:5000")
                    .WithEnvironment(Constants.EnvironmentVariables.GraceServerUri, "http://localhost:5000")
                    .WithHttpEndpoint(targetPort = 5000, name = "http")
                    .WithHttpsEndpoint(targetPort = 5001, name = "https")
                |> ignore

            if not isAzureDebugRun then
                Console.WriteLine("Configuring Grace.Server for DebugLocal with local emulators.")
                let azuriteDataPath = Path.Combine(stateRoot, "azurite")
                let cosmosCertPath = Path.Combine(stateRoot, "cosmos-cert")
                let serviceBusConfigPath = Path.Combine(stateRoot, "servicebus")

                Directory.CreateDirectory azuriteDataPath
                |> ignore

                Directory.CreateDirectory cosmosCertPath |> ignore

                Directory.CreateDirectory serviceBusConfigPath
                |> ignore

                let serviceBusTopicName =
                    resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusTopic
                    |> Option.defaultValue Constants.GraceEventStreamTopic

                let operationalFactsTopicName =
                    resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                    |> Option.defaultValue Constants.GraceOperationalFactsTopic

                let serviceBusSubscriptionName =
                    resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusSubscription
                    |> Option.defaultValue "grace-server"

                let serviceBusConfigFile =
                    if skipServiceBus then
                        None
                    else
                        let path = Path.Combine(serviceBusConfigPath, $"config_{Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}.json")
                        createServiceBusConfiguration path configuration
                        Some path

                let azuriteContainerName =
                    runSuffix
                    |> Option.map (fun suffix -> $"azurite-{suffix}")
                    |> Option.defaultValue "azurite"

                let azurite =
                    builder
                        .AddContainer("azurite", "mcr.microsoft.com/azure-storage/azurite", "latest")
                        .WithContainerName(azuriteContainerName)
                        .WithArgs("azurite", "--skipApiVersionCheck", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0")
                        .WithBindMount(azuriteDataPath, "/data")
                        .WithEnvironment(
                            "AZURITE_ACCOUNTS",
                            "gracevcsdevelopment:Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
                        )
                        .WithEndpoint(targetPort = 10000, port = 10000, name = "blob", scheme = "http")
                        .WithEndpoint(targetPort = 10001, port = 10001, name = "queue", scheme = "http")
                        .WithEndpoint(targetPort = 10002, port = 10002, name = "table", scheme = "http")

                if isTestRun then
                    azurite.WithLifetime(ContainerLifetime.Session)
                    |> ignore

                let azuriteBlobHostAndPort =
                    azurite
                        .GetEndpoint("blob")
                        .Property(EndpointProperty.HostAndPort)

                let azuriteQueueHostAndPort =
                    azurite
                        .GetEndpoint("queue")
                        .Property(EndpointProperty.HostAndPort)

                let azuriteTableHostAndPort =
                    azurite
                        .GetEndpoint("table")
                        .Property(EndpointProperty.HostAndPort)

                let cosmosDatabaseName =
                    configuration[getConfigKey Constants.EnvironmentVariables.AzureCosmosDBDatabaseName]
                    |> Option.ofObj
                    |> Option.defaultValue "grace-dev"

                let cosmosDbContainerName =
                    configuration[getConfigKey Constants.EnvironmentVariables.AzureCosmosDBContainerName]
                    |> Option.ofObj
                    |> Option.defaultValue "grace-events"

                let cosmosGatewayHostPort = 8081

                let cosmos =
                    builder
                        .AddAzureCosmosDB("cosmos")
                        .RunAsPreviewEmulator(fun emulator ->
                            emulator
                                .WithContainerName(
                                    runSuffix
                                    |> Option.map (fun suffix -> $"cosmosdb-emulator-{suffix}")
                                    |> Option.defaultValue "cosmosdb-emulator"
                                )
                                .WithEnvironment("ACCEPT_EULA", "Y")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "10")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "false")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "127.0.0.1")
                                .WithEnvironment("ENABLE_OTLP_EXPORTER", "true")
                                .WithEnvironment("LOG_LEVEL", "info")
                                .WithArgs("--protocol", "https")
                                .WithDataExplorer(1234)
                                .WithGatewayPort(cosmosGatewayHostPort)
                            |> ignore

                            if isTestRun then
                                emulator.WithLifetime(ContainerLifetime.Session)
                                |> ignore)

                cosmos
                    .AddCosmosDatabase(cosmosDatabaseName)
                    .AddContainer(cosmosDbContainerName, "/PartitionKey")
                |> ignore

                let cosmosConnStr = cosmos.Resource.ConnectionStringExpression

                let azuriteConnectionBuilder = ReferenceExpressionBuilder()

                azuriteConnectionBuilder.AppendLiteral(
                    "DefaultEndpointsProtocol=http;AccountName=gracevcsdevelopment;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://"
                )

                azuriteConnectionBuilder.AppendValueProvider(azuriteBlobHostAndPort, null)
                azuriteConnectionBuilder.AppendLiteral("/gracevcsdevelopment;QueueEndpoint=http://")
                azuriteConnectionBuilder.AppendValueProvider(azuriteQueueHostAndPort, null)
                azuriteConnectionBuilder.AppendLiteral("/gracevcsdevelopment;TableEndpoint=http://")
                azuriteConnectionBuilder.AppendValueProvider(azuriteTableHostAndPort, null)
                azuriteConnectionBuilder.AppendLiteral("/gracevcsdevelopment;")

                let azuriteConnection = azuriteConnectionBuilder.Build()

                graceServer
                    .WithParentRelationship(azurite.Resource)
                    .WithParentRelationship(cosmos.Resource)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureStorageConnectionString, azuriteConnection)
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureStorageKey,
                        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.AzureCosmosDBConnectionString, cosmosConnStr)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureCosmosDBDatabaseName, cosmosDatabaseName)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureCosmosDBContainerName, cosmosDbContainerName)
                    .WithEnvironment(Constants.EnvironmentVariables.GraceLogDirectory, logDirectory)
                    .WithEnvironment(Constants.EnvironmentVariables.DebugEnvironment, "Local")
                |> ignore

                if not skipServiceBus then
                    let serviceBusSqlPassword =
                        configuration["grace:azure_service_bus:sqlpassword"]
                        |> Option.ofObj
                        |> Option.defaultValue "SqlIsAwesome1!"

                    let serviceBusConfigFilePath =
                        serviceBusConfigFile
                        |> Option.defaultWith (fun () -> invalidOp "Service Bus config file was not created.")

                    let serviceBusSqlResourceName =
                        runSuffix
                        |> Option.map (fun suffix -> $"servicebus-sql-{suffix}")
                        |> Option.defaultValue "servicebus-sql"

                    let serviceBusSql =
                        builder
                            .AddContainer(serviceBusSqlResourceName, "mcr.microsoft.com/mssql/server", "2022-latest")
                            .WithContainerName(serviceBusSqlResourceName)
                            .WithEnvironment("ACCEPT_EULA", "Y")
                            .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                            .WithEndpoint(targetPort = 1433, name = "sql", scheme = "tcp")

                    if isTestRun then
                        serviceBusSql
                            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024")
                            .WithLifetime(ContainerLifetime.Session)
                        |> ignore
                    else
                        match configuration["grace:azure_service_bus:sqlmemory"] with
                        | value when not (String.IsNullOrWhiteSpace value) ->
                            serviceBusSql.WithEnvironment("MSSQL_MEMORY_LIMIT_MB", value)
                            |> ignore
                        | _ -> ()

                    let serviceBusSqlEndpoint = serviceBusSql.GetEndpoint("sql")

                    let serviceBusEmulatorResourceName =
                        runSuffix
                        |> Option.map (fun suffix -> $"servicebus-emulator-{suffix}")
                        |> Option.defaultValue "servicebus-emulator"

                    let serviceBusEmulator =
                        builder
                            .AddContainer(serviceBusEmulatorResourceName, "mcr.microsoft.com/azure-messaging/servicebus-emulator", "latest")
                            .WithContainerName(serviceBusEmulatorResourceName)
                            .WithParentRelationship(serviceBusSql.Resource)
                            .WithEnvironment("ACCEPT_EULA", "Y")
                            .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                            .WithEnvironment("SQL_SERVER", serviceBusSqlResourceName)
                            .WithEnvironment("SQL_WAIT_INTERVAL", "10")
                            .WithBindMount(serviceBusConfigFilePath, "/ServiceBus_Emulator/ConfigFiles/Config.json")
                            .WithEndpoint(targetPort = 5672, port = 5672, name = "amqp", scheme = "amqp")
                            .WithEndpoint(targetPort = 5300, port = 5300, name = "management", scheme = "http")

                    if isTestRun then
                        serviceBusEmulator.WithLifetime(ContainerLifetime.Session)
                        |> ignore

                    let serviceBusHostAndPort =
                        serviceBusEmulator
                            .GetEndpoint("amqp")
                            .Property(EndpointProperty.HostAndPort)

                    let serviceBusConnectionBuilder = ReferenceExpressionBuilder()
                    serviceBusConnectionBuilder.AppendLiteral("Endpoint=sb://")
                    serviceBusConnectionBuilder.AppendValueProvider(serviceBusHostAndPort, null)

                    serviceBusConnectionBuilder.AppendLiteral(
                        ";SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
                    )

                    let serviceBusConnection = serviceBusConnectionBuilder.Build()

                    graceServer
                        .WithParentRelationship(serviceBusEmulator.Resource)
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusConnectionString, serviceBusConnection)
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusTopic, serviceBusTopicName)
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)
                        .WithEnvironment(ResourceNames.OperationalFactsProcessorSubscriptionSetting, ResourceNames.OperationalFactsProcessorSubscription)
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusSubscription, serviceBusSubscriptionName)
                    |> ignore

                    builder
                        .AddProject("grace-operations-worker", "..\\Grace.Operations.Worker\\Grace.Operations.Worker.fsproj")
                        .WithParentRelationship(serviceBusSql.Resource)
                        .WithParentRelationship(serviceBusEmulator.Resource)
                        .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                        .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusConnectionString, serviceBusConnection)
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
                        .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)
                        .WithEnvironment(ResourceNames.OperationalFactsProcessorSubscriptionSetting, ResourceNames.OperationalFactsProcessorSubscription)
                        .WithEnvironment(
                            Func<EnvironmentCallbackContext, Task> (fun context ->
                                task {
                                    let! sqlEndpoint = serviceBusSqlEndpoint.GetValueAsync(context.CancellationToken)

                                    if String.IsNullOrWhiteSpace sqlEndpoint then
                                        invalidOp "Service Bus SQL endpoint was not allocated for operations worker configuration."

                                    let sqlEndpointUri = Uri(sqlEndpoint)
                                    let sqlDataSource = BuildSqlTcpDataSource(sqlEndpointUri.Host, sqlEndpointUri.Port)
                                    context.Logger.LogInformation("Configured operations worker SQL data source {SqlDataSource}.", sqlDataSource)

                                    context.EnvironmentVariables[
                                        ResourceNames.OperationsSqlConnectionStringSetting
                                    ] <-
                                        $"Server={sqlDataSource};Initial Catalog=GraceOperations;User ID=sa;Password={serviceBusSqlPassword};TrustServerCertificate=True;Encrypt=False;"
                                })
                        )
                        .WithEnvironment(Constants.EnvironmentVariables.DebugEnvironment, "Local")
                        .WithOtlpExporter()
                    |> ignore
                else
                    Console.WriteLine("Skipping Service Bus emulator for this test run (GRACE_TEST_SKIP_SERVICEBUS=1).")

                Console.WriteLine("Grace.Server DebugLocal environment configured:")
                Console.WriteLine("  - Azurite at http://localhost:10000-10002")
                Console.WriteLine($"  - Azurite data at {azuriteDataPath}")
                Console.WriteLine("  - Cosmos emulator at http://localhost:8081")

                if not skipServiceBus then
                    Console.WriteLine("  - Service Bus emulator at amqp://localhost:5672")
                    Console.WriteLine($"  - Service Bus config at {serviceBusConfigFile}")

                Console.WriteLine("  - Aspire dashboard at http://localhost:18888")
                Console.WriteLine($"  - OTLP endpoint {otlpEndpoint}")
            else
                Console.WriteLine("Configuring Grace.Server for DebugAzure with real Azure resources.")
                let azureStorageConnectionString = resolveSetting configuration Constants.EnvironmentVariables.AzureStorageConnectionString
                let azureStorageAccountName = resolveSetting configuration Constants.EnvironmentVariables.AzureStorageAccountName

                let azureStorageAccountName =
                    match azureStorageConnectionString with
                    | None ->
                        let accountName = getRequiredSetting configuration Constants.EnvironmentVariables.AzureStorageAccountName
                        Console.WriteLine($"Using Azure Storage account: {accountName}.")
                        Some accountName
                    | Some _ ->
                        Console.WriteLine("Using Azure Storage connection string from configuration.")
                        azureStorageAccountName

                let cosmosdbEndpoint = getRequiredSetting configuration Constants.EnvironmentVariables.AzureCosmosDBEndpoint
                Console.WriteLine($"Using Cosmos DB endpoint: {cosmosdbEndpoint}.")
                let cosmosDatabaseName = getRequiredSetting configuration Constants.EnvironmentVariables.AzureCosmosDBDatabaseName
                let cosmosContainerName = getRequiredSetting configuration Constants.EnvironmentVariables.AzureCosmosDBContainerName
                let serviceBusConnectionString = resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusConnectionString
                let serviceBusNamespace = resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusNamespace

                if serviceBusConnectionString.IsNone
                   && serviceBusNamespace.IsNone then
                    invalidOp
                        $"DebugAzure requires '{Constants.EnvironmentVariables.AzureServiceBusConnectionString}' or '{Constants.EnvironmentVariables.AzureServiceBusNamespace}' for operational usage ingestion."

                let serviceBusTopic = resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusTopic
                let operationalFactsTopic = getRequiredSetting configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic
                ensureDistinctServiceBusTopics serviceBusTopic (Some operationalFactsTopic)
                let operationalFactsProcessorSubscription = getRequiredSetting configuration ResourceNames.OperationalFactsProcessorSubscriptionSetting
                ensureOperationalFactsProcessorSubscription (Some operationalFactsProcessorSubscription)
                let serviceBusSubscription = resolveSetting configuration Constants.EnvironmentVariables.AzureServiceBusSubscription
                let operationsSqlConnectionString = getRequiredSetting configuration ResourceNames.OperationsSqlConnectionStringSetting

                graceServer
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureStorageAccountName,
                        azureStorageAccountName
                        |> Option.defaultValue null
                    )
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureStorageConnectionString,
                        azureStorageConnectionString
                        |> Option.defaultValue null
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.AzureCosmosDBEndpoint, cosmosdbEndpoint)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureCosmosDBDatabaseName, cosmosDatabaseName)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureCosmosDBContainerName, cosmosContainerName)
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                        serviceBusConnectionString
                        |> Option.defaultValue null
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusNamespace, serviceBusNamespace |> Option.defaultValue null)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusTopic, serviceBusTopic |> Option.defaultValue null)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopic)
                    .WithEnvironment(ResourceNames.OperationalFactsProcessorSubscriptionSetting, ResourceNames.OperationalFactsProcessorSubscription)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusSubscription, serviceBusSubscription |> Option.defaultValue null)
                    .WithEnvironment(Constants.EnvironmentVariables.GraceLogDirectory, logDirectory)
                    .WithEnvironment(Constants.EnvironmentVariables.DebugEnvironment, "Azure")
                |> ignore

                builder
                    .AddProject("grace-operations-worker", "..\\Grace.Operations.Worker\\Grace.Operations.Worker.fsproj")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                        serviceBusConnectionString
                        |> Option.defaultValue null
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusNamespace, serviceBusNamespace |> Option.defaultValue null)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopic)
                    .WithEnvironment(ResourceNames.OperationalFactsProcessorSubscriptionSetting, ResourceNames.OperationalFactsProcessorSubscription)
                    .WithEnvironment(ResourceNames.OperationsSqlConnectionStringSetting, operationsSqlConnectionString)
                    .WithEnvironment(Constants.EnvironmentVariables.DebugEnvironment, "Azure")
                    .WithOtlpExporter()
                |> ignore

                Console.WriteLine("Grace.Server DebugAzure environment configured (no emulators started):")
                Console.WriteLine("  - Azure Storage: using DefaultAzureCredential.")
                Console.WriteLine("  - Azure Cosmos: using DefaultAzureCredential.")
                Console.WriteLine("  - Azure Service Bus: using DefaultAzureCredential.")
                Console.WriteLine($"  - Operational facts processor subscription: {operationalFactsProcessorSubscription}.")
                Console.WriteLine("  - Aspire dashboard at http://localhost:18888")
                Console.WriteLine($"  - OTLP endpoint {otlpEndpoint}")
        else
            let cosmos = builder.AddAzureCosmosDB("cosmos")

            let cosmosDatabase =
                cosmos.AddCosmosDatabase(
                    configuration[getConfigKey Constants.EnvironmentVariables.AzureCosmosDBDatabaseName]
                    |> Option.ofObj
                    |> Option.defaultValue "grace-dev"
                )

            cosmosDatabase.AddContainer(
                configuration[getConfigKey Constants.EnvironmentVariables.AzureCosmosDBContainerName]
                |> Option.ofObj
                |> Option.defaultValue "grace-events",
                "/PartitionKey"
            )
            |> ignore

            let storage = builder.AddAzureStorage("storage")
            let blobStorage = storage.AddBlobContainer("directoryversions")
            let diffStorage = storage.AddBlobContainer("diffs")
            let zipStorage = storage.AddBlobContainer("zipfiles")
            let serviceBus = builder.AddAzureServiceBus("servicebus")

            let serviceBusTopicName =
                configuration[getConfigKey Constants.EnvironmentVariables.AzureServiceBusTopic]
                |> Option.ofObj
                |> Option.defaultValue Constants.GraceEventStreamTopic

            let operationalFactsTopicName =
                configuration[getConfigKey Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic]
                |> Option.ofObj
                |> Option.defaultValue Constants.GraceOperationalFactsTopic

            ensureDistinctServiceBusTopics (Some serviceBusTopicName) (Some operationalFactsTopicName)

            let graceEventSubscriptionName =
                configuration[getConfigKey Constants.EnvironmentVariables.AzureServiceBusSubscription]
                |> Option.ofObj
                |> Option.defaultValue "grace-server"

            serviceBus
                .AddServiceBusTopic(ResourceNames.GraceEventTopic, serviceBusTopicName)
                .AddServiceBusSubscription(ResourceNames.GraceEventSubscription, graceEventSubscriptionName)
            |> ignore

            serviceBus
                .AddServiceBusTopic(ResourceNames.OperationalFactsTopic, operationalFactsTopicName)
                .WithProperties(fun topic ->
                    topic.RequiresDuplicateDetection <- true
                    topic.DuplicateDetectionHistoryTimeWindow <- TimeSpan.FromMinutes 5.0)
                .AddServiceBusSubscription(ResourceNames.OperationalFactsProcessorSubscriptionResource, ResourceNames.OperationalFactsProcessorSubscription)
            |> ignore

            let otlpEndpoint =
                configuration["grace:otlp_endpoint"]
                |> Option.ofObj
                |> Option.defaultValue "http://localhost:18889"

            let publishLogDirectory =
                configuration["grace:log_directory"]
                |> Option.ofObj
                |> Option.defaultValue "/tmp/grace-logs"

            let graceServer =
                builder
                    .AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithReference(cosmosDatabase :?> IResourceBuilder<IResourceWithConnectionString>)
                    .WithReference(blobStorage :?> IResourceBuilder<IResourceWithConnectionString>)
                    .WithReference(diffStorage :?> IResourceBuilder<IResourceWithConnectionString>)
                    .WithReference(zipStorage :?> IResourceBuilder<IResourceWithConnectionString>)
                    .WithReference(serviceBus :?> IResourceBuilder<IResourceWithConnectionString>)
                    .WithParentRelationship(redis.Resource)
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Production")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(
                        Constants.EnvironmentVariables.ApplicationInsightsConnectionString,
                        configuration[getConfigKey Constants.EnvironmentVariables.ApplicationInsightsConnectionString]
                        |> Option.ofObj
                        |> Option.defaultValue ""
                    )
                    .WithEnvironment(
                        Constants.EnvironmentVariables.GraceServerUri,
                        configuration[getConfigKey Constants.EnvironmentVariables.GraceServerUri]
                        |> Option.ofObj
                        |> Option.defaultValue "https://localhost:5001"
                    )
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureCosmosDBDatabaseName,
                        configuration[getConfigKey Constants.EnvironmentVariables.AzureCosmosDBDatabaseName]
                        |> Option.ofObj
                        |> Option.defaultValue "grace-dev"
                    )
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureCosmosDBContainerName,
                        configuration[getConfigKey Constants.EnvironmentVariables.AzureCosmosDBContainerName]
                        |> Option.ofObj
                        |> Option.defaultValue "grace-events"
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(Constants.EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(Constants.EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(Constants.EnvironmentVariables.RedisHost, "localhost")
                    .WithEnvironment(Constants.EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(
                        Constants.EnvironmentVariables.OrleansClusterId,
                        configuration[getConfigKey Constants.EnvironmentVariables.OrleansClusterId]
                        |> Option.ofObj
                        |> Option.defaultValue "production"
                    )
                    .WithEnvironment(
                        Constants.EnvironmentVariables.OrleansServiceId,
                        configuration[getConfigKey Constants.EnvironmentVariables.OrleansServiceId]
                        |> Option.ofObj
                        |> Option.defaultValue "grace-prod"
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.GracePubSubSystem, pubSubSystem)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusTopic, serviceBusTopicName)
                    .WithEnvironment(Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)
                    .WithEnvironment(ResourceNames.OperationalFactsProcessorSubscriptionSetting, ResourceNames.OperationalFactsProcessorSubscription)
                    .WithEnvironment(
                        Constants.EnvironmentVariables.AzureServiceBusSubscription,
                        configuration[getConfigKey Constants.EnvironmentVariables.AzureServiceBusSubscription]
                        |> Option.ofObj
                        |> Option.defaultValue "grace-server"
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.GraceLogDirectory, publishLogDirectory)
                    .WithEnvironment(
                        Constants.EnvironmentVariables.GraceAuthOidcAuthority,
                        configuration[Constants.EnvironmentVariables.GraceAuthOidcAuthority]
                    )
                    .WithEnvironment(Constants.EnvironmentVariables.GraceAuthOidcAudience, configuration[Constants.EnvironmentVariables.GraceAuthOidcAudience])
                    .WithEnvironment(
                        Constants.EnvironmentVariables.GraceAuthOidcCliClientId,
                        configuration[Constants.EnvironmentVariables.GraceAuthOidcCliClientId]
                    )
                    .WithHttpEndpoint(targetPort = 5000, name = "http")
                    .WithHttpsEndpoint(targetPort = 5001, name = "https")
                    .AsHttp2Service()
                    .WithOtlpExporter()

            let forwardedAuthKeys = List<string>()
            addOptionalEnvironment graceServer configuration Constants.EnvironmentVariables.GraceAuthOidcAuthority forwardedAuthKeys
            addOptionalEnvironment graceServer configuration Constants.EnvironmentVariables.GraceAuthOidcAudience forwardedAuthKeys
            logForwardedSettings "Grace.Server auth settings" forwardedAuthKeys
            Console.WriteLine("Grace.Server publish/production environment configured (Azure resources with MI by default).")
            Console.WriteLine("  - Redis remains local container")
            Console.WriteLine($"  - OTLP endpoint {otlpEndpoint}")

        use appHost = builder.Build()

        let loggerFactory =
            match appHost.Services.GetService(typeof<ILoggerFactory>) with
            | :? ILoggerFactory as existing -> existing
            | _ -> LoggerFactory.Create(fun logging -> logging.AddSimpleConsole() |> ignore)

        let logger = loggerFactory.CreateLogger("Grace.Aspire.AppHost")
        let stopwatch = Stopwatch.StartNew()

        try
            appHost.Run()
            stopwatch.Stop()
            logger.LogInformation("Aspire host exited normally. elapsedMs={Elapsed}", stopwatch.ElapsedMilliseconds)
            0
        with
        | ex ->
            stopwatch.Stop()
            logger.LogError(ex, "Aspire host terminated with error. elapsedMs={Elapsed}", stopwatch.ElapsedMilliseconds)
            1
    with
    | ex ->
        Console.WriteLine($"Error starting Aspire host: {ex.Message}")
        Console.WriteLine(ex.StackTrace)
        1
