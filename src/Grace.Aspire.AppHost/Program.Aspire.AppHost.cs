extern alias Shared;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Redis;
using Grace.Shared;
using static Grace.Shared.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using static Grace.Types.Common;
using static Shared::Grace.Shared.Constants;

public partial class Program
{
    private const string AspireResourceModeEnvVar = "ASPIRE_RESOURCE_MODE";
    private const string AspireResourceModeLocal = "Local";
    private const string AspireResourceModeAzure = "Azure";
    private const string GraceEventTopicResourceName = "grace-event-topic";
    private const string GraceEventSubscriptionResourceName = "grace-event-subscription";
    private const string OperationalFactsTopicResourceName = "grace-operational-facts-topic";
    private const string GraceUsageCollectorSubscriptionName = "grace-usage-collector";
    private const string GraceUsageCollectorSubscriptionResourceName = "grace-usage-collector-subscription";
    private const string OperationalFactsProcessorSubscriptionSettingName = "grace__azure_service_bus__operational_facts_processor_subscription";
    private const string OperationsSqlConnectionStringSettingName = "grace__operations__sql__connectionstring";
    internal const string DebugAzureBootstrapModeEnvironmentVariable = "GRACE_DEBUGAZURE_BOOTSTRAP_MODE";
    internal const string DebugAzureBootstrapUserIdEnvironmentVariable = "GRACE_DEBUGAZURE_BOOTSTRAP_USER_ID";
    internal const string DebugAzureBootstrapModeSuppress = "Suppress";
    internal const string DebugAzureBootstrapModeExactUser = "ExactUser";

    internal sealed record AuthorizationBootstrapSettings(string? Users, string? Groups);

