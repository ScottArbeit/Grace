namespace Grace.CLI.Command

open Grace.CLI
open Grace.CLI.Common
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Client
open Grace.SDK
open Grace.Types.Common
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks

/// Groups the doctor command parser, handlers, and output helpers.
module Doctor =

    [<Literal>]
    let ReportVersion = "doctor-report-v1"

    [<Literal>]
    let private CliCatalogCheckId = "cli.catalog"

    [<Literal>]
    let private ConfigFileDiscoverCheckId = "config.file.discover"

    [<Literal>]
    let private ConfigFileParseCheckId = "config.file.parse"

    [<Literal>]
    let private ConfigRepositoryIdentityCheckId = "config.repository.identity"

    [<Literal>]
    let private ConfigServerUriCheckId = "config.server-uri.valid"

    [<Literal>]
    let private ServerUriConsistencyCheckId = "server-uri.consistency"

    [<Literal>]
    let private UserConfigDiscoverCheckId = "user-config.file.discover"

    [<Literal>]
    let private UserConfigParseCheckId = "user-config.file.parse"

    [<Literal>]
    let private IgnoreEntriesParseCheckId = "ignore.entries.parse"

    [<Literal>]
    let private AuthSourceDetectedCheckId = "auth.source.detected"

    [<Literal>]
    let private AuthEnvTokenValidCheckId = "auth.env-token.valid"

    [<Literal>]
    let private AuthTokenFileUnsupportedCheckId = "auth.token-file.unsupported"

    [<Literal>]
    let private AuthOidcConfigurationCheckId = "auth.oidc.configuration"

    [<Literal>]
    let private StateDbFilePresentCheckId = "state.db.file-present"

    [<Literal>]
    let private StateDbReadOnlyOpenCheckId = "state.db.read-only-open"

    [<Literal>]
    let private StateDbSchemaVersionCheckId = "state.db.schema-version"

    [<Literal>]
    let private StateDbRequiredTablesCheckId = "state.db.required-tables"

    [<Literal>]
    let private StateDbRequiredIndexesCheckId = "state.db.required-indexes"

    [<Literal>]
    let private StateDbIntegrityCheckId = "state.db.integrity-check"

    [<Literal>]
    let private StateDbForeignKeyCheckId = "state.db.foreign-key-check"

    [<Literal>]
    let private ObjectCacheIndexReadableCheckId = "object-cache.index-readable"

    [<Literal>]
    let private WorkingTreeScanCheckId = "working-tree.scan"

    [<Literal>]
    let private ServerHealthzReachableCheckId = "server.healthz.reachable"

    [<Literal>]
    let private ServerLifecycleHeadersCheckId = "server.lifecycle.headers"

    [<Literal>]
    let private ServerAuthPrincipalAvailableCheckId = "server.auth-principal.available"

    [<Literal>]
    let private ExpectedLocalStateSchemaVersion = "5"

    [<Literal>]
    let private DoctorServerProbeTimeoutMilliseconds = 1500

    /// Defines the options parsed by the doctor command handlers.
    module private Options =
        let full =
            new Option<bool>(
                OptionName.Full,
                Required = false,
                Description = "Include checks reserved for the full doctor profile.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

        let offline =
            new Option<bool>(
                OptionName.Offline,
                Required = false,
                Description = "Limit the scaffolded report to checks that can run without network access.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

        let listChecks =
            new Option<bool>(
                OptionName.ListChecks,
                Required = false,
                Description = "List the inert doctor check catalog without running diagnostics.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

        let check =
            new Option<string []>(
                OptionName.Check,
                Required = false,
                Description = "Filter the scaffolded doctor report by check ID or category. Repeat or separate values with commas.",
                Arity = ArgumentArity.OneOrMore
            )

        let strict =
            new Option<bool>(
                OptionName.Strict,
                Required = false,
                Description = "Return a failing exit code when the doctor report status is Warning.",
                Arity = ArgumentArity.Zero,
                DefaultValueFactory = (fun _ -> false)
            )

    let catalog: LocalOutputDto.DoctorCheckDto array =
        [|
            {
                Id = CliCatalogCheckId
                Category = "CLI"
                Title = "CLI command catalog"
                Description = "Verifies that the Grace CLI command catalog is available."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = ConfigFileDiscoverCheckId
                Category = "Configuration"
                Title = "Grace configuration discovery"
                Description = "Finds .grace/graceconfig.json by walking upward from the current directory without creating it."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = ConfigFileParseCheckId
                Category = "Configuration"
                Title = "Grace configuration parse"
                Description = "Reads .grace/graceconfig.json without rewriting or normalizing it."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = ConfigRepositoryIdentityCheckId
                Category = "Configuration"
                Title = "Repository identity"
                Description = "Reports whether the repository configuration includes owner, organization, repository, and branch identity values."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = ConfigServerUriCheckId
                Category = "Configuration"
                Title = "Configured server URI"
                Description = "Validates the server URI stored in .grace/graceconfig.json without probing the server."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = ServerUriConsistencyCheckId
                Category = "Configuration"
                Title = "Server URI consistency"
                Description = "Compares the configured server URI with GRACE_SERVER_URI when both are present."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = UserConfigDiscoverCheckId
                Category = "User configuration"
                Title = "User configuration discovery"
                Description = "Checks whether ~/.grace/userconfig.json exists without creating it."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = UserConfigParseCheckId
                Category = "User configuration"
                Title = "User configuration parse"
                Description = "Reads ~/.grace/userconfig.json without creating or rewriting it."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = IgnoreEntriesParseCheckId
                Category = "Ignore"
                Title = ".graceignore entries"
                Description = "Reads .graceignore entries, ignoring comments and blank lines, without creating the file."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = AuthSourceDetectedCheckId
                Category = "Authentication"
                Title = "Authentication source"
                Description = "Classifies the likely local authentication source from environment configuration without acquiring credentials."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = AuthEnvTokenValidCheckId
                Category = "Authentication"
                Title = "GRACE_TOKEN PAT shape"
                Description = "Validates GRACE_TOKEN with the pure Grace PAT parser without printing or verifying the token."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = AuthTokenFileUnsupportedCheckId
                Category = "Authentication"
                Title = "GRACE_TOKEN_FILE unsupported"
                Description = "Reports unsupported token-file configuration and recommends GRACE_TOKEN."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = AuthOidcConfigurationCheckId
                Category = "Authentication"
                Title = "OIDC environment configuration"
                Description = "Checks OIDC M2M and CLI environment completeness without token requests or secure-store reads."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = StateDbFilePresentCheckId
                Category = "Local state"
                Title = "Local state database file"
                Description = "Checks the configured .grace/grace-local.db path without creating directories or files."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = StateDbReadOnlyOpenCheckId
                Category = "Local state"
                Title = "Local state read-only open"
                Description = "Opens the local state SQLite database in read-only mode without initialization, migration, or repair."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = StateDbSchemaVersionCheckId
                Category = "Local state"
                Title = "Local state schema version"
                Description = "Reads the local state schema_version metadata without writing defaults."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = StateDbRequiredTablesCheckId
                Category = "Local state"
                Title = "Local state required tables"
                Description = "Verifies required local state tables from sqlite_master without creating schema objects."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = StateDbRequiredIndexesCheckId
                Category = "Local state"
                Title = "Local state required indexes"
                Description = "Verifies required local state indexes from sqlite_master without creating schema objects."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = StateDbIntegrityCheckId
                Category = "Local state"
                Title = "SQLite integrity check"
                Description = "Runs SQLite integrity_check read-only and reports the result without repair."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = StateDbForeignKeyCheckId
                Category = "Local state"
                Title = "SQLite foreign-key check"
                Description = "Runs SQLite foreign_key_check read-only and reports violations without mutation."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = ObjectCacheIndexReadableCheckId
                Category = "Object cache"
                Title = "Object-cache index readability"
                Description = "Reads object-cache metadata tables from the local state database without repairing rows."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = WorkingTreeScanCheckId
                Category = "Working tree"
                Title = "Working-tree drift scan"
                Description =
                    "Skipped by default; --full or an explicit working-tree.scan selection compares the working tree to a read-only local-state snapshot."
                DefaultEnabled = true
                SupportsOffline = true
            }
            {
                Id = "identity.auth-session"
                Category = "Identity"
                Title = "Authentication session"
                Description = "Reserved for a later authentication diagnostic. Scaffold only; no auth state is read in this slice."
                DefaultEnabled = false
                SupportsOffline = false
            }
            {
                Id = "server.connectivity"
                Category = "Server"
                Title = "Grace server connectivity"
                Description =
                    "Reserved compatibility catalog entry. Use the specific server.healthz, server.lifecycle, or server.auth-principal checks for active probes."
                DefaultEnabled = false
                SupportsOffline = false
            }
            {
                Id = ServerHealthzReachableCheckId
                Category = "Server"
                Title = "Grace server /healthz reachability"
                Description = "Resolves the effective Grace server URI and probes /healthz with a short timeout."
                DefaultEnabled = false
                SupportsOffline = false
            }
            {
                Id = ServerLifecycleHeadersCheckId
                Category = "Server"
                Title = "Grace server lifecycle headers"
                Description = "Surfaces Grace SDK lifecycle response headers from the anonymous /healthz probe."
                DefaultEnabled = false
                SupportsOffline = false
            }
            {
                Id = ServerAuthPrincipalAvailableCheckId
                Category = "Server"
                Title = "Grace authenticated principal"
                Description = "Optionally probes /authenticate/me only when a valid non-refreshing GRACE_TOKEN PAT is present."
                DefaultEnabled = false
                SupportsOffline = false
            }
        |]

    /// Converts command data into the required shape.
    let private tokenizeChecks (values: string array) =
        if isNull values then
            Array.empty
        else
            values
            |> Array.collect (fun value ->
                value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    ||| StringSplitOptions.TrimEntries
                ))
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.distinctBy (fun value -> value.ToUpperInvariant())

    /// Models the explicit access-assignment scope selected by mutually exclusive CLI options.
    type private SelectionError = Unknown of string array

    /// Coordinates category matches token behavior for this CLI command path.
    let private categoryMatchesToken (category: string) (token: string) =
        category.Equals(token, StringComparison.OrdinalIgnoreCase)
        || category
            .Replace(" ", "-", StringComparison.OrdinalIgnoreCase)
            .Equals(token, StringComparison.OrdinalIgnoreCase)

    /// Coordinates selected catalog entries behavior for this CLI command path.
    let private selectedCatalogEntries full offline listOnly requestedTokens =
        let profileEntries =
            catalog
            |> Array.filter (fun check ->
                (full || listOnly || check.DefaultEnabled)
                && (listOnly || not offline || check.SupportsOffline))

        if Array.isEmpty requestedTokens then
            Ok(profileEntries)
        else
            let selected = ResizeArray<LocalOutputDto.DoctorCheckDto>()
            let unknown = ResizeArray<string>()

            for token in requestedTokens do
                let matches =
                    catalog
                    |> Array.filter (fun check ->
                        check.Id.Equals(token, StringComparison.OrdinalIgnoreCase)
                        || categoryMatchesToken check.Category token)

                if Array.isEmpty matches then
                    unknown.Add(token)
                else
                    for check in matches do
                        if not (selected.Exists(fun existing -> existing.Id.Equals(check.Id, StringComparison.OrdinalIgnoreCase))) then
                            selected.Add(check)

            if unknown.Count > 0 then
                Error(Unknown(unknown |> Seq.toArray))
            else
                Ok(selected |> Seq.toArray)

    /// Validates checks from parsed options and returns a correlated GraceError when input is invalid.
    let private validateChecks parseResult full offline listOnly requestedTokens =
        match selectedCatalogEntries full offline listOnly requestedTokens with
        | Ok checks -> Ok checks
        | Error (Unknown unknown) ->
            let tokens = String.Join(", ", unknown)
            Error(GraceError.Create $"Unknown doctor check token: {tokens}." (getCorrelationId parseResult))

    /// Formats should render human report data for Spectre.Console output.
    let private shouldRenderHumanReport parseResult =
        not (parseResult |> json)
        && (parseResult |> hasOutput)

    /// Models the explicit access-assignment scope selected by mutually exclusive CLI options.
    type private ConfigurationInspectionState =
        | ConfigurationLoaded of Configuration.GraceConfigurationInspection
        | ConfigurationMissing of string
        | ConfigurationMalformed of path: string * message: string

    /// Models the explicit access-assignment scope selected by mutually exclusive CLI options.
    type private DoctorInspectionContext =
        {
            Full: bool
            Offline: bool
            RequestedTokens: string array
            ConfigurationState: ConfigurationInspectionState
            UserConfiguration: UserConfiguration.UserConfigurationInspection
            EnvironmentServerUri: string option
            AuthInspection: Auth.AuthInspection
            LocalStateInspection: Lazy<LocalStateDb.ReadOnlyLocalStateInspection>
            ServerHealthzInspection: Lazy<ServerProbeInspection>
        }

    and private EffectiveServerUri =
        | EffectiveServerUriResolved of Uri * string
        | EffectiveServerUriUnavailable of string

    and ServerProbeResponse =
        {
            StatusCode: HttpStatusCode
            ReasonPhrase: string
            LifecycleDiagnostics: ClientIdentity.LifecycleDiagnostics option
            Body: string
        }

    and ServerProbeInspection =
        | ServerProbeSucceeded of ServerProbeResponse
        | ServerProbeSkipped of string
        | ServerProbeInvalidUri of string
        | ServerProbeTimedOut of string
        | ServerProbeTlsFailed of string
        | ServerProbeConnectionFailed of string
        | ServerProbeFailed of string

    /// Normalizes Grace ids for optional text by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeOptionalText value = if String.IsNullOrWhiteSpace(value) then None else Some(value.Trim())

    /// Defines structured data exchanged by CLI helpers.
    type ServerProbeRequest = { BaseUri: Uri; RelativePath: string; BearerToken: string option; Timeout: TimeSpan }

    /// Normalizes Grace ids for bearer token by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeBearerToken (token: string) =
        let trimmed = token.Trim()

        if trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            trimmed.Substring("Bearer ".Length).Trim()
        else
            trimmed

    /// Builds command objects or parameters for execution.
    let private buildProbeUri (baseUri: Uri) (relativePath: string) =
        if not baseUri.IsAbsoluteUri then
            Error "Effective server URI is not absolute."
        elif
            not (baseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            && not (baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        then
            Error $"Effective server URI uses unsupported scheme '{baseUri.Scheme}'."
        else
            let baseText = baseUri.AbsoluteUri.TrimEnd('/')
            let pathText = relativePath.TrimStart('/')
            Ok(Uri($"{baseText}/{pathText}"))

    /// Coordinates default server probe behavior for this CLI command path.
    let private defaultServerProbe (request: ServerProbeRequest) =
        task {
            match buildProbeUri request.BaseUri request.RelativePath with
            | Error message -> return ServerProbeInvalidUri message
            | Ok requestUri ->
                use cancellationSource = new CancellationTokenSource(request.Timeout)
                use httpClient = new HttpClient()
                httpClient.Timeout <- Timeout.InfiniteTimeSpan
                ClientIdentity.applyHeaders httpClient |> ignore

                use httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri)

                match request.BearerToken with
                | Some token -> httpRequest.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                | None -> ()

                try
                    use! response = httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationSource.Token)
                    let! body = response.Content.ReadAsStringAsync(cancellationSource.Token)

                    return
                        ServerProbeSucceeded
                            {
                                StatusCode = response.StatusCode
                                ReasonPhrase =
                                    response.ReasonPhrase
                                    |> Option.ofObj
                                    |> Option.defaultValue String.Empty
                                LifecycleDiagnostics = ClientIdentity.parseLifecycleDiagnostics response
                                Body = body
                            }
                with
                | :? OperationCanceledException when cancellationSource.IsCancellationRequested ->
                    return ServerProbeTimedOut $"Timed out after {int request.Timeout.TotalMilliseconds} ms while probing {requestUri}."
                | :? HttpRequestException as ex when
                    not (isNull ex.InnerException)
                    && ex
                        .InnerException
                        .GetType()
                        .Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                    ->
                    return ServerProbeTlsFailed $"TLS negotiation failed while probing {requestUri}: {ex.Message}"
                | :? HttpRequestException as ex -> return ServerProbeConnectionFailed $"Could not connect to {requestUri}: {ex.Message}"
                | ex -> return ServerProbeFailed $"Unexpected server probe failure for {requestUri}: {ex.Message}"
        }

    let mutable private serverProbeFactory: ServerProbeRequest -> Task<ServerProbeInspection> = defaultServerProbe

    /// Coordinates set server probe factory for tests behavior for this CLI command path.
    let setServerProbeFactoryForTests factory = serverProbeFactory <- factory

    /// Coordinates reset server probe factory for tests behavior for this CLI command path.
    let resetServerProbeFactoryForTests () = serverProbeFactory <- defaultServerProbe

    /// Resolves effective server uri from command options, configuration, or local state.
    let private resolveEffectiveServerUri (configurationState: ConfigurationInspectionState) environmentServerUri =
        /// Tries to map resolve and returns a GraceError instead of throwing on unsupported input.
        let tryResolve source value =
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, uri when
                uri.Scheme = Uri.UriSchemeHttp
                || uri.Scheme = Uri.UriSchemeHttps
                ->
                EffectiveServerUriResolved(uri, source)
            | true, uri -> EffectiveServerUriUnavailable $"Effective server URI from {source} uses unsupported scheme '{uri.Scheme}'."
            | false, _ -> EffectiveServerUriUnavailable $"Effective server URI from {source} is not an absolute URI: {value}."

        match environmentServerUri with
        | Some value -> tryResolve Constants.EnvironmentVariables.GraceServerUri value
        | None ->
            match configurationState with
            | ConfigurationLoaded inspection -> tryResolve Constants.GraceConfigFileName inspection.Configuration.ServerUri
            | ConfigurationMissing _ ->
                EffectiveServerUriUnavailable
                    $"No effective server URI was available because {Constants.GraceConfigFileName} was not found and {Constants.EnvironmentVariables.GraceServerUri} is not set."
            | ConfigurationMalformed _ ->
                EffectiveServerUriUnavailable
                    $"No effective server URI was available because {ConfigFileParseCheckId} failed and {Constants.EnvironmentVariables.GraceServerUri} is not set."

    /// Coordinates valid environment pat behavior for this CLI command path.
    let private validEnvironmentPat (authInspection: Auth.AuthInspection) =
        if authInspection.GraceTokenValid then
            Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceToken)
            |> normalizeOptionalText
            |> Option.map normalizeBearerToken
        else
            None

    /// Coordinates probe server behavior for this CLI command path.
    let private probeServer relativePath bearerToken effectiveServerUri =
        match effectiveServerUri with
        | EffectiveServerUriUnavailable message -> Task.FromResult(ServerProbeInvalidUri message)
        | EffectiveServerUriResolved (uri, _) ->
            {
                BaseUri = uri
                RelativePath = relativePath
                BearerToken = bearerToken
                Timeout = TimeSpan.FromMilliseconds(float DoctorServerProbeTimeoutMilliseconds)
            }
            |> serverProbeFactory

    /// Builds the doctor inspection context from CLI options, environment, and local Grace configuration.
    let private createInspectionContext full offline requestedTokens =
        let configurationState =
            match Configuration.tryInspectCurrentDirectoryConfiguration () with
            | Ok inspection -> ConfigurationLoaded inspection
            | Error (Configuration.ConfigurationFileNotFound message) -> ConfigurationMissing message
            | Error (Configuration.ConfigurationFileMalformed (path, message)) -> ConfigurationMalformed(path, message)

        let localStateDbPath =
            match configurationState with
            | ConfigurationLoaded inspection -> inspection.Configuration.GraceStatusFile
            | ConfigurationMalformed (path, _) -> Path.Combine(Path.GetDirectoryName(path), Constants.GraceLocalStateDbFileName)
            | ConfigurationMissing _ -> Path.Combine(Environment.CurrentDirectory, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

        let environmentServerUri =
            Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
            |> normalizeOptionalText

        let effectiveServerUri = resolveEffectiveServerUri configurationState environmentServerUri

        {
            Full = full
            Offline = offline
            RequestedTokens = requestedTokens
            ConfigurationState = configurationState
            UserConfiguration = UserConfiguration.tryInspectUserConfiguration ()
            EnvironmentServerUri = environmentServerUri
            AuthInspection = Auth.inspectAuthEnvironment ()
            LocalStateInspection = lazy (LocalStateDb.inspectReadOnly localStateDbPath)
            ServerHealthzInspection =
                lazy
                    (if offline then
                         ServerProbeSkipped "Skipped because --offline is set; doctor did not perform any server network probe."
                     else
                         probeServer "healthz" None effectiveServerUri
                         |> fun task -> task.GetAwaiter().GetResult())
        }

    /// Coordinates check result behavior for this CLI command path.
    let private checkResult checkId category title status severity summary : LocalOutputDto.DoctorCheckResultDto =
        { Id = checkId; Category = category; Title = title; Status = status; Severity = severity; Summary = summary }

    /// Coordinates skipped behavior for this CLI command path.
    let private skipped (check: LocalOutputDto.DoctorCheckDto) summary = checkResult check.Id check.Category check.Title "Skipped" "Info" summary

    /// Checks whether missing required fields is true for the parsed command input.
    let private missingRequiredFields (fields: Auth.AuthEnvironmentFieldStatus array) =
        fields
        |> Array.filter (fun field -> field.Required && not field.IsSet)
        |> Array.map (fun field -> field.Name)

    /// Coordinates present field names behavior for this CLI command path.
    let private presentFieldNames (fields: Auth.AuthEnvironmentFieldStatus array) =
        fields
        |> Array.filter (fun field -> field.IsSet)
        |> Array.map (fun field -> field.Name)

    /// Formats field names values into the text shown in Spectre.Console tables or command output.
    let private formatFieldNames (names: string array) = if Array.isEmpty names then "none" else String.Join(", ", names)

    /// Formats list or none values into the text shown in Spectre.Console tables or command output.
    let private formatListOrNone (values: string array) = if Array.isEmpty values then "none" else String.Join(", ", values)

    /// Coordinates lifecycle summary behavior for this CLI command path.
    let private lifecycleSummary (diagnostics: ClientIdentity.LifecycleDiagnostics) =
        let details = ResizeArray<string>()

        match diagnostics.Status with
        | Some status -> details.Add($"status={status}")
        | None -> ()

        match diagnostics.UnsupportedAfter with
        | Some unsupportedAfter -> details.Add($"unsupportedAfter={unsupportedAfter}")
        | None -> ()

        match diagnostics.MinimumVersion with
        | Some minimumVersion -> details.Add($"minimumVersion={minimumVersion}")
        | None -> ()

        match diagnostics.RecommendedVersion with
        | Some recommendedVersion -> details.Add($"recommendedVersion={recommendedVersion}")
        | None -> ()

        match diagnostics.UpdateUrl, diagnostics.UpdateUrlIsHttps with
        | Some updateUrl, Some true -> details.Add($"updateUrl={updateUrl}")
        | Some _, Some false -> details.Add("updateUrl=<suppressed because server value was not HTTPS>")
        | Some updateUrl, _ -> details.Add($"updateUrl={updateUrl}")
        | None, _ -> ()

        String.Join("; ", details)

    /// Coordinates lifecycle status needs user action behavior for this CLI command path.
    let private lifecycleStatusNeedsUserAction (diagnostics: ClientIdentity.LifecycleDiagnostics) =
        match diagnostics.Status with
        | Some status when status.Equals("deprecated", StringComparison.OrdinalIgnoreCase) -> true
        | Some status when status.Equals("unsupported", StringComparison.OrdinalIgnoreCase) -> true
        | _ -> false

    /// Checks whether lifecycle has warning is true for the parsed command input.
    let private lifecycleHasWarning (diagnostics: ClientIdentity.LifecycleDiagnostics) =
        lifecycleStatusNeedsUserAction diagnostics
        || diagnostics.UnsupportedAfterIsMalformed
        || diagnostics.UpdateUrlIsHttps = Some false

    /// Coordinates healthz lifecycle rejection summary behavior for this CLI command path.
    let private healthzLifecycleRejectionSummary (response: ServerProbeResponse) =
        response.LifecycleDiagnostics
        |> Option.filter lifecycleStatusNeedsUserAction
        |> Option.map (fun diagnostics ->
            $"Reached /healthz with HTTP {(int response.StatusCode)} {response.ReasonPhrase}, and Grace SDK lifecycle headers reported client status requiring action: {lifecycleSummary diagnostics}. Update the Grace CLI/SDK or otherwise address the lifecycle status before retrying.")

    /// Evaluates is successful http status against parsed options and command state.
    let private isSuccessfulHttpStatus (statusCode: HttpStatusCode) = int statusCode >= 200 && int statusCode <= 299

    /// Coordinates probe failure summary behavior for this CLI command path.
    let private probeFailureSummary inspection =
        match inspection with
        | ServerProbeSkipped message -> message
        | ServerProbeInvalidUri message -> message
        | ServerProbeTimedOut message -> message
        | ServerProbeTlsFailed message -> message
        | ServerProbeConnectionFailed message -> message
        | ServerProbeFailed message -> message
        | ServerProbeSucceeded response -> $"Server returned HTTP {(int response.StatusCode)} {response.ReasonPhrase}."

    /// Coordinates local state unavailable summary behavior for this CLI command path.
    let private localStateUnavailableSummary checkId (inspection: LocalStateDb.ReadOnlyLocalStateInspection) =
        if not inspection.ParentDirectoryExists then
            $"Skipped because {StateDbFilePresentCheckId} did not find the local .grace directory for {inspection.DbPath}; doctor did not create it."
        elif inspection.DbPathIsDirectory then
            $"Skipped because {StateDbFilePresentCheckId} found a directory at the database path {inspection.DbPath}."
        elif not inspection.DbFileExists then
            $"Skipped because {StateDbFilePresentCheckId} did not find {inspection.DbPath}; doctor did not create it."
        else
            match inspection.OpenError with
            | Some message -> $"Skipped because {StateDbReadOnlyOpenCheckId} could not open the database read-only: {message}"
            | None -> $"Skipped because {checkId} requires a read-only local state database inspection."

    /// Sends or polls the working tree scan explicitly requested OIDC/auth request used by interactive CLI authentication.
    let private workingTreeScanExplicitlyRequested (context: DoctorInspectionContext) =
        context.RequestedTokens
        |> Array.exists (fun token ->
            token.Equals(WorkingTreeScanCheckId, StringComparison.OrdinalIgnoreCase)
            || categoryMatchesToken "Working tree" token)

    /// Coordinates count differences behavior for this CLI command path.
    let private countDifferences differenceType (differences: List<FileSystemDifference>) =
        differences
        |> Seq.filter (fun difference -> difference.DifferenceType = differenceType)
        |> Seq.length

    let private workingTreeScanRemediation =
        "Run `grace maintenance scan` to inspect details or `grace maintenance update-index` when you intentionally want local state to match the current working tree."

    /// Models the explicit access-assignment scope selected by mutually exclusive CLI options.
    type private WorkingTreeScanSummaryResult =
        | WorkingTreeScanPrerequisiteSkipped of string
        | WorkingTreeScanFailed of string
        | WorkingTreeScanSummary of string

    let mutable private workingTreeScanner = Services.scanWorkingTreeForDifferencesReadOnly

    /// Coordinates set working tree scanner for tests behavior for this CLI command path.
    let setWorkingTreeScannerForTests scanner = workingTreeScanner <- scanner

    /// Coordinates reset working tree scanner for tests behavior for this CLI command path.
    let resetWorkingTreeScannerForTests () = workingTreeScanner <- Services.scanWorkingTreeForDifferencesReadOnly

    /// Coordinates working tree scan summary behavior for this CLI command path.
    let private workingTreeScanSummary (context: DoctorInspectionContext) =
        match context.ConfigurationState with
        | ConfigurationMissing _ ->
            WorkingTreeScanPrerequisiteSkipped $"Skipped because {ConfigFileDiscoverCheckId} did not find {Constants.GraceConfigFileName}."
        | ConfigurationMalformed _ -> WorkingTreeScanPrerequisiteSkipped $"Skipped because {ConfigFileParseCheckId} failed."
        | ConfigurationLoaded inspection ->
            let localStateInspection = context.LocalStateInspection.Value

            if not localStateInspection.OpenedReadOnly then
                WorkingTreeScanPrerequisiteSkipped(localStateUnavailableSummary WorkingTreeScanCheckId localStateInspection)
            elif localStateInspection.MissingRequiredTables.Length > 0 then
                WorkingTreeScanPrerequisiteSkipped $"Skipped because {StateDbRequiredTablesCheckId} did not find all required local-state tables."
            elif localStateInspection.SchemaVersion
                 <> Some ExpectedLocalStateSchemaVersion then
                WorkingTreeScanPrerequisiteSkipped
                    $"Skipped because {StateDbSchemaVersionCheckId} did not find schema_version {ExpectedLocalStateSchemaVersion}."
            else
                let configuration = inspection.Configuration

                let snapshotResult =
                    LocalStateDb.readStatusSnapshotReadOnly
                        configuration.GraceStatusFile
                        configuration.OwnerId
                        configuration.OrganizationId
                        configuration.RepositoryId
                    |> fun task -> task.GetAwaiter().GetResult()

                match snapshotResult with
                | Error message -> WorkingTreeScanPrerequisiteSkipped $"Skipped because the read-only local-state snapshot could not be read: {message}"
                | Ok snapshot ->
                    let hasRootDirectoryVersion =
                        snapshot.Index.Values
                        |> Seq.exists (fun directoryVersion -> directoryVersion.RelativePath = Constants.RootDirectoryPath)

                    if not hasRootDirectoryVersion then
                        WorkingTreeScanSummary(
                            "Local-state snapshot is missing the root directory version; doctor did not rebuild it. "
                            + workingTreeScanRemediation
                        )
                    else
                        let scanInput: Services.WorkingTreeScanInput =
                            {
                                RootDirectory = inspection.RootDirectory
                                GraceDirectory = configuration.GraceDirectory
                                GraceStatusFile = configuration.GraceStatusFile
                                DirectoryIgnoreEntries =
                                    inspection.Ignore.DirectoryEntries
                                    |> Array.map (fun entry -> $"*{entry}")
                                    |> Array.distinct
                                FileIgnoreEntries = inspection.Ignore.FileEntries
                            }

                        let scanResult =
                            workingTreeScanner scanInput snapshot
                            |> fun task -> task.GetAwaiter().GetResult()

                        match scanResult with
                        | Error message -> WorkingTreeScanFailed $"Working-tree scan failed without updating local state: {message}"
                        | Ok differences when differences.Count = 0 ->
                            WorkingTreeScanSummary "Working tree matches the read-only local-state snapshot. Doctor did not update the maintenance index."
                        | Ok differences ->
                            let added = countDifferences Add differences
                            let changed = countDifferences Change differences
                            let deleted = countDifferences Delete differences

                            WorkingTreeScanSummary
                                $"Working tree differs from the read-only local-state snapshot: {differences.Count} total, {added} added, {changed} changed, {deleted} deleted. {workingTreeScanRemediation}"

    /// Coordinates oidc summary behavior for this CLI command path.
    let private oidcSummary (auth: Auth.AuthInspection) =
        let m2mMissing = missingRequiredFields auth.M2mFields
        let cliMissing = missingRequiredFields auth.CliFields
        let m2mPresent = presentFieldNames auth.M2mFields
        let cliPresent = presentFieldNames auth.CliFields

        if auth.M2mComplete && auth.CliComplete then
            "OIDC M2M and CLI environment configuration are complete. No token was requested and no secure token storage was read."
        elif auth.M2mComplete then
            $"OIDC M2M environment configuration is complete; CLI OIDC is incomplete with missing required keys: {formatFieldNames cliMissing}. No token was requested."
        elif auth.CliComplete then
            $"OIDC CLI environment configuration is complete; M2M OIDC is incomplete with missing required keys: {formatFieldNames m2mMissing}. No browser, device-code flow, or secure token storage was used."
        elif auth.HasPartialM2m || auth.HasPartialCli then
            $"OIDC environment configuration is partial. M2M present keys: {formatFieldNames m2mPresent}; M2M missing required keys: {formatFieldNames m2mMissing}. CLI present keys: {formatFieldNames cliPresent}; CLI missing required keys: {formatFieldNames cliMissing}."
        else
            "No OIDC environment configuration was detected. Set the OIDC M2M or CLI environment keys, or provide GRACE_TOKEN."

    /// Coordinates result for check behavior for this CLI command path.
    let private resultForCheck (context: DoctorInspectionContext) (check: LocalOutputDto.DoctorCheckDto) : LocalOutputDto.DoctorCheckResultDto =
        /// Coordinates ok behavior for this CLI command path.
        let ok summary = checkResult check.Id check.Category check.Title "Ok" "Info" summary
        /// Coordinates warning behavior for this CLI command path.
        let warning summary = checkResult check.Id check.Category check.Title "Warning" "Warning" summary
        /// Coordinates failed behavior for this CLI command path.
        let failed summary = checkResult check.Id check.Category check.Title "Failed" "Error" summary

        match check.Id with
        | CliCatalogCheckId -> ok "Doctor check catalog is available."
        | AuthSourceDetectedCheckId ->
            let auth = context.AuthInspection

            if auth.HasUsableCredentialSource then
                ok $"Likely authentication source: {auth.ActiveSource}. Doctor did not acquire, refresh, store, or validate credentials with a provider."
            elif auth.GraceTokenError.IsSome then
                warning $"Likely authentication source: {auth.ActiveSource}. {auth.GraceTokenError.Value}"
            elif auth.GraceTokenFilePresent then
                warning
                    $"Likely authentication source: {auth.ActiveSource}. Local token files are unsupported; unset {Constants.EnvironmentVariables.GraceTokenFile} and set {Constants.EnvironmentVariables.GraceToken}."
            elif auth.HasPartialM2m || auth.HasPartialCli then
                warning $"No complete authentication source was detected. {oidcSummary auth}"
            else
                warning $"No authentication source was detected. Set {Constants.EnvironmentVariables.GraceToken}, OIDC M2M variables, or OIDC CLI variables."
        | AuthEnvTokenValidCheckId ->
            let auth = context.AuthInspection

            if not auth.GraceTokenPresent then
                skipped check $"{Constants.EnvironmentVariables.GraceToken} is not set; no environment PAT was validated."
            elif auth.GraceTokenValid then
                ok
                    $"{Constants.EnvironmentVariables.GraceToken} is set and has a valid Grace PAT shape. The token value was not printed or verified against the server."
            else
                failed (
                    auth.GraceTokenError
                    |> Option.defaultValue
                        $"{Constants.EnvironmentVariables.GraceToken} is not a valid Grace PAT. Set a token with prefix {Grace.Types.PersonalAccessToken.TokenPrefix}."
                )
        | AuthTokenFileUnsupportedCheckId ->
            let auth = context.AuthInspection

            if auth.GraceTokenFilePresent then
                failed
                    $"Local token files are unsupported. Unset {Constants.EnvironmentVariables.GraceTokenFile} and set {Constants.EnvironmentVariables.GraceToken} to a Grace PAT."
            else
                ok $"{Constants.EnvironmentVariables.GraceTokenFile} is not set."
        | AuthOidcConfigurationCheckId ->
            let auth = context.AuthInspection
            let summary = oidcSummary auth

            if auth.M2mComplete || auth.CliComplete then ok summary
            elif auth.HasPartialM2m || auth.HasPartialCli then warning summary
            else skipped check summary
        | ServerHealthzReachableCheckId ->
            match context.ServerHealthzInspection.Value with
            | ServerProbeSkipped message -> skipped check message
            | ServerProbeSucceeded response when isSuccessfulHttpStatus response.StatusCode ->
                ok $"Reached /healthz with HTTP {(int response.StatusCode)} {response.ReasonPhrase}; timeout was {DoctorServerProbeTimeoutMilliseconds} ms."
            | ServerProbeSucceeded response ->
                match healthzLifecycleRejectionSummary response with
                | Some summary -> failed summary
                | None ->
                    failed
                        $"Reached /healthz but the server returned HTTP {(int response.StatusCode)} {response.ReasonPhrase}; check server health and GRACE_SERVER_URI."
            | ServerProbeTimedOut message -> failed $"{message} Check the effective server URI, network path, proxy settings, or server startup state."
            | ServerProbeTlsFailed message -> failed $"{message} Verify the server certificate and HTTPS endpoint configuration."
            | ServerProbeConnectionFailed message -> failed $"{message} Check that the Grace server is running and reachable."
            | ServerProbeInvalidUri message ->
                failed
                    $"{message} Set {Constants.EnvironmentVariables.GraceServerUri} or {Constants.GraceConfigFileName} serverUri to an absolute http/https URI."
            | ServerProbeFailed message -> failed message
        | ServerLifecycleHeadersCheckId ->
            match context.ServerHealthzInspection.Value with
            | ServerProbeSkipped message -> skipped check message
            | ServerProbeSucceeded response when not (isSuccessfulHttpStatus response.StatusCode) ->
                match healthzLifecycleRejectionSummary response with
                | Some summary -> failed summary
                | None ->
                    failed
                        $"Could not inspect lifecycle headers because /healthz returned HTTP {(int response.StatusCode)} {response.ReasonPhrase}; check server health and GRACE_SERVER_URI."
            | ServerProbeSucceeded response ->
                match response.LifecycleDiagnostics with
                | None -> ok "No Grace SDK lifecycle headers were returned by /healthz."
                | Some diagnostics when lifecycleHasWarning diagnostics ->
                    warning
                        $"Lifecycle headers were returned by /healthz with advisory warnings: {lifecycleSummary diagnostics}. Malformed unsupported-after dates or non-HTTPS update URLs should be corrected server-side."
                | Some diagnostics -> ok $"Lifecycle headers were returned by /healthz: {lifecycleSummary diagnostics}."
            | failure -> failed $"Could not inspect lifecycle headers because /healthz did not produce a response: {probeFailureSummary failure}"
        | ServerAuthPrincipalAvailableCheckId ->
            if context.Offline then
                skipped check "Skipped because --offline is set; doctor did not perform the authenticated principal probe."
            else
                match validEnvironmentPat context.AuthInspection with
                | None ->
                    skipped
                        check
                        $"Skipped because no valid non-refreshing {Constants.EnvironmentVariables.GraceToken} PAT is available. Doctor did not use OIDC, token refresh, secure storage, browser, device-code, or SDK auth provider paths."
                | Some token ->
                    let effectiveServerUri = resolveEffectiveServerUri context.ConfigurationState context.EnvironmentServerUri

                    match probeServer "authenticate/me" (Some token) effectiveServerUri
                          |> fun task -> task.GetAwaiter().GetResult()
                        with
                    | ServerProbeSucceeded response when isSuccessfulHttpStatus response.StatusCode ->
                        ok
                            $"Authenticated principal endpoint /authenticate/me returned HTTP {(int response.StatusCode)} {response.ReasonPhrase} using the non-refreshing {Constants.EnvironmentVariables.GraceToken} PAT source."
                    | ServerProbeSucceeded response ->
                        failed
                            $"Authenticated principal endpoint /authenticate/me returned HTTP {(int response.StatusCode)} {response.ReasonPhrase}; verify the PAT, claims, and server authentication configuration."
                    | ServerProbeTimedOut message -> failed $"{message} Check the effective server URI, network path, proxy settings, or server startup state."
                    | ServerProbeTlsFailed message -> failed $"{message} Verify the server certificate and HTTPS endpoint configuration."
                    | ServerProbeConnectionFailed message -> failed $"{message} Check that the Grace server is running and reachable."
                    | ServerProbeInvalidUri message ->
                        failed
                            $"{message} Set {Constants.EnvironmentVariables.GraceServerUri} or {Constants.GraceConfigFileName} serverUri to an absolute http/https URI."
                    | ServerProbeSkipped message -> skipped check message
                    | ServerProbeFailed message -> failed message
        | StateDbFilePresentCheckId ->
            let inspection = context.LocalStateInspection.Value

            if not inspection.ParentDirectoryExists then
                warning
                    $"Local state directory was not found for {inspection.DbPath}; doctor did not create it. Run a Grace command that initializes local state when you are ready to create evidence."
            elif inspection.DbPathIsDirectory then
                failed
                    $"Local state database path is a directory: {inspection.DbPath}. Move the directory aside or restore a valid {Constants.GraceLocalStateDbFileName} file."
            elif not inspection.DbFileExists then
                warning
                    $"Local state database was not found at {inspection.DbPath}; doctor did not create it. Run a Grace command that initializes local state when local status is expected."
            else
                ok $"Local state database file exists at {inspection.DbPath}."
        | StateDbReadOnlyOpenCheckId ->
            let inspection = context.LocalStateInspection.Value

            if inspection.OpenedReadOnly then
                ok "Opened the local state database with SQLite read-only mode. No initialization, migration, WAL change, or repair was attempted."
            elif inspection.DbPathIsDirectory then
                failed $"Could not open local state read-only because the DB path is a directory: {inspection.DbPath}."
            elif inspection.ParentDirectoryExists
                 && inspection.DbFileExists then
                let openError =
                    inspection.OpenError
                    |> Option.defaultValue "unknown SQLite error"

                failed $"Could not open local state read-only: {openError}"
            else
                skipped check (localStateUnavailableSummary StateDbReadOnlyOpenCheckId inspection)
        | StateDbSchemaVersionCheckId ->
            let inspection = context.LocalStateInspection.Value

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbSchemaVersionCheckId inspection)
            else
                match inspection.SchemaVersion with
                | Some version when version = ExpectedLocalStateSchemaVersion -> ok $"Local state schema_version is {ExpectedLocalStateSchemaVersion}."
                | Some version ->
                    failed
                        $"Local state schema_version is {version}; expected {ExpectedLocalStateSchemaVersion}. Doctor did not migrate, recreate, or move corrupt database files."
                | None -> failed "Local state schema_version metadata is missing or unreadable. Doctor did not write default metadata."
        | StateDbRequiredTablesCheckId ->
            let inspection = context.LocalStateInspection.Value

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbRequiredTablesCheckId inspection)
            elif Array.isEmpty inspection.MissingRequiredTables then
                ok "All required local state tables are present."
            else
                failed $"Missing required local state tables: {formatListOrNone inspection.MissingRequiredTables}. Doctor did not create schema objects."
        | StateDbRequiredIndexesCheckId ->
            let inspection = context.LocalStateInspection.Value

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbRequiredIndexesCheckId inspection)
            elif Array.isEmpty inspection.MissingRequiredIndexes then
                ok "All required local state indexes are present."
            else
                failed $"Missing required local state indexes: {formatListOrNone inspection.MissingRequiredIndexes}. Doctor did not create schema objects."
        | StateDbIntegrityCheckId ->
            let inspection = context.LocalStateInspection.Value

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbIntegrityCheckId inspection)
            elif inspection.IntegrityCheckRows.Length = 1
                 && inspection
                     .IntegrityCheckRows[ 0 ]
                     .Equals("ok", StringComparison.OrdinalIgnoreCase) then
                ok "SQLite integrity_check returned ok."
            else
                failed $"SQLite integrity_check reported: {formatListOrNone inspection.IntegrityCheckRows}. Doctor did not repair or rewrite the database."
        | StateDbForeignKeyCheckId ->
            let inspection = context.LocalStateInspection.Value

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbForeignKeyCheckId inspection)
            elif Array.isEmpty inspection.ForeignKeyViolations then
                ok "SQLite foreign_key_check returned no violations."
            else
                failed
                    $"SQLite foreign_key_check reported violations: {formatListOrNone inspection.ForeignKeyViolations}. Doctor did not repair object-cache rows."
        | ObjectCacheIndexReadableCheckId ->
            let inspection = context.LocalStateInspection.Value

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary ObjectCacheIndexReadableCheckId inspection)
            else
                match inspection.ObjectCacheReadable with
                | Some true -> ok "Object-cache metadata tables are readable from the local state database without mutation."
                | Some false ->
                    let objectCacheError =
                        inspection.ObjectCacheError
                        |> Option.defaultValue "unknown SQLite error"

                    failed $"Object-cache metadata is not readable: {objectCacheError}. Doctor did not repair object-cache rows."
                | None -> skipped check "Object-cache metadata readability was not attempted."
        | WorkingTreeScanCheckId ->
            if
                not context.Full
                && not (workingTreeScanExplicitlyRequested context)
            then
                skipped
                    check
                    $"Skipped in the default profile because working-tree drift scanning can hash repository files. Re-run with --full or --check {WorkingTreeScanCheckId}. {workingTreeScanRemediation}"
            else
                match workingTreeScanSummary context with
                | WorkingTreeScanPrerequisiteSkipped message -> skipped check message
                | WorkingTreeScanFailed message -> failed message
                | WorkingTreeScanSummary summary when summary.StartsWith("Working tree matches", StringComparison.Ordinal) -> ok summary
                | WorkingTreeScanSummary summary -> warning summary
        | ConfigFileDiscoverCheckId ->
            match context.ConfigurationState with
            | ConfigurationLoaded inspection -> ok $"Found {Constants.GraceConfigFileName} at {inspection.Path}; repository root {inspection.RootDirectory}."
            | ConfigurationMissing message -> warning $"{message} No configuration file was created."
            | ConfigurationMalformed (path, _) -> ok $"Found {Constants.GraceConfigFileName} at {path}, but parsing is reported by {ConfigFileParseCheckId}."
        | ConfigFileParseCheckId ->
            match context.ConfigurationState with
            | ConfigurationLoaded inspection -> ok $"Parsed {inspection.Path} without rewriting it."
            | ConfigurationMissing _ -> skipped check $"Skipped because {ConfigFileDiscoverCheckId} did not find {Constants.GraceConfigFileName}."
            | ConfigurationMalformed (_, message) -> failed $"Could not parse {Constants.GraceConfigFileName}: {message}"
        | ConfigRepositoryIdentityCheckId ->
            match context.ConfigurationState with
            | ConfigurationLoaded inspection ->
                let configuration = inspection.Configuration

                let missing =
                    [|
                        if configuration.OwnerId = OwnerId.Empty then "ownerId"

                        if String.IsNullOrWhiteSpace(string configuration.OwnerName) then "ownerName"

                        if configuration.OrganizationId = OrganizationId.Empty then "organizationId"

                        if String.IsNullOrWhiteSpace(string configuration.OrganizationName) then
                            "organizationName"

                        if configuration.RepositoryId = RepositoryId.Empty then "repositoryId"

                        if String.IsNullOrWhiteSpace(string configuration.RepositoryName) then
                            "repositoryName"

                        if configuration.BranchId = BranchId.Empty then "branchId"

                        if String.IsNullOrWhiteSpace(string configuration.BranchName) then "branchName"
                    |]

                if Array.isEmpty missing then
                    ok $"Repository identity is populated for root {inspection.RootDirectory}."
                else
                    let missingText = String.Join(", ", missing)
                    warning $"Repository identity has blank or empty values: {missingText}."
            | ConfigurationMissing _ -> skipped check $"Skipped because {ConfigFileDiscoverCheckId} did not find {Constants.GraceConfigFileName}."
            | ConfigurationMalformed _ -> skipped check $"Skipped because {ConfigFileParseCheckId} failed."
        | ConfigServerUriCheckId ->
            match context.ConfigurationState with
            | ConfigurationLoaded inspection ->
                match Uri.TryCreate(inspection.Configuration.ServerUri, UriKind.Absolute) with
                | true, uri when
                    uri.Scheme = Uri.UriSchemeHttp
                    || uri.Scheme = Uri.UriSchemeHttps
                    ->
                    let normalizedUri = uri.AbsoluteUri.TrimEnd('/')
                    ok $"Configured server URI is {normalizedUri}."
                | true, uri -> warning $"Configured server URI uses unsupported scheme '{uri.Scheme}'."
                | false, _ -> failed $"Configured server URI is not an absolute URI: {inspection.Configuration.ServerUri}."
            | ConfigurationMissing _ -> skipped check $"Skipped because {ConfigFileDiscoverCheckId} did not find {Constants.GraceConfigFileName}."
            | ConfigurationMalformed _ -> skipped check $"Skipped because {ConfigFileParseCheckId} failed."
        | ServerUriConsistencyCheckId ->
            match context.ConfigurationState, context.EnvironmentServerUri with
            | ConfigurationLoaded inspection, Some environmentServerUri ->
                match Uri.TryCreate(inspection.Configuration.ServerUri, UriKind.Absolute), Uri.TryCreate(environmentServerUri, UriKind.Absolute) with
                | (true, configured), (true, environmentValue) ->
                    let configuredValue = configured.AbsoluteUri.TrimEnd('/')
                    let environmentValue = environmentValue.AbsoluteUri.TrimEnd('/')

                    if configuredValue.Equals(environmentValue, StringComparison.OrdinalIgnoreCase) then
                        ok $"Configured server URI and {Constants.EnvironmentVariables.GraceServerUri} match."
                    else
                        warning $"Configured server URI '{configuredValue}' differs from {Constants.EnvironmentVariables.GraceServerUri} '{environmentValue}'."
                | _, (false, _) -> warning $"{Constants.EnvironmentVariables.GraceServerUri} is not an absolute URI: {environmentServerUri}."
                | (false, _), _ -> skipped check $"Skipped because {ConfigServerUriCheckId} did not produce a valid configured URI."
            | ConfigurationLoaded _, None ->
                ok $"{Constants.EnvironmentVariables.GraceServerUri} is not set; configuration server URI is the active local value."
            | ConfigurationMissing _, _ -> skipped check $"Skipped because {ConfigFileDiscoverCheckId} did not find {Constants.GraceConfigFileName}."
            | ConfigurationMalformed _, _ -> skipped check $"Skipped because {ConfigFileParseCheckId} failed."
        | UserConfigDiscoverCheckId ->
            if context.UserConfiguration.Exists then
                ok $"Found user configuration at {context.UserConfiguration.Path}."
            else
                warning $"User configuration file was not found at {context.UserConfiguration.Path}; no file was created."
        | UserConfigParseCheckId ->
            if not context.UserConfiguration.Exists then
                skipped check $"Skipped because {UserConfigDiscoverCheckId} did not find userconfig.json."
            else
                match context.UserConfiguration.ErrorMessage with
                | Some message -> failed $"Could not parse user configuration: {message}"
                | None -> ok $"Parsed user configuration at {context.UserConfiguration.Path} without rewriting it."
        | IgnoreEntriesParseCheckId ->
            match context.ConfigurationState with
            | ConfigurationLoaded inspection ->
                let ignore = inspection.Ignore

                match ignore.ErrorMessage with
                | Some message -> failed $"Could not parse {Constants.GraceIgnoreFileName}: {message}"
                | None when ignore.Exists ->
                    ok
                        $".graceignore has {ignore.Entries.Length} active entries: {ignore.FileEntries.Length} file patterns and {ignore.DirectoryEntries.Length} directory patterns."
                | None -> warning $".graceignore was not found at {ignore.Path}; no file was created."
            | ConfigurationMissing _ -> skipped check $"Skipped because {ConfigFileDiscoverCheckId} did not find {Constants.GraceConfigFileName}."
            | ConfigurationMalformed _ -> skipped check $"Skipped because {ConfigFileParseCheckId} failed."
        | _ -> skipped check "No diagnostic implementation is registered for this check."

    /// Lists only result for check data through the CLI service call and output pipeline.
    let private listOnlyResultForCheck (check: LocalOutputDto.DoctorCheckDto) : LocalOutputDto.DoctorCheckResultDto =
        {
            Id = check.Id
            Category = check.Category
            Title = check.Title
            Status = "Skipped"
            Severity = "Info"
            Summary = "Catalog discovery only; diagnostic probes were not run."
        }

    /// Coordinates summarize behavior for this CLI command path.
    let private summarize (checks: LocalOutputDto.DoctorCheckResultDto array) =
        /// Coordinates count behavior for this CLI command path.
        let count status =
            checks
            |> Array.filter (fun check -> check.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
            |> Array.length

        {
            LocalOutputDto.DoctorSummaryDto.Total = checks.Length
            LocalOutputDto.DoctorSummaryDto.Ok = count "Ok"
            LocalOutputDto.DoctorSummaryDto.Warning = count "Warning"
            LocalOutputDto.DoctorSummaryDto.Failed = count "Failed"
            LocalOutputDto.DoctorSummaryDto.Skipped = count "Skipped"
        }

    /// Coordinates report status behavior for this CLI command path.
    let private reportStatus (summary: LocalOutputDto.DoctorSummaryDto) =
        if summary.Failed > 0 then "Failed"
        elif summary.Warning > 0 then "Warning"
        else "Ok"

    /// Coordinates diagnostic exit code behavior for this CLI command path.
    let diagnosticExitCode strict (report: LocalOutputDto.DoctorReportDto) =
        if report.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) then
            1
        elif
            strict
            && report.Status.Equals("Warning", StringComparison.OrdinalIgnoreCase)
        then
            1
        else
            0

    /// Builds a doctor report that includes token inspection results alongside environment checks.
    let private createReportForChecksWithTokens full offline listOnly requestedTokens (checks: LocalOutputDto.DoctorCheckDto array) =
        let results =
            if listOnly then
                checks |> Array.map listOnlyResultForCheck
            else
                let context = createInspectionContext full offline requestedTokens
                checks |> Array.map (resultForCheck context)

        let summary = summarize results
        let status = reportStatus summary

        let report: LocalOutputDto.DoctorReportDto =
            {
                ReportVersion = ReportVersion
                Status = status
                ExitCode = 0
                Full = full
                Offline = offline
                Strict = false
                ListOnly = listOnly
                RequestedChecks = Array.empty
                Catalog = checks
                Checks = results
                Summary = summary
            }

        { report with ExitCode = diagnosticExitCode false report }

    /// Builds a doctor report from the collected environment and configuration checks.
    let createReportForChecks full offline listOnly (checks: LocalOutputDto.DoctorCheckDto array) =
        createReportForChecksWithTokens full offline listOnly Array.empty checks

    /// Coordinates with status behavior for this CLI command path.
    let withStatus status (report: LocalOutputDto.DoctorReportDto) =
        let checks =
            if report.Checks.Length = 0 then
                report.Checks
            else
                report.Checks
                |> Array.mapi (fun index check -> if index = 0 then { check with Status = status } else check)

        let summary = summarize checks
        let normalizedStatus = reportStatus summary
        let updated = { report with Status = normalizedStatus; Checks = checks; Summary = summary }
        { updated with ExitCode = diagnosticExitCode updated.Strict updated }

    /// Builds the final doctor report shown to the user or emitted as machine-readable output.
    let private createReport full offline strict listOnly requestedTokens checks =
        let report = createReportForChecksWithTokens full offline listOnly requestedTokens checks

        let report = { report with Strict = strict; RequestedChecks = requestedTokens }

        { report with ExitCode = diagnosticExitCode strict report }

    /// Renders human report results only when the selected output mode includes human-readable console text.
    let private renderHumanReport (report: LocalOutputDto.DoctorReportDto) =
        AnsiConsole.MarkupLine("[bold]Grace doctor[/]")
        AnsiConsole.MarkupLine($"Status: {report.Status}; checks: {report.Summary.Total}; exit code: {report.ExitCode}")

        let table = Table(Border = TableBorder.Rounded)
        table.AddColumn("Check") |> ignore
        table.AddColumn("Category") |> ignore
        table.AddColumn("Status") |> ignore
        table.AddColumn("Summary") |> ignore

        for check in report.Checks do
            table.AddRow(Markup.Escape(check.Id), Markup.Escape(check.Category), Markup.Escape(check.Status), Markup.Escape(check.Summary))
            |> ignore

        AnsiConsole.Write(table)

    /// Executes the invoke command by binding ParseResult values to the SDK request and CLI output contract.
    type Invoke() =
        inherit SynchronousCommandLineAction()

        /// Runs the asynchronous invoke action when System.CommandLine dispatches the parsed command.
        override _.Invoke(parseResult: ParseResult) : int =
            if
                (parseResult |> verbose)
                && not (parseResult |> hasSelect)
            then
                printParseResult parseResult

            let full = parseResult.GetValue(Options.full)
            let offline = parseResult.GetValue(Options.offline)
            let strict = parseResult.GetValue(Options.strict)
            let listChecks = parseResult.GetValue(Options.listChecks)

            let requestedTokens =
                parseResult.GetValue(Options.check)
                |> tokenizeChecks

            match validateChecks parseResult full offline listChecks requestedTokens with
            | Error error -> renderOutput parseResult (Error error)
            | Ok checks ->
                let report = createReport full offline strict listChecks requestedTokens checks
                let renderResult = renderOutput parseResult (Ok(GraceReturnValue.Create report (getCorrelationId parseResult)))

                if renderResult = 0
                   && shouldRenderHumanReport parseResult then
                    renderHumanReport report

                if renderResult <> 0 then renderResult else diagnosticExitCode strict report

    let Build =
        let doctorCommand =
            new Command("doctor", Description = "Inspect the local Grace environment with inert scaffolded diagnostics.")
            |> addOption Options.full
            |> addOption Options.offline
            |> addOption Options.listChecks
            |> addOption Options.check
            |> addOption Options.strict

        doctorCommand.Action <- Invoke()
        doctorCommand
