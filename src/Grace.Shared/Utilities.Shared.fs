namespace Grace.Shared

open Microsoft.Extensions.Caching.Memory
open Microsoft.FSharp.NativeInterop
open Microsoft.FSharp.Reflection
open NodaTime
open NodaTime.Text
open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net.Http.Json
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Net.Http
open System.Net.Security
open System.Net
open System
open System.Reflection
open System.Collections.Concurrent
open System.Buffers
open Microsoft.Extensions.ObjectPool

#nowarn "9"

module Combinators =
    let either okFunc errorFunc graceResult =
        match graceResult with
        | Result.Ok s -> okFunc s
        | Result.Error f -> errorFunc f

    let ok x = Result.Ok x
    let error x = Result.Error x

    let bind f = either f error

    let (>>=) x f = bind f x

    let (>=>) s1 s2 = s1 >> bind s2

module Utilities =
    let memoryCacheOptions = MemoryCacheOptions(TrackStatistics = false, TrackLinkedCacheEntries = false, ExpirationScanFrequency = TimeSpan.FromSeconds(30.0))
    let memoryCache: IMemoryCache = new MemoryCache(memoryCacheOptions)

    /// Defines a PooledObjectPolicy specialized for the StringBuilder type.
    type StringBuilderPooledObjectPolicy() =
        inherit PooledObjectPolicy<StringBuilder>()

        override _.Create() = new StringBuilder()

        override _.Return(sb: StringBuilder) =
            sb.Clear() |> ignore
            true

    let pooledObjectPolicy = StringBuilderPooledObjectPolicy()

    /// An ObjectPool that can be used to efficiently get StringBuilder instances.
    let stringBuilderPool = ObjectPool.Create<StringBuilder>(pooledObjectPolicy)

    /// Gets the current instant.
    let getCurrentInstant () = SystemClock.Instance.GetCurrentInstant()

    /// Gets a future instant by adding the provided duration to the current time.
    let getFutureInstant (duration: Duration) = getCurrentInstant().Plus(duration)

    /// Formats an instant as a string in ExtendedIso format.
    ///
    /// Example: "2019-06-15T13:45:30.9040833Z".
    let formatInstantExtended (instant: Instant) =
        let instantString = instant.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)

        if instantString.Length = 28 then
            instantString
        else
            // Pad the fractional seconds with zeros.
            let zerosToAdd = 28 - instantString.Length
            let extraZeros = String.replicate zerosToAdd "0"
            $"{instantString.Substring(0, instantString.Length - 1)}{extraZeros}Z"

    //$"{instant.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture),-28}"

    /// Gets the current instant as a string in ExtendedIso format.
    ///
    /// Example: "2031-06-15T13:45:30.9040833Z".
    let getCurrentInstantExtended () = getCurrentInstant () |> formatInstantExtended

    /// Formats an instant as a string in General format.
    ///
    /// Example: "2019-06-15T13:45:30Z".
    let formatInstantGeneral (instant: Instant) = instant.ToString(InstantPattern.General.PatternText, CultureInfo.InvariantCulture)

    /// Gets a future Instant, by adding the provided duration to the current time, and outputs it as a string in ExtendedIso format.
    ///
    /// Example output: "2024-06-01T08:27:04.3273839Z"
    let formatFutureInstantExtended (duration: Duration) = formatInstantExtended (getCurrentInstant().Plus(duration))

    /// Gets the current instant as a string in General format.
    ///
    /// Example: "2019-06-15T13:45:30Z".
    let getCurrentInstantGeneral () = getCurrentInstant () |> formatInstantGeneral

    /// Converts an Instant to local time, and produces a string in short date/time format, using the CurrentUICulture.
    let instantToLocalTime (instant: Instant) = instant.ToDateTimeUtc().ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)

    /// Gets the current instant in local time as a string in short date/time format, using the CurrentUICulture.
    let getCurrentInstantLocal () = getCurrentInstant () |> instantToLocalTime

    /// Ensures that the DateTime is printed in exactly the same number of characters, so the output is aligned.
    let formatDateTimeAligned (dateTime: DateTime) =
        let datePart = dateTime.ToString("ddd yyyy-MM-dd")
        let timePart = dateTime.ToString("h:mm:ss tt")
        let formattedTimePart = if timePart.Length = 10 then " " + timePart else timePart
        sprintf "%s %s" datePart formattedTimePart

    /// Ensures that the Instant is printed in exactly the same number of characters, so the output is aligned.
    let formatInstantAligned (instant: Instant) = formatDateTimeAligned (instant.ToDateTimeUtc())

    let mutable lockObject = new Threading.Lock()

    /// Logs the message to the console, with the current instant and thread ID.
    let logToConsole message = lock lockObject (fun () -> printfn $"{getCurrentInstantExtended ()} {Environment.CurrentManagedThreadId:X2} {message}")

    /// Converts an environment variable name to a key used to look up the value in IConfiguration.
    let getConfigKey (environmentVariableName: string) = environmentVariableName.Replace("__", ":")

    /// Gets the elapsed time since the start time, in milliseconds, right-aligned in a string of not less than 7 characters.
    ///
    /// If the duration is less than 7 characters, it is padded with spaces.
    /// If the duration is more than 7 characters, nothing is truncated.
    let getDurationRightAligned_ms (time: Instant) =
        let milliseconds = $"{getCurrentInstant().Minus(time).TotalMilliseconds:F3}"
        let result = (String.replicate (Math.Max(7 - milliseconds.Length, 0)) " ") + milliseconds // Right-align, 7 characters.
        result

    /// Gets the first eight characters of a SHA256 hash.
    let getShortSha256Hash (sha256Hash: String) = if sha256Hash.Length >= 8 then sha256Hash.Substring(0, 8) else String.Empty

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

    let defaultForType (t: Type) : obj = if t.IsValueType then Activator.CreateInstance t else null

    /// Converts a string into the corresponding case of a discriminated union type.
    ///
    /// If the case has fields, they will be initialized to their default values (null for reference types, zero/false for value types).
    ///
    /// Examples:
    ///
    ///   discriminatedUnionFromString<Animal> "Dog" -> Animal.Dog
    ///
    ///   discriminatedUnionFromString<Status> "InProgress" (where InProgress has a PercentDone field) -> Status.InProgress 0
    let discriminatedUnionFromString<'T> (s: string) =
        match
            FSharpType.GetUnionCases typeof<'T>
            |> Array.filter (fun case -> String.Compare(case.Name, s, ignoreCase = true) = 0)
        with
        | [| case |] ->
            let fieldCount = case.GetFields().Length

            if fieldCount = 0 then
                Some(FSharpValue.MakeUnion(case, [||]) :?> 'T)
            else
                let fields = case.GetFields()
                let unionCaseParameters = List<objnull>(fieldCount)

                for i in 0 .. fieldCount - 1 do
                    let propertyType = fields[i].PropertyType
                    unionCaseParameters.Add(defaultForType (propertyType))

                Some(FSharpValue.MakeUnion(case, unionCaseParameters.ToArray()) :?> 'T)
        | _ -> None

    /// Gets the cases of a discriminated union as an array of strings.
    ///
    /// Example: listCases<Animal> -> [| "Dog"; "Cat" |]
    let listCases<'T> () = FSharpType.GetUnionCases typeof<'T> |> Array.map (fun c -> c.Name)

    /// Gets the cases of discriminated union for serialization.
    let GetKnownTypes<'T> () =
        typeof<'T>.GetNestedTypes(BindingFlags.Public ||| BindingFlags.NonPublic)
        |> Array.filter FSharpType.IsUnion

    /// Serializes an object to JSON, using Grace's custom JsonSerializerOptions.
    let serialize<'T> item = JsonSerializer.Serialize<'T>(item, Constants.JsonSerializerOptions)

    /// Serializes an object to JSON and writes it to a stream, using Grace's custom JsonSerializerOptions.
    let serializeAsync<'T> (stream: Stream) item = task { return! JsonSerializer.SerializeAsync<'T>(stream, item, Constants.JsonSerializerOptions) }

    /// Deserializes a JSON string to a provided type, using Grace's custom JsonSerializerOptions.
    let deserialize<'T> (s: string) = JsonSerializer.Deserialize<'T>(s, Constants.JsonSerializerOptions)

    /// Deserializes a stream of JSON to a provided type, using Grace's custom JsonSerializerOptions.
    let deserializeAsync<'T> (stream: Stream) = task { return! JsonSerializer.DeserializeAsync<'T>(stream, Constants.JsonSerializerOptions) }

    /// Deserializes the Content from an HttpResponseMessage to the provided type, using Grace's custom JsonSerializerOptions.
    let deserializeContent<'T> (response: HttpResponseMessage) =
        task {
            let! stream = response.Content.ReadAsStreamAsync()
            return! deserializeAsync<'T> stream
        }

    /// Create JsonContent from the provided object, using Grace's custom JsonSerializerOptions.
    let createJsonContent<'T> item = JsonContent.Create(item, options = Constants.JsonSerializerOptions)

    /// Returns true if Grace is running on a Windows machine.
    let runningOnWindows =
        match Environment.OSVersion.Platform with
        | PlatformID.Win32NT
        | PlatformID.Win32S
        | PlatformID.Win32Windows
        | PlatformID.WinCE -> true
        | _ -> false

    /// Returns true if Grace is running on a MacOS machine.
    let runningOnMacOS =
        match Environment.OSVersion.Platform with
        | PlatformID.MacOSX -> true
        | _ -> false

    /// Returns true if Grace is running on a Unix or Linux machine.
    let runningOnLinux =
        match Environment.OSVersion.Platform with
        | PlatformID.Unix -> true
        | _ -> false

    /// Returns true if Grace is running in a browser.
    let runningOnBrowser =
        match Environment.OSVersion.Platform with
        | PlatformID.Other -> true
        | _ -> false

    /// Returns the given path, replacing any Windows-style backslash characters (\) with forward-slash characters (/).
    let normalizedTimeSpan = TimeSpan.FromMinutes(1.0)

    /// Converts backslashes to forward slashes, and caches the result for performance.
    let normalizeFilePath (filePath: string) =
        let mutable result = String.Empty

        if not <| memoryCache.TryGetValue(filePath, &result) then
            let normalized = filePath.Replace(@"\", "/")
            memoryCache.Set(filePath, normalized, normalizedTimeSpan) |> ignore
            normalized
        else
            result

    /// Switches "/" to "\" when we're running on Windows.
    let getNativeFilePath (filePath: string) = if runningOnWindows then filePath.Replace("/", @"\") else filePath

    /// File stream options for reading files efficiently in Grace.
    let fileStreamOptionsRead =
        FileStreamOptions(
            BufferSize = 8 * 1024,
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = (FileOptions.Asynchronous ||| FileOptions.SequentialScan)
        )

    /// File stream options for writing files efficiently in Grace.
    let fileStreamOptionsWrite =
        FileStreamOptions(BufferSize = 8 * 1024, Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous)

    /// Returns the directory of a file, relative to the root of the repository's working directory.
    let getRelativeDirectory (filePath: string) rootDirectory =
        let standardizedFilePath = normalizeFilePath filePath
        let standardizedRootDirectory = normalizeFilePath rootDirectory
        //logToConsole $"In getRelativeDirectory: standardizedFilePath: {standardizedFilePath}; standardizedRootDirectory: {standardizedRootDirectory}."
        //let originalFileRelativePath =
        //    if String.IsNullOrEmpty(standardizedRootDirectory) then standardizedFilePath else Path.GetRelativePath(standardizedRootDirectory, standardizedFilePath)
        //logToConsole $"originalFileRelativePath: {originalFileRelativePath}."
        let relativePathParts = standardizedFilePath.Split("/")
        //logToConsole $"relativePathParts.Length: {relativePathParts.Length}"
        if relativePathParts.Length = 1 then
            Constants.RootDirectoryPath
        else
            let sb = stringBuilderPool.Get()

            try
                let relativeDirectoryPath =
                    relativePathParts[0..^1]
                    |> Array.fold (fun (sb: StringBuilder) currentPart -> sb.Append($"{currentPart}/")) sb

                relativeDirectoryPath.Remove(relativeDirectoryPath.Length - 1, 1) |> ignore // Remove trailing slash.
                //logToConsole $"relativeDirectoryPath.ToString(): {relativeDirectoryPath.ToString()}"
                (relativeDirectoryPath.ToString())
            finally
                stringBuilderPool.Return(sb)

    /// Returns the directory of a file, relative to the root of the repository's working directory.
    let getLocalRelativeDirectory (filePath: string) rootDirectory =
        let standardizedFilePath = normalizeFilePath filePath
        let standardizedRootDirectory = normalizeFilePath rootDirectory
        //logToConsole $"In getRelativeDirectory: standardizedRootDirectory: {standardizedFilePath}; standardizedRootDirectory: {standardizedRootDirectory}."
        let originalFileRelativePath =
            if String.IsNullOrEmpty(standardizedRootDirectory) then
                standardizedFilePath
            else
                Path.GetRelativePath(standardizedRootDirectory, standardizedFilePath)
        //logToConsole $"In getRelativeDirectory: originalFileRelativePath: {originalFileRelativePath}"
        let relativePathParts = originalFileRelativePath.Split(Path.DirectorySeparatorChar)
        //logToConsole $"In getRelativeDirectory: relativePathParts.Length: {relativePathParts.Length}; relativePathParts[0]: {relativePathParts[0]}"
        if
            relativePathParts.Length = 1
            && relativePathParts[0] = Constants.RootDirectoryPath
        then
            Constants.RootDirectoryPath
        else
            let sb = stringBuilderPool.Get()

            try
                let relativeDirectoryPath =
                    relativePathParts
                    |> Array.fold (fun (sb: StringBuilder) currentPart -> sb.Append($"{currentPart}{Path.DirectorySeparatorChar}")) sb

                relativeDirectoryPath.Remove(relativeDirectoryPath.Length - 1, 1) |> ignore
                //logToConsole $"In getRelativeDirectory: relativeDirectoryPath.ToString(): {relativeDirectoryPath.ToString()}"
                (relativeDirectoryPath.ToString())
            finally
                stringBuilderPool.Return(sb)


    [<Literal>]
    let CorrelationIdAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._-"

    /// Returns a randomly-generated, 12-character NanoId as a new CorrelationId.
    let generateCorrelationId () =
        // According to https://alex7kom.github.io/nano-nanoid-cc/?alphabet=~._-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz&size=12&speed=1000&speedUnit=second
        //   if we generate 1000 NanoId's per second with our CorrelationIdAlphabet, it'll take 4 months before there's even a 1% chance of a collision.
        //
        //   (It'll be a while before Grace requires 1000 CorrelationId's per second, but let's assume it might.)
        //
        //   Even if there is a collision, who cares? it's just a CorrelationId, and it will be in requests for different owners/orgs/repos/branches/etc.
        //
        //   One of the really nice features of NanoId's vs. Guid's is that you get to choose the strength of the uniqueness guarantee by choosing the size of the NanoId.
        //   I'm selecting 12 characters because it's just long enough to be reliably unique enough for _this_ purpose. CorrelationId's show up everywhere
        //   in Grace, and get stored in multiple ways, so it's nice to keep them as small as possible while being unique _enough_.
        //
        //   The caller can always specify their own CorrelationId if they want to.
        NanoidDotNet.Nanoid.Generate(CorrelationIdAlphabet, size = 12)

    /// Returns either the supplied correlationId, if not null, or a new Guid.
    let ensureNonEmptyCorrelationId (correlationId: string) =
        if not <| String.IsNullOrEmpty(correlationId) then
            correlationId
        else
            generateCorrelationId ()

    /// Formats a byte array as a string. For example, [0xab, 0x15, 0x03] -> "ab1503"
    ///
    /// Each byte is formatted as a two-character hexadecimal number.
    ///
    /// NOTE: This is different from Encoding.UTF8.GetString, which interprets the bytes as UTF-8 characters.
    let byteArrayToString (array: Span<byte>) =
        let sb = stringBuilderPool.Get()

        try
            for b in array do
                sb.Append($"{b:x2}") |> ignore

            sb.ToString()
        finally
            stringBuilderPool.Return(sb)

    /// Converts a string of hexadecimal numbers to a byte array. For example, "ab1503" -> [0xab, 0x15, 0x03]
    ///
    /// The hex string must have an even number of digits; for this function, "1a8" will throw an ArgumentException, but "01a8" is valid and will be converted to a byte array.
    ///
    /// NOTE: This is different from Encoding.UTF8.GetBytes(), which interprets the string as UTF-8 characters.
    let stringAsByteArray (s: ReadOnlySpan<char>) =
        if s.Length % 2 <> 0 then
            raise (ArgumentException("The hexadecimal string must have an even number of digits.", nameof s))

        let byteArrayLength = int32 (s.Length / 2)
        let bytes = Array.zeroCreate byteArrayLength

        for index in [ 0..byteArrayLength ] do
            let byteValue = s.Slice(index * 2, 2)
            bytes[index] <- Byte.Parse(byteValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture)

        bytes

    /// Serializes any value to JSON using Grace's default JsonSerializerOptions, and then converts the JSON string to a byte array.
    let toByteArray<'T> (value: 'T) =
        let json = serialize value
        Encoding.UTF8.GetBytes json

    /// Deserializes a byte array to a value of type 'T' using Grace's default JsonSerializerOptions.
    let fromByteArray<'T> (bytes: ReadOnlySpan<byte>) =
        let json = Encoding.UTF8.GetString(bytes)
        deserialize<'T> json

    /// The universal Grace exception response type
    type ExceptionResponse =
        { ``exception``: string
          innerException: string }

        override this.ToString() =
            match this.innerException with
            | null -> $"Exception: {this.``exception``}{Environment.NewLine}{Environment.NewLine}"
            | innerEx -> $"Exception: {this.``exception``}{Environment.NewLine}{Environment.NewLine}Inner exception: {this.innerException}{Environment.NewLine}"

        /// Creates an ExceptionResponse instance from an Exception-based instance.
        static member Create(ex: Exception) =
            //#if DEBUG
            let exceptionMessage (ex: Exception) =
                $"Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}Stack trace:{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}"

            match ex.InnerException with
            | null -> { ``exception`` = exceptionMessage ex; innerException = "null" }
            | innerEx -> { ``exception`` = exceptionMessage ex; innerException = exceptionMessage ex.InnerException }
    //#else
    //        {|message = $"Internal server error, and, yes, it's been logged. The correlationId is in the X-Correlation-Id header."|}
    //#endif

    let flattenValueTask (valueTask: ValueTask<ValueTask<'T>>) =
        if valueTask.IsCompleted then
            valueTask.Result
        else
            valueTask.GetAwaiter().GetResult()

    /// Calls Task.FromResult<'T>() with the provided value.
    let returnTask<'T> value = Task.FromResult<'T>(value)

    /// Calls ValueTask.FromResult<'T>() with the provided value.
    let returnValueTask<'T> value = ValueTask.FromResult<'T>(value)

    /// Monadic bind for the nested monad Task<Result<'T, 'TError>>.
    let bindTaskResult (result: Task<Result<'T, 'TError>>) (f: 'T -> Task<Result<'U, 'TError>>) =
        (task {
            match! result with
            | Ok returnValue -> return (f returnValue)
            | Error error -> return Error error |> returnTask
        })
            .Unwrap()

    /// Custom monadic bind operator for the nested monad Task<Result<'T, 'TError>>.
    let inline (>>=!) (result: Task<Result<'T, 'TError>>) (f: 'T -> Task<Result<'U, 'TError>>) = bindTaskResult result f

    //let inline (>>=!) (result: ValueTask<Result<'T, 'TError>>) (f: 'T -> ValueTask<Result<'U, 'TError>>) =
    //    bindTaskResult result f

    /// Checks if a string begins with a path separator character.
    let pathContainsSeparator (path: string) =
        path.Contains(Path.DirectorySeparatorChar)
        || path.Contains(Path.AltDirectorySeparatorChar)

    /// Returns the number of segments in a given path.
    ///
    /// Examples:
    ///
    /// "foo/bar/demo.js" -> 3
    ///
    /// "topLevelFile.js" -> 1
    ///
    /// "." (i.e. root directory) -> 0
    let countSegments (path: string) =
        if path.Contains(Path.DirectorySeparatorChar) then
            path.Split(Path.DirectorySeparatorChar).Length
        elif path.Contains(Path.AltDirectorySeparatorChar) then
            path.Split(Path.AltDirectorySeparatorChar).Length
        elif path = Constants.RootDirectoryPath then
            0
        else
            1

    /// Returns the parent directory path of a given path, or None if this is the root directory of the repository.
    let getParentPath (path: string) =
        if path = Constants.RootDirectoryPath then
            None
        else
            let lastIndex = path.LastIndexOfAny([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])

            if lastIndex = -1 then
                Some Constants.RootDirectoryPath
            else
                Some(path.Substring(0, lastIndex))

    /// Gets a value for the Content-Type HTTP header for storing a file.
    ///
    /// If the file extension is found in the MimeTypes package, we'll use the content type from there.
    /// If it's not, and the file is binary, we'll use "application/octet-stream", otherwise we'll use "application/text".
    let getContentType (fileInfo: FileInfo) isBinary =
        let mutable mimeType = String.Empty

        if MimeTypes.MimeTypeMap.TryGetMimeType(fileInfo.Name, &mimeType) then mimeType
        elif isBinary then "application/octet-stream"
        else "application/text"

    /// Creates a Span<`T> on the stack to minimize heap usage and GC. This is an F# implementation of the C# keyword `stackalloc`.
    /// This should be used for smaller allocations, as the stack has ~1MB size.
    // Borrowed with appreciation from https://bartoszsypytkowski.com/writing-high-performance-f-code/.
    let inline stackalloc<'a when 'a: unmanaged> (length: int) : Span<'a> =
        let p = NativePtr.stackalloc<'a> length |> NativePtr.toVoidPtr
        Span<'a>(p, length)

    let propertyLookupByType = ConcurrentDictionary<Type, PropertyInfo array>()

    /// Creates a dictionary from the property names and values of a set of parameters.
    let getParametersAsDictionary<'T> (obj: 'T) =
        let mutable properties = Array.Empty<PropertyInfo>()

        if not <| propertyLookupByType.TryGetValue(typeof<'T>, &properties) then
            properties <- typeof<'T>.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
            propertyLookupByType.TryAdd(typeof<'T>, properties) |> ignore

        let dictionary = Dictionary<string, obj>()

        for prop in properties do
            let value = prop.GetValue(obj)

            dictionary[$"parameter:{prop.Name}"] <- value

        dictionary :> IReadOnlyDictionary<string, obj>

    /// This construct is equivalent to using IHttpClientFactory in the ASP.NET Dependency Injection container, for code (like this) that isn't using GenericHost.
    ///
    /// See https://docs.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-8.0#alternatives-to-ihttpclientfactory for more information.
    let socketsHttpHandler =
        new SocketsHttpHandler(
            AllowAutoRedirect = true, // We expect to use Traffic Manager or equivalents, so there will be redirects.
            MaxAutomaticRedirections = 6, // Not sure of the exact right number, but definitely want a limit here.
            SslOptions =
                SslClientAuthenticationOptions(
                    EnabledSslProtocols =
                        Security.Authentication.SslProtocols.Tls12
                        + Security.Authentication.SslProtocols.Tls13
                ),
            AutomaticDecompression = DecompressionMethods.All, // We'll store blobs using GZip, and we'll enable Brotli on the server
            EnableMultipleHttp2Connections = true, // I doubt this will ever happen, but don't mind making it possible
            PooledConnectionLifetime = TimeSpan.FromMinutes(2.0), // Default is 2m
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2.0) // Default is 2m
        )

    /// Gets an HttpClient instance from an enhanced, custom HttpClientFactory.
    let getHttpClient (correlationId: string) =
        let traceIdBytes = stackalloc<byte> 16
        let parentIdBytes = stackalloc<byte> 8
        Random.Shared.NextBytes(traceIdBytes)
        Random.Shared.NextBytes(parentIdBytes)
        let traceId = byteArrayToString (traceIdBytes)
        let parentId = byteArrayToString (parentIdBytes)

        let httpClient = new HttpClient(handler = socketsHttpHandler, disposeHandler = false)

        httpClient.DefaultRequestVersion <- HttpVersion.Version20 // We'll aggressively move to Version30 as soon as we can.
        httpClient.DefaultRequestHeaders.Add(Constants.Traceparent, $"00-{traceId}-{parentId}-01")
        httpClient.DefaultRequestHeaders.Add(Constants.Tracestate, $"graceserver-{parentId}")
        httpClient.DefaultRequestHeaders.Add(Constants.CorrelationIdHeaderKey, $"{correlationId}")
        httpClient.DefaultRequestHeaders.Add(Constants.ServerApiVersionHeaderKey, "Edge")
        //httpClient.DefaultVersionPolicy <- HttpVersionPolicy.RequestVersionOrHigher
#if DEBUG
        httpClient.Timeout <- TimeSpan.FromSeconds(1800.0) // Keeps client commands open while debugging.
        //httpClient.Timeout <- TimeSpan.FromSeconds(7.5)  // Fast fail for normal testing.
#else
        httpClient.Timeout <- TimeSpan.FromSeconds(15.0) // Fast fail for testing network connectivity.
#endif
        httpClient

    /// Returns the object file name for a given relative path, including the SHA-256 hash.
    /// Example: foo.txt with a SHA-256 hash of "8e798...980c" -> "foo_8e798...980c.txt".
    let getObjectFileName (relativePath: string) (sha256Hash: string) =
        let file = FileInfo(relativePath)

        if file.Extension = String.Empty then
            $"{file.Name}_{sha256Hash}"
        else
            $"{file.Name.Replace(file.Extension, String.Empty)}_{sha256Hash}{file.Extension}"

    /// Gets the name of the machine or node, without the prefix of the container name.
    let getMachineName =
        let nameParts = Environment.MachineName.Split('-')
        // return the last two parts of the machine name with a hyphen in between
        if nameParts.Length > 1 then
            $"{nameParts.[nameParts.Length - 2]}-{nameParts.[nameParts.Length - 1]}"
        else
            Environment.MachineName