    private static void Main(string[] args)
    {
        try
        {
            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
                .AddUserSecrets(typeof(Program).Assembly, optional: true)
                .AddEnvironmentVariables()
                .Build();

            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

            // Run-mode switch:
            //   - Local (default): containers/emulators for Azurite, Cosmos emulator, ServiceBus emulator
            //   - Azure: debug locally, but use real Azure resources (connection strings from config/env/user-secrets)
            var resourceMode =
                Environment.GetEnvironmentVariable(AspireResourceModeEnvVar)
                ?? configuration[$"grace:{AspireResourceModeEnvVar}"]
                ?? AspireResourceModeLocal;

            var isRunMode = builder.ExecutionContext.IsRunMode;
            var isPublishMode = builder.ExecutionContext.IsPublishMode;
            Console.WriteLine($"Aspire execution context: Run={isRunMode}; Publish={isPublishMode}.");
            var isAzureDebugRun =
                isRunMode &&
                resourceMode.Equals(AspireResourceModeAzure, StringComparison.OrdinalIgnoreCase);
            var isTestRun =
                Environment.GetEnvironmentVariable("GRACE_TESTING") is string testValue
                && (testValue.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || testValue.Equals("true", StringComparison.OrdinalIgnoreCase));
            var runSuffix = isTestRun
                ? (Environment.GetEnvironmentVariable("GRACE_TEST_RUN_ID") ?? Guid.NewGuid().ToString("N"))
                : null;
            static bool IsTruthy(string? value) =>
                !string.IsNullOrWhiteSpace(value)
                && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
            var skipServiceBus = isTestRun && IsTruthy(Environment.GetEnvironmentVariable("GRACE_TEST_SKIP_SERVICEBUS"));
            var useFixedTestPorts = isTestRun && IsTruthy(Environment.GetEnvironmentVariable("GRACE_TEST_FIXED_PORTS"));
            var pubSubSystem = skipServiceBus ? "UnknownPubSubProvider" : "AzureServiceBus";
            if (isTestRun)
            {
                CleanupDockerContainers(new[] { "servicebus-sql", "servicebus-emulator" });
            }

            // Redis: keep local container for both run modes (Local + Azure debug), and even in publish mode if you like.
            var redisContainerName = runSuffix is null ? "redis" : $"redis-{runSuffix}";
            var redis = builder.AddContainer("redis", "redis", "8.6.3")
                .WithContainerName(redisContainerName)
                //.WithLifetime(ContainerLifetime.Session)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEndpoint(targetPort: 6379, port: 6379, name: "tcp", scheme: "tcp");
            var redisEndpoint = redis.GetEndpoint("tcp");
            if (isTestRun)
            {
                redis.WithLifetime(ContainerLifetime.Session);
            }

            if (!isPublishMode)
            {
                // =========================
                // RUN MODE (debug / local)
                // =========================

                // Common settings for local debugging
                var otlpEndpoint = configuration["grace:otlp_endpoint"] ?? "http://localhost:18889";
                var stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grace", "aspire");
                var logDirectory = Path.Combine(stateRoot, "logs");

                Directory.CreateDirectory(stateRoot);
                Directory.CreateDirectory(logDirectory);

                var cacheTargetPort = GetAvailableTcpPort();
                var cacheUrl = "http://127.0.0.1:" + cacheTargetPort;
                var graceCache = builder.AddProject("grace-cache", "..\\Grace.Cache\\Grace.Cache.fsproj")
                    .WithEnvironment("ASPNETCORE_URLS", cacheUrl)
                    .WithHttpEndpoint(targetPort: cacheTargetPort, name: "http");
                var forwardedCacheKeys = new List<string>();
                AddOptionalEnvironment(graceCache, configuration, "Cache__DatabasePath", forwardedCacheKeys);
                AddOptionalEnvironment(graceCache, configuration, "Cache__ManagedRoot", forwardedCacheKeys);
                LogForwardedSettings("Grace.Cache local settings", forwardedCacheKeys);

                // These get set in both Local and Azure-debug runs.
                var orleansClusterId = configuration[getConfigKey(EnvironmentVariables.OrleansClusterId)] ?? "local";
                var orleansServiceId = configuration[getConfigKey(EnvironmentVariables.OrleansServiceId)] ?? "grace-dev";

                if (isTestRun && runSuffix is not null)
                {
                    orleansClusterId = $"{orleansClusterId}-test-{runSuffix}";
                    orleansServiceId = $"{orleansServiceId}-test-{runSuffix}";
                }

                Console.WriteLine($"Using Orleans ClusterId='{orleansClusterId}' and ServiceId='{orleansServiceId}'.");

                var graceServer = builder.AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithParentRelationship(redis)
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(EnvironmentVariables.ApplicationInsightsConnectionString, configuration[getConfigKey(EnvironmentVariables.ApplicationInsightsConnectionString)] ?? string.Empty)
                    .WithEnvironment(EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(EnvironmentVariables.OrleansClusterId, orleansClusterId)
                    .WithEnvironment(EnvironmentVariables.OrleansServiceId, orleansServiceId)
                    .WithEnvironment(EnvironmentVariables.GracePubSubSystem, pubSubSystem)
                    .AsHttp2Service()
                    .WithOtlpExporter();

                var forwardedAuthKeys = new List<string>();
                AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthOidcAuthority, forwardedAuthKeys);
                AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthOidcAudience, forwardedAuthKeys);
                var authorizationBootstrapSettings = ResolveAuthorizationBootstrapSettings(configuration, isAzureDebugRun);
                AddOptionalEnvironment(
                    graceServer,
                    EnvironmentVariables.GraceAuthzBootstrapSystemAdminUsers,
                    authorizationBootstrapSettings.Users,
                    forwardedAuthKeys);
                AddOptionalEnvironment(
                    graceServer,
                    EnvironmentVariables.GraceAuthzBootstrapSystemAdminGroups,
                    authorizationBootstrapSettings.Groups,
                    forwardedAuthKeys);
                LogForwardedSettings("Grace.Server auth settings", forwardedAuthKeys);

                if (isTestRun && !useFixedTestPorts)
                {
                    var graceTargetPort = GetAvailableTcpPort();

                    graceServer
                        .WithHttpEndpoint(targetPort: graceTargetPort, name: "http")
                        .WithEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:" + graceTargetPort);
                }
                else
                {
                    graceServer
                        .WithEnvironment("ASPNETCORE_URLS", "https://+:5001;http://+:5000")
                        .WithEnvironment(EnvironmentVariables.GraceServerUri, "http://localhost:5000")
                        .WithHttpEndpoint(targetPort: 5000, name: "http")
                        .WithHttpsEndpoint(targetPort: 5001, name: "https");
                }

                if (!isAzureDebugRun)
                {
                    // -------------------------
                    // DebugLocal (default): containers/emulators
                    // -------------------------
                    Console.WriteLine("Configuring Grace.Server for DebugLocal with local emulators.");
                    graceServer.WithEnvironment(async context =>
                    {
                        var endpoint = await redisEndpoint.GetValueAsync(context.CancellationToken);
                        if (string.IsNullOrWhiteSpace(endpoint))
                        {
                            throw new InvalidOperationException("Aspire did not allocate the local Redis endpoint.");
                        }

                        var endpointUri = new Uri(endpoint);
                        context.EnvironmentVariables[EnvironmentVariables.RedisHost] = endpointUri.Host;
                        context.EnvironmentVariables[EnvironmentVariables.RedisPort] = endpointUri.Port.ToString();
                    });
                    var azuriteDataPath = Path.Combine(stateRoot, "azurite");
                    var cosmosCertPath = Path.Combine(stateRoot, "cosmos-cert");
                    var serviceBusConfigPath = Path.Combine(stateRoot, "servicebus");

                Directory.CreateDirectory(azuriteDataPath);
                Directory.CreateDirectory(cosmosCertPath);
                Directory.CreateDirectory(serviceBusConfigPath);

                var serviceBusTopicName =
                    ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusTopic)
                    ?? Constants.GraceEventStreamTopic;
                var operationalFactsTopicName =
                    ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusOperationalFactsTopic)
                    ?? Constants.GraceOperationalFactsTopic;
                var serviceBusSubscriptionName =
                    ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusSubscription)
                    ?? "grace-server";
                var graceUsageCollectorSubscriptionName =
                    ResolveSetting(configuration, OperationalFactsProcessorSubscriptionSettingName)
                    ?? GraceUsageCollectorSubscriptionName;

                // Create Service Bus emulator config (when enabled for tests)
                string? serviceBusConfigFile = null;
                if (!skipServiceBus)
                {
                    serviceBusConfigFile = Path.Combine(
                        serviceBusConfigPath,
                        $"config_{Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}.json");
                    CreateServiceBusConfiguration(serviceBusConfigFile, configuration);
                }

