namespace Grace.Shared

open Microsoft.Extensions.Caching.Memory
open Microsoft.FSharp.Reflection
open NodaTime
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
open MessagePack
open MessagePack.FSharp
open MessagePack.NodaTime
open MessagePack.Resolvers

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
    JsonSerializerOptions.AllowTrailingCommas <- true
    JsonSerializerOptions.DefaultBufferSize <- 64 * 1024
    JsonSerializerOptions.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingDefault // JsonSerializerOptions.IgnoreNullValues is deprecated. This is the new way to say it.
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
            .WithResolver(CompositeResolver.Create(NodatimeResolver.Instance, StandardResolver.Instance))
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithSecurity(MessagePackSecurity.UntrustedData)

    /// Converts both the type name and case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Animal.Dog"
    let getDiscriminatedUnionFullName (x: 'T) =
        let discriminatedUnionType = typeof<'T>
        let (case, _) = FSharpValue.GetUnionFields(x, discriminatedUnionType)
        $"{discriminatedUnionType.Name}.{case.Name}"

    /// Converts just the case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Dog"
    let getDiscriminatedUnionCaseName (x: 'T) =
        let discriminatedUnionType = typeof<'T>
        let (case, _) = FSharpValue.GetUnionFields(x, discriminatedUnionType)
        $"{case.Name}"

    /// The name of the Grace System user.
    [<Literal>]
    let GraceSystemUser = "gracesystem"

    /// The name of the Dapr service for Grace object storage.
    let GraceObjectStorage = "graceobjectstorage"

    /// The name of the Dapr service for Actor storage. This should be a document database.
    let GraceActorStorage = "actorstorage"

    /// The name of the Dapr service for Grace event pub/sub.
    let GracePubSubService = "graceevents"

    /// The name of the event topic to publish to.
    let GraceEventStreamTopic = "graceeventstream"

    /// The name of the reminders topic to publish to.
    let GraceRemindersStorage = "actorstorage" // For now, keeping reminders in the same storage as actors.

    /// The name of the reminders topic to publish to.
    let GraceRemindersTopic = "gracereminders"

    /// The name of the reminders subscription to subscribe to.
    let GraceRemindersSubscription = "reminder-service"

    /// The name of the Dapr service for retrieving application secrets.
    let GraceSecretStoreName = "kubernetessecretstore"

    /// The name of the directory that holds Grace information in a repository.
    let GraceConfigDirectory = ".grace"

    /// The name of the directory that holds locally-cached files in a repository.
    let GraceObjectsDirectory = "objects"

    /// The name of Grace's configuration file.
    let GraceConfigFileName = "graceconfig.json"

    /// The directory name of Grace's DirectoryVersion cache directory.
    let GraceDirectoryVersionCacheName = "directoryVersions"

    /// The name of the file that holds the file specifications to ignore.
    let GraceIgnoreFileName = "graceignore.txt"

    /// The name of the file that holds the current local index for Grace.
    let GraceStatusFileName = "gracestatus.msgpack"

    /// The name of the file that holds the current local index for Grace.
    let GraceObjectCacheFile = "graceObjectCache.msgpack"

    /// The default branch name for new repositories.
    [<Literal>]
    let InitialBranchName = "main"

    /// The configuration version number used by this release of Grace.
    let CurrentConfigurationVersion = "0.1"

    /// The configuration version number used by this release of Grace.
    let ServerApiVersionHeaderKey = "X-Api-Version"

    /// A list of known Grace Server API version strings.
    type ServerApiVersions =
        | ``V2022-02-01``
        | Latest
        | Edge

        override this.ToString() = getDiscriminatedUnionFullName this

    /// Environment variables used by Grace.
    module EnvironmentVariables =
        /// The environment variable that contains the Azure Cosmos DB Connection String.
        [<Literal>]
        let AzureCosmosDBConnectionString = "azurecosmosdbconnectionstring"

        /// The environment variable that contains the Azure Storage Connection String.
        [<Literal>]
        let AzureStorageConnectionString = "azurestorageconnectionstring"

        /// The environment variable that contains the Azure Storage Key.
        [<Literal>]
        let AzureStorageKey = "azurestoragekey"

        /// The environment variable that contains the name of the CosmosDB container to use for Grace.
        [<Literal>]
        let CosmosContainerName = "cosmoscontainername"

        /// The environment variable that contains the name of the CosmosDB database to use for Grace.
        [<Literal>]
        let CosmosDatabaseName = "cosmosdatabasename"

        /// The environment variable that contains the Dapr application ID.
        [<Literal>]
        let DaprAppId = "DAPR_APP_ID"

        /// The environment variable that contains the Dapr server Uri. The Uri should not include a port number.
        [<Literal>]
        let DaprServerUri = "DAPR_SERVER_URI"

        /// The environment variable that contains the application's port.
        [<Literal>]
        let DaprAppPort = "DAPR_APP_PORT"

        /// The environment variable that contains the Dapr HTTP port.
        [<Literal>]
        let DaprHttpPort = "DAPR_HTTP_PORT"

        /// The environment variable that contains the Dapr gRPC port.
        [<Literal>]
        let DaprGrpcPort = "DAPR_GRPC_PORT"

        /// The environment variable that contains the maximum number of reminders that each Grace instance should retrieve from the database and publish for processing.
        [<Literal>]
        let GraceReminderBatchSize = "gracereminderbatchsize"

    /// The default CacheControl header for object storage.
    [<Literal>]
    let BlobCacheControl = "public,max-age=86400,no-transform"

    /// The expiration time for a Shared Access Signature token, in minutes.
    let SharedAccessSignatureExpiration = 15.0

    /// The path that indicates the root directory of the repository.
    let RootDirectoryPath = "."

    /// The key for the HttpContext metadata value that holds the CorrelationId for this transaction.
    let CorrelationId = "correlationId"

    /// The header name for a W3C trace.
    let Traceparent = "traceparent"

    /// The header name for W3C trace state.
    let Tracestate = "tracestate"

    /// The key for the HttpRequest and HttpResponse header that holds the CorrelationId for this transaction.
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
    let DefaultRetryPolicy =
        Policy
            .Handle<Exception>(fun ex -> ex.GetType() <> typeof<KeyNotFoundException>)
            .WaitAndRetry(backoffWithJitter)

    /// An exponential retry policy, with backoffs starting at 0.25s, and retrying 8 times.
    let DefaultAsyncRetryPolicy =
        Policy
            .Handle<Exception>(fun ex -> ex.GetType() <> typeof<KeyNotFoundException>)
            .WaitAndRetryAsync(backoffWithJitter)

    let private fileCopyBackoff = Backoff.LinearBackoff(initialDelay = (TimeSpan.FromSeconds(1.0)), retryCount = 16, factor = 1.5, fastFirst = false)

    /// A linear retry policy for copying files locally, with backoffs starting at 1s and retrying 16 times.
    // This retry policy helps with large files. `grace watch` will see that the file is arriving, but if that file takes longer to be written than the next tick,
    // we get an IOException when we try to compute the Sha256Hash and copy it to the object directory. This policy allows us to wait until the file is complete.
    let DefaultFileCopyRetryPolicy =
        Policy
            .Handle<IOException>(fun ex -> ex.GetType() <> typeof<KeyNotFoundException>)
            .WaitAndRetry(fileCopyBackoff)

    /// Grace's global settings for Parallel.ForEach/ForEachAsync expressions; sets MaxDegreeofParallelism to maximize performance.
    // I'm choosing a higher-than-usual number here because these parallel loops are used in code where most of the time is spent on network
    //   and disk traffic - and therefore Task<'T> - and we can run lots of them simultaneously.
    let ParallelOptions = ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount * 2)

    /// Default directory size magic value.
    let InitialDirectorySize = int64 -1

    /// The default root branch Id for a repository.
    let DefaultParentBranchId = Guid("38EC9A98-00B0-4FA3-8CC5-ACFB04E445A7") // There's nothing special about this Guid. I just generated it one day.

    /// The name of the inter-process communication file used by grace watch to share status with other invocations of Grace.
    [<Literal>]
    let IpcFileName = "graceWatchStatus.json"

    /// The name of the file to let `grace watch` know that `grace rebase` or `grace switch` is underway.
    [<Literal>]
    let UpdateInProgressFileName = "graceUpdateInProgress.txt"

    /// The custom alphabet to use when generating a CorrelationId. This alphabet is URL-safe. Consists of "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._-".
    [<Literal>]
    let CorrelationIdAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._-"

    /// The default value for a timestamp during record construction. Equal to 2000-01-01T00:00:00Z.
    let DefaultTimestamp = Instant.FromDateTimeUtc(DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))

    /// Values used with Grace's MemoryCache.
    module MemoryCache =
        /// A special Guid value that means "false" or "we know there's no value here". Used in places where the cache entry value is a Guid.
        let EntityDoesNotExistGuid = Guid("27F21D8A-DA1D-4C73-8773-4AA5A5712612") // There's nothing special about this Guid. I just generated it one day.

        /// A special Guid value that means "false" or "we know there's no value here". Used in places where the cache entry value is a Guid.
        let EntityDoesNotExist = box EntityDoesNotExistGuid

        /// The default value to store in Grace's MemoryCache when an entity is known to exist. This is a one-character string because MemoryCache values are Objects, and constant string avoids boxing.
        [<Literal>]
        let ExistsValue = "y"

        /// The default value to store in Grace's MemoryCache when a value is known not to exist. This is a one-character string because MemoryCache values are Objects, and constant string avoids boxing.
        [<Literal>]
        let DoesNotExistValue = "n"

        /// The default expiration time for a memory cache entry, in minutes.
#if DEBUG
        let DefaultExpirationTime = TimeSpan.FromMinutes(2.0)
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
