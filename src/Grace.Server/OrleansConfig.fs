namespace Grace.Server

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open System

module OrleansConfig =
    /// Configures Orleans for clustering and state storage.
    let configureOrleans (hostBuilder: IHostBuilder) =
        hostBuilder.ConfigureServices(fun services ->
            services.AddOrleans(fun siloBuilder ->
                // Configure clustering for production and local development.
                let isDevelopment =
                    match Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") with
                    | "Development" -> true
                    | _ -> false

                if isDevelopment then
                    siloBuilder.UseLocalhostClustering()
                else
                    siloBuilder.UseAzureStorageClustering(fun options ->
                        options.ConnectionString <- Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING"))

                // Configure state storage.
                siloBuilder.AddAzureTableGrainStorage(
                    "Default",
                    fun options -> options.ConnectionString <- Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                )

                siloBuilder.AddAzureBlobGrainStorage(
                    "BlobStorage",
                    fun options -> options.ConnectionString <- Environment.GetEnvironmentVariable("AZURE_BLOB_CONNECTION_STRING")
                )

                siloBuilder.AddCosmosDBGrainStorage(
                    "CosmosDB",
                    fun options ->
                        options.AccountEndpoint <- Environment.GetEnvironmentVariable("COSMOSDB_ENDPOINT")
                        options.AccountKey <- Environment.GetEnvironmentVariable("COSMOSDB_KEY")
                        options.DatabaseName <- Environment.GetEnvironmentVariable("COSMOSDB_DATABASE")
                )))
