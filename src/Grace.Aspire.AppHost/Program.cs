using System.Collections.Immutable;
using System.Configuration;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Dapr;
using Aspire.Microsoft.Azure.Cosmos;
using Azure.Identity;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Prometheus;

internal class Program
{
    private static void Main(string[] args)
    {
        try
        {
            string daprConfigurationPath()
            {
                string daprConfigPath = Environment.GetEnvironmentVariable("DAPR_CONFIG_PATH") ?? string.Empty;
                if (String.IsNullOrEmpty(daprConfigPath))
                {
                    return daprConfigPath!;
                }
                else
                {
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    {
                        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".dapr{Path.DirectorySeparatorChar}components");
                    }
                    else
                    {
                        return Path.Combine(Environment.GetEnvironmentVariable("HOME")!, $".dapr{Path.DirectorySeparatorChar}components");
                    }
                }
            };

            // Build the configuration
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true, true) // Load appsettings.json
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", false, true) // Load environment-specific settings
                .AddEnvironmentVariables() // Include environment variables
                .Build();

            string applicationInsightsConnectionString = config["APPLICATIONINSIGHTS_CONNECTION_STRING"] ?? string.Empty;
            string azureCosmosDbConnectionString = config["COSMOS_CONNECTION_STRING"] ?? string.Empty;

            /// The main Aspire application builder instance.
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

            builder.Configuration.AddEnvironmentVariables();
            builder.AddConnectionString("applicationInsights", "APPLICATIONINSIGHTS_CONNECTION_STRING");
            builder.AddConnectionString("cosmosdb", "COSMOS_CONNECTION_STRING");

            // Add the parent Dapr resource.
            builder.AddDapr(options =>
            {
                options.DaprPath = config["DAPR_PATH"];
                options.EnableTelemetry = true;
            });

            // This checks if we're publishing to deploy to a cloud provider, or running Grace locally.
            // We want to configure different services in each context.
            if (builder.ExecutionContext.IsPublishMode)
            {
                // Stuff we want when we're publishing (i.e. deploying) to a cloud provider.
                // -------------------------------------------------------------------------

            }
            else
            {
                // Stuff we want when we're running Grace locally.
                // -----------------------------------------------

                // Add local containers as services for Dapr.
                var zipkin = builder.AddContainer("zipkin", "openzipkin/zipkin", "latest");
                zipkin.WithEndpoint(containerPort: 9411, hostPort: 9411, name: "zipkin-http", scheme: "http");

                //var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "latest")
                //                        .WithBindMount("../prometheus", "/etc/prometheus", isReadOnly: true);
                //prometheus.WithEndpoint(containerPort: 9090, hostPort: 9090, name: "prometheus-http", scheme: "http");

                //var jaeger = builder.AddContainer("jaeger", "jaegertracing/all-in-one", "latest");
                //jaeger.WithHttpEndpoint(16686);

                var otelCollector = builder.AddContainer("otelCollector", "otel/opentelemetry-collector", "latest");

                IResourceBuilder<RedisResource> redis = builder.AddRedis("redis", 6379).WithImageTag("latest");

                // Add secret store.
                var secretstore = builder.AddDaprComponent("secretstore", "secretstores.azure.keyvault",
                    new DaprComponentOptions { LocalPath = Path.Combine(daprConfigurationPath(), "secretstore - local.yaml") });

                // Add actor storage.
                Console.WriteLine($"*** {Path.Combine(daprConfigurationPath(), "actorstorage - local.yaml")}");
                var actorstorage = builder.AddDaprStateStore("actorstorage", new DaprComponentOptions { LocalPath = Path.Combine(daprConfigurationPath(), "actorstorage - local.yaml") });
                //var actorstorage = builder.AddDaprComponent("actorstorage", "state.azure.cosmosdb",
                //    new DaprComponentOptions { LocalPath = Path.Combine(daprConfigurationPath(), "actorstorage - local.yaml") });

                // Add pub/sub provider for graceeventstream.
                var graceeventstream = builder.AddDaprPubSub("graceeventstream", new DaprComponentOptions { LocalPath = Path.Combine(daprConfigurationPath(), "graceeventstream - local.yaml") });
                //var graceeventstream = builder.AddDaprComponent("graceeventstream", "pubsub.azure.servicebus",
                //    new DaprComponentOptions { LocalPath = Path.Combine(daprConfigurationPath(), "graceeventstream - local.yaml") });

                // Add subscription to pub/sub provider.
                //builder.AddDaprComponent("graceevents", "pubsub.azure.servicebus",
                //    new DaprComponentOptions { LocalPath = Path.Combine(daprConfigurationPath(), "graceeventstream - subscription - local.yaml") });

                // Dump the configuration to the console.
                foreach (var kvp in config.AsEnumerable().ToImmutableSortedDictionary())
                {
                    //Console.WriteLine($"{kvp.Key}: {kvp.Value}");
                }
                Console.WriteLine();

                var graceServer = builder.AddProject<Projects.Grace_Server>("grace-server")
                    .WithDaprSidecar(new DaprSidecarOptions
                    {
                        AppId = "grace-server",
                        AppPort = 5000,
                        AppProtocol = "http",
                        Config = Path.Combine(daprConfigurationPath(), "dapr-config.yaml"),
                        DaprHttpPort = 3500,
                        DaprGrpcPort = 50001,
                        ResourcesPaths = ImmutableHashSet.Create(daprConfigurationPath()),
                        EnableApiLogging = true,
                        LogLevel = "debug"    // "info" or "debug"
                    })
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                    .WithEnvironment("ASPNETCORE_HTTP_PORTS", "5000")
                    .WithEnvironment("ASPNETCORE_HTTPS_PORTS", "5001")
                    .WithEnvironment("ASPNETCORE_URLS", "https://+:5001;http://+:5000")
                    .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", applicationInsightsConnectionString)
                    .WithEnvironment("DAPR_SERVER_URI", "http://127.0.0.1")
                    .WithEnvironment("DAPR_APP_PORT", "5000")
                    .WithEnvironment("DAPR_HTTP_PORT", "3500")
                    .WithEnvironment("DAPR_GRPC_PORT", "50001")
                    .WithEnvironment("TEMP", "/tmp")
                    .WithEnvironment("azurecosmosdbconnectionstring", config["azurecosmosdbconnectionstring"])
                    .WithEnvironment("azurestorageconnectionstring", config["azurestorageconnectionstring"])
                    .WithEnvironment("azurestoragekey", config["azurestoragekey"])
                    .WithEnvironment("cosmosdatabasename", config["cosmosdatabasename"])
                    .WithEnvironment("cosmoscontainername", config["cosmoscontainername"])
                    .WithReference(secretstore)
                    .WithReference(actorstorage)
                    .WithReference(graceeventstream)
                    //.WithReference(keyVault)
                    .WithReference(redis)
                    .WithOtlpExporter();
            }

            //builder.AddDaprStateStore("actorstorage");
            //builder.AddDaprPubSub("graceeventstream");
            //builder.AddDaprPubSub("graceevents");

            builder.Build().Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: | {ex.Message} |");
            Console.WriteLine($"Stack trace: | {ex.StackTrace} |");
            Console.WriteLine($"{ex.InnerException}");
            Console.WriteLine();
            //Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ex));
        }
    }
}
