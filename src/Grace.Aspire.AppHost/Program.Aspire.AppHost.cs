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
using static Grace.Types.Types;
using static Shared::Grace.Shared.Constants;

public partial class Program
{
    private const string AspireResourceModeEnvVar = "ASPIRE_RESOURCE_MODE";
    private const string AspireResourceModeLocal = "Local";
    private const string AspireResourceModeAzure = "Azure";

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
            var pubSubSystem = skipServiceBus ? "UnknownPubSubProvider" : "AzureServiceBus";
            if (isTestRun)
            {
                CleanupDockerContainers(new[] { "servicebus-sql", "servicebus-emulator" });
            }

            // Redis: keep local container for both run modes (Local + Azure debug), and even in publish mode if you like.
            var redisContainerName = runSuffix is null ? "redis" : $"redis-{runSuffix}";
            var redis = builder.AddContainer("redis", "redis", "latest")
                .WithContainerName(redisContainerName)
                //.WithLifetime(ContainerLifetime.Session)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEndpoint(targetPort: 6379, port: 6379);
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

                // These get set in both Local and Azure-debug runs.
                var orleansClusterId = configuration[getConfigKey(EnvironmentVariables.OrleansClusterId)] ?? "local";
                var orleansServiceId = configuration[getConfigKey(EnvironmentVariables.OrleansServiceId)] ?? "gracevcs-dev";

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
                    .WithEnvironment(EnvironmentVariables.RedisHost, "127.0.0.1")
                    .WithEnvironment(EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(EnvironmentVariables.OrleansClusterId, orleansClusterId)
                    .WithEnvironment(EnvironmentVariables.OrleansServiceId, orleansServiceId)
                    .WithEnvironment(EnvironmentVariables.GracePubSubSystem, pubSubSystem)
                    .AsHttp2Service()
                    .WithOtlpExporter();

                var forwardedAuthKeys = new List<string>();
                AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthOidcAuthority, forwardedAuthKeys);
                AddOptionalEnvironment(graceServer, configuration, EnvironmentVariables.GraceAuthOidcAudience, forwardedAuthKeys);
                LogForwardedSettings("Grace.Server auth settings", forwardedAuthKeys);

                if (isTestRun)
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
                    var azuriteDataPath = Path.Combine(stateRoot, "azurite");
                    var cosmosCertPath = Path.Combine(stateRoot, "cosmos-cert");
                    var serviceBusConfigPath = Path.Combine(stateRoot, "servicebus");

                Directory.CreateDirectory(azuriteDataPath);
                Directory.CreateDirectory(cosmosCertPath);
                Directory.CreateDirectory(serviceBusConfigPath);

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

                    // Cosmos emulator (your existing approach)
                    const string cosmosKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
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
                    var cosmosConnStr = BuildCosmosEmulatorConnectionString(cosmos, cosmosKey);

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
                            .WithEndpoint(targetPort: 1433, port: 21433, name: "sql", scheme: "tcp");
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
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
                            .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, "grace-server");
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
                    var azureStorageAccountName = GetRequired(configuration, getConfigKey(EnvironmentVariables.AzureStorageAccountName));
                    Console.WriteLine($"Using Azure Storage account: {azureStorageAccountName}.");

                    var cosmosdbEndpoint = GetRequired(configuration, getConfigKey(EnvironmentVariables.AzureCosmosDBEndpoint));
                    Console.WriteLine($"Using Cosmos DB endpoint: {cosmosdbEndpoint}.");

                    graceServer
                        .WithEnvironment(EnvironmentVariables.AzureStorageAccountName, azureStorageAccountName)
                        .WithEnvironment(EnvironmentVariables.AzureStorageConnectionString, configuration[getConfigKey(EnvironmentVariables.AzureStorageConnectionString)])
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBEndpoint, cosmosdbEndpoint)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBDatabaseName)])
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, configuration[getConfigKey(EnvironmentVariables.AzureCosmosDBContainerName)])
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, configuration[getConfigKey(EnvironmentVariables.AzureServiceBusNamespace)])
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, configuration[getConfigKey(EnvironmentVariables.AzureServiceBusTopic)])
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, configuration[getConfigKey(EnvironmentVariables.AzureServiceBusSubscription)])
                        .WithEnvironment(EnvironmentVariables.GraceLogDirectory, logDirectory)
                        .WithEnvironment(EnvironmentVariables.DebugEnvironment, "Azure");

                    Console.WriteLine("Grace.Server DebugAzure environment configured (no emulators started):");
                    Console.WriteLine("  - Azure Storage: using DefaultAzureCredential.");
                    Console.WriteLine("  - Azure Cosmos: using DefaultAzureCredential.");
                    Console.WriteLine("  - Azure Service Bus: using DefaultAzureCredential.");
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
                _ = serviceBus.AddServiceBusTopic(configuration[getConfigKey(EnvironmentVariables.AzureServiceBusTopic)] ?? "graceeventstream")
                    .AddServiceBusSubscription(configuration[getConfigKey(EnvironmentVariables.AzureServiceBusSubscription)] ?? "grace-server");

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
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, configuration[getConfigKey(EnvironmentVariables.AzureServiceBusTopic)] ?? "graceeventstream")
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

    private static string? GetRequired(IConfiguration configuration, string key)
    {
        var v = configuration[key];
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static void AddOptionalEnvironment(
        IResourceBuilder<ProjectResource> resource,
        IConfiguration configuration,
        string name,
        IList<string> forwardedKeys)
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

        if (!string.IsNullOrWhiteSpace(value))
        {
            resource.WithEnvironment(name, value);
            forwardedKeys?.Add(name);
        }
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

    private static ReferenceExpression BuildCosmosEmulatorConnectionString(
        IResourceBuilder<AzureCosmosDBResource> cosmos,
        string accountKey)
    {
            if (cosmos.Resource is IResourceWithEndpoints cosmosWithEndpoints)
            {
                EndpointReference? selected = null;

            foreach (var endpoint in cosmosWithEndpoints.GetEndpoints())
            {
                if (endpoint.EndpointAnnotation.TargetPort == 8081)
                {
                    selected = endpoint;
                    break;
                }

                if (endpoint.IsHttps)
                {
                    selected = endpoint;
                    break;
                }

                selected ??= endpoint;
            }

                if (selected is not null)
                {
                    var scheme = selected.EndpointAnnotation.UriScheme;
                    if (string.IsNullOrWhiteSpace(scheme))
                    {
                        scheme = selected.IsHttps ? "https" : "http";
                    }

                var hostAndPort = selected.Property(EndpointProperty.HostAndPort);
                return ReferenceExpression.Create($"AccountEndpoint={scheme}://{hostAndPort}/;AccountKey={accountKey};");
            }
        }

        return cosmos.Resource.ConnectionStringExpression;
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
        var topicName = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusTopic) ?? "graceeventstream";
        var subscriptionName = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureServiceBusSubscription) ?? "grace-server";
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
                        Topics = new[]
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
