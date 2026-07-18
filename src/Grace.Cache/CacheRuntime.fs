namespace Grace.Cache

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Security.AccessControl
open System.Security.Principal
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open Grace.Shared
open Grace.Shared.ArtifactGrant
open Grace.Shared.Utilities
open Grace.Types.CacheRegistration
open Grace.Types.Common

/// Records the durable active and candidate key references involved in one recoverable Cache identity promotion.
type CandidateKeyTransition =
    {
        ActiveKeyName: string
        ActivePublicKey: CacheIdentityPublicKey
        CandidateKeyName: string
        CandidatePublicKey: CacheIdentityPublicKey
    }

/// Captures the mutually exclusive durable local lifecycle for automatic Cache signing-key rotation.
type CacheKeyRotationLifecycle =
    | Ready
    | CandidatePending of CandidateKeyTransition
    | OperatorRecoveryRequired

/// Holds the machine-scoped operational facts that a registered Grace Cache may retain locally.
type CacheMachineConfiguration =
    {
        CacheId: Guid
        Endpoint: string
        AllowHttpEndpoint: bool
        ServerUri: string
        ActiveKeyName: string
        ActivePublicKey: CacheIdentityPublicKey
        RotationLifecycle: CacheKeyRotationLifecycle
    }

/// Records the only durable local evidence permitted while a cache enrollment reaches a confirmed or ambiguous outcome.
type CacheEnrollmentRecovery =
    {
        Endpoint: string
        ServerUri: string
        RepositoryScopes: (Guid * Guid) array
        ActiveKeyName: string
        ActivePublicKey: CacheIdentityPublicKey
        RecoveryStatus: string
        CacheId: Guid option
    }

/// Names the only local writes needed to finish a server-accepted enrollment without losing its known CacheId.
type AcceptedEnrollmentFinalizationEffects<'configuration> =
    {
        WriteRecovery: string -> CacheEnrollmentRecovery -> Result<unit, string>
        WriteConfiguration: CacheEnrollmentRecovery -> Guid -> Result<'configuration, string>
        ClearRecovery: string -> Result<unit, string>
    }

/// Represents the safe operational facts that may be returned by the local cache control surface.
type CacheRuntimeStatus = { Lifecycle: string; CacheId: string option; Transport: string option }

/// Distinguishes a completed identity synchronization from the server-mandated retry boundary during startup rotation.
type internal IdentitySynchronizationResult =
    | Synchronized of CacheRuntimeStatus
    | RotationRetryAfter of TimeSpan
    | OperatorRecoveryRequired of string

/// Supplies the protected local reads used to produce a redacted cache status without granting status a lifecycle mutation path.
type CacheRuntimeStatusReadEffects =
    {
        ValidateStorage: string -> Result<unit, string>
        ValidateFile: string -> Result<unit, string>
        ReadRecovery: string -> Result<CacheEnrollmentRecovery option, string>
        ReadConfiguration: string -> Result<CacheMachineConfiguration, string>
    }

/// Provides safe runtime-result values without carrying cache key, token, repository, or server configuration data.
module CacheRuntimeStatus =

    /// Builds a stable registered result for cache process control verbs.
    let registered (cacheId: Guid) transport = { Lifecycle = "registered"; CacheId = Some(cacheId.ToString("D")); Transport = Some transport }

    /// Builds the redacted status returned while ambiguous enrollment evidence blocks every normal cache operation.
    let enrollmentRecoveryRequired = { Lifecycle = "enrollment-recovery-required"; CacheId = None; Transport = None }

    /// Builds the stable terminal status emitted when persisted key recovery requires an administrator.
    let operatorRecoveryRequired (cacheId: Guid) transport =
        { Lifecycle = "operator-recovery-required"; CacheId = Some(cacheId.ToString("D")); Transport = transport }

/// Parses the cache identity rotation interval without accepting silently clamped or malformed deployment configuration.
module CacheRotationInterval =

    [<Literal>]
    let EnvironmentVariable = "GRACE_CACHE_KEY_ROTATION_INTERVAL_MINUTES"

    /// Reads the configured inclusive 15 through 10080 minute interval, defaulting only when the setting is absent.
    let fromEnvironment readEnvironment =
        match readEnvironment EnvironmentVariable with
        | null
        | "" -> Ok RegistrationLifetime.DefaultRotationIntervalMinutes
        | value ->
            match Int32.TryParse value with
            | true, minutes when
                minutes
                >= RegistrationLifetime.MinimumRotationIntervalMinutes
                && minutes
                   <= RegistrationLifetime.MaximumRotationIntervalMinutes
                ->
                Ok minutes
            | _ -> Error "Grace Cache key rotation interval is invalid."

    /// Validates the current deployment interval before candidate recovery or creation can cause a key or server effect.
    let internal beforeSynchronization readEnvironment continueSynchronization =
        fromEnvironment readEnvironment
        |> Result.bind continueSynchronization

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

        try
            let mutex = new Mutex(true, name, &createdNew)

            if createdNew then
                Ok(new MachineInstanceLease(mutex))
            else
                mutex.Dispose()
                Error "A Grace Cache process is already active on this machine."
        with
        | :? UnauthorizedAccessException
        | :? IOException
        | :? PlatformNotSupportedException
        | :? WaitHandleCannotBeOpenedException
        | :? ArgumentException -> Error "A Grace Cache process is already active on this machine."

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
    | Status
    | Run

