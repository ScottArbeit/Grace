namespace Grace.Cache

open System
open System.Collections.Generic
open System.IO
open System.IO.Pipes
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Security.AccessControl
open System.Security.Principal
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Grace.Shared
open Grace.Shared.ArtifactGrant
open Grace.Shared.Utilities
open Grace.Types.CacheRegistration
open Grace.Types.Common

/// Records the two opaque service-key references involved in one recoverable Cache key rotation.
type PendingKeyTransition = { CurrentKeyName: string; ReplacementKeyName: string }

/// Holds the machine-scoped operational facts that a registered Grace Cache may retain locally.
type CacheMachineConfiguration =
    {
        CacheId: Guid
        Endpoint: string
        AllowHttpEndpoint: bool
        ServerUri: string
        IdentityKeyName: string
        PendingKeyTransition: PendingKeyTransition option
    }

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

/// Decodes only the established cache-registration result envelope and treats malformed successful responses as rejected.
module CacheServerResponse =

    /// Returns the server registration result only when the response has the expected non-null Grace envelope.
    let tryReadRegistrationResult (body: string) =
        try
            let envelope = JsonSerializer.Deserialize<GraceReturnValue<CacheRegistrationResult>>(body, Constants.JsonSerializerOptions)

            if
                isNull (box envelope)
                || isNull (box envelope.ReturnValue)
            then
                Error()
            else
                Ok envelope.ReturnValue
        with
        | :? JsonException
        | :? NotSupportedException -> Error()

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
        | true, uri when
            uri.AbsolutePath <> "/"
            || not (String.IsNullOrEmpty uri.Query)
            || not (String.IsNullOrEmpty uri.Fragment)
            ->
            Error "Cache endpoint must be an HTTP or HTTPS origin with path '/'."
        | true, uri when uri.Scheme = Uri.UriSchemeHttps && not allowHttp -> Ok()
        | true, uri when uri.Scheme = Uri.UriSchemeHttp && allowHttp -> Ok()
        | true, uri when uri.Scheme = Uri.UriSchemeHttp -> Error "HTTP cache endpoints require the explicit --allow-http exception."
        | true, _ -> Error "Cache endpoint must use HTTP or HTTPS."

    /// Parses enrollment input before the cache process creates a service key, writes configuration, or contacts Grace Server.
    let private parseEnrollment arguments =
        let allowHttp = arguments |> Array.contains "--allow-http"

        let valueOptions =
            set [ "--endpoint"
                  "--display-name"
                  "--owner-id"
                  "--organization-id"
                  "--repository-id"
                  "--repository-organization-id" ]

        /// Rejects positional values and unrecognized options so marker-only switches cannot silently consume a false value.
        let hasOnlyRecognizedTokens =
            let rec loop index =
                if index >= arguments.Length then
                    true
                elif arguments[index] = "--enroll"
                     || arguments[index] = "--allow-http" then
                    loop (index + 1)
                elif valueOptions.Contains arguments[index] then
                    if index + 1 >= arguments.Length
                       || arguments[index + 1]
                           .StartsWith("--", StringComparison.Ordinal) then
                        false
                    else
                        loop (index + 2)
                else
                    false

            loop 0

        match hasOnlyRecognizedTokens,
              requiredSingle "--endpoint" arguments,
              requiredSingle "--display-name" arguments,
              requiredGuid "--owner-id" arguments,
              valuesFor "--organization-id" arguments,
              requiredGuids "--repository-id" arguments,
              requiredGuids "--repository-organization-id" arguments
            with
        | false, _, _, _, _, _, _ -> Error "Cache enrollment input is invalid."
        | true, Ok endpoint, Ok displayName, Ok ownerId, Ok organizationValues, Ok repositoryIds, Ok repositoryOrganizationIds ->
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
        else
            let oneShotMarker =
                if arguments |> Array.contains "--rotate-now" then "--rotate-now", RotateNow
                elif arguments |> Array.contains "--status" then "--status", Status
                else "--run", Run

            if arguments = [| fst oneShotMarker |] then
                Ok(snd oneShotMarker)
            else
                Error "Cache process control verbs do not accept additional arguments."

    /// Converts a successful runtime result to redacted machine-readable JSON.
    let private success status = { ExitCode = 0; Payload = JsonSerializer.Serialize(status, Constants.JsonSerializerOptions) }

    /// Converts any parser or runtime error to a stable redacted process failure without reflecting sensitive input.
    let private failure operation =
        let error =
            if operation = "enrollment" then
                "Cache enrollment failed. Inspect registration status or explicitly begin a new enrollment."
            else
                $"Cache {operation} failed."

        { ExitCode = 1; Payload = JsonSerializer.Serialize({| Lifecycle = "failed"; Error = error |}, Constants.JsonSerializerOptions) }

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
        | Ok RotateNow ->
            // Rotation is owned by the active host through its protected local IPC channel, not a competing singleton claimant.
            match effects.RotateNow() with
            | Ok status -> success status
            | Error _ -> failure "rotation"
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

    /// Resolves the service-account configuration path outside every repository working copy.
    let configurationPath () = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Grace", "Cache", "cache.runtime.json")

    /// Validates the exact endpoint and explicit HTTP exception before a cache process can use it.
    let validateEndpoint (endpoint: string) allowHttpEndpoint =
        match Uri.TryCreate(endpoint, UriKind.Absolute) with
        | false, _ -> Error "Cache endpoint must be an absolute HTTP or HTTPS URI."
        | true, uri when
            uri.AbsolutePath <> "/"
            || not (String.IsNullOrEmpty uri.Query)
            || not (String.IsNullOrEmpty uri.Fragment)
            ->
            Error "Cache endpoint must be an HTTP or HTTPS origin with path '/'."
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
        elif configuration.PendingKeyTransition
             |> Option.exists (fun pending ->
                 String.IsNullOrWhiteSpace pending.CurrentKeyName
                 || String.IsNullOrWhiteSpace pending.ReplacementKeyName
                 || pending.CurrentKeyName = pending.ReplacementKeyName) then
            Error "Grace Cache pending key transition is invalid."
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
                let configuration = JsonSerializer.Deserialize<CacheMachineConfiguration>(File.ReadAllText path, Constants.JsonSerializerOptions)

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
                        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, Constants.JsonSerializerOptions))
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

