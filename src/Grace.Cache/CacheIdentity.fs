namespace Grace.Cache

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// Represents the canonical public P-256 half produced for one protected local enrollment attempt.
[<CLIMutable>]
type internal CacheIdentityPublicKey = { PublicKeyX: string; PublicKeyY: string }

/// Represents the server-accepted facts persisted with one local static cache identity.
[<CLIMutable>]
type internal CacheAcceptedRegistration = { CacheId: Guid; Endpoint: string; PublicKey: CacheIdentityPublicKey }

/// Represents the private on-disk registration payload that binds accepted server facts to the staged key fingerprint.
[<CLIMutable>]
type private CacheReadyRegistration = { Configuration: CacheAcceptedRegistration; PublicKeyFingerprint: string }

/// Represents the opaque local identity state available to Cache runtime callers.
[<RequireQualifiedAccess>]
type internal CacheIdentityInspection =
    | Missing
    | AttemptPresent
    | Ready
    | Invalid
    | Inaccessible

/// Represents the only failures exposed by the protected local identity boundary.
[<RequireQualifiedAccess>]
type internal CacheIdentityError =
    | UnsupportedPlatform
    | StateUnavailable

/// Owns Linux-only protected static-key staging, ready publication, and opaque local inspection.
module internal CacheIdentity =

    [<Literal>]
    let private AttemptDirectoryName = "attempt"

    [<Literal>]
    let private ReadyDirectoryName = "ready"

    [<Literal>]
    let private IdentityFileName = "identity.pk8"

    [<Literal>]
    let private RegistrationFileName = "registration.json"

    let private directoryMode =
        UnixFileMode.UserRead
        ||| UnixFileMode.UserWrite
        ||| UnixFileMode.UserExecute

    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    /// Encodes fixed-width P-256 coordinate or digest bytes using canonical base64url.
    let private base64Url (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Produces the private configuration fingerprint from the exact canonical P-256 coordinate concatenation.
    let private fingerprint (x: byte array) (y: byte array) =
        Array.concat [ x; y ]
        |> SHA256.HashData
        |> base64Url

    /// Builds the fixed child path without exposing it through a result or error.
    let private child root name = Path.Combine(root, name)

    /// Classifies a directory probe without leaking filesystem exception details.
    let private probeDirectory (path: string) =
        try
            Directory.GetFileSystemEntries(path) |> ignore
            Ok true
        with
        | :? DirectoryNotFoundException -> Ok false
        | :? UnauthorizedAccessException -> Error CacheIdentityInspection.Inaccessible
        | _ -> Error CacheIdentityInspection.Invalid

    /// Checks exact owner-only Linux modes and preserves inaccessible as a separate opaque classification.
    let private inspectMode (path: string) (expected: UnixFileMode) =
        try
            if File.GetUnixFileMode(path) = expected then
                Ok()
            else
                Error CacheIdentityInspection.Invalid
        with
        | :? UnauthorizedAccessException -> Error CacheIdentityInspection.Inaccessible
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Error CacheIdentityInspection.Invalid
        | _ -> Error CacheIdentityInspection.Invalid

    /// Validates the caller-managed root before it can hold protected cache identity state.
    let private validateRoot root =
        if not (OperatingSystem.IsLinux()) then
            Error CacheIdentityError.UnsupportedPlatform
        elif String.IsNullOrWhiteSpace root then
            Error CacheIdentityError.StateUnavailable
        else
            match probeDirectory root with
            | Ok true ->
                match inspectMode root directoryMode with
                | Ok () -> Ok()
                | Error _ -> Error CacheIdentityError.StateUnavailable
            | _ -> Error CacheIdentityError.StateUnavailable

    /// Writes one owner-only file and flushes its bytes before any directory publication can occur.
    let private writePrivateFile path bytes =
        try
            use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            File.SetUnixFileMode(path, fileMode)
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush(true)

            inspectMode path fileMode
            |> Result.mapError (fun _ -> CacheIdentityError.StateUnavailable)
        with
        | _ -> Error CacheIdentityError.StateUnavailable

    /// Checks whether a value is canonical base64url for one required P-256 coordinate.
    let private tryCanonicalCoordinate value =
        if String.IsNullOrWhiteSpace value then
            None
        else
            try
                let padded =
                    value.Replace('-', '+').Replace('_', '/')
                    + String('=', (4 - value.Length % 4) % 4)

                let decoded = Convert.FromBase64String padded

                if decoded.Length = 32 && base64Url decoded = value then Some decoded else None
            with
            | _ -> None

    /// Validates accepted server facts before writing a private ready configuration.
    let private tryExpectedFingerprint configuration =
        if
            isNull (box configuration)
            || configuration.CacheId = Guid.Empty
            || String.IsNullOrWhiteSpace configuration.Endpoint
            || isNull (box configuration.PublicKey)
            || not
                (
                    Uri.TryCreate(configuration.Endpoint, UriKind.Absolute)
                    |> fst
                )
        then
            None
        else
            match tryCanonicalCoordinate configuration.PublicKey.PublicKeyX, tryCanonicalCoordinate configuration.PublicKey.PublicKeyY with
            | Some x, Some y -> Some(fingerprint x y)
            | _ -> None

    /// Generates one protected P-256 key below a new fixed attempt directory and returns its canonical public half only.
    let createAttempt root =
        match validateRoot root with
        | Error error -> Error error
        | Ok () ->
            let attempt = child root AttemptDirectoryName
            let ready = child root ReadyDirectoryName

            if
                Directory.Exists(attempt)
                || Directory.Exists(ready)
            then
                Error CacheIdentityError.StateUnavailable
            else
                try
                    Directory.CreateDirectory(attempt) |> ignore
                    File.SetUnixFileMode(attempt, directoryMode)

                    match inspectMode attempt directoryMode with
                    | Error _ -> Error CacheIdentityError.StateUnavailable
                    | Ok () ->
                        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                        let parameters = key.ExportParameters(false)

                        match parameters.Q.X, parameters.Q.Y with
                        | x, y when
                            not (isNull x)
                            && not (isNull y)
                            && x.Length = 32
                            && y.Length = 32
                            ->
                            match writePrivateFile (child attempt IdentityFileName) (key.ExportPkcs8PrivateKey()) with
                            | Ok () -> Ok { PublicKeyX = base64Url x; PublicKeyY = base64Url y }
                            | Error error -> Error error
                        | _ -> Error CacheIdentityError.StateUnavailable
                with
                | _ -> Error CacheIdentityError.StateUnavailable

    /// Publishes an accepted registration only after its key fingerprint matches the staged private key and all modes are protected.
    let commitReady root configuration =
        match validateRoot root, tryExpectedFingerprint configuration with
        | Error error, _ -> Error error
        | _, None -> Error CacheIdentityError.StateUnavailable
        | Ok (), Some expectedFingerprint ->
            let attempt = child root AttemptDirectoryName
            let ready = child root ReadyDirectoryName
            let identityPath = child attempt IdentityFileName
            let registrationPath = child attempt RegistrationFileName

            if
                Directory.Exists(ready)
                || not (Directory.Exists(attempt))
            then
                Error CacheIdentityError.StateUnavailable
            else
                try
                    match inspectMode attempt directoryMode, inspectMode identityPath fileMode with
                    | Ok (), Ok () ->
                        let privateBytes = File.ReadAllBytes(identityPath)
                        use key = ECDsa.Create()
                        let mutable bytesRead = 0
                        key.ImportPkcs8PrivateKey(privateBytes, &bytesRead)
                        let parameters = key.ExportParameters(false)

                        match parameters.Q.X, parameters.Q.Y with
                        | x, y when
                            not (isNull x)
                            && not (isNull y)
                            && fingerprint x y = expectedFingerprint
                            ->
                            let registrationBytes =
                                JsonSerializer.SerializeToUtf8Bytes({ Configuration = configuration; PublicKeyFingerprint = expectedFingerprint })

                            match writePrivateFile registrationPath registrationBytes with
                            | Error error -> Error error
                            | Ok () ->
                                match inspectMode root directoryMode,
                                      inspectMode attempt directoryMode,
                                      inspectMode identityPath fileMode,
                                      inspectMode registrationPath fileMode
                                    with
                                | Ok (), Ok (), Ok (), Ok () ->
                                    Directory.Move(attempt, ready)

                                    inspectMode ready directoryMode
                                    |> Result.mapError (fun _ -> CacheIdentityError.StateUnavailable)
                                | _ -> Error CacheIdentityError.StateUnavailable
                        | _ -> Error CacheIdentityError.StateUnavailable
                    | _ -> Error CacheIdentityError.StateUnavailable
                with
                | _ -> Error CacheIdentityError.StateUnavailable

    /// Reads one ready configuration without emitting raw paths, filesystem errors, keys, or fingerprints.
    let private inspectReady ready =
        let identityPath = child ready IdentityFileName
        let registrationPath = child ready RegistrationFileName

        match inspectMode ready directoryMode, inspectMode identityPath fileMode, inspectMode registrationPath fileMode with
        | Error CacheIdentityInspection.Inaccessible, _, _
        | _, Error CacheIdentityInspection.Inaccessible, _
        | _, _, Error CacheIdentityInspection.Inaccessible -> CacheIdentityInspection.Inaccessible
        | Error _, _, _
        | _, Error _, _
        | _, _, Error _ -> CacheIdentityInspection.Invalid
        | Ok (), Ok (), Ok () ->
            try
                let registration = JsonSerializer.Deserialize<CacheReadyRegistration>(File.ReadAllBytes(registrationPath))

                match if isNull (box registration) then
                          None
                      else
                          tryExpectedFingerprint registration.Configuration
                    with
                | None -> CacheIdentityInspection.Invalid
                | Some expectedFingerprint ->
                    use key = ECDsa.Create()
                    let mutable bytesRead = 0
                    key.ImportPkcs8PrivateKey(File.ReadAllBytes(identityPath), &bytesRead)
                    let parameters = key.ExportParameters(false)

                    match parameters.Q.X, parameters.Q.Y with
                    | x, y when
                        not (isNull x)
                        && not (isNull y)
                        && fingerprint x y = expectedFingerprint
                        && String.Equals(registration.PublicKeyFingerprint, expectedFingerprint, StringComparison.Ordinal)
                        ->
                        CacheIdentityInspection.Ready
                    | _ -> CacheIdentityInspection.Invalid
            with
            | :? UnauthorizedAccessException -> CacheIdentityInspection.Inaccessible
            | _ -> CacheIdentityInspection.Invalid

    /// Inspects fixed local identity markers without mutating, repairing, deleting, or exposing protected state details.
    let inspect root =
        if not (OperatingSystem.IsLinux()) then
            Error CacheIdentityError.UnsupportedPlatform
        else
            match probeDirectory root with
            | Error CacheIdentityInspection.Inaccessible -> Ok CacheIdentityInspection.Inaccessible
            | Error _ -> Ok CacheIdentityInspection.Invalid
            | Ok false -> Ok CacheIdentityInspection.Missing
            | Ok true ->
                match inspectMode root directoryMode with
                | Error CacheIdentityInspection.Inaccessible -> Ok CacheIdentityInspection.Inaccessible
                | Error _ -> Ok CacheIdentityInspection.Invalid
                | Ok () ->
                    let attempt = child root AttemptDirectoryName
                    let ready = child root ReadyDirectoryName

                    match probeDirectory attempt, probeDirectory ready with
                    | Error CacheIdentityInspection.Inaccessible, _
                    | _, Error CacheIdentityInspection.Inaccessible -> Ok CacheIdentityInspection.Inaccessible
                    | Error _, _
                    | _, Error _ -> Ok CacheIdentityInspection.Invalid
                    | Ok false, Ok false -> Ok CacheIdentityInspection.Missing
                    | Ok true, Ok true -> Ok CacheIdentityInspection.Invalid
                    | Ok true, Ok false ->
                        match inspectMode attempt directoryMode, inspectMode (child attempt IdentityFileName) fileMode with
                        | Ok (), Ok () -> Ok CacheIdentityInspection.AttemptPresent
                        | Error CacheIdentityInspection.Inaccessible, _
                        | _, Error CacheIdentityInspection.Inaccessible -> Ok CacheIdentityInspection.Inaccessible
                        | _ -> Ok CacheIdentityInspection.Invalid
                    | Ok false, Ok true -> Ok(inspectReady ready)

    /// Best-effort cleanup for a failed caller operation; it intentionally has no cancellation token or failure result.
    let discardAttempt root =
        if
            OperatingSystem.IsLinux()
            && not (String.IsNullOrWhiteSpace root)
        then
            try
                let attempt = child root AttemptDirectoryName

                if Directory.Exists(attempt) then Directory.Delete(attempt, true)
            with
            | _ -> ()
