// FILE: Program.Aspire.AppHost.cs
extern alias Shared;

using Aspire.Hosting;
using Aspire.Hosting.Redis;
using Grace.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using static Grace.Types.Types;
using static Shared::Grace.Shared.Constants;

internal class Program
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
                .AddEnvironmentVariables()
                .Build();

            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

            // Run-mode switch:
            //   - Local (default): containers/emulators for Azurite, Cosmos emulator, ServiceBus emulator
            //   - Azure: debug locally, but use real Azure resources (connection strings from config/env/user-secrets)
            var resourceMode =
                Environment.GetEnvironmentVariable(AspireResourceModeEnvVar)
                ?? configuration[$"Grace:{AspireResourceModeEnvVar}"]
                ?? AspireResourceModeLocal;

            var isRunMode = builder.ExecutionContext.IsRunMode;
            var isPublishMode = builder.ExecutionContext.IsPublishMode;
            var isAzureDebugRun =
                isRunMode &&
                resourceMode.Equals(AspireResourceModeAzure, StringComparison.OrdinalIgnoreCase);

            // Redis: keep local container for both run modes (Local + Azure debug), and even in publish mode if you like.
            var redis = builder.AddContainer("redis", "redis", "latest")
                .WithContainerName("redis")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEndpoint(targetPort: 6379, port: 6379);

            if (!isPublishMode)
            {
                // =========================
                // RUN MODE (debug / local)
                // =========================

                // Common settings for local debugging
                var otlpEndpoint = configuration["Grace:OtlpEndpoint"] ?? "http://localhost:18889";

                // These get set in both Local and Azure-debug runs.
                var graceServer = builder.AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithParentRelationship(redis)
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                    .WithEnvironment("ASPNETCORE_URLS", "https://+:5001;http://+:5000")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(EnvironmentVariables.ApplicationInsightsConnectionString, configuration["Grace:ApplicationInsightsConnectionString"] ?? string.Empty)
                    .WithEnvironment(EnvironmentVariables.GraceServerUri, "http://localhost:5000")
                    .WithEnvironment(EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(EnvironmentVariables.RedisHost, "127.0.0.1")
                    .WithEnvironment(EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(EnvironmentVariables.OrleansClusterId, configuration["Grace:Orleans:ClusterId"] ?? "local")
                    .WithEnvironment(EnvironmentVariables.OrleansServiceId, configuration["Grace:Orleans:ServiceId"] ?? "gracevcs-dev")
                    .WithEnvironment(EnvironmentVariables.GracePubSubSystem, "AzureServiceBus")
                    .AsHttp2Service()
                    .WithOtlpExporter();

                if (!isAzureDebugRun)
                {
                    // -------------------------
                    // DebugLocal (default): containers/emulators
                    // -------------------------
                    var stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grace", "aspire");
                    Directory.CreateDirectory(stateRoot);

                    var azuriteDataPath = Path.Combine(stateRoot, "azurite");
                    var cosmosCertPath = Path.Combine(stateRoot, "cosmos-cert");
                    var serviceBusConfigPath = Path.Combine(stateRoot, "servicebus");

                    Directory.CreateDirectory(azuriteDataPath);
                    Directory.CreateDirectory(cosmosCertPath);
                    Directory.CreateDirectory(serviceBusConfigPath);

                    // Create Service Bus emulator config
                    var serviceBusConfigFile = Path.Combine(serviceBusConfigPath, "config.json");
                    CreateServiceBusConfiguration(serviceBusConfigFile, configuration);

                    var azurite = builder.AddContainer("azurite", "mcr.microsoft.com/azure-storage/azurite", "latest")
                        .WithContainerName("azurite")
                        .WithBindMount(azuriteDataPath, "/data")
                        .WithLifetime(ContainerLifetime.Persistent)
                        .WithEnvironment("AZURITE_ACCOUNTS", "gracevcsdevelopment:Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                        .WithEndpoint(targetPort: 10000, port: 10000, name: "blob", scheme: "http")
                        .WithEndpoint(targetPort: 10001, port: 10001, name: "queue", scheme: "http")
                        .WithEndpoint(targetPort: 10002, port: 10002, name: "table", scheme: "http");

                    // Cosmos emulator (your existing approach)
                    const string cosmosKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
                    var cosmosConnStr = $"AccountEndpoint=http://localhost:8081/;AccountKey={cosmosKey};";

#pragma warning disable ASPIRECOSMOSDB001
                    var cosmos = builder.AddAzureCosmosDB("cosmos")
                        .RunAsPreviewEmulator(emulator =>
                        {
                            emulator
                                .WithContainerName("cosmosdb-emulator")
                                .WithLifetime(ContainerLifetime.Persistent)
                                .WithVolume("cosmosdb-emulator-data", "/data")
                                .WithEnvironment("ACCEPT_EULA", "Y")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "10")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "true")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "127.0.0.1")
                                .WithEnvironment("ENABLE_OTLP_EXPORTER", "true")
                                .WithEnvironment("LOG_LEVEL", "info")
                                .WithDataExplorer(1234)
                                .WithGatewayPort(8081);
                        });
#pragma warning restore ASPIRECOSMOSDB001

                    _ = cosmos.AddCosmosDatabase(configuration["Grace:Cosmos:DatabaseName"] ?? "grace-dev")
                        .AddContainer(configuration["Grace:Cosmos:ContainerName"] ?? "grace-events", "/PartitionKey");

                    // Service Bus emulator
                    var serviceBusSqlPassword = configuration["Grace:ServiceBus:SqlPassword"] ?? "SqlIsAwesome1!";

                    var serviceBusSql = builder.AddContainer("servicebus-sql", "mcr.microsoft.com/mssql/server", "2022-latest")
                        .WithContainerName("servicebus-sql")
                        .WithEnvironment("ACCEPT_EULA", "Y")
                        .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                        .WithLifetime(ContainerLifetime.Persistent)
                        .WithEndpoint(targetPort: 1433, port: 21433, name: "sql", scheme: "tcp");

                    var serviceBusEmulator = builder.AddContainer("servicebus-emulator", "mcr.microsoft.com/azure-messaging/servicebus-emulator", "latest")
                        .WithContainerName("servicebus-emulator")
                        .WithParentRelationship(serviceBusSql)
                        .WithEnvironment("ACCEPT_EULA", "Y")
                        .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                        .WithEnvironment("SQL_SERVER", "servicebus-sql")
                        .WithEnvironment("SQL_WAIT_INTERVAL", "10")
                        .WithLifetime(ContainerLifetime.Persistent)
                        .WithBindMount(serviceBusConfigFile, "/ServiceBus_Emulator/ConfigFiles/Config.json")
                        .WithEndpoint(targetPort: 5672, port: 5672, name: "amqp", scheme: "amqp")
                        .WithEndpoint(targetPort: 5300, port: 5300, name: "management", scheme: "http");

                    var serviceBusConnectionString =
                        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

                    // Wire up Grace.Server with emulator endpoints
                    graceServer
                        .WithParentRelationship(azurite)
                        .WithParentRelationship(cosmos)
                        .WithParentRelationship(serviceBusEmulator)
                        .WithEnvironment(EnvironmentVariables.AzureStorageConnectionString,
                            "DefaultEndpointsProtocol=http;AccountName=gracevcsdevelopment;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/gracevcsdevelopment;QueueEndpoint=http://127.0.0.1:10001/gracevcsdevelopment;TableEndpoint=http://127.0.0.1:10002/gracevcsdevelopment;")
                        .WithEnvironment(EnvironmentVariables.AzureStorageKey,
                            "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBConnectionString, cosmosConnStr)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, configuration["Grace:Cosmos:DatabaseName"])
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, configuration["Grace:Cosmos:ContainerName"])
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusConnectionString, serviceBusConnectionString)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, "grace-server")
                        .WithEnvironment(EnvironmentVariables.DebugEnvironment, "Local");

                    Console.WriteLine("Grace.Server DebugLocal environment configured:");
                    Console.WriteLine("  - Azurite at http://localhost:10000-10002");
                    Console.WriteLine($"  - Azurite data at {azuriteDataPath}");
                    Console.WriteLine("  - Cosmos emulator at http://localhost:8081");
                    Console.WriteLine("  - Service Bus emulator at amqp://localhost:5672");
                    Console.WriteLine($"  - Service Bus config at {serviceBusConfigFile}");
                    Console.WriteLine("  - Aspire dashboard at http://localhost:18888");
                    Console.WriteLine($"  - OTLP endpoint {otlpEndpoint}");
                }
                else
                {
                    // -------------------------
                    // DebugAzure: still run locally under debugger, but use REAL Azure resources
                    // -------------------------

                    var azureStorageAccountName = GetRequired(configuration, "Grace:AzureStorage:AccountName");

                    var cosmosdbEndpoint = GetRequired(configuration, "Grace:Cosmos:AccountEndpoint");
                    Console.WriteLine($"Using Cosmos DB endpoint: {cosmosdbEndpoint}.");

                    var cosmosDbName = configuration["Grace:Cosmos:DatabaseName"];
                    var azureCosmosDBContainerName = configuration["Grace:Cosmos:ContainerName"];

                    var serviceBusTopic = configuration["Grace:ServiceBus:TopicName"];
                    var serviceBusSub = configuration["Grace:ServiceBus:SubscriptionName"];
                    var serviceBusNamespace = configuration["Grace:ServiceBus:Namespace"];

                    graceServer
                        .WithEnvironment(EnvironmentVariables.AzureStorageAccountName, azureStorageAccountName)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBEndpoint, cosmosdbEndpoint)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, cosmosDbName)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, azureCosmosDBContainerName)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, serviceBusNamespace)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, serviceBusTopic)
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, serviceBusSub)
                        .WithEnvironment(EnvironmentVariables.DebugEnvironment, "Azure");

                    Console.WriteLine("Grace.Server DebugAzure environment configured (no emulators started):");
                    Console.WriteLine("  - Azure Storage: from ConnectionStrings:AzureStorage (or Grace:AzureStorageConnectionString)");
                    Console.WriteLine("  - Azure Cosmos: from ConnectionStrings:Cosmos (or Grace:AzureCosmosDBConnectionString)");
                    Console.WriteLine("  - Azure Service Bus: from ConnectionStrings:ServiceBus (or Grace:AzureServiceBusConnectionString)");
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
                var cosmosDatabase = cosmos.AddCosmosDatabase(configuration["Grace:Cosmos:DatabaseName"] ?? "grace-dev");
                _ = cosmosDatabase.AddContainer(configuration["Grace:Cosmos:ContainerName"] ?? "grace-events", "/PartitionKey");

                var storage = builder.AddAzureStorage("storage");
                var blobStorage = storage.AddBlobContainer("directoryversions");
                var diffStorage = storage.AddBlobContainer("diffs");
                var zipStorage = storage.AddBlobContainer("zipfiles");

                var serviceBus = builder.AddAzureServiceBus("servicebus");
                _ = serviceBus.AddServiceBusTopic(configuration["Grace:ServiceBus:TopicName"] ?? "graceeventstream")
                    .AddServiceBusSubscription(configuration["Grace:ServiceBus:SubscriptionName"] ?? "grace-server");

                var otlpEndpoint = configuration["Grace:OtlpEndpoint"] ?? "http://localhost:18889";

                _ = builder.AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithReference(cosmosDatabase)
                    .WithReference(blobStorage)
                    .WithReference(diffStorage)
                    .WithReference(zipStorage)
                    .WithReference(serviceBus)
                    .WithParentRelationship(redis)
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Production")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(EnvironmentVariables.ApplicationInsightsConnectionString, configuration["Grace:ApplicationInsightsConnectionString"] ?? string.Empty)
                    .WithEnvironment(EnvironmentVariables.GraceServerUri, configuration["Grace:ServerUri"] ?? "https://localhost:5001")
                    .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, configuration["Grace:Cosmos:DatabaseName"] ?? "grace-dev")
                    .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, configuration["Grace:Cosmos:ContainerName"] ?? "grace-events")
                    .WithEnvironment(EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(EnvironmentVariables.RedisHost, "localhost")
                    .WithEnvironment(EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(EnvironmentVariables.OrleansClusterId, configuration["Grace:Orleans:ClusterId"] ?? "production")
                    .WithEnvironment(EnvironmentVariables.OrleansServiceId, configuration["Grace:Orleans:ServiceId"] ?? "grace-prod")
                    .WithEnvironment(EnvironmentVariables.GracePubSubSystem, "AzureServiceBus")
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, configuration["Grace:ServiceBus:TopicName"] ?? "graceeventstream")
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, configuration["Grace:ServiceBus:SubscriptionName"] ?? "grace-server")
                    .AsHttp2Service()
                    .WithOtlpExporter();

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