/// Keeps cache process command effects behind an injectable runtime boundary for deterministic dispatch proof.
type CacheProcessEffects =
    {
        Enroll: CacheEnrollmentInput -> Result<CacheRuntimeStatus, string>
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
            not (String.IsNullOrEmpty uri.UserInfo)
            || uri.AbsolutePath <> "/"
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
            [ "--enroll"; "--status"; "--run" ]
            |> List.filter (fun marker -> arguments |> Array.contains marker)
            |> List.length

        if markerCount <> 1 then
            Error "Specify exactly one of --run, --enroll, or --status."
        elif arguments |> Array.contains "--enroll" then
            parseEnrollment arguments |> Result.map Enroll
        else
            let oneShotMarker =
                if arguments |> Array.contains "--status" then
                    "--status", Status
                else
                    "--run", Run

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
                "Cache enrollment failed. Administrator inspection or revocation is required before another enrollment."
            else
                $"Cache {operation} failed."

        { ExitCode = 1; Payload = JsonSerializer.Serialize({| Lifecycle = "failed"; Error = error |}, Constants.JsonSerializerOptions) }

    /// Returns the stable redacted failure used when the private cache-process marker is missing or invalid before runtime dispatch.
    let processFailure () = failure "process"

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
        | Ok Status ->
            match effects.Status() with
            | Ok status -> success status
            | Error _ -> failure "status"
        | Ok Run ->
            match effects.Run() with
            | Ok status -> success status
            | Error _ -> failure "startup"

/// Selects the supported Unix lstat layouts used to validate deployment-provisioned cache configuration paths.
module CacheUnixPathIdentity =

    /// Returns mode and owner offsets for explicit Linux and macOS x64 and arm64 layouts.
    let tryGetStatOffsets isLinux isMacOS architecture =
        match isLinux, isMacOS, architecture with
        | true, false, Architecture.X64 -> Ok(24, 28, false)
        | true, false, Architecture.Arm64 -> Ok(16, 24, false)
        | false, true, Architecture.X64
        | false, true, Architecture.Arm64 -> Ok(4, 16, true)
        | _ -> Error()

/// Provides machine-scoped Grace Cache configuration without consulting repository configuration or CLI local state.
module CacheMachineConfiguration =

    [<Literal>]
    let private directoryFileType = 0x4000

    [<Literal>]
    let private regularFileType = 0x8000

    [<DllImport("libc", SetLastError = true)>]
    extern int private lstat(string path, byte [] statBuffer)

    [<DllImport("libc", SetLastError = true)>]
    extern uint32 private geteuid()

    /// Captures Unix ownership and mode metadata read with the ABI layout selected for a supported operating system and CPU.
    type private UnixPathIdentity = { OwnerUserId: uint32; Mode: int }

    /// Reads Linux and macOS stat metadata on their supported x64 and arm64 layouts without accepting malformed native output.
    let private tryReadUnixPathIdentity path =
        try
            let architecture = RuntimeInformation.ProcessArchitecture

            match CacheUnixPathIdentity.tryGetStatOffsets (OperatingSystem.IsLinux()) (OperatingSystem.IsMacOS()) architecture with
            | Error () -> Error "Grace Cache machine configuration is not securely provisioned."
            | Ok (modeOffset, ownerOffset, modeIsUInt16) ->
                let bytes = Array.zeroCreate<byte> 256

                if lstat (path, bytes) <> 0 then
                    Error "Grace Cache machine configuration is not securely provisioned."
                else
                    let mode =
                        if modeIsUInt16 then
                            int (BitConverter.ToUInt16(bytes, modeOffset))
                        else
                            BitConverter.ToInt32(bytes, modeOffset)

                    Ok { OwnerUserId = BitConverter.ToUInt32(bytes, ownerOffset); Mode = mode }
        with
        | :? PlatformNotSupportedException
        | :? DllNotFoundException
        | :? EntryPointNotFoundException
        | :? BadImageFormatException -> Error "Grace Cache machine configuration is not securely provisioned."

    /// Verifies a Unix path is the exact owner, type, and mode selected by the machine deployment contract.
    let private validateUnixPath expectedOwner expectedType expectedMode path =
        match tryReadUnixPathIdentity path with
        | Ok identity when
            (identity.Mode &&& 0xF000) = expectedType
            && (identity.Mode &&& 0o777) = expectedMode
            && identity.OwnerUserId = expectedOwner
            ->
            Ok()
        | _ -> Error "Grace Cache machine configuration is not securely provisioned."

    /// Resolves the one system-wide Unix leaf that deployment provisions for the configured cache service account.
    let private unixConfigurationDirectory () = "/var/lib/grace/cache"

    /// Resolves the service-account configuration path outside every repository working copy.
    let configurationPath () =
        if OperatingSystem.IsLinux()
           || OperatingSystem.IsMacOS() then
            Path.Combine(unixConfigurationDirectory (), "cache.runtime.json")
        else
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Grace", "Cache", "cache.runtime.json")

    /// Validates the provisioned Unix parent and service-owned leaf before any recovery, configuration, key, listener, or server effect.
    let validateProvisionedStorage (path: string) =
        if OperatingSystem.IsLinux()
           || OperatingSystem.IsMacOS() then
            try
                let leaf = Path.GetDirectoryName path

                if String.IsNullOrWhiteSpace leaf then
                    Error "Grace Cache machine configuration is not securely provisioned."
                else
                    let parent = Directory.GetParent(leaf)

                    if isNull parent then
                        Error "Grace Cache machine configuration is not securely provisioned."
                    else
                        match validateUnixPath 0u directoryFileType 0o755 parent.FullName, validateUnixPath (geteuid ()) directoryFileType 0o700 leaf with
                        | Ok (), Ok () -> Ok()
                        | _ -> Error "Grace Cache machine configuration is not securely provisioned."
            with
            | :? IOException
            | :? UnauthorizedAccessException -> Error "Grace Cache machine configuration is not securely provisioned."
        else
            Ok()

    /// Validates an existing Unix machine-configuration file after its protected parent and leaf have already been accepted.
    let validateProvisionedFile path =
        if OperatingSystem.IsLinux()
           || OperatingSystem.IsMacOS() then
            match validateProvisionedStorage path with
            | Error error -> Error error
            | Ok () ->
                let file = FileInfo(path)

                if not (isNull file.LinkTarget) then
                    Error "Grace Cache machine configuration is insecure. Ask an administrator to repair the provisioned cache directory."
                elif file.Exists then
                    validateUnixPath (geteuid ()) regularFileType 0o600 path
                else
                    Ok()
        else
            Ok()

    /// Validates an existing Linux identity-key leaf without following links or accepting ownership, type, or mode drift.
    let validateProvisionedIdentityKeyFile path =
        if OperatingSystem.IsLinux() then
            match validateProvisionedStorage path with
            | Error error -> Error error
            | Ok () ->
                try
                    let file = FileInfo(path)

                    if isNull file.LinkTarget && file.Exists then
                        validateUnixPath (geteuid ()) regularFileType 0o600 path
                    else
                        Error "Grace Cache machine configuration is not securely provisioned."
                with
                | :? IOException
                | :? UnauthorizedAccessException -> Error "Grace Cache machine configuration is not securely provisioned."
        else
            Ok()

    /// Maps a Linux-only opaque identity reference to its fixed protected PKCS#8 leaf without exposing a caller-controlled path.
    let tryGetLinuxIdentityKeyPath keyReference =
        let prefix = "linux-"

        if
            not (String.IsNullOrWhiteSpace keyReference)
            && keyReference.StartsWith(prefix, StringComparison.Ordinal)
        then
            match Guid.TryParseExact(keyReference[prefix.Length ..], "N") with
            | true, identifier -> Some(Path.Combine(unixConfigurationDirectory (), $"identity-{identifier:N}.pk8"))
            | _ -> None
        else
            None

    /// Validates the exact endpoint and explicit HTTP exception before a cache process can use it.
    let validateEndpoint (endpoint: string) allowHttpEndpoint =
        match Uri.TryCreate(endpoint, UriKind.Absolute) with
        | false, _ -> Error "Cache endpoint must be an absolute HTTP or HTTPS URI."
        | true, uri when
            not (String.IsNullOrEmpty uri.UserInfo)
            || uri.AbsolutePath <> "/"
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
        elif String.IsNullOrWhiteSpace configuration.ActiveKeyName then
            Error "Grace Cache identity key reference is required."
        elif
            isNull (box configuration.ActivePublicKey)
            || not (CacheRegistrationProof.isValidPublicKey configuration.ActivePublicKey)
        then
            Error "Grace Cache identity public verification key is invalid."
        elif isNull (box configuration.RotationLifecycle) then
            Error "Grace Cache rotation lifecycle is invalid."
        elif
            match configuration.RotationLifecycle with
            | CandidatePending pending ->
                String.IsNullOrWhiteSpace pending.ActiveKeyName
                || String.IsNullOrWhiteSpace pending.CandidateKeyName
                || pending.ActiveKeyName = pending.CandidateKeyName
                || isNull (box pending.ActivePublicKey)
                || not (CacheRegistrationProof.isValidPublicKey pending.ActivePublicKey)
                || isNull (box pending.CandidatePublicKey)
                || not (CacheRegistrationProof.isValidPublicKey pending.CandidatePublicKey)
            | Ready
            | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> false
        then
            Error "Grace Cache pending key transition is invalid."
        else
            match validateEndpoint configuration.Endpoint configuration.AllowHttpEndpoint, Uri.TryCreate(configuration.ServerUri, UriKind.Absolute) with
            | Error error, _ -> Error error
            | Ok _, (true, serverUri) when
                String.IsNullOrEmpty serverUri.UserInfo
                && String.IsNullOrEmpty serverUri.Query
                && String.IsNullOrEmpty serverUri.Fragment
                && (serverUri.Scheme = Uri.UriSchemeHttps
                    || serverUri.Scheme = Uri.UriSchemeHttp)
                ->
                Ok()
            | Ok _, _ -> Error "Grace Server URI must be an absolute HTTP or HTTPS URI."

    /// Reads the registered machine configuration without creating files or recovering missing enrollment state.
    let tryRead (path: string) =
        match validateProvisionedFile path with
        | Error error -> Error error
        | Ok () ->
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
                    if not (Directory.Exists directory) then
                        if OperatingSystem.IsLinux()
                           || OperatingSystem.IsMacOS() then
                            raise (new DirectoryNotFoundException())
                        else
                            Directory.CreateDirectory directory |> ignore

                    let temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp"

                    try
                        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, Constants.JsonSerializerOptions))

                        if OperatingSystem.IsLinux()
                           || OperatingSystem.IsMacOS() then
                            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

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

        match configuration.RotationLifecycle with
        | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> CacheRuntimeStatus.operatorRecoveryRequired configuration.CacheId transport
        | Ready
        | CandidatePending _ -> { Lifecycle = "registered"; CacheId = Some(configuration.CacheId.ToString("D")); Transport = transport }

