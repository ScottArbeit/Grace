// FILE: Program.Aspire.AppHost.cs
extern alias Shared;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Redis;
using Grace.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
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
            Console.WriteLine($"Aspire execution context: Run={isRunMode}; Publish={isPublishMode}.");
            var isAzureDebugRun =
                isRunMode &&
                resourceMode.Equals(AspireResourceModeAzure, StringComparison.OrdinalIgnoreCase);
            var isTestRun =
                Environment.GetEnvironmentVariable("GRACE_TESTING") is string testValue
                && (testValue.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || testValue.Equals("true", StringComparison.OrdinalIgnoreCase));

            // Redis: keep local container for both run modes (Local + Azure debug), and even in publish mode if you like.
            var redis = builder.AddContainer("redis", "redis", "latest")
                .WithContainerName("redis")
                //.WithLifetime(ContainerLifetime.Session)
                .WithEnvironment("ACCEPT_EULA", "Y")
                .WithEndpoint(targetPort: 6379, port: 6379);

            if (!isPublishMode)
            {
                // =========================
                // RUN MODE (debug / local)
                // =========================

                // Common settings for local debugging
                var otlpEndpoint = configuration["Grace:OtlpEndpoint"] ?? "http://localhost:18889";
                var stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grace", "aspire");
                var logDirectory = Path.Combine(stateRoot, "logs");

                Directory.CreateDirectory(stateRoot);
                Directory.CreateDirectory(logDirectory);

                // These get set in both Local and Azure-debug runs.
                var orleansClusterId = configuration["Grace:Orleans:ClusterId"] ?? "local";
                var orleansServiceId = configuration["Grace:Orleans:ServiceId"] ?? "gracevcs-dev";

                if (isTestRun)
                {
                    var runSuffix = Guid.NewGuid().ToString("N");
                    orleansClusterId = $"{orleansClusterId}-test-{runSuffix}";
                    orleansServiceId = $"{orleansServiceId}-test-{runSuffix}";
                }

                var graceServer = builder.AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithParentRelationship(redis)
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(EnvironmentVariables.ApplicationInsightsConnectionString, configuration["Grace:ApplicationInsightsConnectionString"] ?? string.Empty)
                    .WithEnvironment(EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(EnvironmentVariables.RedisHost, "127.0.0.1")
                    .WithEnvironment(EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(EnvironmentVariables.OrleansClusterId, orleansClusterId)
                    .WithEnvironment(EnvironmentVariables.OrleansServiceId, orleansServiceId)
                    .WithEnvironment(EnvironmentVariables.GracePubSubSystem, "AzureServiceBus")
                    .AsHttp2Service()
                    .WithOtlpExporter();

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
                        .WithHttpEndpoint(port: 5000, name: "http")
                        .WithHttpsEndpoint(port: 5001, name: "https");
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

                    // Create Service Bus emulator config
                    var serviceBusConfigFile = Path.Combine(
                        serviceBusConfigPath,
                        $"config_{Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}.json");
                    CreateServiceBusConfiguration(serviceBusConfigFile, configuration);

                    var azurite = builder.AddContainer("azurite", "mcr.microsoft.com/azure-storage/azurite", "latest")
                        .WithContainerName("azurite")
                        .WithBindMount(azuriteDataPath, "/data")
                        //.WithLifetime(ContainerLifetime.Session)
                        .WithEnvironment("AZURITE_ACCOUNTS", "gracevcsdevelopment:Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                        .WithEndpoint(targetPort: 10000, port: 10000, name: "blob", scheme: "http")
                        .WithEndpoint(targetPort: 10001, port: 10001, name: "queue", scheme: "http")
                        .WithEndpoint(targetPort: 10002, port: 10002, name: "table", scheme: "http");
                    var azuriteBlobEndpoint = azurite.GetEndpoint("blob");
                    var azuriteQueueEndpoint = azurite.GetEndpoint("queue");
                    var azuriteTableEndpoint = azurite.GetEndpoint("table");
                    var azuriteBlobHostAndPort = azuriteBlobEndpoint.Property(EndpointProperty.HostAndPort);
                    var azuriteQueueHostAndPort = azuriteQueueEndpoint.Property(EndpointProperty.HostAndPort);
                    var azuriteTableHostAndPort = azuriteTableEndpoint.Property(EndpointProperty.HostAndPort);

                    // Cosmos emulator (your existing approach)
                    const string cosmosKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
                    var cosmosDatabaseName = configuration["Grace:Cosmos:DatabaseName"] ?? "grace-dev";
                    var cosmosContainerName = configuration["Grace:Cosmos:ContainerName"] ?? "grace-events";
                    const int cosmosGatewayHostPort = 8081;

#pragma warning disable ASPIRECOSMOSDB001
                    var cosmos = builder.AddAzureCosmosDB("cosmos")
                        .RunAsPreviewEmulator(emulator =>
                        {
                            emulator
                                .WithContainerName("cosmosdb-emulator")
                                //.WithLifetime(ContainerLifetime.Session)
                                .WithEnvironment("ACCEPT_EULA", "Y")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "10")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "false")
                                .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "127.0.0.1")
                                .WithEnvironment("ENABLE_OTLP_EXPORTER", "true")
                                .WithEnvironment("LOG_LEVEL", "info")
                                .WithDataExplorer(1234)
                                .WithGatewayPort(cosmosGatewayHostPort);
                        });
#pragma warning restore ASPIRECOSMOSDB001

                    _ = cosmos.AddCosmosDatabase(cosmosDatabaseName)
                        .AddContainer(cosmosContainerName, "/PartitionKey");
                    var cosmosConnStr = BuildCosmosEmulatorConnectionString(cosmos, cosmosKey);

                    // Service Bus emulator
                    var serviceBusSqlPassword = configuration["Grace:ServiceBus:SqlPassword"] ?? "SqlIsAwesome1!";

                    var serviceBusSql = builder.AddContainer("servicebus-sql", "mcr.microsoft.com/mssql/server", "2022-latest")
                        .WithContainerName("servicebus-sql")
                        .WithEnvironment("ACCEPT_EULA", "Y")
                        .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                        //.WithLifetime(ContainerLifetime.Session)
                        .WithEndpoint(targetPort: 1433, port: 21433, name: "sql", scheme: "tcp");

                    var serviceBusEmulator = builder.AddContainer("servicebus-emulator", "mcr.microsoft.com/azure-messaging/servicebus-emulator", "latest")
                        .WithContainerName("servicebus-emulator")
                        .WithParentRelationship(serviceBusSql)
                        .WithEnvironment("ACCEPT_EULA", "Y")
                        .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                        .WithEnvironment("SQL_SERVER", "servicebus-sql")
                        .WithEnvironment("SQL_WAIT_INTERVAL", "10")
                        //.WithLifetime(ContainerLifetime.Session)
                        .WithBindMount(serviceBusConfigFile, "/ServiceBus_Emulator/ConfigFiles/Config.json")
                        .WithEndpoint(targetPort: 5672, port: 5672, name: "amqp", scheme: "amqp")
                        .WithEndpoint(targetPort: 5300, port: 5300, name: "management", scheme: "http");

                    var serviceBusAmqpEndpoint = serviceBusEmulator.GetEndpoint("amqp");
                    var serviceBusHostAndPort = serviceBusAmqpEndpoint.Property(EndpointProperty.HostAndPort);

                    // Wire up Grace.Server with emulator endpoints
                    graceServer
                        .WithParentRelationship(azurite)
                        .WithParentRelationship(cosmos)
                        .WithParentRelationship(serviceBusEmulator)
                        .WithEnvironment(
                            EnvironmentVariables.AzureStorageConnectionString,
                            ReferenceExpression.Create(
                                $"DefaultEndpointsProtocol=http;AccountName=gracevcsdevelopment;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://{azuriteBlobHostAndPort}/gracevcsdevelopment;QueueEndpoint=http://{azuriteQueueHostAndPort}/gracevcsdevelopment;TableEndpoint=http://{azuriteTableHostAndPort}/gracevcsdevelopment;")
                        )
                        .WithEnvironment(EnvironmentVariables.AzureStorageKey,
                            "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBConnectionString, cosmosConnStr)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, cosmosDatabaseName)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, cosmosContainerName)
                        .WithEnvironment(
                            EnvironmentVariables.AzureServiceBusConnectionString,
                            ReferenceExpression.Create(
                                $"Endpoint=sb://{serviceBusHostAndPort};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
                            )
                        )
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, "grace-server")
                        .WithEnvironment(EnvironmentVariables.GraceLogDirectory, logDirectory)
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

                    Console.WriteLine("Configuring Grace.Server for DebugAzure with real Azure resources.");
                    var azureStorageAccountName = GetRequired(configuration, "Grace:AzureStorage:AccountName");
                    Console.WriteLine($"Using Azure Storage account: {azureStorageAccountName}.");

                    var cosmosdbEndpoint = GetRequired(configuration, "Grace:AzureCosmosDB:AccountEndpoint");
                    Console.WriteLine($"Using Cosmos DB endpoint: {cosmosdbEndpoint}.");

                    graceServer
                        .WithEnvironment(EnvironmentVariables.AzureStorageAccountName, azureStorageAccountName)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBEndpoint, cosmosdbEndpoint)
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBDatabaseName, configuration["Grace:AzureCosmosDB:DatabaseName"])
                        .WithEnvironment(EnvironmentVariables.AzureCosmosDBContainerName, configuration["Grace:AzureCosmosDB:ContainerName"])
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, configuration["Grace:ServiceBus:Namespace"])
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, configuration["Grace:ServiceBus:TopicName"])
                        .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, configuration["Grace:ServiceBus:SubscriptionName"])
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
                var publishLogDirectory = configuration["Grace:LogDirectory"] ?? "/tmp/grace-logs";

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
                    .WithEnvironment(EnvironmentVariables.GraceLogDirectory, publishLogDirectory)
                    .WithHttpEndpoint(port: 5000, name: "http")
                    .WithHttpsEndpoint(port: 5001, name: "https")
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
                    scheme = "http";
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
