namespace Grace.Cache

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Shared
open Grace.Shared.ArtifactGrant
open Grace.Shared.Utilities
open Grace.Types.CacheRegistration
open Grace.Types.Common

/// Holds the machine-scoped operational facts that a registered Grace Cache may retain locally.
type CacheMachineConfiguration = { CacheId: Guid; Endpoint: string; AllowHttpEndpoint: bool; ServerUri: string; IdentityKeyName: string }

/// Represents the safe operational facts that may be returned by the local cache control surface.
type CacheRuntimeStatus = { Lifecycle: string; CacheId: string option; Transport: string option }

/// Provides safe runtime-result values without carrying cache key, token, repository, or server configuration data.
module CacheRuntimeStatus =

    /// Builds a stable registered result for cache process control verbs.
    let registered (cacheId: Guid) transport = { Lifecycle = "registered"; CacheId = Some(cacheId.ToString("D")); Transport = Some transport }

/// Holds the operating-system mutex lease that makes one active cache process machine-wide.
type MachineInstanceLease internal (mutex: Mutex) =
    interface IDisposable with
        /// Releases the operating-system guard on normal cache-process shutdown.
        member _.Dispose() =
            mutex.ReleaseMutex()
            mutex.Dispose()

/// Acquires the fixed operating-system guard before any cache store, listener, recovery, or server mutation can begin.
module MachineInstanceGuard =

    /// Uses a Windows global mutex so independent sessions cannot start separate cache processes.
    let private guardName =
        if OperatingSystem.IsWindows() then
            "Global\\Grace.Cache.MachineSingleton.v1"
        else
            "Grace.Cache.MachineSingleton.v1"

    /// Acquires a named guard atomically and returns a loser result without attempting later cache side effects.
    let tryAcquireWithName name =
        let mutable createdNew = false
        let mutex = new Mutex(true, name, &createdNew)

        if createdNew then
            Ok(new MachineInstanceLease(mutex))
        else
            mutex.Dispose()
            Error "A Grace Cache process is already active on this machine."

    /// Acquires the one fixed cache guard whose identity cannot vary with database, endpoint, repository, or runtime settings.
    let tryAcquire () = tryAcquireWithName guardName

/// Holds the explicit enrollment values supplied by the thin CLI process boundary.
type CacheEnrollmentInput =
    {
        Endpoint: string
        AllowHttpEndpoint: bool
        DisplayName: string
        OwnerId: Guid
        OrganizationId: Guid option
        RepositoryScopes: (Guid * Guid) array
    }

/// Models a cache process command after all syntactic input is validated before runtime side effects begin.
type CacheProcessVerb =
    | Enroll of CacheEnrollmentInput
    | RotateNow
    | Status
    | Run

/// Keeps cache process command effects behind an injectable runtime boundary for deterministic dispatch proof.
type CacheProcessEffects =
    {
        Enroll: CacheEnrollmentInput -> Result<CacheRuntimeStatus, string>
        RotateNow: unit -> Result<CacheRuntimeStatus, string>
        Status: unit -> Result<CacheRuntimeStatus, string>
        Run: unit -> Result<CacheRuntimeStatus, string>
    }

/// Carries the redacted machine-readable result of one cache process command.
type CacheProcessResult = { ExitCode: int; Payload: string }

