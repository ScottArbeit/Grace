namespace Grace.Cache

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// Represents the canonical public P-256 half produced for one protected local enrollment attempt.
[<CLIMutable>]
type internal CacheIdentityPublicKey = { PublicKeyX: string; PublicKeyY: string }

/// Identifies one exact repository assignment retained in the private ready configuration.
[<CLIMutable>]
type internal CacheAcceptedRepositoryScope = { OrganizationId: Guid; RepositoryId: Guid }

/// Represents the server-accepted facts persisted with one local static cache identity.
[<CLIMutable>]
type internal CacheAcceptedRegistration =
    {
        CacheId: Guid
        DisplayName: string
        BoundaryKind: string
        OwnerId: Guid
        OrganizationId: Guid option
        RepositoryScopes: CacheAcceptedRepositoryScope array
        Endpoint: string
        ProtocolVersion: string
        PublicKey: CacheIdentityPublicKey
    }

/// Stores one repository assignment through ordinary mutable properties so System.Text.Json has a stable private-file shape.
type private CacheReadyRepositoryScope() =
    member val OrganizationId = Guid.Empty with get, set
    member val RepositoryId = Guid.Empty with get, set

/// Stores all ready-registration facts through ordinary mutable properties so private F# records are never serialized as empty objects.
type private CacheReadyRegistration() =
    member val CacheId = Guid.Empty with get, set
    member val DisplayName = String.Empty with get, set
    member val BoundaryKind = String.Empty with get, set
    member val OwnerId = Guid.Empty with get, set
    member val HasOrganizationId = false with get, set
    member val OrganizationId = Guid.Empty with get, set
    member val RepositoryScopes: CacheReadyRepositoryScope array = Array.empty with get, set
    member val Endpoint = String.Empty with get, set
    member val ProtocolVersion = String.Empty with get, set
    member val PublicKeyX = String.Empty with get, set
    member val PublicKeyY = String.Empty with get, set
    member val PublicKeyFingerprint = String.Empty with get, set

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
        | :? IOException -> Error CacheIdentityInspection.Inaccessible
        | _ -> Error CacheIdentityInspection.Invalid

    /// Checks exact owner-only Linux modes and preserves inaccessible as a separate opaque classification.
    let private inspectMode (path: string) (expected: UnixFileMode) =
        try
            let actual = File.GetUnixFileMode(path)

            let requiredAccess =
                if expected = directoryMode then
                    UnixFileMode.UserRead ||| UnixFileMode.UserExecute
                else
                    UnixFileMode.UserRead

            if (actual &&& requiredAccess) <> requiredAccess then
                Error CacheIdentityInspection.Inaccessible
            elif actual = expected then
                Ok()
            else
                Error CacheIdentityInspection.Invalid
        with
        | :? UnauthorizedAccessException -> Error CacheIdentityInspection.Inaccessible
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Error CacheIdentityInspection.Invalid
        | :? IOException -> Error CacheIdentityInspection.Inaccessible
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

    /// Verifies the complete accepted registration facts required to bind ready state to one exact server enrollment.
    let private hasCompleteAcceptedRegistration configuration =
        let hasValidRepositoryScopes (repositoryScopes: CacheAcceptedRepositoryScope array) =
            if isNull repositoryScopes
               || repositoryScopes.Length = 0 then
                false
            else
                let allScopesAreComplete =
                    repositoryScopes
                    |> Array.forall (fun scope ->
                        not (isNull (box scope))
                        && scope.OrganizationId <> Guid.Empty
                        && scope.RepositoryId <> Guid.Empty)

                let hasNoDuplicateRepositories =
                    repositoryScopes
                    |> Array.map (fun scope -> scope.RepositoryId)
                    |> Array.distinct
                    |> Array.length
                    |> fun distinctCount -> distinctCount = repositoryScopes.Length

                allScopesAreComplete && hasNoDuplicateRepositories

        not (isNull (box configuration))
        && not (String.IsNullOrWhiteSpace configuration.DisplayName)
        && configuration.OwnerId <> Guid.Empty
        && not (String.IsNullOrWhiteSpace configuration.ProtocolVersion)
        && hasValidRepositoryScopes configuration.RepositoryScopes
        && (match configuration.BoundaryKind, configuration.OrganizationId with
            | "Owner", None -> true
            | "Organization", Some organizationId when organizationId <> Guid.Empty -> true
            | _ -> false)

    /// Copies one complete accepted registration into the explicit private-file representation.
    let private toReadyRegistration configuration expectedFingerprint =
        let registration = CacheReadyRegistration()
        registration.CacheId <- configuration.CacheId
        registration.DisplayName <- configuration.DisplayName
        registration.BoundaryKind <- configuration.BoundaryKind
        registration.OwnerId <- configuration.OwnerId

        match configuration.OrganizationId with
        | Some organizationId ->
            registration.HasOrganizationId <- true
            registration.OrganizationId <- organizationId
        | None -> ()

        registration.RepositoryScopes <-
            configuration.RepositoryScopes
            |> Array.map (fun scope ->
                let persistedScope = CacheReadyRepositoryScope()
                persistedScope.OrganizationId <- scope.OrganizationId
                persistedScope.RepositoryId <- scope.RepositoryId
                persistedScope)

        registration.Endpoint <- configuration.Endpoint
        registration.ProtocolVersion <- configuration.ProtocolVersion
        registration.PublicKeyX <- configuration.PublicKey.PublicKeyX
        registration.PublicKeyY <- configuration.PublicKey.PublicKeyY
        registration.PublicKeyFingerprint <- expectedFingerprint
        registration

    /// Returns the fingerprint only when every private-file field has the required stable registration shape.
    let private tryReadyRegistrationFingerprint (registration: CacheReadyRegistration) =
        let hasValidRepositoryScopes (repositoryScopes: CacheReadyRepositoryScope array) =
            if isNull repositoryScopes
               || repositoryScopes.Length = 0 then
                false
            else
                let allScopesAreComplete =
                    repositoryScopes
                    |> Array.forall (fun scope ->
                        not (obj.ReferenceEquals(scope, null))
                        && scope.OrganizationId <> Guid.Empty
                        && scope.RepositoryId <> Guid.Empty)

                let hasNoDuplicateRepositories =
                    repositoryScopes
                    |> Array.map (fun scope -> scope.RepositoryId)
                    |> Array.distinct
                    |> Array.length
                    |> fun distinctCount -> distinctCount = repositoryScopes.Length

                allScopesAreComplete && hasNoDuplicateRepositories

        let hasValidBoundary (readyRegistration: CacheReadyRegistration) =
            match readyRegistration.BoundaryKind, readyRegistration.HasOrganizationId, readyRegistration.OrganizationId with
            | "Owner", false, organizationId when organizationId = Guid.Empty -> true
            | "Organization", true, organizationId when organizationId <> Guid.Empty -> true
            | _ -> false

        if
            obj.ReferenceEquals(registration, null)
            || registration.CacheId = Guid.Empty
            || String.IsNullOrWhiteSpace registration.DisplayName
            || registration.OwnerId = Guid.Empty
            || String.IsNullOrWhiteSpace registration.Endpoint
            || String.IsNullOrWhiteSpace registration.ProtocolVersion
            || not (hasValidBoundary registration)
            || not (hasValidRepositoryScopes registration.RepositoryScopes)
            || not
                (
                    Uri.TryCreate(registration.Endpoint, UriKind.Absolute)
                    |> fst
                )
        then
            None
        else
            match tryCanonicalCoordinate registration.PublicKeyX, tryCanonicalCoordinate registration.PublicKeyY with
            | Some x, Some y ->
                let expectedFingerprint = fingerprint x y

                if String.Equals(registration.PublicKeyFingerprint, expectedFingerprint, StringComparison.Ordinal) then
                    Some expectedFingerprint
                else
                    None
            | _ -> None

    /// Checks a deserialize result preserves all accepted fields before publication can expose the ready directory.
    let private matchesAcceptedRegistration (configuration: CacheAcceptedRegistration) expectedFingerprint (registration: CacheReadyRegistration) =
        let sameRepositoryScopes =
            not (isNull configuration.RepositoryScopes)
            && not (isNull registration.RepositoryScopes)
            && configuration.RepositoryScopes.Length = registration.RepositoryScopes.Length
            && Array.forall2
                (fun (expected: CacheAcceptedRepositoryScope) (actual: CacheReadyRepositoryScope) ->
                    not (obj.ReferenceEquals(actual, null))
                    && expected.OrganizationId = actual.OrganizationId
                    && expected.RepositoryId = actual.RepositoryId)
                configuration.RepositoryScopes
                registration.RepositoryScopes

        registration.CacheId = configuration.CacheId
        && String.Equals(registration.DisplayName, configuration.DisplayName, StringComparison.Ordinal)
        && String.Equals(registration.BoundaryKind, configuration.BoundaryKind, StringComparison.Ordinal)
        && registration.OwnerId = configuration.OwnerId
        && (match configuration.OrganizationId with
            | Some organizationId ->
                registration.HasOrganizationId
                && registration.OrganizationId = organizationId
            | None ->
                not registration.HasOrganizationId
                && registration.OrganizationId = Guid.Empty)
        && sameRepositoryScopes
        && String.Equals(registration.Endpoint, configuration.Endpoint, StringComparison.Ordinal)
        && String.Equals(registration.ProtocolVersion, configuration.ProtocolVersion, StringComparison.Ordinal)
        && String.Equals(registration.PublicKeyX, configuration.PublicKey.PublicKeyX, StringComparison.Ordinal)
        && String.Equals(registration.PublicKeyY, configuration.PublicKey.PublicKeyY, StringComparison.Ordinal)
        && String.Equals(registration.PublicKeyFingerprint, expectedFingerprint, StringComparison.Ordinal)

    /// Serializes and rehydrates the production private-file shape before allowing the ready directory to be published.
    let private trySerializeReadyRegistration configuration expectedFingerprint =
        try
            let persisted = toReadyRegistration configuration expectedFingerprint
            let bytes = JsonSerializer.SerializeToUtf8Bytes persisted
            let roundTripped = JsonSerializer.Deserialize<CacheReadyRegistration> bytes

            match tryReadyRegistrationFingerprint roundTripped with
            | Some actualFingerprint when
                String.Equals(actualFingerprint, expectedFingerprint, StringComparison.Ordinal)
                && matchesAcceptedRegistration configuration expectedFingerprint roundTripped
                ->
                Some bytes
            | _ -> None
        with
        | _ -> None

    /// Imports exactly one complete P-256 PKCS#8 private key and returns its fixed-width public coordinates.
    let private tryImportP256 (privateBytes: byte array) =
        if isNull privateBytes || privateBytes.Length = 0 then
            None
        else
            try
                use key = ECDsa.Create()
                let mutable bytesRead = 0
                key.ImportPkcs8PrivateKey(privateBytes, &bytesRead)
                let parameters = key.ExportParameters(true)
                let p256Oid = ECCurve.NamedCurves.nistP256.Oid.Value

                match parameters.Curve.Oid.Value, parameters.Q.X, parameters.Q.Y, parameters.D with
                | curveOid, x, y, privateScalar when
                    bytesRead = privateBytes.Length
                    && String.Equals(curveOid, p256Oid, StringComparison.Ordinal)
                    && not (isNull x)
                    && not (isNull y)
                    && not (isNull privateScalar)
                    && x.Length = 32
                    && y.Length = 32
                    && privateScalar.Length = 32
                    ->
                    Some(x, y)
                | _ -> None
            with
            | _ -> None

    /// Inspects staged identity bytes so only a complete protected P-256 key can represent an enrollment attempt.
    let private inspectAttempt attempt =
        let identityPath = child attempt IdentityFileName

        match inspectMode attempt directoryMode, inspectMode identityPath fileMode with
        | Error CacheIdentityInspection.Inaccessible, _
        | _, Error CacheIdentityInspection.Inaccessible -> CacheIdentityInspection.Inaccessible
        | Error _, _
        | _, Error _ -> CacheIdentityInspection.Invalid
        | Ok (), Ok () ->
            try
                match File.ReadAllBytes(identityPath) |> tryImportP256 with
                | Some _ -> CacheIdentityInspection.AttemptPresent
                | None -> CacheIdentityInspection.Invalid
            with
            | :? UnauthorizedAccessException -> CacheIdentityInspection.Inaccessible
            | :? IOException -> CacheIdentityInspection.Inaccessible
            | _ -> CacheIdentityInspection.Invalid

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
        | _, Some _ when not (hasCompleteAcceptedRegistration configuration) -> Error CacheIdentityError.StateUnavailable
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

                        match tryImportP256 privateBytes with
                        | Some (x, y) when fingerprint x y = expectedFingerprint ->
                            match trySerializeReadyRegistration configuration expectedFingerprint with
                            | None -> Error CacheIdentityError.StateUnavailable
                            | Some registrationBytes ->
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

                match tryReadyRegistrationFingerprint registration with
                | None -> CacheIdentityInspection.Invalid
                | Some expectedFingerprint ->
                    match File.ReadAllBytes(identityPath) |> tryImportP256 with
                    | Some (x, y) when
                        fingerprint x y = expectedFingerprint
                        && String.Equals(registration.PublicKeyFingerprint, expectedFingerprint, StringComparison.Ordinal)
                        ->
                        CacheIdentityInspection.Ready
                    | _ -> CacheIdentityInspection.Invalid
            with
            | :? UnauthorizedAccessException -> CacheIdentityInspection.Inaccessible
            | :? IOException -> CacheIdentityInspection.Inaccessible
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
                    | Ok true, Ok false -> Ok(inspectAttempt attempt)
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
