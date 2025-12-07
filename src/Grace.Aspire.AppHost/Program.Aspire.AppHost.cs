extern alias Shared;

using Aspire.Hosting;
using Aspire.Hosting.Redis;
using Grace.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using static Grace.Types.Types;
using static Shared::Grace.Shared.Constants;

internal class Program
{
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

            if (!builder.ExecutionContext.IsPublishMode)
            {
                var stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grace", "aspire");
                Directory.CreateDirectory(stateRoot);

                var azuriteDataPath = Path.Combine(stateRoot, "azurite");
                var cosmosDataPath = Path.Combine(stateRoot, "cosmos-data");
                var cosmosCertPath = Path.Combine(stateRoot, "cosmos-cert");
                var serviceBusConfigPath = Path.Combine(stateRoot, "servicebus");

                Directory.CreateDirectory(azuriteDataPath);
                Directory.CreateDirectory(cosmosDataPath);
                Directory.CreateDirectory(cosmosCertPath);
                Directory.CreateDirectory(serviceBusConfigPath);

                // Create the Service Bus Emulator configuration file
                var serviceBusConfigFile = Path.Combine(serviceBusConfigPath, "config.json");
                CreateServiceBusConfig(serviceBusConfigFile, configuration);

                var azurite = builder.AddContainer("azurite", "mcr.microsoft.com/azure-storage/azurite", "latest")
                    .WithContainerName("azurite")
                    .WithBindMount(azuriteDataPath, "/data")
                    .WithLifetime(ContainerLifetime.Persistent)
                    .WithEnvironment("AZURITE_ACCOUNTS", "gracevcsdevelopment:Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                    .WithEndpoint(targetPort: 10000, port: 10000, name: "blob", scheme: "http")
                    .WithEndpoint(targetPort: 10001, port: 10001, name: "queue", scheme: "http")
                    .WithEndpoint(targetPort: 10002, port: 10002, name: "table", scheme: "http");

                //var redis = builder.AddRedis("redis").WithContainerName("redis-grace");

                // This is a well-known default key.
                const string cosmosKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

                // then set the env using the chosen endpoint
                var cosmosConnStr = $"AccountEndpoint=https://localhost:8081/;AccountKey={cosmosKey};";

                // This tells Aspire to start/manage a Cosmos emulator for dev and
                // to wire up the CosmosClient DI for you.
                var cosmos = builder.AddAzureCosmosDB("cosmos")
                    .RunAsEmulator(emulator => {emulator
                         .WithContainerName("cosmosdb-emulator")
                         .WithLifetime(ContainerLifetime.Persistent)
                         .WithDataVolume("cosmosdb-volume")
                         .WithEnvironment("ACCEPT_EULA", "Y")
                         .WithEnvironment("AZURE_COSMOS_EMULATOR_PARTITION_COUNT", "10")
                         .WithEnvironment("AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE", "true")
                         .WithEnvironment("AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE", "127.0.0.1")
                         .WithGatewayPort(8081);
                        // (If you later need preview/linux emulator: .RunAsPreviewEmulator(...))
                    });

                var cosmosDatabase = cosmos.AddCosmosDatabase(configuration["Grace:Cosmos:DatabaseName"] ?? "grace-dev");
                var cosmosContainer = cosmosDatabase.AddContainer(configuration["Grace:Cosmos:ContainerName"] ?? "grace-events", "/PartitionKey");

                var serviceBusSqlPassword = configuration["ServiceBus:SqlPassword"] ?? $"Zz{NanoidDotNet.Nanoid.Generate(CorrelationIdAlphabet, 16)}!$";

                var serviceBusSql = builder.AddContainer("servicebus-sql", "mcr.microsoft.com/mssql/server", "2022-latest")
                    .WithContainerName("servicebus-sql")
                    .WithEnvironment("ACCEPT_EULA", "Y")
                    .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                    .WithLifetime(ContainerLifetime.Persistent)
                    .WithEndpoint(targetPort: 1433, port: 21433, name: "sql", scheme: "tcp");

                var serviceBusEmulator = builder.AddContainer("service-bus-emulator", "mcr.microsoft.com/azure-messaging/servicebus-emulator", "latest")
                    .WithContainerName("servicebus-emulator")
                    .WithEnvironment("ACCEPT_EULA", "Y")
                    .WithEnvironment("MSSQL_SA_PASSWORD", serviceBusSqlPassword)
                    .WithEnvironment("SQL_SERVER", "servicebus-sql")
                    .WithEnvironment("SQL_WAIT_INTERVAL", "10")
                    .WithLifetime(ContainerLifetime.Persistent)
                    // Mount the configuration file
                    .WithBindMount(serviceBusConfigFile, "/ServiceBus_Emulator/ConfigFiles/Config.json")
                    .WithEndpoint(targetPort: 5672, port: 5672, name: "amqp", scheme: "amqp")
                    .WithEndpoint(targetPort: 5300, port: 5300, name: "management", scheme: "http")
                    .WithReferenceRelationship(serviceBusSql);

                var otlpEndpoint = "http://localhost:18889";

                // Connection string for the Service Bus Emulator
                var serviceBusConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

                var graceServer = builder.AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj")
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                    .WithEnvironment("ASPNETCORE_URLS", "https://+:5001;http://+:5000")
                    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                    .WithEnvironment("OTLP_ENDPOINT_URL", otlpEndpoint)
                    .WithEnvironment(EnvironmentVariables.ApplicationInsightsConnectionString, configuration["Grace:ApplicationInsightsConnectionString"] ?? String.Empty)
                    .WithEnvironment(EnvironmentVariables.GraceServerUri, "http://localhost:5000")
                    .WithEnvironment(EnvironmentVariables.AzureStorageConnectionString, "DefaultEndpointsProtocol=http;AccountName=gracevcsdevelopment;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/gracevcsdevelopment;QueueEndpoint=http://127.0.0.1:10001/gracevcsdevelopment;TableEndpoint=http://127.0.0.1:10002/gracevcsdevelopment;")
                    .WithEnvironment(EnvironmentVariables.AzureStorageKey, "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==")
                    .WithEnvironment(EnvironmentVariables.AzureCosmosDBConnectionString, cosmosConnStr)
                    .WithEnvironment(EnvironmentVariables.CosmosDatabaseName, configuration["Grace:Cosmos:DatabaseName"] ?? "grace-dev")
                    .WithEnvironment(EnvironmentVariables.CosmosContainerName, configuration["Grace:Cosmos:ContainerName"] ?? "grace-events")
                    .WithEnvironment(EnvironmentVariables.DirectoryVersionContainerName, "directoryversions")
                    .WithEnvironment(EnvironmentVariables.DiffContainerName, "diffs")
                    .WithEnvironment(EnvironmentVariables.ZipFileContainerName, "zipfiles")
                    .WithEnvironment(EnvironmentVariables.RedisHost, "6379")
                    .WithEnvironment(EnvironmentVariables.RedisPort, "6379")
                    .WithEnvironment(EnvironmentVariables.OrleansClusterId, configuration["Grace:Orleans:ClusterId"] ?? "local")
                    .WithEnvironment(EnvironmentVariables.OrleansServiceId, configuration["Grace:Orleans:ServiceId"] ?? "grace-dev")
                    .WithEnvironment(EnvironmentVariables.GracePubSubSystem, getDiscriminatedUnionCaseName(GracePubSubSystem.AzureServiceBus))
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusConnectionString, serviceBusConnectionString)
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusNamespace, "sbemulatorns")
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusTopic, "graceeventstream")
                    .WithEnvironment(EnvironmentVariables.AzureServiceBusSubscription, "grace-server")
                    .AsHttp2Service()
                    .WithOtlpExporter();

                Console.WriteLine("Grace.Server local environment configured:");
                Console.WriteLine("  - Azurite (blob/queue/table) at http://localhost:10000-10002");
                Console.WriteLine($"  - Azurite data is stored at {azuriteDataPath}.");
                Console.WriteLine($"  - Redis at localhost:6379 (container name 'redis-grace')");
                Console.WriteLine("  - Cosmos DB emulator HTTPS endpoint at https://localhost:8081");
                Console.WriteLine("  - Service Bus emulator AMQP endpoint at amqp://localhost:5672");
                Console.WriteLine($"  - Service Bus config at {serviceBusConfigFile}");
                Console.WriteLine("  - Aspire dashboard available at http://localhost:18888");
                Console.WriteLine($"  - OTLP exporter targeting {otlpEndpoint}");
            }
            else
            {
                var graceServer = builder.AddProject("grace-server", "..\\Grace.Server\\Grace.Server.fsproj");
                graceServer.WithOtlpExporter();
            }

            // Build the model and run. Use ILogger + Stopwatch so we log normal + error exit paths with elapsed time.
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

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null // Keep PascalCase to match expected config format
    };

    /// <summary>
    /// Creates the Service Bus Emulator configuration file with namespace, topics, and subscriptions.
    /// </summary>
    private static void CreateServiceBusConfig(string configFilePath, IConfiguration configuration)
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
                        Name = "sbemulatorns", // Fixed namespace name - cannot be changed
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
                    Type = "Console" // Options: "Console", "File", or "Console,File"
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