/// Persists one narrow enrollment-recovery record without becoming a retry ledger or general workflow engine.
module CacheEnrollmentRecovery =

    [<Literal>]
    let private preparedStatus = "prepared"

    [<Literal>]
    let private acceptedStatus = "accepted-cache-id"

    [<Literal>]
    let private unknownStatus = "unknown-cache-id"

    /// Resolves the companion recovery path alongside the machine-scoped cache configuration.
    let recoveryPath (configurationPath: string) = Path.Combine(Path.GetDirectoryName(configurationPath), "cache.enrollment-recovery.json")

    /// Creates the evidence that must exist before enrollment reaches Grace Server, including its normalized recovery URI.
    let prepare endpoint serverUri repositoryScopes identityKeyName identityPublicKey =
        {
            Endpoint = endpoint
            ServerUri = serverUri
            RepositoryScopes = repositoryScopes
            ActiveKeyName = identityKeyName
            ActivePublicKey = identityPublicKey
            RecoveryStatus = preparedStatus
            CacheId = None
        }

    /// Marks a record with the immutable server-generated CacheId before local registration finalization begins.
    let accept cacheId recovery = { recovery with RecoveryStatus = acceptedStatus; CacheId = Some cacheId }

    /// Preserves the permitted evidence when enrollment may have reached Grace Server without a valid CacheId.
    let unknown recovery = { recovery with RecoveryStatus = unknownStatus; CacheId = None }

    /// Identifies the only record state that blocks automatic recovery and requires administrator inspection or revocation.
    let isUnknown recovery =
        recovery.RecoveryStatus = unknownStatus
        && recovery.CacheId.IsNone

    /// Atomically writes local recovery evidence before remote enrollment or after a confirmed CacheId response.
    let write (path: string) (recovery: CacheEnrollmentRecovery) =
        try
            let directory = Path.GetDirectoryName path

            if String.IsNullOrWhiteSpace directory then
                Error "Grace Cache enrollment recovery could not be written."
            else
                if not (Directory.Exists directory) then
                    if OperatingSystem.IsLinux()
                       || OperatingSystem.IsMacOS() then
                        raise (new DirectoryNotFoundException())
                    else
                        Directory.CreateDirectory directory |> ignore

                let temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp"

                try
                    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(recovery, Constants.JsonSerializerOptions))

                    if OperatingSystem.IsLinux()
                       || OperatingSystem.IsMacOS() then
                        File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

                    File.Move(temporaryPath, path, true)
                    Ok()
                finally
                    if File.Exists temporaryPath then File.Delete temporaryPath
        with
        | :? IOException
        | :? UnauthorizedAccessException -> Error "Grace Cache enrollment recovery could not be written."

    /// Reads existing recovery evidence without creating a record or treating malformed data as an enrollment retry opportunity.
    let tryRead (path: string) =
        try
            if not (File.Exists path) then
                Ok None
            else
                let recovery = JsonSerializer.Deserialize<CacheEnrollmentRecovery>(File.ReadAllText path, Constants.JsonSerializerOptions)

                if isNull (box recovery)
                   || String.IsNullOrWhiteSpace recovery.Endpoint
                   || String.IsNullOrWhiteSpace recovery.ServerUri
                   || isNull recovery.RepositoryScopes
                   || String.IsNullOrWhiteSpace recovery.ActiveKeyName
                   || isNull (box recovery.ActivePublicKey)
                   || not (CacheRegistrationProof.isValidPublicKey recovery.ActivePublicKey)
                   || String.IsNullOrWhiteSpace recovery.RecoveryStatus then
                    Error "Grace Cache enrollment recovery is invalid."
                elif
                    match Uri.TryCreate(recovery.Endpoint, UriKind.Absolute) with
                    | true, endpoint ->
                        CacheMachineConfiguration.validateEndpoint recovery.Endpoint (endpoint.Scheme = Uri.UriSchemeHttp)
                        |> Result.isError
                    | false, _ -> true
                then
                    Error "Grace Cache enrollment recovery is invalid."
                elif
                    match Uri.TryCreate(recovery.ServerUri, UriKind.Absolute) with
                    | true, serverUri when
                        String.IsNullOrEmpty serverUri.UserInfo
                        && (serverUri.Scheme = Uri.UriSchemeHttp
                            || serverUri.Scheme = Uri.UriSchemeHttps)
                        ->
                        false
                    | _ -> true
                then
                    Error "Grace Cache enrollment recovery is invalid."
                else
                    Ok(Some recovery)
        with
        | :? JsonException -> Error "Grace Cache enrollment recovery is invalid."
        | :? IOException
        | :? UnauthorizedAccessException -> Error "Grace Cache enrollment recovery could not be read."

    /// Removes recovery evidence only after local finalization or remote compensation has reached a confirmed outcome.
    let clear (path: string) =
        try
            if File.Exists path then File.Delete path
            Ok()
        with
        | :? IOException
        | :? UnauthorizedAccessException -> Error "Grace Cache enrollment recovery could not be cleared."

/// Finalizes a known accepted CacheId through local-write failures without changing it into an unknown enrollment outcome.
module AcceptedEnrollmentFinalization =

    /// Uses a known CacheId for every later local write and retains accepted recovery evidence whenever configuration cannot finish.
    let finalize effects recoveryPath recovery cacheId =
        let acceptedRecovery = CacheEnrollmentRecovery.accept cacheId recovery

        let finishConfiguration () =
            effects.WriteConfiguration acceptedRecovery cacheId
            |> Result.bind (fun configuration ->
                effects.ClearRecovery recoveryPath
                |> Result.map (fun () -> configuration))

        match effects.WriteRecovery recoveryPath acceptedRecovery with
        | Ok () -> finishConfiguration ()
        | Error _ ->
            match finishConfiguration () with
            | Ok configuration -> Ok configuration
            | Error error ->
                // A second best-effort write can preserve the known server identity after a transient first failure.
                effects.WriteRecovery recoveryPath acceptedRecovery
                |> ignore

                Error error

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
type CacheKeyRotationLifecycleEffects = { WriteConfiguration: CacheMachineConfiguration -> Result<unit, string>; DeleteKey: string -> Result<unit, string> }

