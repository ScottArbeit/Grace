namespace Grace.CLI.Command

open Grace.CLI
open Grace.CLI.Common
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Client
open Grace.Types.Common
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Threading
open System.Threading.Tasks

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
    let private ExpectedLocalStateSchemaVersion = "2"

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
                Description = "Reserved for a later server connectivity diagnostic. Scaffold only; no network probe is executed."
                DefaultEnabled = false
                SupportsOffline = false
            }
        |]

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

    type private SelectionError =
        | Unknown of string array
        | OfflineExcluded of string array

    let private selectedCatalogEntries full offline listOnly requestedTokens =
        let profileEntries =
            catalog
            |> Array.filter (fun check ->
                (full || listOnly || check.DefaultEnabled)
                && (not offline || check.SupportsOffline))

        if Array.isEmpty requestedTokens then
            Ok(profileEntries)
        else
            let selected = ResizeArray<LocalOutputDto.DoctorCheckDto>()
            let unknown = ResizeArray<string>()
            let excludedOffline = ResizeArray<string>()

            for token in requestedTokens do
                let matches =
                    catalog
                    |> Array.filter (fun check ->
                        check.Id.Equals(token, StringComparison.OrdinalIgnoreCase)
                        || check.Category.Equals(token, StringComparison.OrdinalIgnoreCase))

                if Array.isEmpty matches then
                    unknown.Add(token)
                else
                    for check in matches do
                        if offline && not check.SupportsOffline then
                            excludedOffline.Add(check.Id)
                        elif not (selected.Exists(fun existing -> existing.Id.Equals(check.Id, StringComparison.OrdinalIgnoreCase))) then
                            selected.Add(check)

            if unknown.Count > 0 then
                Error(Unknown(unknown |> Seq.toArray))
            elif excludedOffline.Count > 0 then
                let tokens =
                    excludedOffline
                    |> Seq.distinctBy (fun value -> value.ToUpperInvariant())
                    |> Seq.toArray

                Error(OfflineExcluded tokens)
            else
                Ok(selected |> Seq.toArray)

    let private validateChecks parseResult full offline listOnly requestedTokens =
        match selectedCatalogEntries full offline listOnly requestedTokens with
        | Ok checks -> Ok checks
        | Error (OfflineExcluded tokens) ->
            let tokens = String.Join(", ", tokens)
            Error(GraceError.Create $"Doctor check token is not available in offline mode: {tokens}." (getCorrelationId parseResult))
        | Error (Unknown unknown) ->
            let tokens = String.Join(", ", unknown)
            Error(GraceError.Create $"Unknown doctor check token: {tokens}." (getCorrelationId parseResult))

    let private shouldRenderHumanReport parseResult =
        not (parseResult |> json)
        && (parseResult |> hasOutput)

    type private ConfigurationInspectionState =
        | ConfigurationLoaded of Configuration.GraceConfigurationInspection
        | ConfigurationMissing of string
        | ConfigurationMalformed of string

    type private DoctorInspectionContext =
        {
            ConfigurationState: ConfigurationInspectionState
            UserConfiguration: UserConfiguration.UserConfigurationInspection
            EnvironmentServerUri: string option
            AuthInspection: Auth.AuthInspection
            LocalStateInspection: LocalStateDb.ReadOnlyLocalStateInspection
        }

    let private normalizeOptionalText value = if String.IsNullOrWhiteSpace(value) then None else Some(value.Trim())

    let private createInspectionContext () =
        let configurationState =
            match Configuration.tryInspectCurrentDirectoryConfiguration () with
            | Ok inspection -> ConfigurationLoaded inspection
            | Error (Configuration.ConfigurationFileNotFound message) -> ConfigurationMissing message
            | Error (Configuration.ConfigurationFileMalformed message) -> ConfigurationMalformed message

        let localStateDbPath =
            match configurationState with
            | ConfigurationLoaded inspection -> inspection.Configuration.GraceStatusFile
            | ConfigurationMissing _
            | ConfigurationMalformed _ -> Path.Combine(Environment.CurrentDirectory, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

        {
            ConfigurationState = configurationState
            UserConfiguration = UserConfiguration.tryInspectUserConfiguration ()
            EnvironmentServerUri =
                Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
                |> normalizeOptionalText
            AuthInspection = Auth.inspectAuthEnvironment ()
            LocalStateInspection = LocalStateDb.inspectReadOnly localStateDbPath
        }

    let private checkResult checkId category title status severity summary : LocalOutputDto.DoctorCheckResultDto =
        { Id = checkId; Category = category; Title = title; Status = status; Severity = severity; Summary = summary }

    let private skipped (check: LocalOutputDto.DoctorCheckDto) summary = checkResult check.Id check.Category check.Title "Skipped" "Info" summary

    let private missingRequiredFields (fields: Auth.AuthEnvironmentFieldStatus array) =
        fields
        |> Array.filter (fun field -> field.Required && not field.IsSet)
        |> Array.map (fun field -> field.Name)

    let private presentFieldNames (fields: Auth.AuthEnvironmentFieldStatus array) =
        fields
        |> Array.filter (fun field -> field.IsSet)
        |> Array.map (fun field -> field.Name)

    let private formatFieldNames (names: string array) = if Array.isEmpty names then "none" else String.Join(", ", names)

    let private formatListOrNone (values: string array) = if Array.isEmpty values then "none" else String.Join(", ", values)

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

    let private resultForCheck (context: DoctorInspectionContext) (check: LocalOutputDto.DoctorCheckDto) : LocalOutputDto.DoctorCheckResultDto =
        let ok summary = checkResult check.Id check.Category check.Title "Ok" "Info" summary
        let warning summary = checkResult check.Id check.Category check.Title "Warning" "Warning" summary
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
        | StateDbFilePresentCheckId ->
            let inspection = context.LocalStateInspection

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
            let inspection = context.LocalStateInspection

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
            let inspection = context.LocalStateInspection

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
            let inspection = context.LocalStateInspection

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbRequiredTablesCheckId inspection)
            elif Array.isEmpty inspection.MissingRequiredTables then
                ok "All required local state tables are present."
            else
                failed $"Missing required local state tables: {formatListOrNone inspection.MissingRequiredTables}. Doctor did not create schema objects."
        | StateDbRequiredIndexesCheckId ->
            let inspection = context.LocalStateInspection

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbRequiredIndexesCheckId inspection)
            elif Array.isEmpty inspection.MissingRequiredIndexes then
                ok "All required local state indexes are present."
            else
                failed $"Missing required local state indexes: {formatListOrNone inspection.MissingRequiredIndexes}. Doctor did not create schema objects."
        | StateDbIntegrityCheckId ->
            let inspection = context.LocalStateInspection

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
            let inspection = context.LocalStateInspection

            if not inspection.OpenedReadOnly then
                skipped check (localStateUnavailableSummary StateDbForeignKeyCheckId inspection)
            elif Array.isEmpty inspection.ForeignKeyViolations then
                ok "SQLite foreign_key_check returned no violations."
            else
                failed
                    $"SQLite foreign_key_check reported violations: {formatListOrNone inspection.ForeignKeyViolations}. Doctor did not repair object-cache rows."
        | ObjectCacheIndexReadableCheckId ->
            let inspection = context.LocalStateInspection

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
        | ConfigFileDiscoverCheckId ->
            match context.ConfigurationState with
            | ConfigurationLoaded inspection -> ok $"Found {Constants.GraceConfigFileName} at {inspection.Path}; repository root {inspection.RootDirectory}."
            | ConfigurationMissing message -> warning $"{message} No configuration file was created."
            | ConfigurationMalformed _ -> ok $"Found {Constants.GraceConfigFileName}, but parsing is reported by {ConfigFileParseCheckId}."
        | ConfigFileParseCheckId ->
            match context.ConfigurationState with
            | ConfigurationLoaded inspection -> ok $"Parsed {inspection.Path} without rewriting it."
            | ConfigurationMissing _ -> skipped check $"Skipped because {ConfigFileDiscoverCheckId} did not find {Constants.GraceConfigFileName}."
            | ConfigurationMalformed message -> failed $"Could not parse {Constants.GraceConfigFileName}: {message}"
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

    let private listOnlyResultForCheck (check: LocalOutputDto.DoctorCheckDto) : LocalOutputDto.DoctorCheckResultDto =
        {
            Id = check.Id
            Category = check.Category
            Title = check.Title
            Status = "Skipped"
            Severity = "Info"
            Summary = "Catalog discovery only; diagnostic probes were not run."
        }

    let private summarize (checks: LocalOutputDto.DoctorCheckResultDto array) =
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

    let private reportStatus (summary: LocalOutputDto.DoctorSummaryDto) =
        if summary.Failed > 0 then "Failed"
        elif summary.Warning > 0 then "Warning"
        else "Ok"

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

    let createReportForChecks full offline listOnly (checks: LocalOutputDto.DoctorCheckDto array) =
        let results =
            if listOnly then
                checks |> Array.map listOnlyResultForCheck
            else
                let context = createInspectionContext ()
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

    let private createReport full offline strict listOnly requestedTokens checks =
        let report = createReportForChecks full offline listOnly checks

        let report = { report with Strict = strict; RequestedChecks = requestedTokens }

        { report with ExitCode = diagnosticExitCode strict report }

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

    type Invoke() =
        inherit SynchronousCommandLineAction()

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