/// Composes cache routes beneath a separately configured Grace Server path base.
module CacheServerRoute =

    /// Appends a route without letting Uri file semantics discard a configured server path base.
    let append (serverUri: Uri) (route: string) =
        let builder = UriBuilder(serverUri)
        let basePath = builder.Path.TrimEnd('/')
        builder.Path <- $"{basePath}/{route.TrimStart('/')}"
        builder.Query <- String.Empty
        builder.Fragment <- String.Empty
        builder.Uri

/// Supplies the durable effects required to finish or roll back one pending service-key transition.
type PendingKeyTransitionEffects =
    {
        WriteConfiguration: CacheMachineConfiguration -> Result<unit, string>
        DeleteKey: string -> Result<unit, string>
        ProbeAcceptedKey: CacheMachineConfiguration -> string -> Result<bool, string>
    }

/// Coordinates the small durable state machine that prevents Cache and Grace Server key references from diverging.
module PendingKeyTransition =

    /// Persists the current and replacement opaque references before a rotation request can reach Grace Server.
    let beginTransition effects (configuration: CacheMachineConfiguration) replacementKeyName =
        let pending = { CurrentKeyName = configuration.IdentityKeyName; ReplacementKeyName = replacementKeyName }

        let pendingConfiguration = { configuration with PendingKeyTransition = Some pending }

        effects.WriteConfiguration pendingConfiguration
        |> Result.map (fun () -> pendingConfiguration)

    /// Clears a rejected replacement only after removing its certificate while retaining the established current key.
    let rejectReplacement effects (configuration: CacheMachineConfiguration) (pending: PendingKeyTransition) =
        match effects.DeleteKey pending.ReplacementKeyName with
        | Error error -> Error error
        | Ok () ->
            let recovered = { configuration with IdentityKeyName = pending.CurrentKeyName; PendingKeyTransition = None }

            effects.WriteConfiguration recovered
            |> Result.map (fun () -> recovered)

    /// Durably switches to the server-accepted key before retiring the prior key and finally clearing the transition.
    let acceptReplacement effects (configuration: CacheMachineConfiguration) (pending: PendingKeyTransition) =
        let switched = { configuration with IdentityKeyName = pending.ReplacementKeyName; PendingKeyTransition = Some pending }

        match effects.WriteConfiguration switched with
        | Error error -> Error error
        | Ok () ->
            match effects.DeleteKey pending.CurrentKeyName with
            | Error error -> Error error
            | Ok () ->
                let finalized = { switched with PendingKeyTransition = None }

                effects.WriteConfiguration finalized
                |> Result.map (fun () -> finalized)

    /// Resolves a persisted incomplete transition using the key currently accepted by Grace Server before normal cache work resumes.
    let reconcile effects (configuration: CacheMachineConfiguration) =
        match configuration.PendingKeyTransition with
        | None -> Ok configuration
        | Some pending ->
            match effects.ProbeAcceptedKey configuration pending.CurrentKeyName with
            | Error error -> Error error
            | Ok true -> rejectReplacement effects configuration pending
            | Ok false ->
                match effects.ProbeAcceptedKey configuration pending.ReplacementKeyName with
                | Error error -> Error error
                | Ok true -> acceptReplacement effects configuration pending
                | Ok false -> Error "Grace Cache key transition could not be reconciled."

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