/// Coordinates the small durable state machine that prevents Cache and Grace Server key references from diverging.
module CacheKeyRotationLifecycle =

    /// Returns true only for authoritative registration results that prove the retained candidate cannot be accepted automatically.
    let isDefinitiveRegistrationRejection =
        function
        | CacheRegistrationRefreshStatus.Expired
        | CacheRegistrationRefreshStatus.Revoked
        | CacheRegistrationRefreshStatus.NotFound -> true
        | _ -> false

    /// Returns true when a durable definitive-rejection record must prevent this process or a restart from creating another candidate.
    let requiresOperatorRecovery (configuration: CacheMachineConfiguration) =
        configuration.RotationLifecycle = CacheKeyRotationLifecycle.OperatorRecoveryRequired

    /// Persists the active and candidate opaque references before candidate submission can reach Grace Server.
    let beginTransition effects (configuration: CacheMachineConfiguration) candidateKeyName candidatePublicKey =
        let pending: CandidateKeyTransition =
            {
                ActiveKeyName = configuration.ActiveKeyName
                ActivePublicKey = configuration.ActivePublicKey
                CandidateKeyName = candidateKeyName
                CandidatePublicKey = candidatePublicKey
            }

        let pendingConfiguration = { configuration with RotationLifecycle = CandidatePending pending }

        effects.WriteConfiguration pendingConfiguration
        |> Result.map (fun () -> pendingConfiguration)

    /// Durably selects the promoted candidate before retiring the former active key and clearing candidate state.
    let acceptReplacement effects (configuration: CacheMachineConfiguration) (pending: CandidateKeyTransition) =
        let switched =
            { configuration with
                ActiveKeyName = pending.CandidateKeyName
                ActivePublicKey = pending.CandidatePublicKey
                RotationLifecycle = CandidatePending pending
            }

        match effects.WriteConfiguration switched with
        | Error error -> Error error
        | Ok () ->
            match effects.DeleteKey pending.ActiveKeyName with
            | Error error -> Error error
            | Ok () ->
                let finalized = { switched with RotationLifecycle = Ready }

                effects.WriteConfiguration finalized
                |> Result.map (fun () -> finalized)

    /// Clears an unaccepted candidate durably before removing its local key so terminal server outcomes cannot retry or self-enroll it.
    let rejectReplacement effects (configuration: CacheMachineConfiguration) (pending: CandidateKeyTransition) =
        let cleared = { configuration with RotationLifecycle = CacheKeyRotationLifecycle.OperatorRecoveryRequired }

        match effects.WriteConfiguration cleared with
        | Error error -> Error error
        | Ok () ->
            match effects.DeleteKey pending.CandidateKeyName with
            | Ok () -> Ok(cleared, None)
            | Error error -> Ok(cleared, Some error)


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

/// Distinguishes a retryable transport failure from a server rejection and a committed-or-unreadable result.
type CachePostFailure =
    | Retryable
    | Rejected
    | Ambiguous

/// Classifies HTTP failures that are definite Grace contract rejections rather than post-dispatch unknown outcomes.
module CacheHttpFailure =

    /// Returns true only for a completed non-rate-limited client error that proves the request was rejected by Grace Server.
    let isDefiniteContractRejection statusCode =
        statusCode >= 400
        && statusCode < 500
        && statusCode <> 429

