namespace Grace.Shared

open Grace.Shared.Resources
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

#nowarn "9"

module Combinators =
    let either okFunc errorFunc graceResult =
        match graceResult with
        | Result.Ok s -> okFunc s
        | Result.Error f -> errorFunc f

    let ok x = Result.Ok x
    let error x = Result.Error x

    let bind f =
        either f error

    let (>>=) x f =
        bind f x

    let (>=>) s1 s2 =
        s1 >> bind s2

module Utilities =
    /// Gets the current instant.
    let getCurrentInstant() = SystemClock.Instance.GetCurrentInstant()

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
    let getCurrentInstantExtended() = getCurrentInstant() |> formatInstantExtended

    /// Formats an instant as a string in General format.
    ///
    /// Example: "2019-06-15T13:45:30Z".
    let formatInstantGeneral (instant: Instant) = instant.ToString(InstantPattern.General.PatternText, CultureInfo.InvariantCulture)

    /// Gets the current instant as a string in General format.
    ///
    /// Example: "2019-06-15T13:45:30Z".
    let getCurrentInstantGeneral() = getCurrentInstant() |> formatInstantGeneral

    /// Converts an Instant to local time, and produces a string in short date/time format, using the CurrentUICulture.
    let instantToLocalTime (instant: Instant) = instant.ToDateTimeUtc().ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)

    /// Gets the current instant in local time as a string in short date/time format, using the CurrentUICulture.
    let getCurrentInstantLocal() = getCurrentInstant() |> instantToLocalTime

    /// Logs the message to the console, with the current instant and thread ID.
    let logToConsole message = printfn $"{getCurrentInstantExtended()} {Environment.CurrentManagedThreadId:X2} {message}"

    /// Gets the first eight characters of a SHA256 hash.
    let getShortSha256Hash (sha256Hash: String) =
        if sha256Hash.Length >= 8 then
            sha256Hash.Substring(0, 8)
        else
            String.Empty

    /// Converts both the type name and case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Animal.Dog"
    let getDiscriminatedUnionFullName (x:'T) = 
        let discriminatedUnionType = typeof<'T>
        let (case, _ ) = FSharpValue.GetUnionFields(x, discriminatedUnionType)
        $"{discriminatedUnionType.Name}.{case.Name}"

    /// Converts just the case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Dog"
    let getDistributedUnionCaseName (x:'T) = 
        let discriminatedUnionType = typeof<'T>
        let (case, _ ) = FSharpValue.GetUnionFields(x, discriminatedUnionType)
        $"{case.Name}"

    /// Converts a string into the corresponding case of a discriminated union type.
    ///
    /// Example: discriminatedUnionFromString<Animal> "Dog" -> Animal.Dog
    let discriminatedUnionFromString<'T> (s:string) =
        match FSharpType.GetUnionCases typeof<'T> |> Array.filter (fun case -> String.Compare(case.Name, s, ignoreCase = true) = 0) with
        |[|case|] -> Some(FSharpValue.MakeUnion(case,[||]) :?> 'T)
        |_ -> None

    /// Gets the cases of a discriminated union as an array of strings.
    let listCases (T: Type) =
        FSharpType.GetUnionCases T |> Array.map (fun c -> c.Name)

    /// Gets the cases of discriminated union for serialization.
    let GetKnownTypes<'T>() = typeof<'T>.GetNestedTypes(BindingFlags.Public ||| BindingFlags.NonPublic) |> Array.filter FSharpType.IsUnion

    /// Serializes an object to JSON, using Grace's custom JsonSerializerOptions.
    let serialize<'T> item =
        JsonSerializer.Serialize<'T>(item, Constants.JsonSerializerOptions)

    /// Serializes an object to JSON and writes it to a stream, using Grace's custom JsonSerializerOptions.
    let serializeAsync<'T> stream item =
        task {
            return! JsonSerializer.SerializeAsync<'T>(stream, item, Constants.JsonSerializerOptions)
        }

    /// Deserializes a JSON string to a provided type, using Grace's custom JsonSerializerOptions.
    let deserialize<'T> (s: string) =
        JsonSerializer.Deserialize<'T>(s, Constants.JsonSerializerOptions)

    /// Deserializes a stream of JSON to a provided type, using Grace's custom JsonSerializerOptions.
    let deserializeAsync<'T> stream =
        task {
            return! JsonSerializer.DeserializeAsync<'T>(stream, Constants.JsonSerializerOptions)
        }

    /// Create JsonContent from the provided object, using Grace JsonSerializerOptions.
    let jsonContent<'T> item =
        JsonContent.Create(item, options = Constants.JsonSerializerOptions)

    /// Retrieves the localized version of a system resource string.
    ///
    /// Note: For now, it's hardcoded to return en_US. I'll fix this when we really implement localization.
    let getLocalizedString stringName = 
        en_US.getString stringName

    /// Returns true if Grace is running on a Windows machine.
    let runningOnWindows =
        match Environment.OSVersion.Platform with
        | PlatformID.Win32NT | PlatformID.Win32S | PlatformID.Win32Windows | PlatformID.WinCE -> true
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
    let normalizeFilePath (filePath: string) = filePath.Replace(@"\", "/")

    /// Switches "/" to "\" when we're running on Windows.
    let getNativeFilePath (filePath: string) =
        if runningOnWindows then
            filePath.Replace("/", @"\")
        else
            filePath

    /// Checks if a file is a binary file by scanning the first 8K for a 0x00 character; if it finds one, we assume the file is binary.
    ///
    /// This is the same algorithm used by Git.
    let isBinaryFile (stream: Stream) =
        task {
            let defaultBytesToCheck = 8 * 1024
            let nulChar = char(0)
            
            // If the file is smaller than 8K, we'll check the whole file.
            let bytesToCheck = if stream.Length > defaultBytesToCheck then defaultBytesToCheck else int(stream.Length)
            
            // Create a buffer to hold the part of the file we're going to check.
            let startingBytes = Array.zeroCreate<byte> bytesToCheck

            // Read the file into the buffer.
            let! bytesRead = stream.ReadAsync(startingBytes, 0, bytesToCheck)

            // Search for a 0x00 character.
            match startingBytes |> Array.tryFind (fun b -> char(b) = nulChar) with
                | Some nul -> return true
                | None -> return false
        }

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
            let relativeDirectoryPath = relativePathParts[0..^1] 
                                        |> Array.fold(fun (sb: StringBuilder) currentPart ->
                                            sb.Append($"{currentPart}/")) (StringBuilder(standardizedFilePath.Length))
            relativeDirectoryPath.Remove(relativeDirectoryPath.Length - 1, 1) |> ignore  // Remove trailing slash.
            //logToConsole $"relativeDirectoryPath.ToString(): {relativeDirectoryPath.ToString()}"
            (relativeDirectoryPath.ToString())

    /// Returns the directory of a file, relative to the root of the repository's working directory.
    let getLocalRelativeDirectory (filePath: string) rootDirectory =
        let standardizedFilePath = normalizeFilePath filePath
        let standardizedRootDirectory = normalizeFilePath rootDirectory
        //logToConsole $"In getRelativeDirectory: standardizedRootDirectory: {standardizedFilePath}; standardizedRootDirectory: {standardizedRootDirectory}."
        let originalFileRelativePath = 
            if String.IsNullOrEmpty(standardizedRootDirectory) then standardizedFilePath else Path.GetRelativePath(standardizedRootDirectory, standardizedFilePath)
        //logToConsole $"In getRelativeDirectory: originalFileRelativePath: {originalFileRelativePath}"
        let relativePathParts = originalFileRelativePath.Split(Path.DirectorySeparatorChar)
        //logToConsole $"In getRelativeDirectory: relativePathParts.Length: {relativePathParts.Length}; relativePathParts[0]: {relativePathParts[0]}"
        if relativePathParts.Length = 1 && relativePathParts[0] = Constants.RootDirectoryPath then
            Constants.RootDirectoryPath
        else
            let relativeDirectoryPath = relativePathParts
                                        |> Array.fold(fun (sb: StringBuilder) currentPart ->
                                            sb.Append($"{currentPart}{Path.DirectorySeparatorChar}")) (StringBuilder(originalFileRelativePath.Length))
            relativeDirectoryPath.Remove(relativeDirectoryPath.Length - 1, 1) |> ignore
            //logToConsole $"In getRelativeDirectory: relativeDirectoryPath.ToString(): {relativeDirectoryPath.ToString()}"
            (relativeDirectoryPath.ToString())

    /// Returns either the supplied correlationId, if not null, or a new Guid.
    let ensureNonEmptyCorrelationId (correlationId: string) =
        if not <| String.IsNullOrEmpty(correlationId) then
            correlationId
        else
            Guid.NewGuid().ToString()

    /// Formats a byte array as a string. For example, [0xab, 0x15, 0x03] -> "ab1503"
    let byteArrayToString (array: Span<byte>) =
        let sb = StringBuilder(array.Length * 2)
        for b in array do
          sb.Append($"{b:x2}") |> ignore
        sb.ToString()

    /// Serializes any value to JSON using Grace's default JsonSerializerOptions, and then converts the JSON string to a byte array.
    let convertToByteArray<'T> (value: 'T) =
        let json = serialize value
        Encoding.UTF8.GetBytes json

    /// Converts a string of hexadecimal numbers to a byte array. For example, "ab1503" -> [0xab, 0x15, 0x03]
    ///
    /// The hex string must have an even number of digits; for this function, "1a8" will throw an ArgumentException, but "01a8" is valid and will be converted to a byte array.
    ///
    /// Note: This is different from Encoding.UTF8.GetBytes().
    let stringAsByteArray (s: ReadOnlySpan<char>) =
        if s.Length % 2 <> 0 then raise (ArgumentException("The hexadecimal string must have an even number of digits.", nameof(s)))

        let byteArrayLength = int32 (s.Length / 2)
        let bytes = Array.zeroCreate byteArrayLength
        for index in [0..byteArrayLength] do
            let byteValue = s.Slice(index * 2, 2)
            bytes[index] <- Byte.Parse(byteValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        bytes

    /// Universal Grace exception reponse type
    type ExceptionResponse = 
        {
            ``exception``: string
            innerException: string
        }
        override this.ToString() = (serialize this).Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace(@"\r\n", Environment.NewLine)

    /// Converts an Exception-based instance into an ExceptionResponse instance.
    let createExceptionResponse (ex: Exception): ExceptionResponse =
//#if DEBUG
        let stackTrace (ex: Exception) = 
            if not <| String.IsNullOrEmpty(ex.StackTrace) then
                //ex.StackTrace.Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace("\r\n", Environment.NewLine)
                serialize ex.StackTrace //.Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace(@"\r\n", Environment.NewLine)
            else
                String.Empty
        let exceptionMessage (ex: Exception) = $"Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}Stack trace:{Environment.NewLine}{stackTrace ex}{Environment.NewLine}"
        match ex.InnerException with
        | null -> {``exception`` = exceptionMessage ex; innerException = "null"}
        | innerEx -> {``exception`` = exceptionMessage ex; innerException = exceptionMessage ex.InnerException}
        //match ex.InnerException with
        //| null -> {``exception`` = serialize ex; innerException = String.Empty}
        //| innerEx -> {``exception`` = serialize ex; innerException = serialize innerEx}
//#else
//        {|message = $"Internal server error, and, yes, it's been logged. The correlationId is in the X-Correlation-Id header."|}
//#endif

    let flattenValueTask (valueTask: ValueTask<ValueTask<'T>>) =
        if valueTask.IsCompleted then
            valueTask.Result
        else
            valueTask.GetAwaiter().GetResult()

    /// Alias for calling Task.FromResult() with the provided value.
    let returnTask<'T> value = Task.FromResult<'T>(value)

    /// Alias for calling ValueTask.FromResult() with the provided value.
    let returnValueTask<'T> value = ValueTask.FromResult<'T>(value)

    /// Monadic bind for the nested monad Task<Result<'T, 'TError>>.
    //let bindTaskResult (result: ValueTask<Result<'T, 'TError>>) (f: 'T -> ValueTask<Result<'U, 'TError>>) =
    //    (task {
    //        match! result with
    //        | Ok returnValue -> return (f returnValue)
    //        | Error error -> return Error error |> returnValueTask
    //    }) |> ValueTask<ValueTask<Result<'U, 'TError>>> |> flattenValueTask
    let bindTaskResult (result: Task<Result<'T, 'TError>>) (f: 'T -> Task<Result<'U, 'TError>>) =
        (task {
            match! result with
            | Ok returnValue -> return (f returnValue)
            | Error error -> return Error error |> returnTask
        }).Unwrap()

    /// Custom monadic bind operator for the nested monad Task<Result<'T, 'TError>>.
    let inline (>>=!) (result: Task<Result<'T, 'TError>>) (f: 'T -> Task<Result<'U, 'TError>>) =
        bindTaskResult result f

    //let inline (>>=!) (result: ValueTask<Result<'T, 'TError>>) (f: 'T -> ValueTask<Result<'U, 'TError>>) =
    //    bindTaskResult result f

    /// Checks if a string begins with a path separator character.
    let pathContainsSeparator (path: string) =
        path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar)

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
                Some (path.Substring(0, lastIndex))

    /// Gets a value for the Content-Type HTTP header for storing a file.
    /// 
    /// If the file extension is found in the MimeTypes package, we'll use the content type from there.
    /// If it's not, and the file is binary, we'll use "application/octet-stream", otherwise we'll use "application/text".   
    let getContentType (fileInfo: FileInfo) isBinary =
        let mutable mimeType = String.Empty
        if MimeTypes.MimeTypeMap.TryGetMimeType(fileInfo.Name, &mimeType) then
            mimeType
        elif
            isBinary then "application/octet-stream"
        else 
            "application/text"

    /// Creates a Span<`T> on the stack to minimize heap usage and GC. This is an F# implementation of the C# keyword `stackalloc`.
    /// This should be used for smaller allocations, as the stack has ~1MB size.
    // Borrowed from https://bartoszsypytkowski.com/writing-high-performance-f-code/.
    let inline stackalloc<'a when 'a: unmanaged> (length: int): Span<'a> =
      let p = NativePtr.stackalloc<'a> length |> NativePtr.toVoidPtr
      Span<'a>(p, length)

    /// Creates a dictionary from the properties of an object.
    let getPropertiesAsDictionary<'T> (obj: 'T) =
        let properties = typeof<'T>.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
        let dict = Dictionary<string, string>()
        for prop in properties do
            let value = prop.GetValue(obj)
            let valueString = match value with
                              | null -> "null"
                              | :? string as s -> s
                              | _ -> value.ToString()
            dict.Add(prop.Name, valueString)
        dict

    /// This construct is equivalent to using IHttpClientFactory in the ASP.NET Dependency Injection container, for code (like this) that isn't using GenericHost.
    ///
    /// See https://docs.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-8.0#alternatives-to-ihttpclientfactory for more information.
    let socketsHttpHandler = new SocketsHttpHandler(
        AllowAutoRedirect = true,                               // We expect to use Traffic Manager or equivalents, so there will be redirects.
        MaxAutomaticRedirections = 6,                           // Not sure of the exact right number, but definitely want a limit here.
        SslOptions = SslClientAuthenticationOptions(EnabledSslProtocols = Security.Authentication.SslProtocols.Tls12 + Security.Authentication.SslProtocols.Tls13),
        AutomaticDecompression = DecompressionMethods.All,      // We'll store blobs using GZip, and we'll enable Brotli on the server
        EnableMultipleHttp2Connections = true,                  // I doubt this will ever happen, but don't mind making it possible
        PooledConnectionLifetime = TimeSpan.FromMinutes(2.0),   // Default is 2m
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2.0) // Default is 2m
    )

    /// Gets an HttpClient instance from an enhanced, custom HttpClientFactory.
    let getHttpClient (correlationId: string) =
        let traceIdBytes = stackalloc<byte> 16
        let parentIdBytes = stackalloc<byte> 8
        Random.Shared.NextBytes(traceIdBytes)
        Random.Shared.NextBytes(parentIdBytes)
        let traceId = byteArrayToString(traceIdBytes)
        let parentId = byteArrayToString(parentIdBytes)

        let httpClient = new HttpClient(handler = socketsHttpHandler, disposeHandler = false)
        httpClient.DefaultRequestVersion <- HttpVersion.Version20   // We'll aggressively move to Version30 as soon as we can.
        httpClient.DefaultRequestHeaders.Add(Constants.Traceparent, $"00-{traceId}-{parentId}-01")
        httpClient.DefaultRequestHeaders.Add(Constants.Tracestate, $"graceserver-{parentId}")
        httpClient.DefaultRequestHeaders.Add(Constants.CorrelationIdHeaderKey, $"{correlationId}")
        httpClient.DefaultRequestHeaders.Add(Constants.ServerApiVersionHeaderKey, $"{Constants.ServerApiVersions.Edge}")
        //httpClient.DefaultVersionPolicy <- HttpVersionPolicy.RequestVersionOrHigher
#if DEBUG
        httpClient.Timeout <- TimeSpan.FromSeconds(1800.0)  // Keeps client commands open while debugging.
        //httpClient.Timeout <- TimeSpan.FromSeconds(7.5)  // Fast fail for normal testing.
#else
        httpClient.Timeout <- TimeSpan.FromSeconds(15.0)  // Fast fail for testing network connectivity.
#endif
        httpClient

    /// Returns the object file name for a given relative path, including the SHA-256 hash.
    /// Example: foo.txt with a SHA-256 hash of "8e798...980c" -> "foo_8e798...980c.txt".
    let getObjectFileName (relativePath: string) (sha256Hash: string) = 
        let file = FileInfo(relativePath)
        $"{file.Name.Replace(file.Extension, String.Empty)}_{sha256Hash}{file.Extension}"