/// Parses and dispatches cache process verbs without letting the CLI reach cache configuration or identity storage.
module CacheProcessCommand =

    /// Reads all values supplied for one repeated process option without accepting missing values.
    let private valuesFor optionName (arguments: string array) =
        let values = ResizeArray<string>()
        let mutable index = 0
        let mutable malformed = false

        while index < arguments.Length do
            if arguments[index] = optionName then
                if index + 1 >= arguments.Length
                   || arguments[index + 1]
                       .StartsWith("--", StringComparison.Ordinal) then
                    malformed <- true
                else
                    values.Add(arguments[index + 1])
                    index <- index + 1

            index <- index + 1

        if malformed then
            Error $"{optionName} requires a value."
        else
            Ok(values |> Seq.toArray)

    /// Reads exactly one process option value and rejects omitted, repeated, or blank values.
    let private requiredSingle optionName arguments =
        match valuesFor optionName arguments with
        | Ok [| value |] when not (String.IsNullOrWhiteSpace value) -> Ok(value.Trim())
        | _ -> Error $"{optionName} must be supplied exactly once."

    /// Parses one non-empty Guid option without allowing a malformed identity to reach key or server code.
    let private requiredGuid optionName arguments =
        match requiredSingle optionName arguments with
        | Error error -> Error error
        | Ok value ->
            match Guid.TryParse value with
            | true, guid when guid <> Guid.Empty -> Ok guid
            | _ -> Error $"{optionName} must be a non-empty Guid."

    /// Parses repeated non-empty Guid values while retaining their explicit caller-provided order.
    let private requiredGuids optionName arguments =
        match valuesFor optionName arguments with
        | Error error -> Error error
        | Ok values when values.Length = 0 -> Error $"{optionName} requires at least one value."
        | Ok values ->
            values
            |> Array.map (fun value ->
                match Guid.TryParse value with
                | true, guid when guid <> Guid.Empty -> Ok guid
                | _ -> Error $"{optionName} must contain only non-empty Guid values.")
            |> Array.fold
                (fun state item ->
                    match state, item with
                    | Ok parsed, Ok value -> Ok(Array.append parsed [| value |])
                    | Error error, _ -> Error error
                    | _, Error error -> Error error)
                (Ok Array.empty)

    /// Validates transport input before malformed enrollment reaches machine configuration or runtime side effects.
    let private validateEnrollmentEndpoint endpoint allowHttp =
        match Uri.TryCreate(endpoint, UriKind.Absolute) with
        | false, _ -> Error "Cache endpoint must be an absolute HTTP or HTTPS URI."
        | true, uri when uri.Scheme = Uri.UriSchemeHttps && not allowHttp -> Ok()
        | true, uri when uri.Scheme = Uri.UriSchemeHttp && allowHttp -> Ok()
        | true, uri when uri.Scheme = Uri.UriSchemeHttp -> Error "HTTP cache endpoints require the explicit --allow-http exception."
        | true, _ -> Error "Cache endpoint must use HTTP or HTTPS."

    /// Parses enrollment input before the cache process creates a service key, writes configuration, or contacts Grace Server.
    let private parseEnrollment arguments =
        let allowHttp = arguments |> Array.contains "--allow-http"

        let knownOptions =
            set [ "--enroll"
                  "--endpoint"
                  "--display-name"
                  "--owner-id"
                  "--organization-id"
                  "--repository-id"
                  "--repository-organization-id"
                  "--allow-http" ]

        let hasUnknownOption =
            arguments
            |> Array.exists (fun argument ->
                argument.StartsWith("--", StringComparison.Ordinal)
                && not (knownOptions.Contains argument))

        match hasUnknownOption,
              requiredSingle "--endpoint" arguments,
              requiredSingle "--display-name" arguments,
              requiredGuid "--owner-id" arguments,
              valuesFor "--organization-id" arguments,
              requiredGuids "--repository-id" arguments,
              requiredGuids "--repository-organization-id" arguments
            with
        | true, _, _, _, _, _, _ -> Error "Cache enrollment input is invalid."
        | false, Ok endpoint, Ok displayName, Ok ownerId, Ok organizationValues, Ok repositoryIds, Ok repositoryOrganizationIds ->
            let organizationId =
                match organizationValues with
                | [||] -> Ok None
                | [| value |] ->
                    match Guid.TryParse value with
                    | true, guid when guid <> Guid.Empty -> Ok(Some guid)
                    | _ -> Error "--organization-id must be a non-empty Guid when supplied."
                | _ -> Error "--organization-id must be supplied at most once."

            match organizationId, validateEnrollmentEndpoint endpoint allowHttp with
            | Error error, _
            | _, Error error -> Error error
            | Ok organizationId, Ok _ when
                repositoryIds.Length
                <> repositoryOrganizationIds.Length
                ->
                Error "--repository-id and --repository-organization-id must have the same number of values."
            | Ok organizationId, Ok _ ->
                let duplicateRepository =
                    repositoryIds |> Array.distinct |> Array.length
                    <> repositoryIds.Length

                if duplicateRepository then
                    Error "--repository-id values must be unique."
                else
                    Ok
                        {
                            Endpoint = endpoint
                            AllowHttpEndpoint = allowHttp
                            DisplayName = displayName
                            OwnerId = ownerId
                            OrganizationId = organizationId
                            RepositoryScopes = Array.zip repositoryOrganizationIds repositoryIds
                        }
        | _ -> Error "Cache enrollment input is invalid."

    /// Parses exactly one supported cache process verb so control markers cannot be combined ambiguously.
    let parse (arguments: string array) =
        let markerCount =
            [
                "--enroll"
                "--rotate-now"
                "--status"
                "--run"
            ]
            |> List.filter (fun marker -> arguments |> Array.contains marker)
            |> List.length

        if markerCount <> 1 then
            Error "Specify exactly one of --run, --enroll, --rotate-now, or --status."
        elif arguments |> Array.contains "--enroll" then
            parseEnrollment arguments |> Result.map Enroll
        elif arguments |> Array.contains "--rotate-now" then
            Ok RotateNow
        elif arguments |> Array.contains "--status" then
            Ok Status
        else
            Ok Run

    /// Converts a successful runtime result to redacted machine-readable JSON.
    let private success status = { ExitCode = 0; Payload = JsonSerializer.Serialize status }

    /// Converts any parser or runtime error to a stable redacted process failure without reflecting sensitive input.
    let private failure operation = { ExitCode = 1; Payload = JsonSerializer.Serialize({| Lifecycle = "failed"; Error = $"Cache {operation} failed." |}) }

    /// Executes one parsed process verb, holding the existing machine-wide guard for stateful one-shot runtime operations.
    let execute (effects: CacheProcessEffects) arguments =
        let guarded operation action =
            match MachineInstanceGuard.tryAcquire () with
            | Error _ -> failure operation
            | Ok lease ->
                use _lease = lease

                match action () with
                | Ok status -> success status
                | Error _ -> failure operation

        match parse arguments with
        | Error _ -> failure "command"
        | Ok (Enroll input) -> guarded "enrollment" (fun () -> effects.Enroll input)
        | Ok RotateNow -> guarded "rotation" effects.RotateNow
        | Ok Status ->
            match effects.Status() with
            | Ok status -> success status
            | Error _ -> failure "status"
        | Ok Run ->
            match effects.Run() with
            | Ok status -> success status
            | Error _ -> failure "startup"