/// Distinguishes an enrollment failure proven to occur before dispatch from an outcome that may have reached Grace Server.
type EnrollmentAttemptResult<'T> =
    | PreSendFailure
    | MayHaveReachedServer
    | EnrollmentCompleted of 'T

/// Retries enrollment only before an HTTP request can reach Grace Server, preventing duplicate immutable Cache registrations.
module EnrollmentRetry =

    /// Executes bounded pre-send retries and stops immediately for any response, timeout, or connection loss after dispatch.
    let execute send pause =
        let mutable attempt = 0
        let mutable result = MayHaveReachedServer
        let mutable retry = true

        while attempt < 3 && retry do
            attempt <- attempt + 1
            result <- send ()

            match result with
            | PreSendFailure when attempt < 3 -> pause attempt
            | PreSendFailure -> retry <- false
            | MayHaveReachedServer
            | EnrollmentCompleted _ -> retry <- false

        result

/// Opens the established service identity before any replacement certificate can be created during key rotation.
module KeyRotationPreparation =

    /// Returns both usable key handles only when the current key opens before replacement creation begins.
    let openCurrentBeforeCreatingReplacement openCurrent createReplacement =
        match openCurrent () with
        | Error error -> Error error
        | Ok currentKey ->
            createReplacement ()
            |> Result.map (fun replacementKey -> currentKey, replacementKey)