                    var azuriteContainerName = runSuffix is null ? "azurite" : $"azurite-{runSuffix}";
                    var azurite = builder.AddContainer("azurite", "mcr.microsoft.com/azure-storage/azurite", "latest")
                        .WithContainerName(azuriteContainerName)
                        .WithArgs(
                            "azurite",
                            "--skipApiVersionCheck",
                            "--blobHost", "0.0.0.0",
                            "--queueHost", "0.0.0.0",
                            "--tableHost", "0.0.0.0")
                        .WithBindMount(azuriteDataPath, "/data")
                        //.WithLifetime(ContainerLifetime.Session)
                        .WithEnvironment("AZURITE_ACCOUNTS", "gracevcsdevelopment:Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                        .WithEndpoint(targetPort: 10000, port: 10000, name: "blob", scheme: "http")
                        .WithEndpoint(targetPort: 10001, port: 10001, name: "queue", scheme: "http")
                        .WithEndpoint(targetPort: 10002, port: 10002, name: "table", scheme: "http");
                    if (isTestRun)
                    {
                        azurite.WithLifetime(ContainerLifetime.Session);
                    }
                    var azuriteBlobEndpoint = azurite.GetEndpoint("blob");
                    var azuriteQueueEndpoint = azurite.GetEndpoint("queue");
                    var azuriteTableEndpoint = azurite.GetEndpoint("table");
                    var azuriteBlobHostAndPort = azuriteBlobEndpoint.Property(EndpointProperty.HostAndPort);
                    var azuriteQueueHostAndPort = azuriteQueueEndpoint.Property(EndpointProperty.HostAndPort);
                    var azuriteTableHostAndPort = azuriteTableEndpoint.Property(EndpointProperty.HostAndPort);

                    // Cosmos emulator
                    var cosmosDatabaseName = configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBDatabaseName)] ?? "grace-dev";
                    var cosmosDbContainerName = configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBContainerName)] ?? "grace-events";
                    const int cosmosGatewayHostPort = 8081;

#pragma warning disable ASPIRECOSMOSDB001
                    var cosmosEmulatorContainerName = runSuffix is null ? "cosmosdb-emulator" : $"cosmosdb-emulator-{runSuffix}";
                    var cosmos = builder.AddAzureCosmosDB("cosmos")
                        .RunAsPreviewEmulator(emulator =>
                        {
                            emulator
                                .WithContainerName(cosmosEmulatorContainerName)
                                //.WithLifetime(ContainerLifetime.Session)
                                .WithEnvironment("ACCEPT_EULA", "Y")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "10")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "false")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "127.0.0.1")
                                .WithEnvironment("ENABLE_OTLP_EXPORTER", "true")
                                .WithEnvironment("LOG_LEVEL", "info")
                                .WithArgs("--protocol", "https")
                                .WithDataExplorer(1234)
                                .WithGatewayPort(cosmosGatewayHostPort);

                            if (isTestRun)
                            {
                                emulator.WithLifetime(ContainerLifetime.Session);
                            }
                        });