/// Provides machine-scoped Grace Cache configuration without consulting repository configuration or CLI local state.
module CacheMachineConfiguration =

    /// The loopback-only control address shared by the cache host and the CLI launcher.
    let ControlEndpoint = Uri("http://127.0.0.1:48731")

    /// Resolves the service-account configuration path outside every repository working copy.
    let configurationPath () = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Grace", "Cache", "cache.runtime.json")

    /// Validates the exact endpoint and explicit HTTP exception before a cache process can use it.
    let validateEndpoint (endpoint: string) allowHttpEndpoint =
        match Uri.TryCreate(endpoint, UriKind.Absolute) with
        | false, _ -> Error "Cache endpoint must be an absolute HTTP or HTTPS URI."
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttps
            && not allowHttpEndpoint
            ->
            Ok uri
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttps
            && allowHttpEndpoint
            ->
            Error "--allow-http is valid only for an HTTP endpoint."
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttp
            && allowHttpEndpoint
            ->
            Ok uri
        | true, uri when uri.Scheme = Uri.UriSchemeHttp -> Error "HTTP cache endpoints require the explicit --allow-http exception."
        | _ -> Error "Cache endpoint must use HTTP or HTTPS."

    /// Validates a configuration before it is persisted or used to bind a listener.
    let validate (configuration: CacheMachineConfiguration) =
        if
            isNull (box configuration)
            || configuration.CacheId = Guid.Empty
        then
            Error "A registered CacheId is required."
        elif String.IsNullOrWhiteSpace configuration.ServerUri then
            Error "Grace Server URI is required."
        elif String.IsNullOrWhiteSpace configuration.IdentityKeyName then
            Error "Grace Cache identity key reference is required."
        else
            match validateEndpoint configuration.Endpoint configuration.AllowHttpEndpoint, Uri.TryCreate(configuration.ServerUri, UriKind.Absolute) with
            | Error error, _ -> Error error
            | Ok _, (true, serverUri) when
                serverUri.Scheme = Uri.UriSchemeHttps
                || serverUri.Scheme = Uri.UriSchemeHttp
                ->
                Ok()
            | Ok _, _ -> Error "Grace Server URI must be an absolute HTTP or HTTPS URI."

    /// Reads the registered machine configuration without creating files or recovering missing enrollment state.
    let tryRead (path: string) =
        try
            if not (File.Exists path) then
                Error "Grace Cache is not enrolled on this machine."
            else
                let configuration = JsonSerializer.Deserialize<CacheMachineConfiguration>(File.ReadAllText path)

                match validate configuration with
                | Ok () -> Ok configuration
                | Error error -> Error $"Grace Cache configuration is invalid: {error}"
        with
        | :? JsonException -> Error "Grace Cache configuration is invalid."
        | :? IOException -> Error "Grace Cache configuration could not be read."
        | :? UnauthorizedAccessException -> Error "Grace Cache configuration could not be read."

    /// Atomically persists only the server-accepted cache identity and safe machine operational settings.
    let write (path: string) (configuration: CacheMachineConfiguration) =
        match validate configuration with
        | Error error -> Error error
        | Ok () ->
            try
                let directory = Path.GetDirectoryName path

                if String.IsNullOrWhiteSpace directory then
                    Error "Grace Cache configuration path is invalid."
                else
                    Directory.CreateDirectory directory |> ignore
                    let temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp"

                    try
                        File.WriteAllText(temporaryPath, JsonSerializer.Serialize configuration)
                        File.Move(temporaryPath, path, true)
                        Ok()
                    finally
                        if File.Exists temporaryPath then File.Delete temporaryPath
            with
            | :? IOException -> Error "Grace Cache configuration could not be written."
            | :? UnauthorizedAccessException -> Error "Grace Cache configuration could not be written."

    /// Converts registered configuration to redacted status without exposing server URLs, tokens, keys, or repository assignments.
    let toStatus (configuration: CacheMachineConfiguration) =
        let transport =
            match Uri.TryCreate(configuration.Endpoint, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps -> Some "https"
            | true, uri when uri.Scheme = Uri.UriSchemeHttp -> Some "http-approved"
            | _ -> None

        { Lifecycle = "registered"; CacheId = Some(configuration.CacheId.ToString("D")); Transport = transport }

/// Runs transient cache-control attempts with a fixed upper bound and no durable retry state.
module CacheRetry =

    /// Executes at most three attempts, retrying only results explicitly marked transient by the caller.
    let execute<'T> (send: unit -> Result<'T, bool>) (pause: int -> unit) =
        let mutable attempt = 0
        let mutable retry = true
        let mutable result: Result<'T, bool> option = None

        while attempt < 3 && retry do
            attempt <- attempt + 1

            match send () with
            | Ok value ->
                retry <- false
                result <- Some(Ok value)
            | Error isTransient ->
                retry <- isTransient && attempt < 3

                if not retry then result <- Some(Error isTransient)

            if retry then pause attempt

        result |> Option.defaultValue (Error true)

