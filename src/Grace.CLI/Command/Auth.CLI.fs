namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Common
open Grace.Shared.Utilities
open Grace.Types.Types
open Microsoft.Identity.Client
open Microsoft.Identity.Client.Extensions.Msal
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading.Tasks

module Auth =

    type AuthSettings =
        { ClientId: string
          Authority: string
          ApiScope: string
          Scopes: string array }

    type AuthAccountInfo =
        { AccountId: string
          Username: string
          TenantId: string
          IsActive: bool }

    type AuthInfo =
        { GraceUserId: string
          Claims: string list }

    let private tryGetEnv name =
        let value = Environment.GetEnvironmentVariable(name)
        if String.IsNullOrWhiteSpace value then None else Some value

    let private getAuthSettings () =
        let clientId = tryGetEnv Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId
        let tenantId = tryGetEnv Constants.EnvironmentVariables.GraceAuthMicrosoftTenantId |> Option.defaultValue "common"
        let authority =
            tryGetEnv Constants.EnvironmentVariables.GraceAuthMicrosoftAuthority
            |> Option.defaultValue $"https://login.microsoftonline.com/{tenantId}"

        let apiScope =
            match tryGetEnv Constants.EnvironmentVariables.GraceAuthMicrosoftApiScope with
            | Some scope -> scope
            | None ->
                match tryGetEnv Constants.EnvironmentVariables.GraceAuthMicrosoftClientId with
                | Some serverClientId -> $"api://{serverClientId}/access"
                | None -> String.Empty

        match clientId, apiScope with
        | Some clientIdValue, apiScopeValue when not (String.IsNullOrWhiteSpace apiScopeValue) ->
            Ok
                { ClientId = clientIdValue
                  Authority = authority
                  ApiScope = apiScopeValue
                  Scopes = [| apiScopeValue; "offline_access"; "openid"; "profile" |] }
        | _ ->
            Error
                $"Authentication is not configured. Set {Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId} and {Constants.EnvironmentVariables.GraceAuthMicrosoftApiScope} (or {Constants.EnvironmentVariables.GraceAuthMicrosoftClientId})."

    let private createCacheHelperAsync () =
        task {
            let cacheDirectory = UserConfiguration.getUserGraceDirectory ()
            UserConfiguration.ensureUserGraceDirectory () |> ignore

            let builder = StorageCreationPropertiesBuilder("grace_msal_cache.bin", cacheDirectory)

            if OperatingSystem.IsMacOS() then
                builder.WithMacKeyChain("Grace", "Grace.CLI.Auth") |> ignore
            elif OperatingSystem.IsLinux() then
                let attribute1 = KeyValuePair<string, string>("application", "grace")
                let attribute2 = KeyValuePair<string, string>("scope", "auth")

                builder.WithLinuxKeyring(
                    "com.grace.auth",
                    MsalCacheHelper.LinuxKeyRingDefaultCollection,
                    "Grace CLI Auth",
                    attribute1,
                    attribute2
                )
                |> ignore

            let storageProperties = builder.Build()
            return! MsalCacheHelper.CreateAsync(storageProperties, null)
        }

    let mutable private cachedClient: (AuthSettings * IPublicClientApplication * MsalCacheHelper) option = None

    let private getClientAsync (settings: AuthSettings) =
        task {
            match cachedClient with
            | Some (cachedSettings, app, helper) when cachedSettings = settings -> return (app, helper)
            | _ ->
                let app =
                    PublicClientApplicationBuilder
                        .Create(settings.ClientId)
                        .WithAuthority(settings.Authority)
                        .Build()

                let! helper = createCacheHelperAsync ()
                helper.RegisterCache(app.UserTokenCache)
                cachedClient <- Some(settings, app, helper)
                return (app, helper)
        }

    let private getActiveAccountId () =
        let config = (UserConfiguration.loadUserConfiguration ()).Configuration
        if String.IsNullOrWhiteSpace config.Auth.ActiveAccountId then
            None
        else
            Some config.Auth.ActiveAccountId

    let private setActiveAccount (account: IAccount) =
        let loadResult = UserConfiguration.loadUserConfiguration ()
        let config = loadResult.Configuration
        config.Auth.ActiveAccountId <- account.HomeAccountId.Identifier
        config.Auth.ActiveTenantId <- account.HomeAccountId.TenantId
        config.Auth.ActiveUsername <- account.Username
        UserConfiguration.saveUserConfiguration config |> ignore

    let private clearActiveAccount () =
        let loadResult = UserConfiguration.loadUserConfiguration ()
        let config = loadResult.Configuration
        config.Auth.ActiveAccountId <- String.Empty
        config.Auth.ActiveTenantId <- String.Empty
        config.Auth.ActiveUsername <- String.Empty
        UserConfiguration.saveUserConfiguration config |> ignore

    let private selectAccount (accounts: IEnumerable<IAccount>) =
        let accountList = accounts |> Seq.toList

        match getActiveAccountId () with
        | Some activeId ->
            accountList
            |> List.tryFind (fun account -> account.HomeAccountId.Identifier = activeId)
            |> Option.orElse (accountList |> List.tryHead)
        | None -> accountList |> List.tryHead

    let tryGetAccessToken () =
        task {
            match getAuthSettings () with
            | Error _ -> return None
            | Ok settings ->
                let! app, _ = getClientAsync settings
                let! accounts = app.GetAccountsAsync()

                match selectAccount accounts with
                | None -> return None
                | Some account ->
                    try
                        let! result = app.AcquireTokenSilent(settings.Scopes, account).ExecuteAsync()
                        return Some result.AccessToken
                    with _ ->
                        return None
        }

    let configureSdkAuth () =
        Grace.SDK.Auth.setTokenProvider (fun () -> tryGetAccessToken ())

    let ensureAccessToken (parseResult: ParseResult) =
        task {
            let correlationId = parseResult |> getCorrelationId
            let! tokenOpt = tryGetAccessToken ()

            match tokenOpt with
            | Some _ -> return ()
            | None ->
                Error(GraceError.Create "Authentication required. Run 'grace auth login' and try again." correlationId)
                |> renderOutput parseResult
                |> ignore
                raise (OperationCanceledException())
        }

    type Login() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                match getAuthSettings () with
                | Error message ->
                    return Error(GraceError.Create message correlationId) |> renderOutput parseResult
                | Ok settings ->
                    try
                        let! app, _ = getClientAsync settings

                        let deviceCodeCallback =
                            Func<DeviceCodeResult, Task>(fun code ->
                                task {
                                    if parseResult |> hasOutput then
                                        AnsiConsole.MarkupLine($"[{Colors.Important}]{Markup.Escape(code.Message)}[/]")
                                })

                        let! result = app.AcquireTokenWithDeviceCode(settings.Scopes, deviceCodeCallback).ExecuteAsync()
                        setActiveAccount result.Account

                        if parseResult |> hasOutput then
                            AnsiConsole.MarkupLine($"[{Colors.Important}]Signed in as {Markup.Escape(result.Account.Username)}.[/]")

                        return Ok(GraceReturnValue.Create "Authenticated." correlationId) |> renderOutput parseResult
                    with ex ->
                        let exceptionResponse = ExceptionResponse.Create ex
                        return Error(GraceError.Create (exceptionResponse.ToString()) correlationId) |> renderOutput parseResult
            }

    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                match getAuthSettings () with
                | Error message ->
                    return Error(GraceError.Create message correlationId) |> renderOutput parseResult
                | Ok settings ->
                    let! app, _ = getClientAsync settings
                    let! accounts = app.GetAccountsAsync()
                    let accountList = accounts |> Seq.toList
                    let activeId = getActiveAccountId ()

                    let accountInfo =
                        accountList
                        |> List.map (fun account ->
                            { AccountId = account.HomeAccountId.Identifier
                              Username = account.Username
                              TenantId = account.HomeAccountId.TenantId
                              IsActive = Some account.HomeAccountId.Identifier = activeId })

                    if parseResult |> hasOutput then
                        if List.isEmpty accountInfo then
                            logToAnsiConsole Colors.Highlighted "No cached accounts. Run 'grace auth login' to authenticate."
                        else
                            let table = Table(Border = TableBorder.Rounded)
                            table.AddColumn("Active") |> ignore
                            table.AddColumn("Username") |> ignore
                            table.AddColumn("Tenant") |> ignore
                            table.AddColumn("AccountId") |> ignore

                            accountInfo
                            |> List.iter (fun account ->
                                let activeMarker = if account.IsActive then "*" else String.Empty
                                table.AddRow(activeMarker, account.Username, account.TenantId, account.AccountId) |> ignore)

                            AnsiConsole.Write(table)

                    return Ok(GraceReturnValue.Create accountInfo correlationId) |> renderOutput parseResult
            }

    type Logout() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                match getAuthSettings () with
                | Error message ->
                    return Error(GraceError.Create message correlationId) |> renderOutput parseResult
                | Ok settings ->
                    let! app, helper = getClientAsync settings
                    let! accounts = app.GetAccountsAsync()

                    for account in accounts do
                        do! app.RemoveAsync(account)

                    clearActiveAccount ()

                    if parseResult |> hasOutput then
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Signed out.[/]")

                    return Ok(GraceReturnValue.Create "Signed out." correlationId) |> renderOutput parseResult
            }

    type WhoAmI() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId
                let parameters = CommonParameters(CorrelationId = correlationId)
                let! result = Grace.SDK.Common.getServer<CommonParameters, AuthInfo>(parameters, "auth/me")

                match result with
                | Ok graceReturnValue ->
                    if parseResult |> hasOutput then
                        AnsiConsole.MarkupLine(
                            $"[{Colors.Important}]Grace user id: {Markup.Escape(graceReturnValue.ReturnValue.GraceUserId)}[/]"
                        )

                        if not <| List.isEmpty graceReturnValue.ReturnValue.Claims then
                            let claimList = String.Join(", ", graceReturnValue.ReturnValue.Claims)
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Claims:[/] {Markup.Escape(claimList)}")

                    return Ok graceReturnValue |> renderOutput parseResult
                | Error error ->
                    return Error error |> renderOutput parseResult
            }

    let Build =
        let authCommand = new Command("auth", Description = "Authenticate with Grace.")

        let loginCommand = new Command("login", Description = "Sign in with device code.")
        loginCommand.Action <- new Login()
        authCommand.Subcommands.Add(loginCommand)

        let statusCommand = new Command("status", Description = "Show cached login status.")
        statusCommand.Action <- new Status()
        authCommand.Subcommands.Add(statusCommand)

        let logoutCommand = new Command("logout", Description = "Sign out and clear cached credentials.")
        logoutCommand.Action <- new Logout()
        authCommand.Subcommands.Add(logoutCommand)

        let whoamiCommand = new Command("whoami", Description = "Show the authenticated Grace principal.")
        whoamiCommand.Action <- new WhoAmI()
        authCommand.Subcommands.Add(whoamiCommand)

        authCommand
