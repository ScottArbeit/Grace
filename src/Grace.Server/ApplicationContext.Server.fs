namespace Grace.Server

open Azure.Storage
open Azure.Storage.Blobs
open CosmosJsonSerializer
open Dapr.Client
open Dapr.Actors.Client
open Grace.Actors.Constants
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Linq
open System.Threading.Tasks
open NodaTime.Text

module ApplicationContext =

    let mutable private configuration: IConfiguration = null
    let Configuration(): IConfiguration = configuration

    let mutable private actorProxyFactory: IActorProxyFactory = null
    let ActorProxyFactory() = actorProxyFactory

    /// <summary>
    /// Sets the Application global configuration.
    /// </summary>
    /// <param name="config">The configuration to set.</param>
    let setConfiguration (config: IConfiguration) =
        let time = getCurrentInstant().ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)
        printfn $"In setConfiguration at {time}."
        configuration <- config

    let setActorProxyFactory proxyFactory =
        actorProxyFactory <- proxyFactory
        Grace.Actors.Services.setActorProxyFactory proxyFactory

    type StorageAccount =
        {
            StorageAccountName: string;
            StorageAccountConnectionString: string;
        }

    let StorageAccounts = ConcurrentDictionary<String, String>()
    let daprClient = DaprClientBuilder().UseJsonSerializationOptions(Constants.JsonSerializerOptions).Build()
    
    let mutable sharedKeyCredential: StorageSharedKeyCredential = null
    //let storageAccountNames = Configuration.Item "StorageAccountNames"
    //let storageAccountNames = (Configuration.Item "StorageAccountNames").Split(";")
    //storageAccountNames |> 
    //    Seq.iter(fun storageAccountName -> 
    //                let secretName = $"{storageAccountName}ConnectionString"
    //                let storageAccountConnectionString = daprClient.GetSecretAsync(Constants.GraceSecretStoreName, secretName).GetAwaiter().GetResult()
    //                StorageAccounts.TryAdd(storageAccountName, storageAccountConnectionString.First().Value) |> ignore
    //            )

    let actorStateStorageProvider = ActorStateStorageProvider.AzureCosmosDb
    let defaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage

    let mutable private cosmosClient: CosmosClient = null
    let mutable private cosmosContainer: Container = null

    let CosmosClient() = 
        if not <| isNull cosmosClient then
            cosmosClient
        else
            (task {
                let! secrets = daprClient.GetSecretAsync(Constants.GraceSecretStoreName, "AzureCosmosDBConnectionString")
                let cosmosDbConnectionString = secrets.First().Value
                let cosmosClientOptions = CosmosClientOptions(
                    ApplicationName = Constants.GraceServerAppId, 
                    EnableContentResponseOnWrite = false, 
                    LimitToEndpoint = true, 
                    Serializer = new CosmosJsonSerializer(Constants.JsonSerializerOptions))
                cosmosClient <- new CosmosClient(cosmosDbConnectionString, cosmosClientOptions)
                return cosmosClient
            }).Result

    let CosmosContainer() = 
        if not <| isNull cosmosContainer then
            cosmosContainer
        else
            (task {
                let! databaseResponse = CosmosClient().CreateDatabaseIfNotExistsAsync(configuration["CosmosDatabaseName"])
                let database = databaseResponse.Database
                let containerProperties = ContainerProperties(Id = configuration["CosmosContainerName"], PartitionKeyPath = "/partitionKey", DefaultTimeToLive = 3600)
                let! containerResponse = database.CreateContainerIfNotExistsAsync(containerProperties)
                cosmosContainer <- containerResponse.Container
                return cosmosContainer
            }).Result

    let Set = 
        task {
            let mutable isReady = false
            let mutable gRPCPort: int = 50001
            let grpcPortString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)
            Int32.TryParse(grpcPortString, &gRPCPort) |> ignore
            while not <| isReady do
                do! Task.Delay(TimeSpan.FromSeconds(1.0))
                logToConsole $"Checking if gRPC port {gRPCPort} is ready."
                let tcpListeners = Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                if tcpListeners.Any(fun tcpListener -> tcpListener.Port = gRPCPort) then
                    logToConsole $"gRPC port is ready."
                    isReady <- true
            let! storageKey = daprClient.GetSecretAsync(Constants.GraceSecretStoreName, "AzureStorageKey")
            sharedKeyCredential <- StorageSharedKeyCredential(defaultObjectStorageAccount, storageKey.First().Value)

            //match actorStateStorageProvider with
            //| AzureCosmosDb ->
            //    let! secrets = daprClient.GetSecretAsync(Constants.GraceSecretStoreName, "AzureCosmosDBConnectionString")
            //    let cosmosDbConnectionString = secrets.First().Value
            //    let cosmosClientOptions = CosmosClientOptions(ApplicationName = Constants.GraceServerAppId, EnableContentResponseOnWrite = false, LimitToEndpoint = true)
            //    cosmosClient <- new CosmosClient(cosmosDbConnectionString, cosmosClientOptions)
            //    let! databaseResponse = cosmosClient.CreateDatabaseIfNotExistsAsync(configuration["CosmosDatabaseName"])
            //    let database = databaseResponse.Database
            //    let containerProperties = ContainerProperties(Id = configuration["CosmosContainerName"], PartitionKeyPath = "/partitionKey")
            //    let! containerResponse = database.CreateContainerIfNotExistsAsync(containerProperties)
            //    container <- containerResponse.Container
            //    ()
            //| DynamoDb -> ()
            //| Nul -> ()
        }