/// Owns service-account P-256 keys without exporting private parameters and calls that use the completed #600 contracts.
module CacheRuntimeControl =

    [<Literal>]
    let private enrollmentTokenEnvironmentVariable = "GRACE_CACHE_ENROLLMENT_TOKEN"

    /// Holds one portable service-account certificate and its private ES256 operation without exposing private parameters.
    type private ServiceIdentityKey(certificate: X509Certificate2, key: ECDsa) =
        member _.Key = key

        interface IDisposable with
            /// Releases the portable certificate-store handle after one runtime operation completes.
            member _.Dispose() =
                key.Dispose()
                certificate.Dispose()

    /// Opens the service account's opaque certificate reference from the portable current-user certificate store.
    let private openKey keyReference =
        try
            use store = new X509Store(StoreName.My, StoreLocation.CurrentUser)
            store.Open(OpenFlags.ReadOnly)
            let matches = store.Certificates.Find(X509FindType.FindByThumbprint, keyReference, false)

            if matches.Count <> 1 then
                Error "Grace Cache service identity is unavailable."
            else
                let certificate = new X509Certificate2(matches[0])
                let key = certificate.GetECDsaPrivateKey()

                if isNull key then
                    certificate.Dispose()
                    Error "Grace Cache service identity is unavailable."
                else
                    Ok(new ServiceIdentityKey(certificate, key))
        with
        | :? CryptographicException
        | :? PlatformNotSupportedException -> Error "Grace Cache service identity is unavailable."

    /// Creates a portable P-256 service-account certificate and persists only its opaque thumbprint reference in configuration.
    let private createKey () =
        try
            use generatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let subject = $"CN=Grace Cache {Guid.NewGuid():N}"
            let request = CertificateRequest(subject, generatedKey, HashAlgorithmName.SHA256)
            let certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1.0), DateTimeOffset.UtcNow.AddYears(10))
            let keyReference = certificate.Thumbprint

            if String.IsNullOrWhiteSpace keyReference then
                certificate.Dispose()
                Error "Grace Cache service identity could not be created."
            else
                use store = new X509Store(StoreName.My, StoreLocation.CurrentUser)
                store.Open(OpenFlags.ReadWrite)
                store.Add(certificate)
                let privateKey = certificate.GetECDsaPrivateKey()

                if isNull privateKey then
                    certificate.Dispose()
                    Error "Grace Cache service identity could not be created."
                else
                    Ok(keyReference, new ServiceIdentityKey(certificate, privateKey))
        with
        | :? CryptographicException
        | :? PlatformNotSupportedException -> Error "Grace Cache service identity could not be created."

    /// Deletes an unaccepted replacement certificate or retires a prior certificate without exporting private key material.
    let private deleteKey keyReference =
        try
            use store = new X509Store(StoreName.My, StoreLocation.CurrentUser)
            store.Open(OpenFlags.ReadWrite)
            let matches = store.Certificates.Find(X509FindType.FindByThumbprint, keyReference, false)

            for certificate in matches do
                store.Remove(certificate)

            Ok()
        with
        | :? CryptographicException
        | :? PlatformNotSupportedException -> Error "Grace Cache service identity cleanup failed."

    /// Produces the canonical #600 public-key DTO from only public P-256 coordinates.
    let private publicKey (key: ECDsa) =
        let parameters = key.ExportParameters(false)

        if isNull parameters.Q.X || isNull parameters.Q.Y then
            Error "Grace Cache service identity public key is invalid."
        else
            Ok(CacheIdentityPublicKey.Create(Base64Url.encode parameters.Q.X, Base64Url.encode parameters.Q.Y))

    /// Reads the current server URI only from machine process configuration, never a repository-local Grace configuration file.
    let private serverUri () =
        let value = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.GraceServerUri

        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttp
            || uri.Scheme = Uri.UriSchemeHttps
            ->
            Ok uri
        | _ -> Error "Grace Server URI is unavailable."

    /// Reads the current normal-login access token supplied transiently by the CLI without persisting or echoing it.
    let private enrollmentToken () =
        let value = Environment.GetEnvironmentVariable enrollmentTokenEnvironmentVariable

        if String.IsNullOrWhiteSpace value then
            Error "Current Grace login is unavailable."
        else
            Ok value

    /// Posts one #600 cache DTO and retries only transient failures a fixed number of times without retaining workflow state.
    let private postWithRetry<'request> (serverUri: Uri) (route: string) (token: string) (request: 'request) =
        let requestUri = Uri(serverUri, route)

        let send () =
            try
                use client = new HttpClient()

                if not (String.IsNullOrWhiteSpace token) then
                    client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

                use content = JsonContent.Create(request, options = Constants.JsonSerializerOptions)

                use response =
                    client
                        .PostAsync(requestUri, content :> HttpContent)
                        .GetAwaiter()
                        .GetResult()

                if response.IsSuccessStatusCode then
                    let body: string =
                        response
                            .Content
                            .ReadAsStringAsync()
                            .GetAwaiter()
                            .GetResult()

                    let envelope = JsonSerializer.Deserialize<GraceReturnValue<CacheRegistrationResult>>(body, Constants.JsonSerializerOptions)

                    if
                        isNull (box envelope)
                        || isNull (box envelope.ReturnValue)
                    then
                        Error false
                    else
                        Ok envelope.ReturnValue
                else
                    Error(int response.StatusCode >= 500)
            with
            | :? HttpRequestException
            | :? TaskCanceledException -> Error true

        CacheRetry.execute send (fun attempt -> Thread.Sleep(TimeSpan.FromMilliseconds(float (attempt * 50))))
        |> Result.mapError (fun _ -> "Grace Server did not complete the cache request.")

    /// Prepares an enrollment target before server mutation without creating a durable enrollment record.
    let private prepareEnrollmentTarget path =
        try
            if File.Exists path then
                Error "Grace Cache is already enrolled on this machine."
            else
                let directory = Path.GetDirectoryName path

                if String.IsNullOrWhiteSpace directory then
                    Error "Grace Cache configuration path is invalid."
                else
                    Directory.CreateDirectory directory |> ignore
                    let probePath = Path.Combine(directory, $".{Guid.NewGuid():N}.probe")
                    File.WriteAllText(probePath, String.Empty)
                    File.Delete(probePath)
                    Ok()
        with
        | :? IOException
        | :? UnauthorizedAccessException -> Error "Grace Cache configuration cannot be prepared."

    /// Maps a validated machine endpoint to its safe status transport label.
    let private transport endpoint =
        match Uri.TryCreate(endpoint, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttps -> "https"
        | true, uri when uri.Scheme = Uri.UriSchemeHttp -> "http-approved"
        | _ -> "unknown"

    /// Enrolls one cache through the #600 administrator route and writes no local configuration until the server accepts its immutable CacheId.
    let enroll input =
        let configurationPath = CacheMachineConfiguration.configurationPath ()

        match prepareEnrollmentTarget configurationPath, serverUri (), enrollmentToken () with
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error -> Error error
        | Ok (), Ok serverUri, Ok token ->
            match createKey () with
            | Error error -> Error error
            | Ok (keyName, key) ->
                use key = key

                match publicKey key.Key with
                | Error error ->
                    deleteKey keyName |> ignore
                    Error error
                | Ok publicKey ->
                    let repositoryScopes = List<CacheRepositoryScope>()

                    input.RepositoryScopes
                    |> Array.iter (fun (organizationId, repositoryId) -> repositoryScopes.Add(CacheRepositoryScope.Create(organizationId, repositoryId)))

                    let request: CacheEnrollmentRequest =
                        {
                            Class = nameof CacheEnrollmentRequest
                            DisplayName = input.DisplayName
                            BoundaryKind =
                                if input.OrganizationId.IsSome then
                                    CacheBoundaryKind.Organization
                                else
                                    CacheBoundaryKind.Owner
                            OwnerId = input.OwnerId
                            OrganizationId = input.OrganizationId
                            RepositoryScopes = repositoryScopes
                            PublicKey = publicKey
                            Endpoint = input.Endpoint
                            AllowHttpEndpoint = input.AllowHttpEndpoint
                            Health = CacheHealthStatus.Healthy
                            SoftwareVersion =
                                typeof<CacheMachineConfiguration>
                                    .Assembly.GetName()
                                    .Version.ToString()
                            ProtocolVersion = "1"
                            PrefetchSupported = false
                        }

                    match Lifecycle.validateEnrollmentRequest request with
                    | Error _ ->
                        deleteKey keyName |> ignore
                        Error "Cache enrollment input is invalid."
                    | Ok () ->
                        match postWithRetry serverUri "cache/enroll" token request with
                        | Ok result when result.Status = CacheRegistrationRefreshStatus.Enrolled ->
                            match result.Registration with
                            | Some registration when registration.CacheId <> Guid.Empty ->
                                let configuration =
                                    {
                                        CacheId = registration.CacheId
                                        Endpoint = input.Endpoint
                                        AllowHttpEndpoint = input.AllowHttpEndpoint
                                        ServerUri = serverUri.AbsoluteUri
                                        IdentityKeyName = keyName
                                    }

                                match CacheMachineConfiguration.write configurationPath configuration with
                                | Ok () -> Ok(CacheRuntimeStatus.registered registration.CacheId (transport input.Endpoint))
                                | Error error ->
                                    deleteKey keyName |> ignore
                                    Error error
                            | _ ->
                                deleteKey keyName |> ignore
                                Error "Grace Server returned an invalid cache enrollment result."
                        | Ok _
                        | Error _ ->
                            deleteKey keyName |> ignore
                            Error "Grace Cache enrollment was not accepted."

    /// Rotates the service key through #600's current-key proof route before retiring the locally retained prior key.
    let rotateNow () =
        let configurationPath = CacheMachineConfiguration.configurationPath ()

        match CacheMachineConfiguration.tryRead configurationPath with
        | Error error -> Error error
        | Ok configuration ->
            match openKey configuration.IdentityKeyName, createKey () with
            | Error error, _
            | _, Error error -> Error error
            | Ok oldKey, Ok (newKeyName, newKey) ->
                use oldKey = oldKey
                use newKey = newKey

                match publicKey newKey.Key with
                | Error error ->
                    deleteKey newKeyName |> ignore
                    Error error
                | Ok newPublicKey ->
                    let unsignedRequest: CacheKeyRotationRequest =
                        {
                            Class = nameof CacheKeyRotationRequest
                            CacheId = configuration.CacheId
                            NewPublicKey = newPublicKey
                            Proof = Unchecked.defaultof<SignedCacheRequestProof>
                        }

                    let digest = CacheRegistrationProof.rotationRequestDigest unsignedRequest

                    let proof =
                        CacheRegistrationProof.createProof
                            oldKey.Key
                            configuration.CacheId
                            CacheRegistrationProof.RotateKeyOperation
                            digest
                            (NodaTime.SystemClock.Instance.GetCurrentInstant())

                    let request = { unsignedRequest with Proof = proof }

                    match postWithRetry (Uri(configuration.ServerUri)) "cache/rotate-key" String.Empty request with
                    | Ok result when result.Status = CacheRegistrationRefreshStatus.Rotated ->
                        let updatedConfiguration = { configuration with IdentityKeyName = newKeyName }

                        match CacheMachineConfiguration.write configurationPath updatedConfiguration with
                        | Error error -> Error error
                        | Ok () ->
                            match deleteKey configuration.IdentityKeyName with
                            | Ok () -> Ok(CacheRuntimeStatus.registered configuration.CacheId (transport configuration.Endpoint))
                            | Error error -> Error error
                    | Ok _
                    | Error _ ->
                        deleteKey newKeyName |> ignore
                        Error "Grace Cache key rotation was not accepted."
