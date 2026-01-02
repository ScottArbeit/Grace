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
open NodaTime
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Threading.Tasks

module Auth =

    type AuthSettings = { ClientId: string; Authority: string; ApiScope: string; Scopes: string array }

    type AuthAccountInfo = { AccountId: string; Username: string; TenantId: string; IsActive: bool }

    type AuthInfo = { GraceUserId: string; Claims: string list }

    let private tryGetEnv name =
        let value = Environment.GetEnvironmentVariable(name)
        if String.IsNullOrWhiteSpace value then None else Some value

    let private normalizeBearerToken (token: string) =
        if token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            token.Substring("Bearer ".Length).Trim()
        else
            token.Trim()

    let private getTokenFilePath () =
        match tryGetEnv Constants.EnvironmentVariables.GraceTokenFile with
        | Some path when Path.IsPathFullyQualified(path) -> path
        | Some path -> Path.GetFullPath(path)
        | None ->
            let userGraceDir = UserConfiguration.getUserGraceDirectory ()
            Path.Combine(userGraceDir, "grace_token")

    let private trySetTokenFilePermissions (path: string) =
        if not (OperatingSystem.IsWindows()) then
            try
                File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            with _ ->
                ()

    let private tryGetTokenFromEnv () =
        match tryGetEnv Constants.EnvironmentVariables.GraceToken with
        | None -> None
        | Some value ->
            let normalized = normalizeBearerToken value
            if String.IsNullOrWhiteSpace normalized then None else Some normalized

    let private tryGetTokenFromFile () =
        let path = getTokenFilePath ()

        if File.Exists(path) then
            let value = File.ReadAllText(path).Trim()

            if String.IsNullOrWhiteSpace value then
                None
            else
                Some(normalizeBearerToken value)
        else
            None

    let private saveTokenToFile (token: string) =
        let path = getTokenFilePath ()
        UserConfiguration.ensureUserGraceDirectory () |> ignore
        File.WriteAllText(path, token)
        trySetTokenFilePermissions path
        path

    let private clearTokenFile () =
        let path = getTokenFilePath ()

        if File.Exists(path) then
            File.Delete(path)
            true
        else
            false

    let private getAuthSettings () =
        let clientId = tryGetEnv Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId

        let tenantId =
            tryGetEnv Constants.EnvironmentVariables.GraceAuthMicrosoftTenantId
            |> Option.defaultValue "common"

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

                builder.WithLinuxKeyring("com.grace.auth", MsalCacheHelper.LinuxKeyRingDefaultCollection, "Grace CLI Auth", attribute1, attribute2)
                |> ignore

            let storageProperties = builder.Build()
            return! MsalCacheHelper.CreateAsync(storageProperties, null)
        }

    let mutable private cachedClient: (AuthSettings * IPublicClientApplication * MsalCacheHelper) option = None

    let private getClientAsync (settings: AuthSettings) =
        task {
            match cachedClient with
            | Some(cachedSettings, app, helper) when cachedSettings = settings -> return (app, helper)
            | _ ->
                let app = PublicClientApplicationBuilder.Create(settings.ClientId).WithAuthority(settings.Authority).Build()

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
            match tryGetTokenFromEnv () with
            | Some token -> return Some token
            | None ->
                match tryGetTokenFromFile () with
                | Some token -> return Some token
                | None ->
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

    let configureSdkAuth () = Grace.SDK.Auth.setTokenProvider (fun () -> tryGetAccessToken ())

    let private parseDurationSeconds (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error "Expires-in value is required."
        else
            let trimmed = value.Trim()

            if trimmed.Length < 2 then
                Error "Expires-in must include a unit suffix: s, m, h, or d."
            else
                let unitChar = Char.ToLowerInvariant(trimmed[trimmed.Length - 1])
                let amountPart = trimmed.Substring(0, trimmed.Length - 1)

                match Int64.TryParse(amountPart) with
                | true, amount when amount > 0L ->
                    let seconds =
                        match unitChar with
                        | 's' -> Some amount
                        | 'm' -> Some(amount * 60L)
                        | 'h' -> Some(amount * 3600L)
                        | 'd' -> Some(amount * 86400L)
                        | _ -> None

                    match seconds with
                    | Some value -> Ok value
                    | None -> Error "Expires-in must end with s, m, h, or d."
                | _ -> Error "Expires-in must start with a positive integer."

    let private formatInstantOption (instant: NodaTime.Instant option) =
        match instant with
        | None -> "Never"
        | Some value -> instantToLocalTime value

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
                | Error message -> return Error(GraceError.Create message correlationId) |> renderOutput parseResult
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

                        return
                            Ok(GraceReturnValue.Create "Authenticated." correlationId)
                            |> renderOutput parseResult
                    with ex ->
                        let exceptionResponse = ExceptionResponse.Create ex

                        return
                            Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
                            |> renderOutput parseResult
            }

    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                match getAuthSettings () with
                | Error message -> return Error(GraceError.Create message correlationId) |> renderOutput parseResult
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

                                table.AddRow(activeMarker, account.Username, account.TenantId, account.AccountId)
                                |> ignore)

                            AnsiConsole.Write(table)

                    return
                        Ok(GraceReturnValue.Create accountInfo correlationId)
                        |> renderOutput parseResult
            }

    type Logout() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                match getAuthSettings () with
                | Error message -> return Error(GraceError.Create message correlationId) |> renderOutput parseResult
                | Ok settings ->
                    let! app, helper = getClientAsync settings
                    let! accounts = app.GetAccountsAsync()

                    for account in accounts do
                        do! app.RemoveAsync(account)

                    clearActiveAccount ()

                    if parseResult |> hasOutput then
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Signed out.[/]")

                    return
                        Ok(GraceReturnValue.Create "Signed out." correlationId)
                        |> renderOutput parseResult
            }

    type WhoAmI() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId
                let parameters = CommonParameters(CorrelationId = correlationId)
                let! result = Grace.SDK.Common.getServer<CommonParameters, AuthInfo> (parameters, "auth/me")

                match result with
                | Ok graceReturnValue ->
                    if parseResult |> hasOutput then
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Grace user id: {Markup.Escape(graceReturnValue.ReturnValue.GraceUserId)}[/]")

                        if not <| List.isEmpty graceReturnValue.ReturnValue.Claims then
                            let claimList = String.Join(", ", graceReturnValue.ReturnValue.Claims)
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Claims:[/] {Markup.Escape(claimList)}")

                    return Ok graceReturnValue |> renderOutput parseResult
                | Error error -> return Error error |> renderOutput parseResult
            }

    module private TokenOptions =
        let name =
            new Option<string>("--name", Required = true, Description = "A friendly name for the personal access token.", Arity = ArgumentArity.ExactlyOne)

        let expiresIn =
            new Option<string>(
                "--expires-in",
                Required = false,
                Description = "Token lifetime with unit suffix: 30d, 12h, 60m, or 3600s.",
                Arity = ArgumentArity.ExactlyOne
            )

        let noExpiry =
            new Option<bool>(
                "--no-expiry",
                Required = false,
                Description = "Create a token with no expiry (if server policy allows).",
                Arity = ArgumentArity.Zero
            )

        let store = new Option<bool>("--store", Required = false, Description = "Store the new token locally after creation.", Arity = ArgumentArity.Zero)

        let includeRevoked =
            new Option<bool>("--include-revoked", Required = false, Description = "Include revoked tokens in the list.", Arity = ArgumentArity.Zero)

        let includeExpired =
            new Option<bool>("--include-expired", Required = false, Description = "Include expired tokens in the list.", Arity = ArgumentArity.Zero)

        let all = new Option<bool>("--all", Required = false, Description = "Include revoked and expired tokens in the list.", Arity = ArgumentArity.Zero)

        let tokenId = new Argument<string>("token-id", Description = "Token id (GUID).")

        let token = new Option<string>("--token", Required = false, Description = "Personal access token to store locally.", Arity = ArgumentArity.ExactlyOne)

        let stdin = new Option<bool>("--stdin", Required = false, Description = "Read the token value from standard input.", Arity = ArgumentArity.Zero)

    type TokenCreate() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                do! ensureAccessToken parseResult

                let tokenName = parseResult.GetValue(TokenOptions.name)
                let expiresInRaw = parseResult.GetValue(TokenOptions.expiresIn)
                let noExpiry = parseResult.GetValue(TokenOptions.noExpiry)
                let store = parseResult.GetValue(TokenOptions.store)

                let expiresInResult =
                    if String.IsNullOrWhiteSpace expiresInRaw then
                        Ok 0L
                    else
                        parseDurationSeconds expiresInRaw

                match expiresInResult with
                | Error message -> return Error(GraceError.Create message correlationId) |> renderOutput parseResult
                | Ok expiresInSeconds ->
                    let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
                    parameters.CorrelationId <- correlationId
                    parameters.TokenName <- tokenName
                    parameters.ExpiresInSeconds <- expiresInSeconds
                    parameters.NoExpiry <- noExpiry

                    let! result = Grace.SDK.PersonalAccessToken.Create parameters

                    match result with
                    | Ok graceReturnValue ->
                        let created = graceReturnValue.ReturnValue
                        let storedPath = if store then Some(saveTokenToFile created.Token) else None

                        if parseResult |> hasOutput then
                            let summary = created.Summary
                            let expiresText = formatInstantOption summary.ExpiresAt

                            AnsiConsole.MarkupLine($"[{Colors.Important}]Token created.[/]")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Name:[/] {Markup.Escape(summary.Name)}")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Token Id:[/] {summary.TokenId}")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Expires:[/] {Markup.Escape(expiresText)}")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Token:[/] {Markup.Escape(created.Token)}")
                            AnsiConsole.MarkupLine($"[{Colors.Deemphasized}]This token will not be shown again.[/]")

                            match storedPath with
                            | Some path -> AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Stored token at:[/] {Markup.Escape(path)}")
                            | None -> ()

                        return Ok graceReturnValue |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
            }

    type TokenList() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                do! ensureAccessToken parseResult

                let includeRevoked = parseResult.GetValue(TokenOptions.includeRevoked)
                let includeExpired = parseResult.GetValue(TokenOptions.includeExpired)
                let includeAll = parseResult.GetValue(TokenOptions.all)

                let parameters = Grace.Shared.Parameters.Auth.ListPersonalAccessTokensParameters()
                parameters.CorrelationId <- correlationId
                parameters.IncludeRevoked <- includeRevoked || includeAll
                parameters.IncludeExpired <- includeExpired || includeAll

                let! result = Grace.SDK.PersonalAccessToken.List parameters

                match result with
                | Ok graceReturnValue ->
                    if parseResult |> hasOutput then
                        let table = Table(Border = TableBorder.Rounded)
                        table.AddColumn("Name") |> ignore
                        table.AddColumn("TokenId") |> ignore
                        table.AddColumn("Created") |> ignore
                        table.AddColumn("Expires") |> ignore
                        table.AddColumn("Last Used") |> ignore
                        table.AddColumn("Revoked") |> ignore

                        graceReturnValue.ReturnValue
                        |> List.iter (fun token ->
                            let created = instantToLocalTime token.CreatedAt
                            let expiresText = formatInstantOption token.ExpiresAt
                            let lastUsed = formatInstantOption token.LastUsedAt
                            let revoked = formatInstantOption token.RevokedAt

                            table.AddRow(
                                Markup.Escape(token.Name),
                                token.TokenId.ToString(),
                                Markup.Escape(created),
                                Markup.Escape(expiresText),
                                Markup.Escape(lastUsed),
                                Markup.Escape(revoked)
                            )
                            |> ignore)

                        AnsiConsole.Write(table)

                    return Ok graceReturnValue |> renderOutput parseResult
                | Error error -> return Error error |> renderOutput parseResult
            }

    type TokenRevoke() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                do! ensureAccessToken parseResult

                let tokenIdRaw = parseResult.GetValue(TokenOptions.tokenId)

                match Guid.TryParse(tokenIdRaw) with
                | false, _ ->
                    return
                        Error(GraceError.Create "Token id must be a valid GUID." correlationId)
                        |> renderOutput parseResult
                | true, tokenId ->
                    let parameters = Grace.Shared.Parameters.Auth.RevokePersonalAccessTokenParameters()
                    parameters.CorrelationId <- correlationId
                    parameters.TokenId <- tokenId

                    let! result = Grace.SDK.PersonalAccessToken.Revoke parameters

                    match result with
                    | Ok graceReturnValue ->
                        if parseResult |> hasOutput then
                            AnsiConsole.MarkupLine($"[{Colors.Important}]Token revoked.[/]")

                        match tryGetTokenFromFile () with
                        | Some tokenValue ->
                            match Grace.Types.PersonalAccessToken.tryParseToken tokenValue with
                            | Some(_, storedTokenId, _) when storedTokenId = tokenId ->
                                if clearTokenFile () && (parseResult |> hasOutput) then
                                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Cleared local token file.[/]")
                            | _ -> ()
                        | None -> ()

                        return Ok graceReturnValue |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
            }

    type TokenSet() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId
                let tokenValue = parseResult.GetValue(TokenOptions.token)
                let readFromStdin = parseResult.GetValue(TokenOptions.stdin)

                if readFromStdin && not (String.IsNullOrWhiteSpace tokenValue) then
                    return
                        Error(GraceError.Create "Provide --token or --stdin, not both." correlationId)
                        |> renderOutput parseResult
                else
                    let! token =
                        if readFromStdin then
                            Console.In.ReadToEndAsync()
                        else
                            Task.FromResult tokenValue

                    if String.IsNullOrWhiteSpace token then
                        return
                            Error(GraceError.Create "Token value is required (--token or --stdin)." correlationId)
                            |> renderOutput parseResult
                    else
                        let normalized = normalizeBearerToken token

                        if String.IsNullOrWhiteSpace normalized then
                            return
                                Error(GraceError.Create "Token value is required (--token or --stdin)." correlationId)
                                |> renderOutput parseResult
                        else
                            match Grace.Types.PersonalAccessToken.tryParseToken normalized with
                            | None ->
                                return
                                    Error(GraceError.Create "Token format is invalid." correlationId)
                                    |> renderOutput parseResult
                            | Some _ ->
                                let path = saveTokenToFile normalized

                                if parseResult |> hasOutput then
                                    AnsiConsole.MarkupLine($"[{Colors.Important}]Token stored.[/]")
                                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Path:[/] {Markup.Escape(path)}")

                                return
                                    Ok(GraceReturnValue.Create "Token stored." correlationId)
                                    |> renderOutput parseResult
            }

    type TokenClear() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId
                let cleared = clearTokenFile ()

                if parseResult |> hasOutput then
                    if cleared then
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Local token cleared.[/]")
                    else
                        AnsiConsole.MarkupLine($"[{Colors.Deemphasized}]No local token file found.[/]")

                return
                    Ok(GraceReturnValue.Create "Token cleared." correlationId)
                    |> renderOutput parseResult
            }

    type TokenStatus() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                let envTokenPresent =
                    match tryGetEnv Constants.EnvironmentVariables.GraceToken with
                    | Some value when not (String.IsNullOrWhiteSpace value) -> true
                    | _ -> false

                let fileTokenPresent = tryGetTokenFromFile () |> Option.isSome
                let tokenFilePath = getTokenFilePath ()

                let! msalAccount =
                    task {
                        match getAuthSettings () with
                        | Error _ -> return None
                        | Ok settings ->
                            let! app, _ = getClientAsync settings
                            let! accounts = app.GetAccountsAsync()
                            return selectAccount accounts
                    }

                let activeSource =
                    if envTokenPresent then
                        "Environment (GRACE_TOKEN)"
                    elif fileTokenPresent then
                        $"Token file ({tokenFilePath})"
                    else
                        match msalAccount with
                        | Some account -> $"MSAL ({account.Username})"
                        | None -> "None"

                if parseResult |> hasOutput then
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]GRACE_TOKEN:[/] {envTokenPresent}")
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Token file:[/] {fileTokenPresent} ({Markup.Escape(tokenFilePath)})")

                    match msalAccount with
                    | Some account -> AnsiConsole.MarkupLine($"[{Colors.Highlighted}]MSAL account:[/] {Markup.Escape(account.Username)}")
                    | None -> AnsiConsole.MarkupLine($"[{Colors.Highlighted}]MSAL account:[/] None")

                    AnsiConsole.MarkupLine($"[{Colors.Important}]Active source:[/] {Markup.Escape(activeSource)}")

                return
                    Ok(GraceReturnValue.Create activeSource correlationId)
                    |> renderOutput parseResult
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

        let tokenCommand = new Command("token", Description = "Manage personal access tokens.")

        let tokenCreateCommand = new Command("create", Description = "Create a personal access token.")
        tokenCreateCommand.Options.Add(TokenOptions.name)
        tokenCreateCommand.Options.Add(TokenOptions.expiresIn)
        tokenCreateCommand.Options.Add(TokenOptions.noExpiry)
        tokenCreateCommand.Options.Add(TokenOptions.store)
        tokenCreateCommand.Action <- new TokenCreate()
        tokenCommand.Subcommands.Add(tokenCreateCommand)

        let tokenListCommand = new Command("list", Description = "List personal access tokens.")
        tokenListCommand.Options.Add(TokenOptions.includeRevoked)
        tokenListCommand.Options.Add(TokenOptions.includeExpired)
        tokenListCommand.Options.Add(TokenOptions.all)
        tokenListCommand.Action <- new TokenList()
        tokenCommand.Subcommands.Add(tokenListCommand)

        let tokenRevokeCommand = new Command("revoke", Description = "Revoke a personal access token.")
        tokenRevokeCommand.Arguments.Add(TokenOptions.tokenId)
        tokenRevokeCommand.Action <- new TokenRevoke()
        tokenCommand.Subcommands.Add(tokenRevokeCommand)

        let tokenSetCommand = new Command("set", Description = "Store a personal access token locally.")
        tokenSetCommand.Options.Add(TokenOptions.token)
        tokenSetCommand.Options.Add(TokenOptions.stdin)
        tokenSetCommand.Action <- new TokenSet()
        tokenCommand.Subcommands.Add(tokenSetCommand)

        let tokenClearCommand = new Command("clear", Description = "Clear the local personal access token.")
        tokenClearCommand.Action <- new TokenClear()
        tokenCommand.Subcommands.Add(tokenClearCommand)

        let tokenStatusCommand = new Command("status", Description = "Show personal access token status.")
        tokenStatusCommand.Action <- new TokenStatus()
        tokenCommand.Subcommands.Add(tokenStatusCommand)

        authCommand.Subcommands.Add(tokenCommand)

        authCommand
