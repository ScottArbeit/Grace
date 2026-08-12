namespace Grace.Cache

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

/// Represents the public P-256 half generated for one local cache enrollment attempt.
[<CLIMutable>]
type CacheIdentityPublicKey = { PublicKeyX: string; PublicKeyY: string }

/// Represents the minimal private configuration committed with one cache identity.
[<CLIMutable>]
type CacheReadyConfiguration =
    {
        Class: string
        CacheId: Guid
        Endpoint: string
        BoundaryKind: string
        OwnerId: Guid
        OrganizationId: Guid
        RepositoryIds: Guid array
        DisplayName: string
        ProtocolVersion: string
        PublicKeyFingerprint: string
    }

/// Represents one prepared local cache identity before server enrollment has returned a success response.
type PreparedCacheIdentity = { StagingDirectory: string; PublicKey: CacheIdentityPublicKey }

/// Represents the redacted local cache enrollment state emitted by `grace cache status`.
[<CLIMutable>]
type CacheLocalStatus =
    {
        Class: string
        Enrollment: string
        [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
        CacheId: Guid option
        [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
        Endpoint: string option
        [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
        BoundaryKind: string option
        [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
        RepositoryCount: int option
        Key: string
    }

/// Owns Linux-only private cache key staging, ready-state commit, and redacted local observation.
module CacheIdentity =

    /// Identifies the systemd-managed cache state root used by the supported Linux deployment profile.
    [<Literal>]
    let StateRoot = "/var/lib/grace-cache"

    [<Literal>]
    let private ReadyDirectoryName = "ready"

    [<Literal>]
    let private StagingPrefix = "staging-"

    [<Literal>]
    let private PrivateKeyFileName = "identity.pkcs8"

    [<Literal>]
    let private ConfigurationFileName = "configuration.json"

    /// Defines the required private mode for the state and staging directories.
    let private directoryMode =
        UnixFileMode.UserRead
        ||| UnixFileMode.UserWrite
        ||| UnixFileMode.UserExecute

    /// Defines the required private mode for private key and configuration files.
    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    /// Encodes binary key material using the URL-safe base64 representation used by cache enrollment.
    let private base64Url (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Computes the stored public-key fingerprint used only for private local key/configuration matching.
    let private fingerprint (key: CacheIdentityPublicKey) =
        Encoding.UTF8.GetBytes($"{key.PublicKeyX}.{key.PublicKeyY}")
        |> SHA256.HashData
        |> base64Url

    /// Builds a complete private ready configuration after the server creates the permanent CacheId.
    module ReadyConfiguration =
        /// Creates the minimal local configuration required to reload a committed cache identity without any server lookup.
        let create cacheId endpoint boundaryKind ownerId organizationId repositoryIds displayName protocolVersion publicKey =
            {
                Class = nameof CacheReadyConfiguration
                CacheId = cacheId
                Endpoint = endpoint
                BoundaryKind = boundaryKind
                OwnerId = ownerId
                OrganizationId = organizationId |> Option.defaultValue Guid.Empty
                RepositoryIds = repositoryIds |> Seq.toArray
                DisplayName = displayName
                ProtocolVersion = protocolVersion
                PublicKeyFingerprint = fingerprint publicKey
            }

    /// Returns a generic local failure without disclosing protected filesystem paths or exception details to callers.
    let private localFailure () = Error "The protected local cache identity state could not be prepared."

    /// Validates that a ready configuration is complete enough to represent a real static enrollment.
    let private isValidConfiguration (configuration: CacheReadyConfiguration) =
        let endpointValid =
            Uri.TryCreate(configuration.Endpoint, UriKind.Absolute)
            |> fst

        configuration.Class = nameof CacheReadyConfiguration
        && configuration.CacheId <> Guid.Empty
        && endpointValid
        && not (String.IsNullOrWhiteSpace(configuration.BoundaryKind))
        && configuration.OwnerId <> Guid.Empty
        && configuration.RepositoryIds.Length > 0
        && (configuration.RepositoryIds
            |> Array.forall (fun repositoryId -> repositoryId <> Guid.Empty))
        && not (String.IsNullOrWhiteSpace(configuration.DisplayName))
        && not (String.IsNullOrWhiteSpace(configuration.ProtocolVersion))
        && not (String.IsNullOrWhiteSpace(configuration.PublicKeyFingerprint))

    /// Verifies the service-managed root exists and has the required private Linux mode before any enrollment send.
    let private verifyRoot root =
        if not (OperatingSystem.IsLinux()) then
            Error "Grace Cache enrollment is supported only on Linux."
        elif not (Directory.Exists(root)) then
            Error "The protected Grace Cache state root is unavailable."
        else
            try
                if File.GetUnixFileMode(root) <> directoryMode then
                    Error "The protected Grace Cache state root is not configured with mode 0700."
                else
                    Ok()
            with
            | :? UnauthorizedAccessException -> Error "The protected Grace Cache state root is inaccessible."
            | _ -> Error "The protected Grace Cache state root is unavailable."

    /// Writes one private file with the required mode and a durable flush before it becomes eligible for ready-state commit.
    let private writePrivateFile path bytes =
        try
            use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush(true)
            File.SetUnixFileMode(path, fileMode)
            Ok()
        with
        | _ -> localFailure ()

    /// Removes a staging directory created by this component and intentionally suppresses cleanup failures after a failed attempt.
    let discard (prepared: PreparedCacheIdentity) =
        try
            if Directory.Exists(prepared.StagingDirectory) then
                Directory.Delete(prepared.StagingDirectory, true)
        with
        | _ -> ()

    /// Deletes unreferenced prior attempt directories while never touching the ready enrollment marker.
    let cleanupStaleStaging root =
        match verifyRoot root with
        | Error _ -> ()
        | Ok () ->
            try
                Directory.GetDirectories(root, $"{StagingPrefix}*")
                |> Array.iter (fun directory ->
                    try
                        Directory.Delete(directory, true)
                    with
                    | _ -> ())
            with
            | _ -> ()

    /// Generates and persists one P-256 identity key below the protected root before the server enrollment request is sent.
    let prepare root =
        match verifyRoot root with
        | Error error -> Error error
        | Ok () ->
            let ready = Path.Combine(root, ReadyDirectoryName)

            if Directory.Exists(ready) then
                Error "A local Grace Cache enrollment already exists and requires explicit manual reset."
            else
                let staging = Path.Combine(root, StagingPrefix + Guid.NewGuid().ToString("N"))

                try
                    Directory.CreateDirectory(staging, directoryMode)
                    |> ignore

                    File.SetUnixFileMode(staging, directoryMode)

                    use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                    let parameters = key.ExportParameters(false)
                    let publicKey = { PublicKeyX = base64Url parameters.Q.X; PublicKeyY = base64Url parameters.Q.Y }

                    match writePrivateFile (Path.Combine(staging, PrivateKeyFileName)) (key.ExportPkcs8PrivateKey()) with
                    | Ok () -> Ok { StagingDirectory = staging; PublicKey = publicKey }
                    | Error error ->
                        try
                            Directory.Delete(staging, true)
                        with
                        | _ -> ()

                        Error error
                with
                | _ ->
                    try
                        if Directory.Exists(staging) then Directory.Delete(staging, true)
                    with
                    | _ -> ()

                    localFailure ()

    /// Commits a prepared key and its verified local configuration using one same-parent no-overwrite directory rename.
    let commitReady (prepared: PreparedCacheIdentity) (configuration: CacheReadyConfiguration) =
        let root = Directory.GetParent(prepared.StagingDirectory)

        if isNull root then
            localFailure ()
        else
            let ready = Path.Combine(root.FullName, ReadyDirectoryName)
            let configurationPath = Path.Combine(prepared.StagingDirectory, ConfigurationFileName)

            if Directory.Exists(ready) then
                Error "A local Grace Cache enrollment already exists and requires explicit manual reset."
            elif not (Directory.Exists(prepared.StagingDirectory)) then
                localFailure ()
            else
                try
                    let payload = JsonSerializer.SerializeToUtf8Bytes(configuration)

                    match writePrivateFile configurationPath payload with
                    | Error error -> Error error
                    | Ok () ->
                        Directory.Move(prepared.StagingDirectory, ready)
                        Ok()
                with
                | _ -> localFailure ()

    /// Reads one protected ready-state file while reducing filesystem failures to the approved status classifications.
    let private readPrivateReadyFile path =
        try
            Ok(File.ReadAllBytes(path))
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Error "missing"
        | :? UnauthorizedAccessException -> Error "inaccessible"
        | _ -> Error "invalid"

    /// Reads ready state without changing any local files and returns only approved redacted fields.
    let status root =
        let notEnrolled =
            {
                Class = nameof CacheLocalStatus
                Enrollment = "notEnrolled"
                CacheId = None
                Endpoint = None
                BoundaryKind = None
                RepositoryCount = None
                Key = "missing"
            }

        let invalid key = { notEnrolled with Enrollment = "invalid"; Key = key }
        let ready = Path.Combine(root, ReadyDirectoryName)

        try
            if not (Directory.Exists(ready)) then
                notEnrolled
            else
                let configurationPath = Path.Combine(ready, ConfigurationFileName)
                let privateKeyPath = Path.Combine(ready, PrivateKeyFileName)

                match readPrivateReadyFile configurationPath, readPrivateReadyFile privateKeyPath with
                | Error "inaccessible", _
                | _, Error "inaccessible" -> invalid "inaccessible"
                | Error _, _ -> invalid "invalid"
                | _, Error "missing" -> invalid "missing"
                | _, Error _ -> invalid "invalid"
                | Ok configurationBytes, Ok privateKeyBytes ->
                    let configuration = JsonSerializer.Deserialize<CacheReadyConfiguration>(configurationBytes)

                    if
                        isNull (box configuration)
                        || not (isValidConfiguration configuration)
                    then
                        invalid "invalid"
                    else
                        use key = ECDsa.Create()
                        let mutable bytesRead = 0
                        key.ImportPkcs8PrivateKey(privateKeyBytes, &bytesRead)
                        let parameters = key.ExportParameters(false)
                        let publicKey = { PublicKeyX = base64Url parameters.Q.X; PublicKeyY = base64Url parameters.Q.Y }

                        if configuration.PublicKeyFingerprint
                           <> fingerprint publicKey then
                            invalid "invalid"
                        else
                            {
                                Class = nameof CacheLocalStatus
                                Enrollment = "enrolled"
                                CacheId = Some configuration.CacheId
                                Endpoint = Some configuration.Endpoint
                                BoundaryKind = Some configuration.BoundaryKind
                                RepositoryCount = Some configuration.RepositoryIds.Length
                                Key = "available"
                            }
        with
        | :? UnauthorizedAccessException -> invalid "inaccessible"
        | _ -> invalid "invalid"