/// Owns service-account P-256 keys without exporting private parameters and calls that use the completed #600 contracts.
module CacheRuntimeControl =

    [<Literal>]
    let private enrollmentTokenEnvironmentVariable = "GRACE_CACHE_ENROLLMENT_TOKEN"

    /// Serializes every refresh, pending-key recovery, and rotation so no callback can observe a half-finished key transition.
    let private lifecycleGate = new SemaphoreSlim(1, 1)

    /// Runs one lifecycle operation exclusively while ensuring every caller releases the shared gate.
    let private serializeLifecycle operation =
        lifecycleGate.Wait()

        try
            operation ()
        finally
            lifecycleGate.Release() |> ignore

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

                let isP256 =
                    not (isNull key)
                    && key.ExportParameters(false).Curve.Oid.Value = "1.2.840.10045.3.1.7"

                if not isP256 then
                    if not (isNull key) then key.Dispose()
                    certificate.Dispose()
                    Error "Grace Cache service identity is unavailable."
                else
                    Ok(new ServiceIdentityKey(certificate, key))
        with
        | :? CryptographicException
        | :? PlatformNotSupportedException -> Error "Grace Cache service identity is unavailable."

    /// Creates a P-256 identity in the executing account store, then reopens it before enrollment can send its public key.
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
                certificate.Dispose()

                match openKey keyReference with
                | Ok key -> Ok(keyReference, key)
                | Error _ ->
                    let matches = store.Certificates.Find(X509FindType.FindByThumbprint, keyReference, false)

                    for storedCertificate in matches do
                        store.Remove(storedCertificate)

                    Error "Grace Cache service identity could not be reopened."
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

    /// Posts one idempotent #600 cache DTO and preserves the existing bounded retry behavior for refresh and rotation.
    let private postWithRetry<'request> (serverUri: Uri) (route: string) (token: string) (request: 'request) =
        let requestUri = CacheServerRoute.append serverUri route

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

                    CacheServerResponse.tryReadRegistrationResult body
                    |> Result.mapError (fun () -> false)
                else
                    Error(int response.StatusCode >= 500)
            with
            | :? HttpRequestException
            | :? TaskCanceledException -> Error true

        CacheRetry.execute send (fun attempt -> Thread.Sleep(TimeSpan.FromMilliseconds(float (attempt * 50))))

    /// Sends enrollment once per post-dispatch attempt, retrying only request construction failures that cannot have reached Grace Server.
    let private postEnrollment (serverUri: Uri) token (request: CacheEnrollmentRequest) =
        let requestUri = CacheServerRoute.append serverUri "cache/enroll"

        let send () =
            let prepared =
                try
                    let client = new HttpClient()

                    if not (String.IsNullOrWhiteSpace token) then
                        client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

                    let content = JsonContent.Create(request, options = Constants.JsonSerializerOptions)
                    Ok(client, content)
                with
                | :? HttpRequestException
                | :? InvalidOperationException -> Error()

            match prepared with
            | Error () -> PreSendFailure
            | Ok (client, content) ->
                use client = client
                use content = content

                try
                    use response =
                        client
                            .PostAsync(requestUri, content :> HttpContent)
                            .GetAwaiter()
                            .GetResult()

                    if response.IsSuccessStatusCode then
                        let body =
                            response
                                .Content
                                .ReadAsStringAsync()
                                .GetAwaiter()
                                .GetResult()

                        match CacheServerResponse.tryReadRegistrationResult body with
                        | Ok result -> EnrollmentCompleted result
                        | Error () -> MayHaveReachedServer
                    else
                        MayHaveReachedServer
                with
                | :? HttpRequestException
                | :? TaskCanceledException -> MayHaveReachedServer

        match EnrollmentRetry.execute send (fun attempt -> Thread.Sleep(TimeSpan.FromMilliseconds(float (attempt * 50)))) with
        | EnrollmentCompleted result -> Ok result
        | PreSendFailure -> Error "Grace Cache enrollment could not be sent. Explicitly begin a new enrollment after correcting the local failure."
        | MayHaveReachedServer -> Error "Grace Cache enrollment outcome is unknown. Inspect registration status or explicitly begin a new enrollment."

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

    /// Builds the fixed operational facts permitted on cache-authenticated registration refresh.
    let private refreshRequest (configuration: CacheMachineConfiguration) (key: ECDsa) =
        let observedAt = NodaTime.SystemClock.Instance.GetCurrentInstant()

        let unsignedRequest: CacheRegistrationRefreshRequest =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = configuration.CacheId
                Endpoint = configuration.Endpoint
                Health = CacheHealthStatus.Healthy
                SoftwareVersion =
                    typeof<CacheMachineConfiguration>
                        .Assembly.GetName()
                        .Version.ToString()
                ProtocolVersion = "1"
                PrefetchSupported = false
                ObservedAt = observedAt
                Proof = Unchecked.defaultof<SignedCacheRequestProof>
            }

        let proof =
            CacheRegistrationProof.createProof
                key
                configuration.CacheId
                CacheRegistrationProof.RefreshOperation
                (CacheRegistrationProof.refreshRequestDigest unsignedRequest)
                observedAt

        { unsignedRequest with Proof = proof }

    /// Sends one existing #618 refresh proof with the supplied opaque key reference.
    let private refreshWithKey (configuration: CacheMachineConfiguration) keyReference =
        match openKey keyReference with
        | Error _ -> Error false
        | Ok key ->
            use key = key
            let request = refreshRequest configuration key.Key
            postWithRetry (Uri(configuration.ServerUri)) "cache/refresh" String.Empty request

    /// Determines whether Grace Server currently accepts one candidate key without treating an ambiguous transport failure as rejection.
    let private probeAcceptedKey configuration keyReference =
        match refreshWithKey configuration keyReference with
        | Error true -> Error "Grace Cache key transition could not be reconciled because Grace Server did not complete the proof request."
        | Error false -> Ok false
        | Ok result ->
            match result.Status with
            | CacheRegistrationRefreshStatus.Refreshed
            | CacheRegistrationRefreshStatus.RefreshNotDue
            | CacheRegistrationRefreshStatus.Expired -> Ok true
            | _ -> Ok false

    /// Reconciles a pending key transition before listener startup, refresh, signing, cache-store work, or a further rotation.
    let resolvePendingKeyTransition configurationPath configuration =
        let effects: PendingKeyTransitionEffects =
            { WriteConfiguration = CacheMachineConfiguration.write configurationPath; DeleteKey = deleteKey; ProbeAcceptedKey = probeAcceptedKey }

        PendingKeyTransition.reconcile effects configuration

    /// Reads configuration only after any pending rotation has reached a durable, server-confirmed outcome.
    let private getReadyConfigurationCore () =
        let configurationPath = CacheMachineConfiguration.configurationPath ()

        match CacheMachineConfiguration.tryRead configurationPath with
        | Error error -> Error error
        | Ok configuration -> resolvePendingKeyTransition configurationPath configuration

    /// Reconciles pending key work before any caller uses the registered machine configuration.
    let getReadyConfiguration () = serializeLifecycle getReadyConfigurationCore

    /// Refreshes one prepared configuration without taking the lifecycle gate recursively.
    let private refreshConfiguration configuration =
        match refreshWithKey configuration configuration.IdentityKeyName with
        | Error _ -> Error "Grace Cache registration refresh was not accepted."
        | Ok result ->
            match result.Status with
            | CacheRegistrationRefreshStatus.Refreshed
            | CacheRegistrationRefreshStatus.RefreshNotDue -> Ok(CacheRuntimeStatus.registered configuration.CacheId (transport configuration.Endpoint))
            | _ -> Error "Grace Cache registration refresh was not accepted."

    /// Refreshes the current cache registration under #618's bounded retry and health-publication behavior.
    let refreshNow () =
        serializeLifecycle (fun () ->
            getReadyConfigurationCore ()
            |> Result.bind refreshConfiguration)

    /// Reconciles and refreshes before host readiness so a restarted cache cannot wait past the active registration lifetime.
    let startupRefresh () =
        serializeLifecycle (fun () ->
            match getReadyConfigurationCore () with
            | Error error -> Error error
            | Ok configuration ->
                refreshConfiguration configuration
                |> Result.map (fun _ -> configuration))

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
                        match postEnrollment serverUri token request with
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
                                        PendingKeyTransition = None
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

    /// Rotates the service key through #600's current-key proof route with a crash-safe local transition before retiring the prior key.
    let private rotateNowCore () =
        let configurationPath = CacheMachineConfiguration.configurationPath ()

        match CacheMachineConfiguration.tryRead configurationPath with
        | Error error -> Error error
        | Ok configuration ->
            match resolvePendingKeyTransition configurationPath configuration with
            | Error error -> Error error
            | Ok configuration ->
                match KeyRotationPreparation.openCurrentBeforeCreatingReplacement (fun () -> openKey configuration.IdentityKeyName) createKey with
                | Error error -> Error error
                | Ok (oldKey, (newKeyName, newKey)) ->
                    use oldKey = oldKey
                    use newKey = newKey

                    let cleanupReplacement error =
                        deleteKey newKeyName |> ignore
                        Error error

                    match publicKey newKey.Key with
                    | Error error -> cleanupReplacement error
                    | Ok newPublicKey ->
                        let effects: PendingKeyTransitionEffects =
                            {
                                WriteConfiguration = CacheMachineConfiguration.write configurationPath
                                DeleteKey = deleteKey
                                ProbeAcceptedKey = probeAcceptedKey
                            }

                        match PendingKeyTransition.beginTransition effects configuration newKeyName with
                        | Error error -> cleanupReplacement error
                        | Ok pendingConfiguration ->
                            let unsignedRequest: CacheKeyRotationRequest =
                                {
                                    Class = nameof CacheKeyRotationRequest
                                    CacheId = configuration.CacheId
                                    NewPublicKey = newPublicKey
                                    Proof = Unchecked.defaultof<SignedCacheRequestProof>
                                }

                            let proof =
                                CacheRegistrationProof.createProof
                                    oldKey.Key
                                    configuration.CacheId
                                    CacheRegistrationProof.RotateKeyOperation
                                    (CacheRegistrationProof.rotationRequestDigest unsignedRequest)
                                    (NodaTime.SystemClock.Instance.GetCurrentInstant())

                            let request = { unsignedRequest with Proof = proof }

                            match postWithRetry (Uri(configuration.ServerUri)) "cache/rotate-key" String.Empty request with
                            | Ok result when result.Status = CacheRegistrationRefreshStatus.Rotated ->
                                match pendingConfiguration.PendingKeyTransition with
                                | Some pending ->
                                    PendingKeyTransition.acceptReplacement effects pendingConfiguration pending
                                    |> Result.map (fun updated -> CacheRuntimeStatus.registered updated.CacheId (transport updated.Endpoint))
                                | None -> Error "Grace Cache key transition could not be finalized."
                            | Ok _
                            | Error false ->
                                match pendingConfiguration.PendingKeyTransition with
                                | Some pending ->
                                    PendingKeyTransition.rejectReplacement effects pendingConfiguration pending
                                    |> Result.mapError id
                                | None -> Error "Grace Cache key transition could not be rolled back."
                                |> Result.bind (fun _ -> Error "Grace Cache key rotation was not accepted.")
                            | Error true -> Error "Grace Cache key rotation outcome is unknown and requires operator inspection."

    /// Rotates only after obtaining the lifecycle gate shared with refresh and recovery.
    let rotateNow () = serializeLifecycle rotateNowCore

