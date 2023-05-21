namespace Grace.Shared

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
        JsonFSharpOptions.Default()
            .WithAllowNullFields(true)
            .WithUnionFieldsName("value")
            .WithUnionTagNamingPolicy(JsonNamingPolicy.CamelCase)
            .WithUnionTagCaseInsensitive(true)
            .WithUnionEncoding(JsonUnionEncoding.ExternalTag ||| 
                JsonUnionEncoding.UnwrapFieldlessTags |||
                JsonUnionEncoding.UnwrapSingleFieldCases ||| 
                JsonUnionEncoding.UnwrapSingleCaseUnions ||| 
                JsonUnionEncoding.NamedFields)
            .WithUnwrapOption(true)

    /// The universal JSON serialization options for Grace.
    let public JsonSerializerOptions = JsonSerializerOptions()
    JsonSerializerOptions.AllowTrailingCommas <- true
    JsonSerializerOptions.Converters.Add(JsonFSharpConverter(jsonFSharpOptions))
    JsonSerializerOptions.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb) |> ignore
    JsonSerializerOptions.DefaultBufferSize <- 64 * 1024
    JsonSerializerOptions.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingDefault  // JsonSerializerOptions.IgnoreNullValues is deprecated. This is the new way to say it.
    JsonSerializerOptions.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    JsonSerializerOptions.PropertyNameCaseInsensitive <- true
    //JsonSerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    JsonSerializerOptions.ReadCommentHandling <- JsonCommentHandling.Skip
    JsonSerializerOptions.ReferenceHandler <- ReferenceHandler.IgnoreCycles
    JsonSerializerOptions.UnknownTypeHandling <- JsonUnknownTypeHandling.JsonElement
    JsonSerializerOptions.WriteIndented <- true
    
    /// Converts the full name of a discriminated union to a string. Example: ServerApiVersions.Latest -> "ServerApiVersions.Latest"
    let discriminatedUnionFullName (value:'T) = 
        let discriminatedUnionType = typeof<'T>
        let (case, _ ) = FSharpValue.GetUnionFields(value, discriminatedUnionType)
        $"{discriminatedUnionType.Name}.{case.Name}"

    /// Converts just the case name of a discriminated union to a string. Example: ServerApiVersions.Latest -> "Latest"
    let discriminatedUnionCaseName (value:'T) = 
        let discriminatedUnionType = typeof<'T>
        let (case, _ ) = FSharpValue.GetUnionFields(value, discriminatedUnionType)
        $"{case.Name}"

    /// The name of the Dapr service running Grace Server.
    let GraceServerAppId = "grace-server"

    /// The name of the Dapr service for Grace object storage.
    let GraceObjectsStorage = "graceObjectsStorage"

    /// The name of the Dapr service for Grace event pub/sub.
    let GracePubSubService = "graceeventstream"

    /// The name of the event topic to publish to.
    let GraceEventStreamTopic = "graceeventstream"

    /// The name of the Dapr service for retrieving application secrets.
    let GraceSecretStoreName = "cloudsecretstore"

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
    let GraceStatusFileName = "gracestatus.json.gz"

    /// The name of the file that holds the current local index for Grace.
    let GraceObjectCacheFile = "graceObjectCache.json.gz"

    /// The default branch name for new repositories.
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
        override this.ToString() = discriminatedUnionFullName this

    /// Environment variables used by Grace.
    module EnvironmentVariables =
        /// The environment variable that contains the Dapr server Uri. The Uri should not include a port number.
        let DaprServerUri = "DAPR_SERVER_URI"

        /// The environment variable that contains the application's port.
        let GraceAppPort = "GRACE_APP_PORT"

        /// The environment variable that contains the Dapr HTTP port.
        let DaprHttpPort = "DAPR_HTTP_PORT"

        /// The environment variable that contains the Dapr gRPC port.
        let DaprGrpcPort = "DAPR_GRPC_PORT"

        let AzureCosmosDBConnectionString = "Azure_CosmosDB_Connection_String"

    /// The default CacheControl header for object storage.
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
    /// Regex: [A-Za-z][A-Za-z0-9\-]{1,63}$
    ///
    /// A valid object name in Grace has between 2 and 64 characters, has a letter for the first character ([A-Za-z]), and letters, numbers, or a dash (-) for the rest ([A-Za-z0-9\-_]{1,63}).
    /// 
    /// See https://regexper.com for a diagram.
    /// </summary>
    let GraceNameRegexText = "^[A-Za-z][A-Za-z0-9\-]{1,63}$"

    /// <summary>
    /// Validates that a string is a valid Grace object name.
    ///
    /// Regex: [A-Za-z][A-Za-z0-9\-]{1,63}$
    ///
    /// A valid object name in Grace has between 2 and 64 characters, has a letter for the first character ([A-Za-z]), and letters, numbers, or a dash (-) for the rest ([A-Za-z0-9\-_]{1,63}).
    /// 
    /// See https://regexper.com for a diagram.
    /// </summary>
    let GraceNameRegex = new Regex(GraceNameRegexText, RegexOptions.CultureInvariant ||| RegexOptions.Compiled, TimeSpan.FromSeconds(2.0))
    // Note: The timeout value of 2s is a crazy big maximum time; matching against this should take less than 1ms.

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

    /// Global settings for Parallel.ForEach statements; sets MaxDegreeofParallelism to maximize performance.
    // I'm choosing a high number here because these parallel loops are used where most of the time is spent on network 
    //   and disk traffic - and therefore Task<'T> - and we can run lots of them simultaneously.
    let ParallelOptions = ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount * 8)

    /// Default directory size magic value.
    let InitialDirectorySize = uint64 Int64.MaxValue

    /// The default root branch Id for a repository.
    let DefaultParentBranchId = Guid("38EC9A98-00B0-4FA3-8CC5-ACFB04E445A7") // There's nothing special about this Guid.

    /// The name of the inter-process communication file used by grace watch to share status with other invocations of Grace.
    let IpcFileName = "GraceWatchStatus.json"

module Results =
    let Ok = 0
    let Exception = -1
    let FileNotFound = -2
    let ConfigurationFileNotFound = -3
    let InvalidConfigurationFile = -4
    let NotParsed = -98
    let CommandNotFound = -99
    let ThisShouldNeverHappen = -999