#pragma warning restore ASPIRECOSMOSDB001

                    _ = cosmos.AddCosmosDatabase(cosmosDatabaseName)
                        .AddContainer(cosmosDbContainerName, "/PartitionKey");
                    var cosmosConnStr = cosmos.Resource.ConnectionStringExpression;

                    graceServer
                        .WithParentRelationship(azurite)
                        .WithParentRelationship(cosmos)
                        .WithEnvironment(
                            EnvironmentVariables.AzureStorageConnectionString,
                            ReferenceExpression.Create(
                                $"DefaultEndpointsProtocol=http;AccountName=gracevcsdevelopment;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://{azuriteBlobHostAndPort}/gracevcsdevelopment;QueueEndpoint=http://{azuriteQueueHostAndPort}/gracevcsdevelopment;TableEndpoint=http://{azuriteTableHostAndPort}/gracevcsdevelopment;")
                        )
                        .WithEnvironment(EnvironmentVariables.AzureStorageKey,
                            "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBConnectionString, cosmosConnStr)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, cosmosDatabaseName)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, cosmosDbContainerName)
                        .WithEnvironment(EnvironmentVariables.GraceLogDirectory, logDirectory)
                        .WithEnvironment(EnvironmentVariables.DebugEnvironment, "Local");

                    if (!skipServiceBus)
                    {
                        // Service Bus emulator
                        var serviceBusSqlPassword = configuration["grace:azure_service_bus:sqlpassword"] ?? "SqlIsAwesome1!";
                        var serviceBusConfigFilePath =
                            serviceBusConfigFile ?? throw new InvalidOperationException("Service Bus config file was not created.");

                        var serviceBusSqlResourceName = runSuffix is null ? "servicebus-sql" : $"servicebus-sql-{runSuffix}";
                        var serviceBusSql = builder.AddContainer(serviceBusSqlResourceName, "mcr.microsoft.com/mssql/server", "2022-latest")
                            .WithContainerName(serviceBusSqlResourceName)
                            .WithEnvironment("ACCEPT_EULA", "Y")
                            .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                            //.WithLifetime(ContainerLifetime.Session)
                            .WithEndpoint(targetPort: 1433, name: "sql", scheme: "tcp");
                        if (isTestRun)
                        {
                            serviceBusSql.WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024");
                        }
                        else
                        {
                            var memoryLimit = configuration["grace:azure_service_bus:sqlmemory"];
                            if (!string.IsNullOrWhiteSpace(memoryLimit))
                            {
                                serviceBusSql.WithEnvironment("MSSQL_MEMORY_LIMIT_MB", memoryLimit);
                            }
                        }
                        if (isTestRun)
                        {
                            serviceBusSql.WithLifetime(ContainerLifetime.Session);
                        }

                        var serviceBusSqlEndpoint = serviceBusSql.GetEndpoint("sql");

                        var serviceBusEmulatorResourceName = runSuffix is null ? "servicebus-emulator" : $"servicebus-emulator-{runSuffix}";
                        var serviceBusEmulator = builder.AddContainer(serviceBusEmulatorResourceName, "mcr.microsoft.com/azure-messaging/servicebus-emulator", "latest")
                            .WithContainerName(serviceBusEmulatorResourceName)
                            .WithParentRelationship(serviceBusSql)
                            .WithEnvironment("ACCEPT_EULA", "Y")
                            .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                            .WithEnvironment("SQL_SERVER", serviceBusSqlResourceName)
                            .WithEnvironment("SQL_WAIT_INTERVAL", "10")
                            //.WithLifetime(ContainerLifetime.Session)
                            .WithBindMount(serviceBusConfigFilePath, "/ServiceBus_Emulator/ConfigFiles/Config.json")
                            .WithEndpoint(targetPort: 5672, port: 5672, name: "amqp", scheme: "amqp")
                            .WithEndpoint(targetPort: 5300, port: 5300, name: "management", scheme: "http");
                        if (isTestRun)
                        {
                            serviceBusEmulator.WithLifetime(ContainerLifetime.Session);
                        }

                        var serviceBusAmqpEndpoint = serviceBusEmulator.GetEndpoint("amqp");
                        var serviceBusHostAndPort = serviceBusAmqpEndpoint.Property(EndpointProperty.HostAndPort);

                        graceServer
                            .WithParentRelationship(serviceBusEmulator)
                            .WithEnvironment(
                                EnvironmentVariables.AzureServiceBusConnectionString,
                                ReferenceExpression.Create(
                                    $"Endpoint=sb://{serviceBusHostAndPort};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
                                )
                            )
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, serviceBusTopicName)
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)
                            .WithEnvironment(OperationalFactsProcessorSubscriptionSettingName, graceUsageCollectorSubscriptionName)
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, serviceBusSubscriptionName);

                        _ = builder.AddProject("grace-operations-worker", "..\\Grace.Operations.Worker\\Grace.Operations.Worker.fsproj")
                            .WithParentRelationship(serviceBusSql)
                            .WithParentRelationship(serviceBusEmulator)
                            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                            .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                            .WithEnvironment(
                                EnvironmentVariables.AzureServiceBusConnectionString,
                                ReferenceExpression.Create(
                                    $"Endpoint=sb://{serviceBusHostAndPort};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
                                )
                            )
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)
                            .WithEnvironment(OperationalFactsProcessorSubscriptionSettingName, graceUsageCollectorSubscriptionName)
                            .WithEnvironment(async context =>
                            {
                                var sqlEndpoint = await serviceBusSqlEndpoint.GetValueAsync(context.CancellationToken);
                                if (string.IsNullOrWhiteSpace(sqlEndpoint))
                                {
                                    throw new InvalidOperationException("Service Bus SQL endpoint was not allocated for operations worker configuration.");
                                }

                                var sqlEndpointUri = new Uri(sqlEndpoint);
                                var sqlDataSource = BuildSqlTcpDataSource(sqlEndpointUri.Host, sqlEndpointUri.Port);
                                context.Logger.LogInformation(
                                    "Configured operations worker SQL data source {SqlDataSource}.",
                                    sqlDataSource);
                                context.EnvironmentVariables[OperationsSqlConnectionStringSettingName] =
                                    $"Server={sqlDataSource};Initial Catalog=GraceOperations;User ID=sa;Password={serviceBusSqlPassword};TrustServerCertificate=True;Encrypt=False;";
                            })
                            .WithEnvironment(EnvironmentVariables.DebugEnvironment, "Local")
                            .WithOtlpExporter();
                    }
                    else
                    {
                        Console.WriteLine("Skipping Service Bus emulator for this test run (GRACE_TEST_SKIP_SERVICEBUS=1).");
                    }

                    Console.WriteLine("Grace.Server DebugLocal environment configured:");
                    Console.WriteLine("  - Azurite at http://localhost:10000-10002");
                    Console.WriteLine($"  - Azurite data at {azuriteDataPath}");
                    Console.WriteLine("  - Cosmos emulator at http://localhost:8081");
                    if (!skipServiceBus)
                    {
                        Console.WriteLine("  - Service Bus emulator at amqp://localhost:5672");
                        Console.WriteLine($"  - Service Bus config at {serviceBusConfigFile}");
                    }
                    Console.WriteLine("  - Aspire dashboard at http://localhost:18888");
                    Console.WriteLine($"  - OTLP endpoint {otlpEndpoint}");
                }
                else
                {
                    // -------------------------
                    // DebugAzure: still run locally under debugger, but use REAL Azure resources
                    // -------------------------

                    Console.WriteLine("Configuring Grace.Server for DebugAzure with real Azure resources.");
                    var azureStorageConnectionString = ResolveSetting(configuration, EnvironmentVariables.AzureStorageConnectionString);
                    var azureStorageAccountName = ResolveSetting(configuration, EnvironmentVariables.AzureStorageAccountName);

                    if (!string.IsNullOrWhiteSpace(azureStorageAccountName))
                    {
                        // An explicitly selected account must not be displaced by an old user-level connection string.
                        azureStorageConnectionString = null;
                        Console.WriteLine($"Using Azure Storage account: {azureStorageAccountName}.");
                    }
                    else if (!string.IsNullOrWhiteSpace(azureStorageConnectionString))
                    {
                        Console.WriteLine("Using Azure Storage connection string from configuration.");
                    }
                    else
                    {
                        azureStorageAccountName = GetRequiredSetting(configuration, EnvironmentVariables.AzureStorageAccountName);
                    }

                    var cosmosdbEndpoint = GetRequiredSetting(configuration, EnvironmentVariables.AzureCosmosDBEndpoint);
                    Console.WriteLine($"Using Cosmos DB endpoint: {cosmosdbEndpoint}.");

                    var cosmosDatabaseName = GetRequiredSetting(configuration, EnvironmentVariables.AzureCosmosDBDatabaseName);
                    var cosmosContainerName = GetRequiredSetting(configuration, EnvironmentVariables.AzureCosmosDBContainerName);
                    var serviceBusConnectionString = ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusConnectionString);
                    var serviceBusNamespace = ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusNamespace);
                    if (string.IsNullOrWhiteSpace(serviceBusConnectionString) && string.IsNullOrWhiteSpace(serviceBusNamespace))
                    {
                        throw new InvalidOperationException(
                            $"DebugAzure requires '{EnvironmentVariables.AzureServiceBusConnectionString}' or '{EnvironmentVariables.AzureServiceBusNamespace}' for operational usage ingestion.");
                    }
                    var serviceBusTopic = ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusTopic);
                    var operationalFactsTopic = GetRequiredSetting(configuration, EnvironmentVariables.AzureServiceBusOperationalFactsTopic);
                    EnsureDistinctServiceBusTopics(serviceBusTopic, operationalFactsTopic);
                    var operationalFactsProcessorSubscription =
                        GetRequiredSetting(configuration, OperationalFactsProcessorSubscriptionSettingName);
                    var serviceBusSubscription = ResolveSetting(configuration, EnvironmentVariables.AzureServiceBusSubscription);
                    var operationsSqlConnectionString = GetRequiredSetting(configuration, OperationsSqlConnectionStringSettingName);
                    var redisHost = ResolveSetting(configuration, EnvironmentVariables.RedisHost);
                    var redisPort = ResolveSetting(configuration, EnvironmentVariables.RedisPort);
                    var redisTls = ResolveSetting(configuration, EnvironmentVariables.RedisTls);
                    var redisUsername = ResolveSetting(configuration, EnvironmentVariables.RedisUsername);
                    var redisPassword = ResolveSetting(configuration, EnvironmentVariables.RedisPassword);
                    var redisCaCertificate = ResolveSetting(configuration, EnvironmentVariables.RedisCaCertificate);

                    if (!string.IsNullOrWhiteSpace(redisHost))
                    {
                        if (!IsTruthy(redisTls)
                            || string.IsNullOrWhiteSpace(redisPort)
                            || string.IsNullOrWhiteSpace(redisUsername)
                            || string.IsNullOrWhiteSpace(redisPassword)
                            || string.IsNullOrWhiteSpace(redisCaCertificate))
                        {
                            throw new InvalidOperationException(
                                "DebugAzure Redis requires explicit TLS, port, ACL username, password, and CA settings.");
                        }
                    }

                    graceServer
                        .WithEnvironment(EnvironmentVariables.AzureStorageAccountName, azureStorageAccountName)
                        .WithEnvironment(EnvironmentVariables.AzureStorageConnectionString, azureStorageConnectionString)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBEndpoint, cosmosdbEndpoint)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, cosmosDatabaseName)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, cosmosContainerName)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusConnectionString, serviceBusConnectionString)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, serviceBusNamespace)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, serviceBusTopic)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopic)
                        .WithEnvironment(OperationalFactsProcessorSubscriptionSettingName, operationalFactsProcessorSubscription)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, serviceBusSubscription)
                        .WithEnvironment(EnvironmentVariables.RedisHost, redisHost ?? "127.0.0.1")
                        .WithEnvironment(EnvironmentVariables.RedisPort, redisPort ?? "6379")
                        .WithEnvironment(EnvironmentVariables.RedisTls, redisTls)
                        .WithEnvironment(EnvironmentVariables.RedisUsername, redisUsername)
                        .WithEnvironment(EnvironmentVariables.RedisPassword, redisPassword)
                        .WithEnvironment(EnvironmentVariables.RedisCaCertificate, redisCaCertificate)
                        .WithEnvironment(EnvironmentVariables.GraceLogDirectory, logDirectory)
                        .WithEnvironment(EnvironmentVariables.DebugEnvironment, "Azure");

                    _ = builder.AddProject("grace-operations-worker", "..\\Grace.Operations.Worker\\Grace.Operations.Worker.fsproj")
                        .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                        .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusConnectionString, serviceBusConnectionString)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, serviceBusNamespace)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopic)
                        .WithEnvironment(OperationalFactsProcessorSubscriptionSettingName, operationalFactsProcessorSubscription)
                        .WithEnvironment(OperationsSqlConnectionStringSettingName, operationsSqlConnectionString)
                        .WithEnvironment(EnvironmentVariables.DebugEnvironment, "Azure")
                        .WithOtlpExporter();

                    Console.WriteLine("Grace.Server DebugAzure environment configured (no emulators started):");
                    Console.WriteLine("  - Azure Storage: using DefaultAzureCredential.");
                    Console.WriteLine("  - Azure Cosmos: using DefaultAzureCredential.");
                    Console.WriteLine("  - Azure Service Bus: using DefaultAzureCredential.");
                    Console.WriteLine(string.IsNullOrWhiteSpace(redisHost)
                        ? "  - Redis: using the local unauthenticated container."
                        : "  - Redis: using the TLS-authenticated infrastructure lab endpoint.");
                    Console.WriteLine($"  - Operational facts processor subscription: {operationalFactsProcessorSubscription}.");
                    Console.WriteLine("  - Aspire dashboard at http://localhost:18888");
                    Console.WriteLine($"  - OTLP endpoint {otlpEndpoint}");
                }
            }
            else
            {
                // =========================
                // PUBLISH MODE (deployment model)
                // =========================

                var cosmos = builder.AddAzureCosmosDB("cosmos");
                var cosmosDatabase = cosmos.AddCosmosDatabase(configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBDatabaseName)] ?? "grace-dev");
                _ = cosmosDatabase.AddContainer(configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBContainerName)] ?? "grace-events", "/PartitionKey");

                var storage = builder.AddAzureStorage("storage");
                var blobStorage = storage.AddBlobContainer("directoryversions");
                var diffStorage = storage.AddBlobContainer("diffs");
                var zipStorage = storage.AddBlobContainer("zipfiles");

                var serviceBus = builder.AddAzureServiceBus("servicebus");
                var serviceBusTopicName =
                    configuration[getConfigKey(EnvironmentVariables.AzureServiceBusTopic)]
                    ?? Constants.GraceEventStreamTopic;
                var operationalFactsTopicName =
                    configuration[getConfigKey(EnvironmentVariables.AzureServiceBusOperationalFactsTopic)]
                    ?? Constants.GraceOperationalFactsTopic;
                EnsureDistinctServiceBusTopics(serviceBusTopicName, operationalFactsTopicName);
                var graceEventSubscriptionName =
                    configuration[getConfigKey(EnvironmentVariables.AzureServiceBusSubscription)]
                    ?? "grace-server";
                var graceUsageCollectorSubscriptionName =
                    configuration[getConfigKey(OperationalFactsProcessorSubscriptionSettingName)]
                    ?? GraceUsageCollectorSubscriptionName;
                _ = serviceBus.AddServiceBusTopic(GraceEventTopicResourceName, serviceBusTopicName)
                    .AddServiceBusSubscription(GraceEventSubscriptionResourceName, graceEventSubscriptionName);
                _ = serviceBus.AddServiceBusTopic(
                        OperationalFactsTopicResourceName,
                        operationalFactsTopicName)
                    .WithProperties(topic =>
                    {
                        topic.RequiresDuplicateDetection = true;
                        topic.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5);
                    })
                    .AddServiceBusSubscription(
                        GraceUsageCollectorSubscriptionResourceName,
                        graceUsageCollectorSubscriptionName);

                var otlpEndpoint = configuration["grace:otlp_endpoint"] ?? "http://localhost:18889";
                var publishLogDirectory = configuration["grace:log_directory"] ?? "/tmp/grace-logs";

                var graceServer = builder.AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithReference(cosmosDatabase)
                    .WithReference(blobStorage)
                    .WithReference(diffStorage)
                    .WithReference(zipStorage)
                    .WithReference(serviceBus)
                    .WithParentRelationship(redis)
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Production")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(EnvironmentVariables.ApplicationInsightsConnectionString, configuration[getConfigKey(EnvironmentVariables.ApplicationInsightsConnectionString)] ?? string.Empty)
                    .WithEnvironment(EnvironmentVariables.GraceServerUri, configuration[getConfigKey(EnvironmentVariables.GraceServerUri)] ?? "https://localhost:5001")
                    .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBDatabaseName)] ?? "grace-dev")
                    .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBContainerName)] ?? "grace-events")
                    .WithEnvironment(EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(EnvironmentVariables.RedisHost, "localhost")
                    .WithEnvironment(EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(EnvironmentVariables.OrleansClusterId, configuration[getConfigKey(EnvironmentVariables.OrleansClusterId)] ?? "production")
                    .WithEnvironment(EnvironmentVariables.OrleansServiceId, configuration[getConfigKey(EnvironmentVariables.OrleansServiceId)] ?? "grace-prod")
                    .WithEnvironment(EnvironmentVariables.GracePubSubSystem, pubSubSystem)
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, serviceBusTopicName)
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusOperationalFactsTopic, operationalFactsTopicName)
                    .WithEnvironment(OperationalFactsProcessorSubscriptionSettingName, graceUsageCollectorSubscriptionName)
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, configuration[getConfigKey(EnvironmentVariables.AzureServiceBusSubscription)] ?? "grace-server")
                    .WithEnvironment(EnvironmentVariables.GraceLogDirectory, publishLogDirectory)
                    .WithEnvironment(EnvironmentVariables.GraceAuthOidcAuthority, configuration[EnvironmentVariables.GraceAuthOidcAuthority])
                    .WithEnvironment(EnvironmentVariables.GraceAuthOidcAudience, configuration[EnvironmentVariables.GraceAuthOidcAudience])
                    .WithEnvironment(EnvironmentVariables.GraceAuthOidcCliClientId, configuration[EnvironmentVariables.GraceAuthOidcCliClientId])
                    .WithHttpEndpoint(targetPort: 5000, name: "http")
                    .WithHttpsEndpoint(targetPort: 5001, name: "https")
                    .AsHttp2Service()
                    .WithOtlpExporter();

                var forwardedAuthKeys = new List<string>();
                AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthOidcAuthority, forwardedAuthKeys);
                AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthOidcAudience, forwardedAuthKeys);
                var authorizationBootstrapSettings = ResolveAuthorizationBootstrapSettings(configuration, false);
                AddOptionalEnvironment(
                    graceServer,
                    EnvironmentVariables.GraceAuthzBootstrapSystemAdminUsers,
                    authorizationBootstrapSettings.Users,
                    forwardedAuthKeys);
                AddOptionalEnvironment(
                    graceServer,
                    EnvironmentVariables.GraceAuthzBootstrapSystemAdminGroups,
                    authorizationBootstrapSettings.Groups,
                    forwardedAuthKeys);
                LogForwardedSettings("Grace.Server auth settings", forwardedAuthKeys);

                Console.WriteLine("Grace.Server publish/production environment configured (Azure resources with MI by default).");
                Console.WriteLine("  - Redis remains local container");
                Console.WriteLine($"  - OTLP endpoint {otlpEndpoint}");
            }

            // Build + run with exit logging (normal + error) and elapsed time.
            using var appHost = builder.Build();
            var loggerFactory = appHost.Services.GetService(typeof(ILoggerFactory)) as ILoggerFactory
                                ?? LoggerFactory.Create(lb => lb.AddSimpleConsole());
            var logger = loggerFactory.CreateLogger("Grace.Aspire.AppHost");
            var sw = Stopwatch.StartNew();

            try
            {
                appHost.Run();
                sw.Stop();
                logger.LogInformation("Aspire host exited normally. elapsedMs={Elapsed}", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(ex, "Aspire host terminated with error. elapsedMs={Elapsed}", sw.ElapsedMilliseconds);
                Environment.Exit(1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting Aspire host: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    internal static string? ResolveSetting(IConfiguration configuration, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration[name];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            var key = Shared::Grace.Shared.Utilities.getConfigKey(name);
            value = configuration[key];
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GetRequiredSetting(IConfiguration configuration, string name)
    {
        var value = ResolveSetting(configuration, name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var key = Shared::Grace.Shared.Utilities.getConfigKey(name);
        throw new InvalidOperationException(
            $"Missing required setting '{name}' (or '{key}') for DebugAzure.");
    }

    private static void EnsureDistinctServiceBusTopics(string? graceEventTopic, string? operationalFactsTopic)
    {
        if (string.IsNullOrWhiteSpace(graceEventTopic) || string.IsNullOrWhiteSpace(operationalFactsTopic))
        {
            return;
        }

        if (graceEventTopic.Trim().Equals(operationalFactsTopic.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Service Bus topic '{EnvironmentVariables.AzureServiceBusOperationalFactsTopic}' must differ from '{EnvironmentVariables.AzureServiceBusTopic}' so usage facts cannot enter the GraceEvent topic/subscriber path.");
        }
    }

    internal static string BuildSqlTcpDataSource(object host, object port)
    {
        var hostValue = Convert.ToString(host)?.Trim();
        var portValue = Convert.ToString(port)?.Trim();

        if (string.IsNullOrWhiteSpace(hostValue))
        {
            throw new ArgumentException("SQL host is required.", nameof(host));
        }

        if (string.IsNullOrWhiteSpace(portValue))
        {
            throw new ArgumentException("SQL port is required.", nameof(port));
        }

        var normalizedHost = hostValue.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
            ? hostValue.Substring(4)
            : hostValue;

        if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHost = IPAddress.Loopback.ToString();
        }

        return $"tcp:{normalizedHost},{portValue}";
    }

    private static void AddOptionalEnvironment(
        IResourceBuilder<ProjectResource> resource,
        IConfiguration configuration,
        string name,
        IList<string> forwardedKeys)
    {
        AddOptionalEnvironment(resource, name, ResolveSetting(configuration, name), forwardedKeys);
    }

    private static void AddOptionalEnvironment(
        IResourceBuilder<ProjectResource> resource,
        string name,
        string? value,
        IList<string> forwardedKeys)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            resource.WithEnvironment(name, value);
            forwardedKeys?.Add(name);
        }
    }

    internal static AuthorizationBootstrapSettings ResolveAuthorizationBootstrapSettings(
        IConfiguration configuration,
        bool allowDebugAzureOverride)
    {
        if (allowDebugAzureOverride)
        {
            var mode = Environment.GetEnvironmentVariable(DebugAzureBootstrapModeEnvironmentVariable);

            if (DebugAzureBootstrapModeSuppress.Equals(mode, StringComparison.Ordinal))
            {
                return new AuthorizationBootstrapSettings(null, null);
            }

            if (DebugAzureBootstrapModeExactUser.Equals(mode, StringComparison.Ordinal))
            {
                var exactUserId = Environment.GetEnvironmentVariable(DebugAzureBootstrapUserIdEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(exactUserId) || exactUserId.Contains(';'))
                {
                    throw new InvalidOperationException(
                        $"{DebugAzureBootstrapModeExactUser} mode requires exactly one non-empty bootstrap user ID.");
                }

                return new AuthorizationBootstrapSettings(exactUserId, null);
            }

            if (!string.IsNullOrWhiteSpace(mode))
            {
                throw new InvalidOperationException($"Unsupported DebugAzure bootstrap mode '{mode}'.");
            }
        }

        return new AuthorizationBootstrapSettings(
            ResolveSetting(configuration, EnvironmentVariables.GraceAuthzBootstrapSystemAdminUsers),
            ResolveSetting(configuration, EnvironmentVariables.GraceAuthzBootstrapSystemAdminGroups));
    }

    private static void LogForwardedSettings(string label, IList<string> forwardedKeys)
    {
        if (forwardedKeys is { Count: > 0 })
        {
            Console.WriteLine($"{label}: {string.Join(", ", forwardedKeys)}.");
        }
        else
        {
            Console.WriteLine($"{label}: none detected.");
        }
    }

    private static void CleanupDockerContainers(IEnumerable<string> containerNames)
    {
        foreach (var name in containerNames)
        {
            try
            {
                var startInfo = new ProcessStartInfo("docker", $"rm -f {name}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(startInfo);
                if (proc is null)
                {
                    continue;
                }

                if (!proc.WaitForExit(5000))
                {
                    try
                    {
                        proc.Kill(true);
                    }
                    catch
                    {
                        // Ignore cleanup errors to avoid failing test runs.
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors to avoid failing test runs.
            }
        }
    }

    private static int GetAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    /// <summary>
    /// Creates the Service Bus Emulator configuration file with namespace, topics, and subscriptions.
    /// </summary>
    private static void CreateServiceBusConfiguration(string configFilePath, IConfiguration configuration)
    {
        var topicName =
            ResolveSetting(configuration, Constants.EnvironmentVariables.AzureServiceBusTopic)
            ?? Constants.GraceEventStreamTopic;
        var operationalFactsTopicName =
            ResolveSetting(configuration, Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic)
            ?? Constants.GraceOperationalFactsTopic;
        EnsureDistinctServiceBusTopics(topicName, operationalFactsTopicName);
        var subscriptionName =
            ResolveSetting(configuration, Constants.EnvironmentVariables.AzureServiceBusSubscription)
            ?? "grace-server";
        var graceUsageCollectorSubscriptionName =
            ResolveSetting(configuration, OperationalFactsProcessorSubscriptionSettingName)
            ?? GraceUsageCollectorSubscriptionName;
        var testSubscriptionName = $"{subscriptionName}-tests";

        var config = new
        {
            UserConfig = new
            {
                Namespaces = new[]
                {
                    new
                    {
                        Name = "sbemulatorns",
                        Queues = Array.Empty<object>(),
                        Topics = new object[]
                        {
                            new
                            {
                                Name = topicName,
                                Properties = new
                                {
                                    DefaultMessageTimeToLive = "PT1H",
                                    DuplicateDetectionHistoryTimeWindow = "PT20S",
                                    RequiresDuplicateDetection = false
                                },
                                Subscriptions = new[]
                                {
                                    new
                                    {
                                        Name = subscriptionName,
                                        Properties = new
                                        {
                                            DeadLetteringOnMessageExpiration = false,
                                            DefaultMessageTimeToLive = "PT1H",
                                            LockDuration = "PT1M",
                                            MaxDeliveryCount = 10,
                                            ForwardDeadLetteredMessagesTo = "",
                                            ForwardTo = "",
                                            RequiresSession = false
                                        },
                                        Rules = Array.Empty<object>()
                                    },
                                    new
                                    {
                                        Name = testSubscriptionName,
                                        Properties = new
                                        {
                                            DeadLetteringOnMessageExpiration = false,
                                            DefaultMessageTimeToLive = "PT1H",
                                            LockDuration = "PT1M",
                                            MaxDeliveryCount = 10,
                                            ForwardDeadLetteredMessagesTo = "",
                                            ForwardTo = "",
                                            RequiresSession = false
                                        },
                                        Rules = Array.Empty<object>()
                                    }
                                }
                            },
                            new
                            {
                                Name = operationalFactsTopicName,
                                Properties = new
                                {
                                    DefaultMessageTimeToLive = "PT1H",
                                    DuplicateDetectionHistoryTimeWindow = "PT5M",
                                    RequiresDuplicateDetection = true
                                },
                                Subscriptions = new[]
                                {
                                    new
                                    {
                                        Name = graceUsageCollectorSubscriptionName,
                                        Properties = new
                                        {
                                            DeadLetteringOnMessageExpiration = false,
                                            DefaultMessageTimeToLive = "PT1H",
                                            LockDuration = "PT1M",
                                            MaxDeliveryCount = 10,
                                            ForwardDeadLetteredMessagesTo = "",
                                            ForwardTo = "",
                                            RequiresSession = false
                                        },
                                        Rules = Array.Empty<object>()
                                    }
                                }
                            }
                        }
                    }
                },
                Logging = new
                {
                    Type = "Console"
                }
            }
        };

        var json = JsonSerializer.Serialize(config, jsonOptions);
        var existingJson = File.Exists(configFilePath) ? File.ReadAllText(configFilePath) : null;
        if (existingJson != json)
        {
            Console.WriteLine($"Creating Service Bus Emulator config at {configFilePath}:\n{json}");
            File.WriteAllText(configFilePath, json);
        }
    }
}