/// Reissues only transport-safe post attempts while preserving ambiguous success outcomes for reconciliation.
module CachePostRetry =
    /// Executes at most three attempts and rebuilds the request payload for every retry.
    let execute<'T> (send: unit -> Result<'T, CachePostFailure>) (pause: int -> unit) =
        let mutable attempt = 0
        let mutable retry = true
        let mutable result: Result<'T, CachePostFailure> option = None

        while attempt < 3 && retry do
            attempt <- attempt + 1

            match send () with
            | Ok value ->
                retry <- false
                result <- Some(Ok value)
            | Error Retryable when attempt < 3 -> pause attempt
            | Error failure ->
                retry <- false
                result <- Some(Error failure)

        result |> Option.defaultValue (Error Retryable)

    /// Rebuilds a request for every send attempt so signed timestamps cannot be replayed after a lost response.
    let executeWithRequest<'request, 'result> (buildRequest: unit -> 'request) (send: 'request -> Result<'result, CachePostFailure>) (pause: int -> unit) =
        execute (fun () -> send (buildRequest ())) pause

/// Distinguishes an enrollment failure proven to occur before dispatch from an outcome that may have reached Grace Server.
type EnrollmentAttemptResult<'T> =
    | PreSendFailure
    | MayHaveReachedServer
    | RejectedByServer
    | EnrollmentCompleted of 'T

/// Classifies the one enrollment dispatch outcome without granting an ambiguous registration an automatic recovery path.
type EnrollmentPostOutcome =
    | EnrollmentAccepted of CacheRegistrationResult
    | EnrollmentRejected
    | EnrollmentNotSent
    | EnrollmentUnknown

/// Distinguishes a rotation failure before dispatch from a response or transport outcome that may follow remote acceptance.
type RotationAttemptResult<'T> =
    | RotationPreSendFailure
    | RotationMayHaveReachedServer
    | RotationRejectedByServer
    | RotationCompleted of 'T

/// Classifies the one rotation dispatch outcome while preserving pending key evidence for every uncertain result.
type RotationPostOutcome =
    | RotationAccepted of CacheRegistrationResult
    | RotationRejected
    | RotationNotSent
    | RotationUnknown

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
            | RejectedByServer
            | EnrollmentCompleted _ -> retry <- false

        result

/// Retries rotation only when construction failed before dispatch, never after a request may have reached Grace Server.
module RotationRetry =

    /// Executes bounded pre-send retries and stops immediately for every completed response, timeout, or connection loss.
    let execute send pause =
        let mutable attempt = 0
        let mutable result = RotationMayHaveReachedServer
        let mutable retry = true

        while attempt < 3 && retry do
            attempt <- attempt + 1
            result <- send ()

            match result with
            | RotationPreSendFailure when attempt < 3 -> pause attempt
            | RotationPreSendFailure -> retry <- false
            | RotationMayHaveReachedServer
            | RotationRejectedByServer
            | RotationCompleted _ -> retry <- false

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

/// Identifies the private-key custody provider selected without exposing private material or provider locations.
type CacheIdentityKeyProvider =
    | LinuxProtectedPkcs8File
    | PlatformX509Store

/// Selects Linux protected-file custody while retaining the existing Windows and macOS platform key stores.
module CacheIdentityKeyCustody =

    /// Returns the sole private-key provider permitted for the supplied platform classification.
    let selectProvider isLinux = if isLinux then LinuxProtectedPkcs8File else PlatformX509Store

/// Creates a candidate-submission proof at the dispatch boundary, after any potentially slow candidate-key probe.
module CacheCandidateSubmissionProof =

    /// Signs the canonical candidate request with the active key using the supplied dispatch timestamp.
    let create (activeKey: ECDsa) (request: CacheKeyCandidateRequest) issuedAt =
        { request with
            Proof =
                CacheRegistrationProof.createProof
                    activeKey
                    request.CacheId
                    CacheRegistrationProof.SubmitCandidateOperation
                    (CacheRegistrationProof.candidateRequestDigest request)
                    issuedAt
        }

/// Owns service-account P-256 keys without exporting private parameters and calls that use the completed #600 contracts.
module CacheRuntimeControl =

    [<Literal>]
    let private enrollmentTokenEnvironmentVariable = "GRACE_CACHE_ENROLLMENT_TOKEN"

    /// Opens a Linux file descriptor with `O_NOFOLLOW` so a checked identity leaf cannot be replaced by a symbolic link.
    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern nativeint private openUnixFile(string path, int flags, int mode)

    [<Literal>]
    let private unixReadOnlyNoFollowCloseOnExec = 0xA0000

    [<Literal>]
    let private unixWriteCreateExclusiveNoFollowCloseOnExec = 0xA00C1

    /// Serializes every refresh, pending-key recovery, and rotation so no callback can observe a half-finished key transition.
    let private lifecycleGate = new SemaphoreSlim(1, 1)

    /// Runs one lifecycle operation exclusively while ensuring every caller releases the shared gate.
    let private serializeLifecycle operation =
        lifecycleGate.Wait()

        try
            operation ()
        finally
            lifecycleGate.Release() |> ignore

    /// Holds one service identity signing operation without exposing private parameters or storage locations.
    type private ServiceIdentityKey(key: ECDsa, release: unit -> unit) =
        member _.Key = key

        interface IDisposable with
            /// Releases the platform-store or Linux file-backed signing handle after one runtime operation completes.
            member _.Dispose() = release ()

    /// Checks whether an ECDsa signing key is exactly the required P-256 identity curve.
    let private isP256Key (key: ECDsa) =
        not (isNull key)
        && key.ExportParameters(false).Curve.Oid.Value = "1.2.840.10045.3.1.7"

    /// Opens the Windows or macOS platform-store key selected by its opaque certificate reference.
    let private openPlatformKey keyReference =
        try
            use store = new X509Store(StoreName.My, StoreLocation.CurrentUser)
            store.Open(OpenFlags.ReadOnly)
            let matches = store.Certificates.Find(X509FindType.FindByThumbprint, keyReference, false)

            if matches.Count <> 1 then
                Error "Grace Cache service identity is unavailable."
            else
                let certificate = new X509Certificate2(matches[0])
                let key = certificate.GetECDsaPrivateKey()

                if not (isP256Key key) then
                    if not (isNull key) then key.Dispose()
                    certificate.Dispose()
                    Error "Grace Cache service identity is unavailable."
                else
                    Ok(
                        new ServiceIdentityKey(
                            key,
                            fun () ->
                                key.Dispose()
                                certificate.Dispose()
                        )
                    )
        with
        | :? CryptographicException
        | :? PlatformNotSupportedException -> Error "Grace Cache service identity is unavailable."

    /// Opens a Linux PKCS#8 key only after its opaque reference and protected regular-file custody have been revalidated.
    let private openLinuxKey keyReference =
        match CacheMachineConfiguration.tryGetLinuxIdentityKeyPath keyReference with
        | None -> Error "Grace Cache service identity is unavailable."
        | Some keyPath ->
            match CacheMachineConfiguration.validateProvisionedIdentityKeyFile keyPath with
            | Error _ -> Error "Grace Cache service identity is unavailable."
            | Ok () ->
                let mutable privateBytes = Array.empty<byte>

                let result =
                    try
                        let nativeHandle = openUnixFile (keyPath, unixReadOnlyNoFollowCloseOnExec, 0)

                        if nativeHandle = -1n then
                            Error "Grace Cache service identity is unavailable."
                        else
                            use keyHandle = new SafeFileHandle(nativeHandle, true)
                            use keyFile = new FileStream(keyHandle, FileAccess.Read)

                            if keyFile.Length <= 0L || keyFile.Length > 16384L then
                                Error "Grace Cache service identity is unavailable."
                            else
                                privateBytes <- Array.zeroCreate (int keyFile.Length)
                                let mutable offset = 0
                                let mutable complete = true

                                while complete && offset < privateBytes.Length do
                                    let read = keyFile.Read(privateBytes, offset, privateBytes.Length - offset)

                                    if read = 0 then complete <- false else offset <- offset + read

                                if not complete then
                                    Error "Grace Cache service identity is unavailable."
                                else
                                    let key = ECDsa.Create()
                                    let mutable bytesRead = 0

                                    try
                                        key.ImportPkcs8PrivateKey(privateBytes, &bytesRead)

                                        if
                                            bytesRead <> privateBytes.Length
                                            || not (isP256Key key)
                                        then
                                            key.Dispose()
                                            Error "Grace Cache service identity is unavailable."
                                        else
                                            Ok(new ServiceIdentityKey(key, (fun () -> key.Dispose())))
                                    with
                                    | :? CryptographicException ->
                                        key.Dispose()
                                        Error "Grace Cache service identity is unavailable."
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException
                    | :? CryptographicException -> Error "Grace Cache service identity is unavailable."

                CryptographicOperations.ZeroMemory privateBytes
                result

    /// Opens the Linux protected PKCS#8 provider or the existing Windows and macOS platform-store provider.
    let private openKey keyReference =
        match CacheIdentityKeyCustody.selectProvider (OperatingSystem.IsLinux()) with
        | LinuxProtectedPkcs8File -> openLinuxKey keyReference
        | PlatformX509Store -> openPlatformKey keyReference

    /// Removes a Linux key only after verifying the same protected regular-file contract required for opening it.
    let private deleteLinuxKey keyReference =
        match CacheMachineConfiguration.tryGetLinuxIdentityKeyPath keyReference with
        | None -> Error "Grace Cache service identity cleanup failed."
        | Some keyPath ->
            try
                if File.Exists keyPath then
                    match CacheMachineConfiguration.validateProvisionedIdentityKeyFile keyPath with
                    | Ok () ->
                        File.Delete keyPath
                        Ok()
                    | Error _ -> Error "Grace Cache service identity cleanup failed."
                else
                    Ok()
            with
            | :? IOException
            | :? UnauthorizedAccessException -> Error "Grace Cache service identity cleanup failed."

    /// Creates and atomically promotes one Linux PKCS#8 P-256 key before reopening it through the normal custody checks.
    let private createLinuxKey () =
        let keyReference = $"linux-{Guid.NewGuid():N}"

        match CacheMachineConfiguration.tryGetLinuxIdentityKeyPath keyReference with
        | None -> Error "Grace Cache service identity could not be created."
        | Some keyPath ->
            match CacheMachineConfiguration.validateProvisionedStorage keyPath with
            | Error _ -> Error "Grace Cache service identity could not be created."
            | Ok () ->
                let temporaryPath = $"{keyPath}.{Guid.NewGuid():N}.tmp"
                let mutable privateBytes = Array.empty<byte>

                try
                    try
                        use generatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                        privateBytes <- generatedKey.ExportPkcs8PrivateKey()

                        let nativeHandle = openUnixFile (temporaryPath, unixWriteCreateExclusiveNoFollowCloseOnExec, 0o600)

                        if nativeHandle = -1n then
                            Error "Grace Cache service identity could not be created."
                        else
                            use temporaryHandle = new SafeFileHandle(nativeHandle, true)
                            use temporaryFile = new FileStream(temporaryHandle, FileAccess.Write)
                            temporaryFile.Write(privateBytes, 0, privateBytes.Length)
                            temporaryFile.Flush(true)
                            temporaryFile.Dispose()

                            match CacheMachineConfiguration.validateProvisionedIdentityKeyFile temporaryPath with
                            | Error _ -> Error "Grace Cache service identity could not be created."
                            | Ok () ->
                                File.Move(temporaryPath, keyPath, false)

                                match openLinuxKey keyReference with
                                | Ok key -> Ok(keyReference, key)
                                | Error _ ->
                                    deleteLinuxKey keyReference |> ignore
                                    Error "Grace Cache service identity could not be reopened."
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException
                    | :? CryptographicException -> Error "Grace Cache service identity could not be created."
                finally
                    CryptographicOperations.ZeroMemory privateBytes

                    try
                        if File.Exists temporaryPath then File.Delete temporaryPath
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> ()

    /// Creates a P-256 identity in the existing platform store, then reopens it before enrollment can send its public key.
    let private createPlatformKey () =
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

    /// Creates a Linux protected file key or preserves the Windows and macOS platform-store creation behavior.
    let private createKey () =
        match CacheIdentityKeyCustody.selectProvider (OperatingSystem.IsLinux()) with
        | LinuxProtectedPkcs8File -> createLinuxKey ()
        | PlatformX509Store -> createPlatformKey ()

    /// Deletes an unaccepted replacement or retires a prior identity only through its selected provider.
    let private deletePlatformKey keyReference =
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

    /// Retires a Linux file key or an existing platform-store key after the durable promotion ordering permits cleanup.
    let private deleteKey keyReference =
        match CacheIdentityKeyCustody.selectProvider (OperatingSystem.IsLinux()) with
        | LinuxProtectedPkcs8File -> deleteLinuxKey keyReference
        | PlatformX509Store -> deletePlatformKey keyReference

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
            String.IsNullOrEmpty uri.UserInfo
            && String.IsNullOrEmpty uri.Query
            && String.IsNullOrEmpty uri.Fragment
            && (uri.Scheme = Uri.UriSchemeHttp
                || uri.Scheme = Uri.UriSchemeHttps)
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

    /// Posts one cache DTO, rebuilding the payload for every transport retry and preserving ambiguous successful responses.
    let private postWithRetry<'request> (serverUri: Uri) (route: string) (token: string) (buildRequest: unit -> 'request) =
        let requestUri = CacheServerRoute.append serverUri route

        let send request =
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
                    |> Result.mapError (fun () -> Ambiguous)
                else if int response.StatusCode >= 500 then
                    Error Retryable
                else
                    Error Rejected
            with
            | :? HttpRequestException
            | :? TaskCanceledException -> Error Retryable

        CachePostRetry.executeWithRequest buildRequest send (fun attempt -> Thread.Sleep(TimeSpan.FromMilliseconds(float (attempt * 50))))

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
                    elif CacheHttpFailure.isDefiniteContractRejection (int response.StatusCode) then
                        RejectedByServer
                    else
                        MayHaveReachedServer
                with
                | :? HttpRequestException
                | :? TaskCanceledException -> MayHaveReachedServer

        match EnrollmentRetry.execute send (fun attempt -> Thread.Sleep(TimeSpan.FromMilliseconds(float (attempt * 50)))) with
        | EnrollmentCompleted result -> EnrollmentAccepted result
        | PreSendFailure -> EnrollmentNotSent
        | RejectedByServer -> EnrollmentRejected
        | MayHaveReachedServer -> EnrollmentUnknown

    /// Sends one active-key candidate submission and treats an unreadable response as requiring reuse of the same local candidate.
    let private postCandidate (serverUri: Uri) (request: CacheKeyCandidateRequest) =
        let requestUri = CacheServerRoute.append serverUri "cache/candidate"

        let send () =
            let prepared =
                try
                    let client = new HttpClient()
                    let content = JsonContent.Create(request, options = Constants.JsonSerializerOptions)
                    Ok(client, content)
                with
                | :? InvalidOperationException
                | :? NotSupportedException -> Error()

            match prepared with
            | Error () -> RotationPreSendFailure
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
                        | Ok result -> RotationCompleted result
                        | Error () -> RotationMayHaveReachedServer
                    elif CacheHttpFailure.isDefiniteContractRejection (int response.StatusCode) then
                        RotationRejectedByServer
                    else
                        RotationMayHaveReachedServer
                with
                | :? HttpRequestException
                | :? TaskCanceledException -> RotationMayHaveReachedServer

        match RotationRetry.execute send (fun attempt -> Thread.Sleep(TimeSpan.FromMilliseconds(float (attempt * 50)))) with
        | RotationCompleted result -> RotationAccepted result
        | RotationPreSendFailure -> RotationNotSent
        | RotationRejectedByServer -> RotationRejected
        | RotationMayHaveReachedServer -> RotationUnknown

    /// Rejects a new enrollment when existing configuration or recovery evidence must be resolved first.
    let private prepareEnrollmentTarget path =
        match CacheMachineConfiguration.validateProvisionedStorage path,
              CacheMachineConfiguration.validateProvisionedFile (CacheEnrollmentRecovery.recoveryPath path)
            with
        | Error error, _
        | _, Error error -> Error error
        | Ok (), Ok () ->
            try
                let recoveryPath = CacheEnrollmentRecovery.recoveryPath path

                if File.Exists path then
                    Error "Grace Cache is already enrolled on this machine."
                elif File.Exists recoveryPath then
                    Error "Grace Cache enrollment recovery requires administrator inspection or revocation before a new enrollment."
                else
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

    /// Builds the fixed operational facts permitted on one cache-authenticated registration refresh.
    let private refreshRequest (configuration: CacheMachineConfiguration) (key: ECDsa) health =
        let observedAt = NodaTime.SystemClock.Instance.GetCurrentInstant()

        let unsignedRequest: CacheRegistrationRefreshRequest =
            {
                Class = nameof CacheRegistrationRefreshRequest
                CacheId = configuration.CacheId
                Endpoint = configuration.Endpoint
                Health = health
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

    /// Sends a refresh proof with a new observation timestamp and signature for every retry attempt.
    let private refreshWithKey (configuration: CacheMachineConfiguration) keyReference health =
        match openKey keyReference with
        | Error _ -> Error Rejected
        | Ok key ->
            use key = key
            postWithRetry (Uri(configuration.ServerUri)) "cache/refresh" String.Empty (fun () -> refreshRequest configuration key.Key health)

    /// Finalizes a known accepted enrollment using the immutable server URI recorded before its original request was dispatched.
    let finalizeAcceptedEnrollment (configurationPath: string) (recovery: CacheEnrollmentRecovery) cacheId =
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath

        let writeConfiguration (acceptedRecovery: CacheEnrollmentRecovery) acceptedCacheId =
            match Uri.TryCreate(acceptedRecovery.Endpoint, UriKind.Absolute), Uri.TryCreate(acceptedRecovery.ServerUri, UriKind.Absolute) with
            | (true, endpoint), (true, serverUri) ->
                let configuration: CacheMachineConfiguration =
                    {
                        CacheId = acceptedCacheId
                        Endpoint = acceptedRecovery.Endpoint
                        AllowHttpEndpoint = endpoint.Scheme = Uri.UriSchemeHttp
                        ServerUri = serverUri.AbsoluteUri
                        ActiveKeyName = acceptedRecovery.ActiveKeyName
                        ActivePublicKey = acceptedRecovery.ActivePublicKey
                        RotationLifecycle = Ready
                    }

                CacheMachineConfiguration.write configurationPath configuration
                |> Result.map (fun () -> configuration)
            | _ -> Error "Grace Cache enrollment recovery is invalid."

        let effects: AcceptedEnrollmentFinalizationEffects<CacheMachineConfiguration> =
            { WriteRecovery = CacheEnrollmentRecovery.write; WriteConfiguration = writeConfiguration; ClearRecovery = CacheEnrollmentRecovery.clear }

        AcceptedEnrollmentFinalization.finalize effects recoveryPath recovery cacheId

    /// Reconciles narrow enrollment evidence before any cache work and stops unknown registrations without retrying them.
    let private reconcileEnrollmentRecovery configurationPath =
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath

        match CacheEnrollmentRecovery.tryRead recoveryPath with
        | Error error -> Error error
        | Ok None -> Ok None
        | Ok (Some recovery) when CacheEnrollmentRecovery.isUnknown recovery ->
            Error "Grace Cache enrollment outcome is unknown. Administrator inspection or revocation is required before normal cache work or a new enrollment."
        | Ok (Some recovery) ->
            match recovery.CacheId with
            | Some cacheId ->
                finalizeAcceptedEnrollment configurationPath recovery cacheId
                |> Result.map Some
            | None ->
                Error
                    "Grace Cache enrollment outcome is unknown. Administrator inspection or revocation is required before normal cache work or a new enrollment."

    /// Reads configuration only after accepted enrollment recovery has reached a durable outcome.
    let private getReadyConfigurationCore () =
        let configurationPath = CacheMachineConfiguration.configurationPath ()
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath

        match CacheMachineConfiguration.validateProvisionedStorage configurationPath, CacheMachineConfiguration.validateProvisionedFile recoveryPath with
        | Error error, _
        | _, Error error -> Error error
        | Ok (), Ok () ->
            match reconcileEnrollmentRecovery configurationPath with
            | Error error -> Error error
            | Ok (Some configuration) -> Ok configuration
            | Ok None ->
                match CacheMachineConfiguration.tryRead configurationPath with
                | Error error -> Error error
                | Ok configuration -> Ok configuration

    /// Reads the enrolled configuration without mutating candidate state; identity synchronization owns candidate recovery.
    let getReadyConfiguration () = serializeLifecycle getReadyConfigurationCore

    /// Reads safe machine status from supplied protected reads without reconciling pending keys, contacting Grace Server, or mutating local recovery state.
    let readStatusWith (effects: CacheRuntimeStatusReadEffects) configurationPath =
        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath

        match effects.ValidateStorage configurationPath, effects.ValidateFile recoveryPath with
        | Error error, _
        | _, Error error -> Error error
        | Ok (), Ok () ->
            match effects.ReadRecovery recoveryPath with
            | Ok (Some _) -> Ok CacheRuntimeStatus.enrollmentRecoveryRequired
            | Error message -> Error message
            | Ok _ ->
                effects.ReadConfiguration configurationPath
                |> Result.map CacheMachineConfiguration.toStatus

    /// Reads safe machine status through the deployment-provisioned protected configuration and recovery paths.
    let readStatus configurationPath =
        let effects: CacheRuntimeStatusReadEffects =
            {
                ValidateStorage = CacheMachineConfiguration.validateProvisionedStorage
                ValidateFile = CacheMachineConfiguration.validateProvisionedFile
                ReadRecovery = CacheEnrollmentRecovery.tryRead
                ReadConfiguration = CacheMachineConfiguration.tryRead
            }

        readStatusWith effects configurationPath

    /// Reads only safe cache status from the machine configuration path used by the active service account.
    let status () = readStatus (CacheMachineConfiguration.configurationPath ())

    /// Refreshes one prepared configuration without taking the lifecycle gate recursively and returns the server-issued expiry facts.
    let private refreshConfiguration configuration health =
        match refreshWithKey configuration configuration.ActiveKeyName health with
        | Error _ -> Error "Grace Cache registration refresh was not accepted."
        | Ok result ->
            match result.Status with
            | CacheRegistrationRefreshStatus.Refreshed
            | CacheRegistrationRefreshStatus.RefreshNotDue ->
                match result.Registration with
                | Some registration -> Ok registration
                | None -> Error "Grace Cache registration refresh was not accepted."
            | _ -> Error "Grace Cache registration refresh was not accepted."

    /// Refreshes through the exclusive lifecycle boundary while retaining both local configuration and actual server registration timing.
    let private refreshRegistrationCore health =
        getReadyConfigurationCore ()
        |> Result.bind (fun configuration ->
            refreshConfiguration configuration health
            |> Result.map (fun registration -> configuration, registration))

    /// Refreshes the current cache registration while preserving an unhealthy scaffold until artifact serving is available.
    let refreshNow () =
        serializeLifecycle (fun () ->
            refreshRegistrationCore CacheHealthStatus.Unhealthy
            |> Result.map (fun (configuration, _) -> CacheRuntimeStatus.registered configuration.CacheId (transport configuration.Endpoint)))

    /// Refreshes the current registration and returns server-issued refresh and expiry times for in-memory host scheduling.
    let refreshRegistrationNow () =
        serializeLifecycle (fun () ->
            refreshRegistrationCore CacheHealthStatus.Unhealthy
            |> Result.map snd)

    /// Marks the registration unhealthy at expiry through the same serialized proof-only refresh boundary without re-enrollment.
    let markRegistrationExpired () =
        serializeLifecycle (fun () ->
            refreshRegistrationCore CacheHealthStatus.Unhealthy
            |> Result.map ignore)

    /// Refreshes after the protected control channel and Kestrel listener are ready, publishing healthy only when artifacts are served.
    let startupRefresh artifactServingAvailable =
        serializeLifecycle (fun () ->
            let health =
                if artifactServingAvailable then
                    CacheHealthStatus.Healthy
                else
                    CacheHealthStatus.Unhealthy

            refreshRegistrationCore health)

    /// Enrolls one cache through the #600 administrator route while preserving only the approved recovery evidence.
    let enroll input =
        let configurationPath = CacheMachineConfiguration.configurationPath ()

        match CacheMachineConfiguration.validateProvisionedStorage configurationPath,
              prepareEnrollmentTarget configurationPath,
              serverUri (),
              enrollmentToken ()
            with
        | Error error, _, _, _
        | _, Error error, _, _
        | _, _, Error error, _
        | _, _, _, Error error -> Error error
        | Ok (), Ok (), Ok serverUri, Ok token ->
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
                        let recoveryPath = CacheEnrollmentRecovery.recoveryPath configurationPath
                        let recovery = CacheEnrollmentRecovery.prepare input.Endpoint serverUri.AbsoluteUri input.RepositoryScopes keyName publicKey

                        match CacheEnrollmentRecovery.write recoveryPath recovery with
                        | Error error ->
                            deleteKey keyName |> ignore
                            Error error
                        | Ok () ->
                            let preserveUnknown () =
                                CacheEnrollmentRecovery.write recoveryPath (CacheEnrollmentRecovery.unknown recovery)
                                |> ignore

                                Error "Grace Cache enrollment outcome is unknown. Administrator inspection or revocation is required before a new enrollment."

                            match postEnrollment serverUri token request with
                            | EnrollmentAccepted result when result.Status = CacheRegistrationRefreshStatus.Enrolled ->
                                match result.Registration with
                                | Some registration when registration.CacheId <> Guid.Empty ->
                                    finalizeAcceptedEnrollment configurationPath recovery registration.CacheId
                                    |> Result.map (fun configuration -> CacheRuntimeStatus.registered configuration.CacheId (transport configuration.Endpoint))
                                | _ -> preserveUnknown ()
                            | EnrollmentAccepted _
                            | EnrollmentRejected
                            | EnrollmentNotSent ->
                                CacheEnrollmentRecovery.clear recoveryPath
                                |> ignore

                                deleteKey keyName |> ignore
                                Error "Grace Cache enrollment was not accepted."
                            | EnrollmentUnknown -> preserveUnknown ()

    /// Reconciles a persisted candidate with candidate-key proof before active-key submission so a lost promotion response never uses a retired key.
    let private completeCandidateSynchronization
        (configurationPath: string)
        (configuration: CacheMachineConfiguration)
        (pending: CandidateKeyTransition)
        isStartup
        =
        let effects: CacheKeyRotationLifecycleEffects = { WriteConfiguration = CacheMachineConfiguration.write configurationPath; DeleteKey = deleteKey }

        match CacheRotationInterval.fromEnvironment Environment.GetEnvironmentVariable with
        | Error error -> Error error
        | Ok intervalMinutes ->
            match openKey configuration.ActiveKeyName, openKey pending.CandidateKeyName with
            | Error _, _ -> Error "Grace Cache active identity is unavailable. Administrator revocation and re-enrollment are required."
            | _, Error _ -> Error "Grace Cache candidate identity is unavailable. Administrator revocation and re-enrollment are required."
            | Ok activeKey, Ok candidateKey ->
                use activeKey = activeKey
                use candidateKey = candidateKey

                let acceptPromotedCandidate () =
                    CacheKeyRotationLifecycle.acceptReplacement effects configuration pending
                    |> Result.map (fun updated -> Synchronized(CacheRuntimeStatus.registered updated.CacheId (transport updated.Endpoint)))

                /// Stops automatic rotation after a definitive server rejection, even when local candidate-key cleanup needs operator attention.
                let requireOperatorRecovery () =
                    CacheKeyRotationLifecycle.rejectReplacement effects configuration pending
                    |> Result.map (fun (_, cleanupFailure) ->
                        let message =
                            match cleanupFailure with
                            | Some _ -> "Grace Cache identity candidate cleanup requires administrator recovery before re-enrollment."
                            | None -> "Grace Cache identity candidate was rejected; administrator recovery is required before re-enrollment."

                        OperatorRecoveryRequired message)

                /// Recognizes both a just-promoted refresh and a post-promotion refresh throttle only when the authoritative registration proves the candidate is active.
                let candidateRefreshConfirmsPromotion (result: CacheRegistrationResult) =
                    (result.Status = CacheRegistrationRefreshStatus.Refreshed
                     || result.Status = CacheRegistrationRefreshStatus.RefreshNotDue)
                    && (result.Registration
                        |> Option.exists (fun registration ->
                            registration.ActivePublicKey = pending.CandidatePublicKey
                            && registration.CandidatePublicKey.IsNone))

                let unsignedRequest: CacheKeyCandidateRequest =
                    {
                        Class = nameof CacheKeyCandidateRequest
                        CacheId = configuration.CacheId
                        CandidatePublicKey = pending.CandidatePublicKey
                        RotationIntervalMinutes = intervalMinutes
                        IsStartup = isStartup
                        Proof = Unchecked.defaultof<SignedCacheRequestProof>
                    }

                match refreshWithKey configuration pending.CandidateKeyName CacheHealthStatus.Unhealthy with
                | Ok promoted when candidateRefreshConfirmsPromotion promoted -> acceptPromotedCandidate ()
                | _ ->
                    // The candidate probe can take longer than the server proof tolerance; sign only for the request being sent.
                    let signedRequest = CacheCandidateSubmissionProof.create activeKey.Key unsignedRequest (NodaTime.SystemClock.Instance.GetCurrentInstant())

                    match postCandidate (Uri(configuration.ServerUri)) signedRequest with
                    | RotationAccepted result when result.Status = CacheRegistrationRefreshStatus.CandidateAccepted ->
                        match refreshWithKey configuration pending.CandidateKeyName CacheHealthStatus.Unhealthy with
                        | Ok promoted when candidateRefreshConfirmsPromotion promoted -> acceptPromotedCandidate ()
                        | _ -> Error "Grace Cache identity candidate could not be promoted; the same candidate remains pending for retry."
                    | RotationAccepted result when result.Status = CacheRegistrationRefreshStatus.RotationRetryAfter ->
                        Ok(RotationRetryAfter(TimeSpan.FromSeconds(float (result.RetryAfterSeconds |> Option.defaultValue 60))))
                    | RotationAccepted result when CacheKeyRotationLifecycle.isDefinitiveRegistrationRejection result.Status -> requireOperatorRecovery ()
                    | RotationAccepted _
                    | RotationRejected -> requireOperatorRecovery ()
                    | RotationNotSent -> Error "Grace Cache identity candidate was not accepted; the same candidate remains pending for retry."
                    | RotationUnknown -> Error "Grace Cache identity candidate outcome is unknown; the same candidate remains pending for retry."

    /// Submits or reuses one locally durable candidate, then promotes it through candidate-key refresh before selecting it locally.
    let private synchronizeReadyConfiguration configurationPath (configuration: CacheMachineConfiguration) isStartup =
        match configuration.RotationLifecycle with
        | CacheKeyRotationLifecycle.OperatorRecoveryRequired ->
            Ok(OperatorRecoveryRequired "Grace Cache identity recovery requires administrator revocation and re-enrollment.")
        | CandidatePending pending -> completeCandidateSynchronization configurationPath configuration pending isStartup
        | Ready ->
            match KeyRotationPreparation.openCurrentBeforeCreatingReplacement (fun () -> openKey configuration.ActiveKeyName) createKey with
            | Error error -> Error error
            | Ok (activeKey, (candidateKeyName, candidateKey)) ->
                use activeKey = activeKey
                use candidateKey = candidateKey

                match publicKey candidateKey.Key with
                | Error error ->
                    deleteKey candidateKeyName |> ignore
                    Error error
                | Ok candidatePublicKey ->
                    let effects: CacheKeyRotationLifecycleEffects =
                        { WriteConfiguration = CacheMachineConfiguration.write configurationPath; DeleteKey = deleteKey }

                    match CacheKeyRotationLifecycle.beginTransition effects configuration candidateKeyName candidatePublicKey with
                    | Error error ->
                        deleteKey candidateKeyName |> ignore
                        Error error
                    | Ok candidateConfiguration ->
                        match candidateConfiguration.RotationLifecycle with
                        | CandidatePending pending -> completeCandidateSynchronization configurationPath candidateConfiguration pending isStartup
                        | Ready
                        | CacheKeyRotationLifecycle.OperatorRecoveryRequired -> Error "Grace Cache candidate state was not persisted."

    /// Validates the active interval before any retained candidate can be opened, reconciled, or replaced.
    let private synchronizeIdentityCore isStartup =
        let configurationPath = CacheMachineConfiguration.configurationPath ()

        match getReadyConfigurationCore () with
        | Error error -> Error error
        | Ok configuration ->
            CacheRotationInterval.beforeSynchronization Environment.GetEnvironmentVariable (fun _ ->
                synchronizeReadyConfiguration configurationPath configuration isStartup)

    /// Synchronizes identity only after obtaining the lifecycle gate shared with refresh and recovery.
    let internal synchronizeIdentity isStartup = serializeLifecycle (fun () -> synchronizeIdentityCore isStartup)
