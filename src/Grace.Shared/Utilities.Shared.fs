namespace Grace.Shared

open Grace.Shared.Resources
open Microsoft.FSharp.Reflection
open NodaTime
open NodaTime.Text
open System
open System.Globalization
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading.Tasks

module Utilities =
    let instantToLocalTime (instant: Instant) = instant.ToDateTimeUtc().ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)
    let getCurrentInstant() = SystemClock.Instance.GetCurrentInstant()
    let getCurrentInstantExtended() = getCurrentInstant().ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)
    let getCurrentInstantGeneral() = getCurrentInstant().ToString(InstantPattern.General.PatternText, CultureInfo.InvariantCulture)
    let getCurrentInstantLocal() = getCurrentInstant() |> instantToLocalTime
    let logToConsole message = printfn $"{getCurrentInstantExtended()} {message}"
     
    let getShortenedSha256Hash (sha256Hash: String) =
        if sha256Hash.Length >= 8 then
            sha256Hash.Substring(0, 8)
        else
            String.Empty

    /// Converts the type name and case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Animal.Dog"
    let discriminatedUnionFullNameToString (x:'T) = 
        let discriminatedUnionType = typeof<'T>
        let (case, _ ) = FSharpValue.GetUnionFields(x, discriminatedUnionType)
        $"{discriminatedUnionType.Name}.{case.Name}"

    /// Converts the case name of a discriminated union to a string.
    ///
    /// Example: Animal.Dog -> "Dog"
    let discriminatedUnionCaseNameToString (x:'T) = 
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

    /// Gets the cases of a discriminated union as an array of s
    let listCases (T: Type) =
        FSharpType.GetUnionCases T |> Array.map (fun c -> c.Name)

    let GetKnownTypes<'T>() = typeof<'T>.GetNestedTypes(BindingFlags.Public ||| BindingFlags.NonPublic) |> Array.filter FSharpType.IsUnion

    /// <summary>
    /// Helper method to serialize the given item to JSON, using the Grace JsonSerializerOptions.
    /// </summary>
    /// <param name="item">Any item to serialize to JSON.</param>
    let serialize<'T> (item: 'T) =
        JsonSerializer.Serialize<'T>(item, options = Constants.JsonSerializerOptions)

    /// <summary>
    /// Helper method to deserialize a given JSON string to a generic type, using the Grace JsonSerializerOptions.
    /// </summary>
    /// <param name="item">Any item to serialize to JSON.</param>
    let deserialize<'T> (s: string) =
        JsonSerializer.Deserialize<'T>(s, options = Constants.JsonSerializerOptions)

    /// <summary>
    /// Retrieves the localized version of a system resource string.
    ///
    /// Note: For now, it's hardcoded to return en_US. I'll fix this when we really implement localization.
    /// </summary>
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

    /// Returns the given path, replacing any Windows-style backslash characters (\) with forward-slash (/).
    let normalizeFilePath (filePath: string) = filePath.Replace(@"\", "/")

    /// Switches "/" to "\" when we're running on Windows.
    let getNativeFilePath (filePath: string) =
        if runningOnWindows then
            filePath.Replace("/", @"\")
        else
            filePath

    /// Checks if a file is a binary file, by scanning the first 8K for a 0x00 character; if it finds one, we assume the file is binary.
    let isBinaryFile (stream: Stream) =
        task {
            let defaultBytesToCheck = 8 * 1024
            let nulChar = char(0)
            
            let bytesToCheck = if stream.Length > defaultBytesToCheck then defaultBytesToCheck else int(stream.Length)
            
            let startingBytes = Array.zeroCreate<byte> bytesToCheck
            let! bytesRead = stream.ReadAsync(startingBytes, 0, bytesToCheck)

            match startingBytes |> Seq.tryFind (fun b -> char(b) = nulChar) with
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

    /// <summary>
    /// Returns either the supplied correlationId, if not null, or a new Guid.
    /// </summary>
    let ensureNonEmptyCorrelationId (correlationId: string) =
        if not <| String.IsNullOrEmpty(correlationId) then
            correlationId
        else
            Guid.NewGuid().ToString()

    /// <summary>
    /// Formats a byte array as a string. For example, [0xab, 0x15, 0x03] -> "ab1503"
    /// </summary>
    /// <param name="array">An array of bytes to convert to a string.</param>
    let byteArrayAsString (array: byte[]) =
        let sb = StringBuilder(array.Length * 2)
        for b in array do
          sb.Append($"{b:x2}") |> ignore
        sb.ToString()

    /// <summary>
    /// Converts a string to a byte array. For example, "ab1503" -> [0xab, 0x15, 0x03]
    /// Note: This is different from Encoding.UTF8.GetBytes().
    /// </summary>
    /// <param name="array">An array of bytes to convert to a string.</param>
    let stringAsByteArray (s: string) =
        if s.Length / 2 % 2 <> 0 then raise (ArgumentException("The hexadecimal string must have an even number of digits.", nameof(s)))

        let byteArrayLength = int32 (s.Length / 2)
        let bytes = Array.zeroCreate byteArrayLength
        let span = s.AsSpan()
        for index in [0..byteArrayLength] do
            let byteValue = s.Substring(index * 2, 2);
            bytes[index] <- Byte.Parse(byteValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        bytes

    /// Universal Grace exception reponse type
    type ExceptionResponse = 
        {
            ``exception``: string
            innerException: string
        }
        override this.ToString() = JsonSerializer.Serialize(this, Constants.JsonSerializerOptions).Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace(@"\r\n", Environment.NewLine)

    /// Converts an Exception-based instance into an ExceptionResponse instance.
    let createExceptionResponse (ex: Exception): ExceptionResponse =
//#if DEBUG
        let stackTrace (ex: Exception) = 
            if not <| String.IsNullOrEmpty(ex.StackTrace) then
                //ex.StackTrace.Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace("\r\n", Environment.NewLine)
                JsonSerializer.Serialize(ex.StackTrace, Constants.JsonSerializerOptions) //.Replace("\\\\\\\\", @"\").Replace("\\\\", @"\").Replace(@"\r\n", Environment.NewLine)
            else
                String.Empty
        let exceptionMessage (ex: Exception) = $"Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}Stack trace:{Environment.NewLine}{stackTrace ex}{Environment.NewLine}"
        match ex.InnerException with
        | null -> {``exception`` = exceptionMessage ex; innerException = "null"}
        | innerEx -> {``exception`` = exceptionMessage ex; innerException = exceptionMessage ex.InnerException}
        //match ex.InnerException with
        //| null -> {``exception`` = JsonSerializer.Serialize(ex, Constants.JsonSerializerOptions); innerException = String.Empty}
        //| innerEx -> {``exception`` = JsonSerializer.Serialize(ex, Constants.JsonSerializerOptions); innerException = JsonSerializer.Serialize(innerEx, Constants.JsonSerializerOptions)}
//#else
//        {|message = $"Internal server error, and, yes, it's been logged. The correlationId is in the X-Correlation-Id header."|}
//#endif

    /// Calls Task.FromResult() with the provided value.
    let returnTask<'T> (value: 'T) =
        Task.FromResult<'T>(value)

    /// Computes text for the time between two instants
    let elapsedBetween (instant1: Instant) (instant2: Instant) =
        let since = if instant2 > instant1 then instant2.Minus(instant1) else instant1.Minus(instant2)
        if since.TotalSeconds < 2 then $"1 second"
        elif since.TotalSeconds < 60 then $"{Math.Floor(since.TotalSeconds):F0} seconds"
        elif since.TotalMinutes < 2 then $"1 minute"
        elif since.TotalMinutes < 60 then $"{Math.Floor(since.TotalMinutes):F0} minutes"
        elif since.TotalHours < 2 then $"1 hour"
        elif since.TotalHours < 24 then $"{Math.Floor(since.TotalHours):F0} hours"
        elif since.TotalDays < 2 then $"1 day"
        elif since.TotalDays < 30 then $"{Math.Floor(since.TotalDays):F0} days"
        elif since.TotalDays < 60 then $"1 month"
        elif since.TotalDays < 365.25 then $"{Math.Floor(since.TotalDays / 30.0):F0} months"
        elif since.TotalDays < 730.5 then $"1 year"
        else $"{Math.Floor(since.TotalDays / 365.25):F0} years"

    /// Computes text for how long ago an instant was.
    let ago (instant: Instant) = $"{elapsedBetween (getCurrentInstant()) instant} ago"

    /// Computes text for how far apart two instants are.
    let apart (instant1: Instant) (instant2: Instant) = $"{elapsedBetween instant1 instant2} apart"
    
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
