namespace Grace.Shared

open NodaTime
open MessagePack
open MessagePack.FSharp
open MessagePack.NodaTime
open MessagePack.Resolvers
open Microsoft.Extensions.Caching.Memory
open Microsoft.FSharp.Reflection
open NodaTime.Serialization.SystemTextJson
open Polly
open Polly.Contrib.WaitAndRetry
open System
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Collections.Generic
open System.Threading.Tasks

module Constants =

    /// The universal serialization options for F#-specific data types in Grace.
    ///
    /// See https://github.com/Tarmil/FSharp.SystemTextJson/blob/master/docs/Customizing.md for more information about these options.
    let private jsonFSharpOptions =
        JsonFSharpOptions
            .Default()
            .WithAllowNullFields(true)
            .WithUnionFieldsName("value")
            .WithUnionTagNamingPolicy(JsonNamingPolicy.CamelCase)
            .WithUnionTagCaseInsensitive(true)
            .WithUnionEncoding(
                JsonUnionEncoding.ExternalTag
                ||| JsonUnionEncoding.UnwrapFieldlessTags
                ||| JsonUnionEncoding.UnwrapSingleFieldCases
                ||| JsonUnionEncoding.UnwrapSingleCaseUnions
                ||| JsonUnionEncoding.NamedFields
            )
            .WithUnwrapOption(true)

    /// The universal JSON serialization options for Grace.
    let public JsonSerializerOptions = JsonSerializerOptions()
    JsonSerializerOptions.Converters.Add(JsonFSharpConverter(jsonFSharpOptions))
    JsonSerializerOptions.Converters.Add(JsonStringEnumConverter(JsonNamingPolicy.CamelCase))
    JsonSerializerOptions.AllowTrailingCommas <- true
    JsonSerializerOptions.DefaultBufferSize <- 64 * 1024
    JsonSerializerOptions.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingDefault // JsonSerializerOptions.IgnoreNullValues is deprecated. This is the new way to say it.
    JsonSerializerOptions.IncludeFields <- true // Include fields in serialization. This is important for F# records and discriminated unions.
    JsonSerializerOptions.IndentSize <- 2
    JsonSerializerOptions.MaxDepth <- 64 // Default is 64, and I'm assuming this setting would need to change if there were a directory depth greater than 64 in a repo.
    JsonSerializerOptions.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    JsonSerializerOptions.PropertyNameCaseInsensitive <- true // Case sensitivity is from the 1970's. We should let it go.
    //JsonSerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    JsonSerializerOptions.ReadCommentHandling <- JsonCommentHandling.Skip
    JsonSerializerOptions.ReferenceHandler <- ReferenceHandler.IgnoreCycles
    JsonSerializerOptions.RespectNullableAnnotations <- true
    JsonSerializerOptions.UnknownTypeHandling <- JsonUnknownTypeHandling.JsonElement
    JsonSerializerOptions.WriteIndented <- true

    JsonSerializerOptions.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb)
    |> ignore

    /// The universal MessagePack serialization options for Grace.
    let messagePackSerializerOptions =
        MessagePackSerializerOptions.Standard
            .WithResolver(
                CompositeResolver.Create(
                    [|
                       // 1) Generated formatters (classes like Grace_Types_Types.DirectoryVersionFormatter1)
                       //GeneratedResolver.Instance
                       // 2) F# helpers for records/DUs
                       FSharpResolver.Instance
                       // 3) NodaTime formatters (Instant, LocalDate, etc.)
                       NodatimeResolver.Instance
                       // 4) Final fallback
                       StandardResolver.Instance |]
                )
            )
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithSecurity(MessagePackSecurity.UntrustedData)

    /// Attempts to locate the union type from a runtime instance, even when the
    /// value is represented by the compiler-generated nested case type.
    let private tryGetUnionType (runtimeType: Type) =
        let rec loop currentType =
            if isNull currentType then None
            elif FSharpType.IsUnion currentType then Some currentType
            else loop currentType.DeclaringType

        loop runtimeType

    /// Converts both the type name and case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Animal.Dog"
    let getDiscriminatedUnionFullName (x: 'T) =
        let runtimeType = x.GetType()

        match tryGetUnionType runtimeType with
        | Some unionType ->
            let (case, _) = FSharpValue.GetUnionFields(x, unionType)
            $"{unionType.Name}.{case.Name}"
        | None -> runtimeType.Name

    /// Converts just the case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Dog"
    let getDiscriminatedUnionCaseName (x: 'T) =
        let runtimeType = x.GetType()

        match tryGetUnionType runtimeType with
        | Some unionType ->
            let (case, _) = FSharpValue.GetUnionFields(x, unionType)
            $"{case.Name}"
        | None -> runtimeType.Name

    /// The name of the Grace System user.
    [<Literal>]
    let GraceSystemUser = "gracesystem"

    /// The name of the storage service for Actor storage. This should be a document database.
    [<Literal>]
    let GraceActorStorage = "actorstorage"

    /// The name of the storage service for in-memory actors.
    [<Literal>]
    let GraceInMemoryStorage = "in-memory"

    /// The name of the service for Grace object storage.
    [<Literal>]
    let GraceObjectStorage = "graceobjectstorage"

    /// The name of the service for Grace event pub/sub.
    [<Literal>]
    let GracePubSubService = "graceevents"

    /// The name of the Orleans stream provider to publish to.
    [<Literal>]
    let GraceEventStreamProvider = "graceeventstreamprovider"

    /// The name of the event topic to publish to.
    [<Literal>]
    let GraceEventStreamTopic = "graceeventstream"

    /// The name of the directory that holds Grace information in a repository.
    [<Literal>]
    let GraceConfigDirectory = ".grace"

    /// The name of the directory that holds locally-cached files in a repository.
    [<Literal>]
    let GraceObjectsDirectory = "objects"

    /// The name of Grace's configuration file.
    [<Literal>]
    let GraceConfigFileName = "graceconfig.json"

    /// The directory name of Grace's DirectoryVersion cache directory.
    [<Literal>]
    let GraceDirectoryVersionCacheName = "directoryVersions"

    /// The folder name to use in object storage for directory version contents .zip files.
    [<Literal>]
    let GraceDirectoryVersionStorageFolderName = "Grace-DirectoryVersionContents"

    /// The name of the file that holds the file specifications to ignore.
    [<Literal>]
    let GraceIgnoreFileName = ".graceignore"

    /// The name of the file that holds the current local index for Grace.
    [<Literal>]
    let GraceStatusFileName = "gracestatus.msgpack"

    /// The name of the file that holds the current local index for Grace.
    [<Literal>]
    let GraceObjectCacheFile = "graceObjectCache.msgpack"

    /// The default branch name for new repositories.
    [<Literal>]
    let InitialBranchName = "main"

    /// The configuration version number used by this release of Grace.
    let CurrentConfigurationVersion = "0.1"

    /// The configuration version number used by this release of Grace.
    [<Literal>]
    let ServerApiVersionHeaderKey = "X-Api-Version"

    /// Environment variables used by Grace.
    module EnvironmentVariables =
        /// The environment variable that contains the Application Insights connection string.
        [<Literal>]
        let ApplicationInsightsConnectionString = "grace__application_insights_connection_string"

        /// The environment variable that contains the Azure Cosmos DB Connection String.
        [<Literal>]
        let AzureCosmosDBConnectionString = "grace__cosmosdb__connectionstring"

        /// The environment variable that contains the Azure Storage Connection String.
        [<Literal>]
        let AzureStorageConnectionString = "grace__azure_storage__connectionstring"

        /// The environment variable that contains the Azure Storage Key.
        [<Literal>]
        let AzureStorageKey = "grace__azure_storage__key"

        /// The environment variable that contains the name of the CosmosDB container to use for Grace.
        [<Literal>]
        let CosmosContainerName = "grace__cosmosdb__container_name"

        /// The environment variable that contains the name of the CosmosDB database to use for Grace.
        [<Literal>]
        let CosmosDatabaseName = "grace__cosmosdb__database_name"

        /// The environment variable that contains the Grace server Uri. The Uri MUST include a port number,
        /// and MUST NOT include a trailing slash.
        [<Literal>]
        let GraceServerUri = "GRACE_SERVER_URI"

        /// The name of the container in object storage that holds memoized RecursiveDirectoryVersions.
        [<Literal>]
        let DirectoryVersionContainerName = "grace__azure_storage__directoryversion_container_name"

        /// The name of the container in object storage that holds cached Diff contents.
        [<Literal>]
        let DiffContainerName = "grace__azure_storage__diff_container_name"

        /// The name of the container in object storage that holds memoized RecursiveDirectoryVersions.
        [<Literal>]
        let ZipFileContainerName = "grace__azure_storage__zipfile_container_name"

        /// The environment variable that contains the maximum number of reminders that each Grace instance should retrieve from the database and publish for processing.
        [<Literal>]
        let GraceReminderBatchSize = "grace__reminder__batch__size"

        /// The environment variable that contains the name of the Orleans cluster to use.
        [<Literal>]
        let OrleansClusterId = "orleans_cluster_id"

        /// The environment variable that contains the name of the Orleans service to use.
        [<Literal>]
        let OrleansServiceId = "orleans_service_id"

        /// The environment variable that contains the Redis host name.
        [<Literal>]
        let RedisHost = "grace__redis__host"

        /// The environment variable that contains the Redis port number.
        [<Literal>]
        let RedisPort = "grace__redis__port"

        /// The environment variable that selects the pub-sub provider.
        [<Literal>]
        let GracePubSubSystem = "grace__pubsub__system"

        /// Azure Service Bus connection string
        [<Literal>]
        let AzureServiceBusConnectionString = "grace__azure_service_bus__connectionstring"

        /// Azure Service Bus fully qualified namespace (e.g., sb://foo.servicebus.windows.net ).
        [<Literal>]
        let AzureServiceBusNamespace = "grace__azure_service_bus__namespace"

        /// Azure Service Bus topic name for Grace events.
        [<Literal>]
        let AzureServiceBusTopic = "grace__azure_service_bus__topic"

        /// Azure Service Bus subscription name for Grace events.
        [<Literal>]
        let AzureServiceBusSubscription = "grace__azure_service_bus__subscription"

        /// AWS SQS queue URL placeholder for future support.
        [<Literal>]
        let AwsSqsQueueUrl = "grace__aws_sqs__queue_url"

        /// AWS region for SQS (future use).
        [<Literal>]
        let AwsRegion = "grace__aws_sqs__region"

        /// Google Cloud Pub/Sub project identifier (future use).
        [<Literal>]
        let GooglePubSubProjectId = "grace__gcp__projectid"

        /// Google Cloud Pub/Sub topic name (future use).
        [<Literal>]
        let GooglePubSubTopic = "grace__gcp__topic"

        /// Google Cloud Pub/Sub subscription name (future use).
        [<Literal>]
        let GooglePubSubSubscription = "grace__gcp__subscription"

    /// The default CacheControl header for object storage.
    [<Literal>]
    let BlobCacheControl = "public,max-age=86400,no-transform"

    /// The expiration time for a Shared Access Signature token, in minutes.
    let SharedAccessSignatureExpiration = 15.0

    /// The default maximum number of reminders to return in list queries.
    [<Literal>]
    let DefaultReminderMaxCount = 100

    /// The path that indicates the root directory of the repository.
    [<Literal>]
    let RootDirectoryPath = "."

    /// The key for the HttpContext metadata value that holds the CorrelationId for this transaction.
    [<Literal>]
    let CorrelationId = "correlationId"

    /// The property name for the CurrentCommand being handled by a grain.
    [<Literal>]
    let CurrentCommandProperty = "currentCommand"

    /// The property name for the ActorName being handled by a grain.
    [<Literal>]
    let ActorNameProperty = "actorName"

    /// The header name for a W3C trace.
    [<Literal>]
    let Traceparent = "traceparent"

    /// The header name for W3C trace state.
    [<Literal>]
    let Tracestate = "tracestate"

    /// The key for the HttpRequest and HttpResponse header that holds the CorrelationId for this transaction.
    [<Literal>]
    let CorrelationIdHeaderKey = "X-Correlation-Id"

    /// <summary>
    /// Validates that a string is a valid Grace object name.
    ///
    /// Regex: ^[A-Za-z][A-Za-z0-9\-]{1,63}$
    ///
    /// A valid object name in Grace has between 2 and 64 characters, has a letter for the first character ([A-Za-z]), and letters, numbers, or a dash (-) for the rest ([A-Za-z0-9\-_]{1,63}).
    ///
    /// See https://regexper.com for a diagram.
    /// </summary>
    let GraceNameRegex = new Regex("^[A-Za-z][A-Za-z0-9\-]{1,63}$", RegexOptions.CultureInvariant ||| RegexOptions.Compiled, TimeSpan.FromSeconds(1.0))
    // Note: The timeout value of 1s is a crazy big maximum time; matching against this should take less than 1ms.

    /// Validates that a string is a full or partial valid SHA-256 hash value, between 2 and 64 hexadecimal characters.
    ///
    /// Regex: ^[0-9a-fA-F]{2,64}$
    let Sha256Regex = new Regex("^[0-9a-fA-F]{2,64}$", RegexOptions.CultureInvariant ||| RegexOptions.Compiled, TimeSpan.FromSeconds(1.0))

    /// The backoff policy used by Grace for server requests.
    let private backoffWithJitter = Backoff.DecorrelatedJitterBackoffV2(medianFirstRetryDelay = (TimeSpan.FromSeconds(0.25)), retryCount = 7, fastFirst = false)

    /// An exponential retry policy, with backoffs starting at 0.25s, and retrying 8 times.
    let DefaultRetryPolicy = Policy.Handle<Exception>(fun ex -> ex.GetType() <> typeof<KeyNotFoundException>).WaitAndRetry(backoffWithJitter)

    /// An exponential retry policy, with backoffs starting at 0.25s, and retrying 8 times.
    let DefaultAsyncRetryPolicy = Policy.Handle<Exception>(fun ex -> ex.GetType() <> typeof<KeyNotFoundException>).WaitAndRetryAsync(backoffWithJitter)

    let private fileCopyBackoff = Backoff.LinearBackoff(initialDelay = (TimeSpan.FromSeconds(1.0)), retryCount = 16, factor = 1.5, fastFirst = false)

    /// A linear retry policy for copying files locally, with backoffs starting at 1s and retrying 16 times.
    // This retry policy helps with large files. `grace watch` will see that the file is arriving, but if that file takes longer to be written than the next tick,
    // we get an IOException when we try to compute the Sha256Hash and copy it to the object directory. This policy allows us to wait until the file is complete.
    let DefaultFileCopyRetryPolicy = Policy.Handle<IOException>(fun ex -> ex.GetType() <> typeof<KeyNotFoundException>).WaitAndRetry(fileCopyBackoff)

    /// Grace's global settings for Parallel.ForEach/ForEachAsync expressions; sets MaxDegreeofParallelism to maximize performance.
    // I'm choosing a higher-than-usual number here because these parallel loops are used in code where most of the time is spent on network
    //   and disk traffic - and therefore Task<'T> - and we can run lots of them simultaneously.
    let ParallelOptions = ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount * 1)

    /// Default directory size magic value.
    let InitialDirectorySize = int64 -1

    /// The default root branch Id for a repository. Value: 38EC9A98-00B0-4FA3-8CC5-ACFB04E445A7.
    let DefaultParentBranchId = Guid("38EC9A98-00B0-4FA3-8CC5-ACFB04E445A7") // There's nothing special about this Guid. I just generated it one day.

    /// A special Grace Event Actor Id used for publishing events in Grace.Server. Value: 63097EF4-9D67-4CFB-8010-938484668E4A.
    let GraceEventActorId = Guid("63097EF4-9D67-4CFB-8010-938484668E4A") // There's nothing special about this Guid. I just generated it one day.

    /// The name of the inter-process communication file used by grace watch to share status with other invocations of Grace.
    [<Literal>]
    let IpcFileName = "graceWatchStatus.json"

    /// The name of the file to let `grace watch` know that `grace rebase` or `grace switch` is underway.
    [<Literal>]
    let UpdateInProgressFileName = "graceUpdateInProgress.txt"

    /// The custom alphabet to use when generating a NanoId for a CorrelationId. This alphabet is URL-safe. Consists of "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._-".
    [<Literal>]
    let CorrelationIdAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._-"

    /// The default value for a timestamp during record construction. Equal to 2000-01-01T00:00:00Z.
    let DefaultTimestamp = Instant.FromDateTimeUtc(DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))

    /// The default name of the account in object storage that holds Grace objects.
    [<Literal>]
    let DefaultObjectStorageAccount = "gracevcsdevelopment"

    /// Values used with Grace's MemoryCache.
    module MemoryCache =
        /// A special Guid value that means "false" or "we know there's no value here". Used in places where the cache entry value is a Guid.
        let EntityDoesNotExistGuid = Guid("27F21D8A-DA1D-4C73-8773-4AA5A5712612") // There's nothing special about this Guid. I just generated it one day.

        /// A special Guid value that means "false" or "we know there's no value here". Used in places where the cache entry value is a Guid.
        let EntityDoesNotExist = box EntityDoesNotExistGuid

        /// The default value to store in Grace's MemoryCache when an entity is known to exist. This is a one-character string because MemoryCache values are Objects, and a const string doesn't require boxing.
        [<Literal>]
        let Exists = "y"

        /// The default value to store in Grace's MemoryCache when a value is known not to exist. This is a one-character string because MemoryCache values are Objects, and a const string doesn't require boxing.
        [<Literal>]
        let DoesNotExist = "n"

        /// The default expiration time for a memory cache entry, in minutes.
#if DEBUG
        let DefaultExpirationTime = TimeSpan.FromMinutes(0.5)
#else
        let DefaultExpirationTime = TimeSpan.FromMinutes(2.0)
#endif

/// A MemoryCacheEntryOptions object that uses Grace's default expiration time.
//let DefaultMemoryCacheEntryOptions = MemoryCacheEntryOptions().SetAbsoluteExpiration(DefaultExpirationTime)

module Results =
    let Ok = 0
    let Exception = -1
    let FileNotFound = -2
    let ConfigurationFileNotFound = -3
    let InvalidConfigurationFile = -4
    let NotParsed = -98
    let CommandNotFound = -99
    let ThisShouldNeverHappen = -999