/// Hosts the protected machine-local rotation request channel owned only by the active cache process.
module CacheLocalControl =

    [<Literal>]
    let private rotationPipeName = "Grace.Cache.RotateNow.v1"

    /// Holds the background accept loop and cancels it when the active cache host shuts down.
    type private RotationServer(cancellation: CancellationTokenSource, ready: ManualResetEventSlim, worker: Task) =
        interface IDisposable with
            /// Stops local-control acceptance before the cache process releases its machine-wide instance lease.
            member _.Dispose() =
                cancellation.Cancel()

                try
                    worker.Wait(TimeSpan.FromSeconds 2.0) |> ignore
                with
                | :? AggregateException -> ()

                ready.Dispose()
                cancellation.Dispose()

    /// Creates the Windows pipe ACL that admits only the cache account and built-in local administrators.
    let private createWindowsPipe () =
        try
            use serviceIdentity = WindowsIdentity.GetCurrent()
            let serviceSid = serviceIdentity.User

            if isNull serviceSid then
                Error "Grace Cache local control authorization could not be established."
            else
                let security = PipeSecurity()
                security.AddAccessRule(PipeAccessRule(serviceSid, PipeAccessRights.ReadWrite, AccessControlType.Allow))

                let administrators = SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                security.AddAccessRule(PipeAccessRule(administrators, PipeAccessRights.ReadWrite, AccessControlType.Allow))

                NamedPipeServerStreamAcl.Create(
                    rotationPipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    256,
                    256,
                    security,
                    HandleInheritability.None,
                    PipeAccessRights.ChangePermissions
                )
                |> Ok
        with
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException
        | :? IdentityNotMappedException -> Error "Grace Cache local control authorization could not be established."

    /// Creates the Unix user-mode pipe, whose owner-only mode admits the service account and root without a network listener.
    let private createUnixPipe () =
        try
            new NamedPipeServerStream(
                rotationPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous
                ||| PipeOptions.CurrentUserOnly
            )
            |> Ok
        with
        | :? UnauthorizedAccessException
        | :? PlatformNotSupportedException
        | :? IOException -> Error "Grace Cache local control authorization could not be established."

    /// Creates a local-only server with the platform's approved access restriction and no network fallback.
    let private createPipe () =
        if OperatingSystem.IsWindows() then
            createWindowsPipe ()
        elif OperatingSystem.IsLinux()
             || OperatingSystem.IsMacOS() then
            createUnixPipe ()
        else
            Error "Grace Cache local control authorization could not be established."

    /// Accepts only the exact rotate marker after the platform ACL or mode has admitted the local caller.
    let private handleRequest rotate (pipe: NamedPipeServerStream) =
        let requestBuffer = Array.zeroCreate<byte> 32
        let bytesRead = pipe.Read(requestBuffer, 0, requestBuffer.Length)
        let request = Text.Encoding.UTF8.GetString(requestBuffer, 0, bytesRead)

        let response =
            if request = "rotate-now" then
                match rotate () with
                | Ok status -> JsonSerializer.Serialize(status, Constants.JsonSerializerOptions)
                | Error _ -> "failed"
            else
                "denied"

        let responseBytes = Text.Encoding.UTF8.GetBytes response
        pipe.Write(responseBytes, 0, responseBytes.Length)
        pipe.Flush()

    /// Starts protected request processing before listener readiness and fails closed when the operating-system restriction is unavailable.
    let startWith rotate =
        match createPipe () with
        | Error error -> Error error
        | Ok probe ->
            probe.Dispose()
            let cancellation = new CancellationTokenSource()
            let ready = new ManualResetEventSlim(false)

            let worker =
                Task.Run(
                    Action (fun () ->
                        while not cancellation.IsCancellationRequested do
                            match createPipe () with
                            | Error _ -> cancellation.Cancel()
                            | Ok pipe ->
                                use pipe = pipe
                                ready.Set()

                                try
                                    pipe
                                        .WaitForConnectionAsync(cancellation.Token)
                                        .GetAwaiter()
                                        .GetResult()

                                    if not cancellation.IsCancellationRequested then handleRequest rotate pipe
                                with
                                | :? OperationCanceledException -> ()
                                | :? IOException -> ())
                )

            if ready.Wait(TimeSpan.FromSeconds 2.0) then
                Ok(new RotationServer(cancellation, ready, worker) :> IDisposable)
            else
                cancellation.Cancel()
                ready.Dispose()
                cancellation.Dispose()
                Error "Grace Cache local control authorization could not be established."

    /// Starts the active-process rotation channel using the shared serialized runtime operation.
    let start () = startWith CacheRuntimeControl.rotateNow

    /// Sends one local-only rotation request to the active cache process without attempting a direct singleton-guard bypass.
    let requestRotation () =
        try
            use pipe = new NamedPipeClientStream(".", rotationPipeName, PipeDirection.InOut, PipeOptions.None)
            pipe.Connect(2000)
            let request = Text.Encoding.UTF8.GetBytes "rotate-now"
            pipe.Write(request, 0, request.Length)
            pipe.Flush()
            let responseBuffer = Array.zeroCreate<byte> 1024
            let bytesRead = pipe.Read(responseBuffer, 0, responseBuffer.Length)
            let response = Text.Encoding.UTF8.GetString(responseBuffer, 0, bytesRead)

            try
                let status = JsonSerializer.Deserialize<CacheRuntimeStatus>(response, Constants.JsonSerializerOptions)

                if isNull (box status) then
                    Error "Grace Cache rotation request was not accepted."
                else
                    Ok status
            with
            | :? JsonException
            | :? NotSupportedException -> Error "Grace Cache rotation request was not accepted."
        with
        | :? TimeoutException
        | :? IOException
        | :? UnauthorizedAccessException -> Error "Grace Cache rotation request was not accepted."
