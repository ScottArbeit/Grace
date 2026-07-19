namespace Grace.CLI.Command

open Grace.CLI.Common
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Diagnostics
open Grace.Shared

/// Provides the thin Grace Cache process launcher without referencing cache implementation assemblies.
module CacheCommand =

    [<Literal>]
    let private cacheInstanceMarker = "grace-cache-service"

    [<Literal>]
    let private enrollmentTokenEnvironmentVariable = "GRACE_CACHE_ENROLLMENT_TOKEN"

    /// Lists inherited credential variables that must never reach a long-lived cache child process.
    let private authenticationEnvironmentVariables =
        [
            Constants.EnvironmentVariables.GraceToken
            Constants.EnvironmentVariables.GraceTokenFile
            Constants.EnvironmentVariables.GraceAuthOidcAuthority
            Constants.EnvironmentVariables.GraceAuthOidcAudience
            Constants.EnvironmentVariables.GraceAuthOidcCliClientId
            Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort
            Constants.EnvironmentVariables.GraceAuthOidcCliScopes
            Constants.EnvironmentVariables.GraceAuthOidcM2mClientId
            Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
            Constants.EnvironmentVariables.GraceAuthOidcM2mScopes
            Constants.EnvironmentVariables.GraceAuthMicrosoftClientId
            Constants.EnvironmentVariables.GraceAuthMicrosoftClientSecret
            Constants.EnvironmentVariables.GraceAuthMicrosoftTenantId
            Constants.EnvironmentVariables.GraceAuthMicrosoftAuthority
            Constants.EnvironmentVariables.GraceAuthMicrosoftApiScope
            Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId
            enrollmentTokenEnvironmentVariable
        ]

    /// Defines process-boundary options accepted by the cache command group.
    module private Options =
        let executable =
            new Option<string>(
                "--cache-executable",
                Required = false,
                Description = "Path to the deployed Grace Cache executable. [default: GRACE_CACHE_EXECUTABLE or Grace.Cache]"
            )

        let endpoint = new Option<string>("--endpoint", Required = true, Description = "The exact public cache endpoint.")
        let allowHttp = new Option<bool>("--allow-http", Description = "Allow only the explicitly enrolled HTTP endpoint.", Arity = ArgumentArity.Zero)
        let displayName = new Option<string>("--display-name", Required = true, Description = "Non-unique cache display name.")
        let ownerId = new Option<string>("--owner-id", Required = true, Description = "Owner ID <Guid> for cache enrollment.")
        let organizationId = new Option<string>("--organization-id", Required = false, Description = "Optional Organization ID <Guid> for cache enrollment.")

        let repositoryId =
            new Option<string []>(
                "--repository-id",
                Required = true,
                AllowMultipleArgumentsPerToken = true,
                Description = "One or more explicit repository IDs <Guid>."
            )

        let repositoryOrganizationId =
            new Option<string []>(
                "--repository-organization-id",
                Required = true,
                AllowMultipleArgumentsPerToken = true,
                Description = "One explicit Organization ID <Guid> for each --repository-id, in the same order."
            )

    /// Resolves the deployed cache executable without consulting repository configuration or local state.
    let private resolveExecutable (parseResult: ParseResult) =
        let configured = parseResult.GetValue Options.executable

        if not (String.IsNullOrWhiteSpace configured) then
            configured
        else
            let fromEnvironment = Environment.GetEnvironmentVariable "GRACE_CACHE_EXECUTABLE"

            if String.IsNullOrWhiteSpace fromEnvironment then
                "Grace.Cache"
            else
                fromEnvironment

    /// Removes inherited Grace credentials before a cache child receives only the explicit transient enrollment token.
    let removeAuthenticationEnvironment (startInfo: ProcessStartInfo) =
        authenticationEnvironmentVariables
        |> List.iter (fun variableName ->
            startInfo.Environment.Remove(variableName)
            |> ignore)

    /// Resolves the effective parent server URI while preserving an HTTP(S) path base and rejecting values unsafe for child propagation.
    let tryGetEffectiveServerUri environmentServerUri configuredServerUri =
        let tryNormalize value =
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, uri when
                (uri.Scheme = Uri.UriSchemeHttp
                 || uri.Scheme = Uri.UriSchemeHttps)
                && String.IsNullOrEmpty uri.UserInfo
                && String.IsNullOrEmpty uri.Query
                && String.IsNullOrEmpty uri.Fragment
                ->
                Ok uri.AbsoluteUri
            | _ -> Error "Grace Server URI is invalid."

        match environmentServerUri with
        | Some value -> tryNormalize value |> Result.map Some
        | None ->
            match configuredServerUri with
            | Some value -> tryNormalize value |> Result.map Some
            | None -> Ok None

    /// Resolves the normal parent Grace context without creating configuration or persisting credentials for cache enrollment.
    let tryGetConfiguredServerUri () =
        let configuredServerUri =
            match Grace.Shared.Client.Configuration.tryInspectCurrentDirectoryConfiguration () with
            | Ok inspection -> Some inspection.Configuration.ServerUri
            | Error _ -> None

        tryGetEffectiveServerUri
            (Environment.GetEnvironmentVariable Constants.EnvironmentVariables.GraceServerUri
             |> Option.ofObj)
            configuredServerUri

    /// Creates a cache process start configuration that retains only a prevalidated Grace Server URI and no inherited credentials.
    let createProcessStartInfo executable enrollmentToken serverUri =
        let startInfo = ProcessStartInfo(executable)
        startInfo.UseShellExecute <- false
        removeAuthenticationEnvironment startInfo

        startInfo.Environment.Remove(Constants.EnvironmentVariables.GraceServerUri)
        |> ignore
        startInfo.Environment[ "GRACE_CACHE_INSTANCE_NAME" ] <- cacheInstanceMarker

        match enrollmentToken, tryGetEffectiveServerUri serverUri None with
        | Some token, Ok(Some effectiveServerUri) ->
            startInfo.Environment[ enrollmentTokenEnvironmentVariable ] <- token

            startInfo.Environment[
                Constants.EnvironmentVariables.GraceServerUri
            ] <- effectiveServerUri
        | Some token, _ -> startInfo.Environment[ enrollmentTokenEnvironmentVariable ] <- token
        | None, _ -> ()

        startInfo

    /// Starts the cache executable through an injectable process factory and maps launch failures to the stable exit-one boundary.
    let invokeProcessWith (startProcess: ProcessStartInfo -> Process) executable arguments enrollmentToken serverUri =
        match tryGetEffectiveServerUri serverUri None with
        | Error _ -> 1
        | Ok effectiveServerUri ->
            try
                let startInfo = createProcessStartInfo executable enrollmentToken effectiveServerUri
                arguments |> List.iter startInfo.ArgumentList.Add

                use childProcess = startProcess startInfo

                if isNull childProcess then
                    1
                else
                    childProcess.WaitForExit()
                    childProcess.ExitCode
            with
            | :? System.ComponentModel.Win32Exception
            | :? InvalidOperationException
            | :? System.IO.FileNotFoundException
            | :? System.IO.DirectoryNotFoundException
            | :? System.UnauthorizedAccessException -> 1

    /// Starts the cache executable with already validated command tokens and returns its exit code.
    let private invokeProcess executable arguments enrollmentToken serverUri =
        invokeProcessWith (fun startInfo -> Process.Start(startInfo)) executable arguments enrollmentToken serverUri

    /// Validates that the selected endpoint uses HTTPS unless the explicit HTTP exception was supplied.
    let validateEndpoint endpoint allowHttp =
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
        | true, uri when uri.Scheme = Uri.UriSchemeHttp -> Error "HTTP cache endpoints require --allow-http."
        | true, _ -> Error "Cache endpoint must use HTTP or HTTPS."

    /// Executes a cache executable verb without letting the CLI open cache configuration, databases, or key storage.
    let private invokeCache parseResult arguments enrollmentToken serverUri = invokeProcess (resolveExecutable parseResult) arguments enrollmentToken serverUri

    /// Executes the foreground cache host process.
    type Run() =
        inherit SynchronousCommandLineAction()

        /// Starts the registered cache process and holds the CLI only for the child process lifetime.
        override _.Invoke(parseResult: ParseResult) : int = invokeCache parseResult [ "--run" ] None None

    /// Executes explicit enrollment through the deployed cache process boundary.
    type Enroll() =
        inherit SynchronousCommandLineAction()

        /// Validates endpoint transport before handing exact enrollment inputs to the cache process.
        override _.Invoke(parseResult: ParseResult) : int =
            let endpoint = parseResult.GetValue Options.endpoint
            let allowHttp = parseResult.GetValue Options.allowHttp

            match validateEndpoint endpoint allowHttp with
            | Error message ->
                Console.Error.WriteLine message
                1
            | Ok () ->
                let repositoryArguments =
                    parseResult.GetValue Options.repositoryId
                    |> Array.collect (fun repositoryId -> [| "--repository-id"; repositoryId |])
                    |> Array.toList

                let repositoryOrganizationArguments =
                    parseResult.GetValue Options.repositoryOrganizationId
                    |> Array.collect (fun organizationId ->
                        [|
                            "--repository-organization-id"
                            organizationId
                        |])
                    |> Array.toList

                let allowHttpArguments = if allowHttp then [ "--allow-http" ] else []

                let organizationArguments =
                    match parseResult.GetValue Options.organizationId with
                    | null
                    | "" -> []
                    | organizationId -> [ "--organization-id"; organizationId ]

                let arguments =
                    [
                        "--enroll"
                        "--endpoint"
                        endpoint
                        "--display-name"
                        parseResult.GetValue Options.displayName
                        "--owner-id"
                        parseResult.GetValue Options.ownerId
                    ]
                    @ repositoryArguments
                      @ repositoryOrganizationArguments
                        @ allowHttpArguments @ organizationArguments

                match Auth.tryGetAccessToken().GetAwaiter().GetResult() with
                | Ok (Some token) ->
                    match tryGetConfiguredServerUri () with
                    | Ok serverUri -> invokeCache parseResult arguments (Some token) serverUri
                    | Error message ->
                        Console.Error.WriteLine message
                        1
                | _ ->
                    Console.Error.WriteLine "Current Grace login is unavailable."
                    1

    /// Executes a safe cache status query through the deployed process boundary.
    type Status() =
        inherit SynchronousCommandLineAction()

        /// Queries the cache executable without reading its machine configuration from the CLI.
        override _.Invoke(parseResult: ParseResult) : int = invokeCache parseResult [ "--status" ] None None

    /// Builds the normal operator command group without advertising prefetch, artifact serving, or cache-selection modes.
    let Build =
        let cacheCommand = new Command("cache", Description = "Run and control the machine-managed Grace Cache service.")

        let runCommand =
            new Command("run", Description = "Run the registered Grace Cache service.")
            |> fun command -> command |> addOption Options.executable

        runCommand.Action <- Run()

        let enrollCommand =
            new Command("enroll", Description = "Enroll this cache machine with the current Grace administrator login.")
            |> addOption Options.executable
            |> addOption Options.endpoint
            |> addOption Options.allowHttp
            |> addOption Options.displayName
            |> addOption Options.ownerId
            |> addOption Options.organizationId
            |> addOption Options.repositoryId
            |> addOption Options.repositoryOrganizationId

        enrollCommand.Action <- Enroll()

        let statusCommand =
            new Command("status", Description = "Show redacted Grace Cache lifecycle status.")
            |> fun command -> command |> addOption Options.executable

        statusCommand.Action <- Status()

        cacheCommand.Subcommands.Add runCommand
        cacheCommand.Subcommands.Add enrollCommand
        cacheCommand.Subcommands.Add statusCommand
        cacheCommand
