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
open Grace.Types.Common
open Microsoft.Identity.Client.Extensions.Msal
open Spectre.Console
open NodaTime
open NodaTime.Text
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.IO
open System.Threading
open System.Threading.Tasks

/// Groups the auth command parser, handlers, and output helpers.
module Auth =

    /// Models login mode values passed between the parser and auth handlers.
    type LoginMode =
        | Pkce
        | Device

    /// Defines structured data exchanged by CLI helpers.
    type OidcCliConfig = { Authority: string; Audience: string; ClientId: string; RedirectPort: int; Scopes: string list }

    /// Defines structured data exchanged by CLI helpers.
    type OidcM2mConfig = { Authority: string; Audience: string; ClientId: string; ClientSecret: string; Scopes: string list }

    /// Defines structured data exchanged by CLI helpers.
    type AuthInfo = { GraceUserId: string; Claims: string list }

    /// Models token bundle values passed between the parser and auth handlers.
    type TokenBundle =
        {
            RefreshToken: string
            AccessToken: string
            AccessTokenExpiresAt: Instant
            Issuer: string
            Audience: string
            Scopes: string
            Subject: string option
            ClientId: string
            CreatedAt: Instant
            UpdatedAt: Instant
        }

    /// Defines structured data exchanged by CLI helpers.
    type TokenResponse = { AccessToken: string; RefreshToken: string option; ExpiresIn: int option; Scope: string option; TokenType: string option }

    /// Models device code response values passed between the parser and auth handlers.
    type DeviceCodeResponse =
        {
            DeviceCode: string
            UserCode: string
            VerificationUri: string
            VerificationUriComplete: string option
            ExpiresIn: int
            IntervalSeconds: int
        }

    /// Defines structured data exchanged by CLI helpers.
    type TokenStore = { Helper: MsalCacheHelper; StorageProperties: StorageCreationProperties; LockFilePath: string; InProcessLock: SemaphoreSlim }

    /// Defines structured data exchanged by CLI helpers.
    type AuthStatusGraceTokenSource = { Present: bool option; Valid: bool option; Error: string option }

    /// Defines structured data exchanged by CLI helpers.
    type AuthStatusTokenFileSource = { Present: bool option; Supported: bool option; Error: string option }

    /// Defines structured data exchanged by CLI helpers.
    type AuthStatusM2mSource = { Configured: bool option }

    /// Models auth status interactive source values passed between the parser and auth handlers.
    type AuthStatusInteractiveSource =
        {
            Configured: bool option
            TokenPresent: bool option
            SecureStoreAvailable: bool option
            AccessTokenExpiresAt: string option
            Subject: string option
            Error: string option
        }

    /// Models auth status sources values passed between the parser and auth handlers.
    type AuthStatusSources =
        {
            GraceToken: AuthStatusGraceTokenSource
            GraceTokenFile: AuthStatusTokenFileSource
            M2m: AuthStatusM2mSource
            Interactive: AuthStatusInteractiveSource
        }

    /// Defines structured data exchanged by CLI helpers.
    type AuthStatusDiagnostic = { Level: string; Source: string; Message: string }

    /// Models auth status output values passed between the parser and auth handlers.
    type AuthStatusOutput =
        {
            Authenticated: bool option
            Status: string
            ActiveSource: string
            Sources: AuthStatusSources
            AccessTokenExpiresAt: string option
            Subject: string option
            Diagnostics: AuthStatusDiagnostic array
        }

    /// Defines structured data exchanged by CLI helpers.
    type AuthEnvironmentFieldStatus = { Name: string; IsSet: bool; Required: bool }

    /// Models auth inspection values passed between the parser and auth handlers.
    type AuthInspection =
        {
            GraceTokenPresent: bool
            GraceTokenValid: bool
            GraceTokenError: string option
            GraceTokenFilePresent: bool
            M2mFields: AuthEnvironmentFieldStatus array
            CliFields: AuthEnvironmentFieldStatus array
            M2mComplete: bool
            CliComplete: bool
            HasPartialM2m: bool
            HasPartialCli: bool
            ActiveSource: string
            HasUsableCredentialSource: bool
        }

    /// Tries to map get env and returns a GraceError instead of throwing on unsupported input.
    let private tryGetEnv name =
        let value = Environment.GetEnvironmentVariable(name)
        if String.IsNullOrWhiteSpace value then None else Some value

    /// Evaluates is env set against parsed options and command state.
    let private isEnvSet name = tryGetEnv name |> Option.isSome

    /// Normalizes Grace ids for bearer token by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeBearerToken (token: string) =
        let trimmed = token.Trim()

        if trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            trimmed.Substring("Bearer ".Length).Trim()
        else
            trimmed

    /// Coordinates auth field behavior for this CLI command path.
    let private authField required name = { Name = name; IsSet = isEnvSet name; Required = required }

    /// Checks whether required fields complete is true for the parsed command input.
    let private requiredFieldsComplete fields =
        fields
        |> Array.filter (fun field -> field.Required)
        |> Array.forall (fun field -> field.IsSet)

    /// Checks whether any fields set is true for the parsed command input.
    let private anyFieldsSet fields = fields |> Array.exists (fun field -> field.IsSet)

    /// Evaluates has partial required fields against parsed options and command state.
    let private hasPartialRequiredFields fields =
        anyFieldsSet fields
        && not (requiredFieldsComplete fields)

    /// Coordinates inspect auth environment behavior for this CLI command path.
    let inspectAuthEnvironment () =
        let tokenValue = tryGetEnv Constants.EnvironmentVariables.GraceToken
        let graceTokenPresent = tokenValue.IsSome

        let graceTokenValid, graceTokenError =
            match tokenValue with
            | None -> false, None
            | Some value ->
                let normalized = normalizeBearerToken value

                match Grace.Types.PersonalAccessToken.tryParseToken normalized with
                | Some _ -> true, None
                | None ->
                    false,
                    Some $"GRACE_TOKEN accepts Grace PATs only (prefix {Grace.Types.PersonalAccessToken.TokenPrefix}). Auth0 access tokens are not valid here."

        let m2mFields =
            [|
                authField true Constants.EnvironmentVariables.GraceAuthOidcAuthority
                authField true Constants.EnvironmentVariables.GraceAuthOidcAudience
                authField true Constants.EnvironmentVariables.GraceAuthOidcM2mClientId
                authField true Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
                authField false Constants.EnvironmentVariables.GraceAuthOidcM2mScopes
            |]

        let cliFields =
            [|
                authField true Constants.EnvironmentVariables.GraceAuthOidcAuthority
                authField true Constants.EnvironmentVariables.GraceAuthOidcAudience
                authField true Constants.EnvironmentVariables.GraceAuthOidcCliClientId
                authField false Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort
                authField false Constants.EnvironmentVariables.GraceAuthOidcCliScopes
            |]

        let m2mComplete = requiredFieldsComplete m2mFields
        let cliComplete = requiredFieldsComplete cliFields
        let hasPartialM2m = hasPartialRequiredFields m2mFields
        let hasPartialCli = hasPartialRequiredFields cliFields
        let graceTokenFilePresent = isEnvSet Constants.EnvironmentVariables.GraceTokenFile

        let activeSource, hasUsableCredentialSource =
            if graceTokenValid then
                $"{Constants.EnvironmentVariables.GraceToken} PAT", true
            elif graceTokenError.IsSome then
                $"{Constants.EnvironmentVariables.GraceToken} invalid", false
            elif graceTokenFilePresent then
                $"{Constants.EnvironmentVariables.GraceTokenFile} unsupported", false
            elif m2mComplete then
                "OIDC M2M environment", true
            elif cliComplete then
                "OIDC CLI environment", true
            else
                "None", false

        {
            GraceTokenPresent = graceTokenPresent
            GraceTokenValid = graceTokenValid
            GraceTokenError = graceTokenError
            GraceTokenFilePresent = graceTokenFilePresent
            M2mFields = m2mFields
            CliFields = cliFields
            M2mComplete = m2mComplete
            CliComplete = cliComplete
            HasPartialM2m = hasPartialM2m
            HasPartialCli = hasPartialCli
            ActiveSource = activeSource
            HasUsableCredentialSource = hasUsableCredentialSource
        }

    /// Normalizes Grace ids for authority by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeAuthority (authority: string) =
        let trimmed = authority.Trim()

        if trimmed.EndsWith("/", StringComparison.Ordinal) then
            trimmed
        else
            $"{trimmed}/"

    /// Parses command input into typed values.
    let private parseScopes (value: string) =
        value.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun scopeValue -> scopeValue.Trim())
        |> Seq.filter (fun scopeValue -> not (String.IsNullOrWhiteSpace scopeValue))
        |> Seq.toList

    /// Coordinates default cli scopes behavior for this CLI command path.
    let private defaultCliScopes () =
        [
            "openid"
            "profile"
            "email"
            "offline_access"
        ]

    /// Builds command objects or parameters for execution.
    let private buildOidcCliConfig (authority: string) (audience: string) (clientId: string) =
        let redirectPort =
            match tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort with
            | Some raw ->
                match Int32.TryParse raw with
                | true, parsed when parsed > 0 -> parsed
                | _ -> 8391
            | None -> 8391

        let scopes =
            match tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcCliScopes with
            | Some raw when not (String.IsNullOrWhiteSpace raw) -> parseScopes raw
            | _ -> defaultCliScopes ()

        { Authority = normalizeAuthority authority; Audience = audience.Trim(); ClientId = clientId.Trim(); RedirectPort = redirectPort; Scopes = scopes }

    /// Tries to map get oidc cli config from env and returns a GraceError instead of throwing on unsupported input.
    let private tryGetOidcCliConfigFromEnv () =
        match tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority,
              tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcAudience,
              tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcCliClientId
            with
        | Some authority, Some audience, Some clientId -> Some(buildOidcCliConfig authority audience clientId)
        | _ -> None

    /// Tries to map get oidc cli config from server and returns a GraceError instead of throwing on unsupported input.
    let private tryGetOidcCliConfigFromServer (correlationId: string) =
        task {
            match tryGetEnv Constants.EnvironmentVariables.GraceServerUri with
            | None -> return Ok None
            | Some _ ->
                let parameters = CommonParameters(CorrelationId = correlationId)
                let! result = Grace.SDK.Auth.getOidcClientConfig parameters

                match result with
                | Ok graceReturnValue ->
                    let config = graceReturnValue.ReturnValue
                    return Ok(Some(buildOidcCliConfig config.Authority config.Audience config.CliClientId))
                | Error error -> return Error error
        }

    /// Tries to map get oidc cli config and returns a GraceError instead of throwing on unsupported input.
    let private tryGetOidcCliConfig (correlationId: string) =
        task {
            match tryGetOidcCliConfigFromEnv () with
            | Some config -> return Ok(Some config)
            | None -> return! tryGetOidcCliConfigFromServer correlationId
        }

    /// Tries to map get oidc m2m config and returns a GraceError instead of throwing on unsupported input.
    let private tryGetOidcM2mConfig () =
        match tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority,
              tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcAudience,
              tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId,
              tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
            with
        | Some authority, Some audience, Some clientId, Some clientSecret ->
            let scopes =
                match tryGetEnv Constants.EnvironmentVariables.GraceAuthOidcM2mScopes with
                | Some raw when not (String.IsNullOrWhiteSpace raw) -> parseScopes raw
                | _ -> []

            Some
                {
                    Authority = normalizeAuthority authority
                    Audience = audience.Trim()
                    ClientId = clientId.Trim()
                    ClientSecret = clientSecret
                    Scopes = scopes
                }
        | _ -> None

    /// Tries to map get grace token from env and returns a GraceError instead of throwing on unsupported input.
    let private tryGetGraceTokenFromEnv () =
        match tryGetEnv Constants.EnvironmentVariables.GraceToken with
        | None -> Ok None
        | Some value ->
            let normalized = normalizeBearerToken value

            if String.IsNullOrWhiteSpace normalized then
                Error $"GRACE_TOKEN is set but empty. Provide a Grace PAT or unset {Constants.EnvironmentVariables.GraceToken}."
            else
                match Grace.Types.PersonalAccessToken.tryParseToken normalized with
                | Some _ -> Ok(Some normalized)
                | None ->
                    Error $"GRACE_TOKEN accepts Grace PATs only (prefix {Grace.Types.PersonalAccessToken.TokenPrefix}). Auth0 access tokens are not valid here."

    /// Reads token store namespace from ParseResult, local configuration, or Grace ids.
    let private getTokenStoreNamespace (config: OidcCliConfig) =
        let serverUri =
            tryGetEnv Constants.EnvironmentVariables.GraceServerUri
            |> Option.defaultValue String.Empty

        $"{config.Authority}|{config.Audience}|{config.ClientId}|{serverUri}"
            .Trim()

    /// Evaluates hash namespace against parsed options and command state.
    let private hashNamespace (value: string) =
        use sha = SHA256.Create()
        let bytes = Encoding.UTF8.GetBytes(value)
        let hash = sha.ComputeHash(bytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    let private tokenStoreCache = System.Collections.Concurrent.ConcurrentDictionary<string, Task<TokenStore>>()

    /// Opens the secure OIDC token store used by interactive CLI authentication.
    let private createTokenStoreAsync (config: OidcCliConfig) =
        task {
            let cacheRoot = UserConfiguration.getUserGraceDirectory ()
            let cacheDirectory = Path.Combine(cacheRoot, "auth")

            Directory.CreateDirectory(cacheDirectory)
            |> ignore

            let key = getTokenStoreNamespace config |> hashNamespace
            let fileName = $"grace_auth_{key}.bin"
            let builder = StorageCreationPropertiesBuilder(fileName, cacheDirectory)

            if OperatingSystem.IsMacOS() then
                builder.WithMacKeyChain("Grace", "Grace.CLI.Auth")
                |> ignore
            elif OperatingSystem.IsLinux() then
                let attribute1 = KeyValuePair<string, string>("application", "grace")
                let attribute2 = KeyValuePair<string, string>("scope", "auth")

                builder.WithLinuxKeyring("com.grace.auth", MsalCacheHelper.LinuxKeyRingDefaultCollection, "Grace CLI Auth", attribute1, attribute2)
                |> ignore

            let storageProperties = builder.Build()
            let! helper = MsalCacheHelper.CreateAsync(storageProperties, null)

            return
                {
                    Helper = helper
                    StorageProperties = storageProperties
                    LockFilePath = $"{storageProperties.CacheFilePath}.lock"
                    InProcessLock = new SemaphoreSlim(1, 1)
                }
        }

    /// Reads token store async from ParseResult, local configuration, or Grace ids.
    let private getTokenStoreAsync (config: OidcCliConfig) =
        let key = getTokenStoreNamespace config
        tokenStoreCache.GetOrAdd(key, (fun _ -> createTokenStoreAsync config))

    /// Updates CLI authentication state for verify secure store async while keeping token handling centralized.
    let private verifySecureStoreAsync (config: OidcCliConfig) =
        task {
            try
                let! store = getTokenStoreAsync config
                store.Helper.VerifyPersistence()
                return Ok store
            with
            | ex -> return Error $"Secure token storage is unavailable: {ex.Message}"
        }

    /// Updates CLI authentication state for with token lock while keeping token handling centralized.
    let private withTokenLock (store: TokenStore) (action: unit -> Task<'T>) =
        task {
            do! store.InProcessLock.WaitAsync()

            try
                use _lock = new CrossPlatLock(store.LockFilePath, 100, 100)
                return! action ()
            finally
                store.InProcessLock.Release() |> ignore
        }

    /// Tries to map load token bundle and returns a GraceError instead of throwing on unsupported input.
    let private tryLoadTokenBundle (store: TokenStore) =
        try
            let data = store.Helper.LoadUnencryptedTokenCache()

            if isNull data || data.Length = 0 then
                None
            else
                let json = Encoding.UTF8.GetString(data)
                let bundle = JsonSerializer.Deserialize<TokenBundle>(json, Constants.JsonSerializerOptions)
                if obj.ReferenceEquals(bundle, null) then None else Some bundle
        with
        | _ -> None

    /// Updates CLI authentication state for save token bundle while keeping token handling centralized.
    let private saveTokenBundle (store: TokenStore) (bundle: TokenBundle) =
        let json = JsonSerializer.Serialize(bundle, Constants.JsonSerializerOptions)
        let data = Encoding.UTF8.GetBytes(json)
        store.Helper.SaveUnencryptedTokenCache(data)

    /// Clears inherited token bundle values so explicitly scoped access commands do not target child resources accidentally.
    let private clearTokenBundle (store: TokenStore) = store.Helper.SaveUnencryptedTokenCache(Array.Empty<byte>())

    /// Tries to map read string and returns a GraceError instead of throwing on unsupported input.
    let private tryReadString (root: JsonElement) (name: string) =
        match root.TryGetProperty(name) with
        | true, value when value.ValueKind = JsonValueKind.String ->
            let strValue = value.GetString()
            if String.IsNullOrWhiteSpace strValue then None else Some strValue
        | _ -> None

    /// Tries to map read int and returns a GraceError instead of throwing on unsupported input.
    let private tryReadInt (root: JsonElement) (name: string) =
        match root.TryGetProperty(name) with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, parsed -> Some parsed
            | _ -> None
        | _ -> None

    /// Parses command input into typed values.
    let private parseTokenResponse (json: string) =
        use document = JsonDocument.Parse(json)
        let root = document.RootElement

        match tryReadString root "access_token" with
        | None -> Error "Token response missing access_token."
        | Some accessToken ->
            Ok
                {
                    AccessToken = accessToken
                    RefreshToken = tryReadString root "refresh_token"
                    ExpiresIn = tryReadInt root "expires_in"
                    Scope = tryReadString root "scope"
                    TokenType = tryReadString root "token_type"
                }

    /// Parses command input into typed values.
    let private parseDeviceCodeResponse (json: string) =
        use document = JsonDocument.Parse(json)
        let root = document.RootElement

        match tryReadString root "device_code", tryReadString root "user_code", tryReadString root "verification_uri", tryReadInt root "expires_in" with
        | Some deviceCode, Some userCode, Some verificationUri, Some expiresIn ->
            let verificationUriComplete = tryReadString root "verification_uri_complete"

            let interval =
                tryReadInt root "interval"
                |> Option.defaultValue 5

            Ok
                {
                    DeviceCode = deviceCode
                    UserCode = userCode
                    VerificationUri = verificationUri
                    VerificationUriComplete = verificationUriComplete
                    ExpiresIn = expiresIn
                    IntervalSeconds = max 1 interval
                }
        | _ -> Error "Device code response missing required fields."

    /// Tries to map read oauth error and returns a GraceError instead of throwing on unsupported input.
    let private tryReadOAuthError (json: string) =
        try
            use document = JsonDocument.Parse(json)
            let root = document.RootElement
            let error = tryReadString root "error"
            let description = tryReadString root "error_description"

            match error, description with
            | Some e, Some d -> Some $"{e}: {d}"
            | Some e, None -> Some e
            | None, Some d -> Some d
            | None, None -> None
        with
        | _ -> None

    /// Builds command objects or parameters for execution.
    let private buildEndpoint (authority: string) (path: string) = $"{authority.TrimEnd('/')}/{path.TrimStart('/')}"

    let private httpClient = new HttpClient()

    /// Tries to map create absolute uri and returns a GraceError instead of throwing on unsupported input.
    let private tryCreateAbsoluteUri (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttps
            || uri.Scheme = Uri.UriSchemeHttp
            ->
            Ok uri
        | _ -> Error $"Invalid OIDC endpoint URL: {url}. Check {Constants.EnvironmentVariables.GraceAuthOidcAuthority}."

    /// Sends or polls the post form async OIDC/auth request used by interactive CLI authentication.
    let private postFormAsync (url: string) (formValues: (string * string) list) =
        task {
            let contentValues =
                formValues
                |> Seq.map (fun (key, value) -> KeyValuePair(key, value))

            use content = new FormUrlEncodedContent(contentValues)

            match tryCreateAbsoluteUri url with
            | Error message -> return Error message
            | Ok uri ->
                let! response = httpClient.PostAsync(uri, content)
                let! body = response.Content.ReadAsStringAsync()

                if response.IsSuccessStatusCode then
                    return Ok body
                else
                    let message = tryReadOAuthError body |> Option.defaultValue body
                    return Error message
        }

    /// Tries to map launch browser and returns a GraceError instead of throwing on unsupported input.
    let private tryLaunchBrowser (url: string) =
        try
            let psi = ProcessStartInfo()
            psi.FileName <- url
            psi.UseShellExecute <- true
            Process.Start(psi) |> ignore
            Ok()
        with
        | ex -> Error ex.Message

    /// Coordinates generate base64 url behavior for this CLI command path.
    let private generateBase64Url (bytes: int) =
        let data = RandomNumberGenerator.GetBytes(bytes)

        Convert
            .ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Coordinates compute code challenge behavior for this CLI command path.
    let private computeCodeChallenge (verifier: string) =
        use sha = SHA256.Create()
        let bytes = Encoding.ASCII.GetBytes(verifier)
        let hash = sha.ComputeHash(bytes)

        Convert
            .ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Tries to map get jwt claim and returns a GraceError instead of throwing on unsupported input.
    let private tryGetJwtClaim (token: string) (claimType: string) =
        try
            let parts = token.Split('.')

            if parts.Length < 2 then
                None
            else
                let payload = parts[ 1 ].Replace('-', '+').Replace('_', '/')

                let padded =
                    payload
                    + String.replicate ((4 - payload.Length % 4) % 4) "="

                let json = Encoding.UTF8.GetString(Convert.FromBase64String(padded))
                use document = JsonDocument.Parse(json)
                tryReadString document.RootElement claimType
        with
        | _ -> None

    /// Builds command objects or parameters for execution.
    let private buildTokenBundle (config: OidcCliConfig) (tokenResponse: TokenResponse) =
        let now = getCurrentInstant ()

        let expiresIn =
            tokenResponse.ExpiresIn
            |> Option.defaultValue 3600

        let expiresAt = now.Plus(Duration.FromSeconds(float expiresIn))

        let issuer =
            tryGetJwtClaim tokenResponse.AccessToken "iss"
            |> Option.defaultValue config.Authority

        let subject = tryGetJwtClaim tokenResponse.AccessToken "sub"

        let scopes =
            tokenResponse.Scope
            |> Option.defaultValue (String.Join(" ", config.Scopes))

        {
            RefreshToken =
                tokenResponse.RefreshToken
                |> Option.defaultValue String.Empty
            AccessToken = tokenResponse.AccessToken
            AccessTokenExpiresAt = expiresAt
            Issuer = issuer
            Audience = config.Audience
            Scopes = scopes
            Subject = subject
            ClientId = config.ClientId
            CreatedAt = now
            UpdatedAt = now
        }

    /// Sends or polls the request token with authorization code async OIDC/auth request used by interactive CLI authentication.
    let private requestTokenWithAuthorizationCodeAsync (config: OidcCliConfig) (redirectUri: string) (code: string) (codeVerifier: string) =
        task {
            let tokenEndpoint = buildEndpoint config.Authority "oauth/token"

            let formValues =
                [
                    "grant_type", "authorization_code"
                    "client_id", config.ClientId
                    "code", code
                    "code_verifier", codeVerifier
                    "redirect_uri", redirectUri
                ]

            let! response = postFormAsync tokenEndpoint formValues

            match response with
            | Ok json -> return parseTokenResponse json
            | Error message -> return Error message
        }

    /// Sends or polls the request device code async OIDC/auth request used by interactive CLI authentication.
    let private requestDeviceCodeAsync (config: OidcCliConfig) =
        task {
            let endpoint = buildEndpoint config.Authority "oauth/device/code"

            let formValues =
                [
                    "client_id", config.ClientId
                    "audience", config.Audience
                    "scope", String.Join(" ", config.Scopes)
                ]

            let! response = postFormAsync endpoint formValues

            match response with
            | Ok json -> return parseDeviceCodeResponse json
            | Error message -> return Error message
        }

    /// Sends or polls the poll device code async OIDC/auth request used by interactive CLI authentication.
    let private pollDeviceCodeAsync (config: OidcCliConfig) (deviceCode: DeviceCodeResponse) =
        task {
            let tokenEndpoint = buildEndpoint config.Authority "oauth/token"

            let expiresAt =
                getCurrentInstant()
                    .Plus(Duration.FromSeconds(float deviceCode.ExpiresIn))

            let mutable delaySeconds = deviceCode.IntervalSeconds
            let mutable finished = false
            let mutable finalResult = Error "Device code expired. Please try again."

            while not finished do
                if getCurrentInstant () >= expiresAt then
                    finished <- true
                    finalResult <- Error "Device code expired. Please try again."
                else
                    let formValues =
                        [
                            "grant_type", "urn:ietf:params:oauth:grant-type:device_code"
                            "device_code", deviceCode.DeviceCode
                            "client_id", config.ClientId
                        ]

                    let! response = postFormAsync tokenEndpoint formValues

                    match response with
                    | Ok json ->
                        finished <- true
                        finalResult <- parseTokenResponse json
                    | Error message ->
                        if message.StartsWith("authorization_pending", StringComparison.OrdinalIgnoreCase) then
                            do! Task.Delay(TimeSpan.FromSeconds(float delaySeconds))
                        elif message.StartsWith("slow_down", StringComparison.OrdinalIgnoreCase) then
                            delaySeconds <- delaySeconds + 5
                            do! Task.Delay(TimeSpan.FromSeconds(float delaySeconds))
                        else
                            finished <- true
                            finalResult <- Error message

            return finalResult
        }

    /// Tries to map acquire token with pkce async and returns a GraceError instead of throwing on unsupported input.
    let private tryAcquireTokenWithPkceAsync (config: OidcCliConfig) (parseResult: ParseResult) =
        task {
            let redirectUri = $"http://127.0.0.1:{config.RedirectPort}/callback"
            let listener = new HttpListener()

            let startResult =
                try
                    listener.Prefixes.Add($"http://127.0.0.1:{config.RedirectPort}/")
                    listener.Start()
                    Ok()
                with
                | ex -> Error $"Failed to listen on {redirectUri}: {ex.Message}"

            match startResult with
            | Error message -> return Error message
            | Ok () ->
                try
                    let state = generateBase64Url 16
                    let codeVerifier = generateBase64Url 32
                    let codeChallenge = computeCodeChallenge codeVerifier

                    let authorizeEndpoint = buildEndpoint config.Authority "authorize"

                    let query =
                        [
                            "response_type", "code"
                            "client_id", config.ClientId
                            "redirect_uri", redirectUri
                            "audience", config.Audience
                            "scope", String.Join(" ", config.Scopes)
                            "code_challenge", codeChallenge
                            "code_challenge_method", "S256"
                            "state", state
                        ]
                        |> List.map (fun (k, v) -> $"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}")
                        |> String.concat "&"

                    let url = $"{authorizeEndpoint}?{query}"

                    match tryCreateAbsoluteUri url with
                    | Error message -> return Error message
                    | Ok _ ->
                        match tryLaunchBrowser url with
                        | Ok () -> ()
                        | Error message ->
                            AnsiConsole.MarkupLine($"[{Colors.Important}]Open this URL in your browser to continue:[/] {Markup.Escape(url)}")
                            AnsiConsole.MarkupLine($"[{Colors.Deemphasized}]Automatic launch failed: {Markup.Escape(message)}[/]")

                        let! context = listener.GetContextAsync()
                        let request = context.Request
                        let response = context.Response

                        /// Writes response data through the CLI output contract.
                        let writeResponse (message: string) =
                            task {
                                use writer = new StreamWriter(response.OutputStream)
                                do! writer.WriteAsync(message)
                                do! writer.FlushAsync()
                                response.Close()
                            }

                        if
                            request.Url.AbsolutePath.TrimEnd('/')
                            <> "/callback"
                        then
                            do! writeResponse "<html><body>Invalid callback path. You may close this window.</body></html>"
                            return Error "Unexpected callback path."
                        else
                            let queryValues = request.QueryString
                            let errorValue = queryValues["error"]

                            if not (String.IsNullOrWhiteSpace errorValue) then
                                do! writeResponse "<html><body>Authentication failed. You may close this window.</body></html>"
                                let description = queryValues["error_description"]
                                return Error $"Authorization error: {errorValue} {description}"
                            else
                                let code = queryValues["code"]
                                let returnedState = queryValues["state"]

                                if String.IsNullOrWhiteSpace code then
                                    do! writeResponse "<html><body>Authentication failed. You may close this window.</body></html>"
                                    return Error "Authorization code missing from callback."
                                elif not (String.Equals(state, returnedState, StringComparison.Ordinal)) then
                                    do! writeResponse "<html><body>Authentication failed. You may close this window.</body></html>"
                                    return Error "Authorization state mismatch."
                                else
                                    do! writeResponse "<html><body>Authentication complete. You may close this window.</body></html>"
                                    let! tokenResponse = requestTokenWithAuthorizationCodeAsync config redirectUri code codeVerifier

                                    return tokenResponse
                finally
                    listener.Stop()
        }

    /// Tries to map acquire token with device flow async and returns a GraceError instead of throwing on unsupported input.
    let private tryAcquireTokenWithDeviceFlowAsync (config: OidcCliConfig) (parseResult: ParseResult) =
        task {
            let! deviceResponse = requestDeviceCodeAsync config

            match deviceResponse with
            | Error message -> return Error message
            | Ok deviceCode ->
                if parseResult |> hasOutput then
                    match deviceCode.VerificationUriComplete with
                    | Some completeUrl -> AnsiConsole.MarkupLine($"[{Colors.Important}]Complete sign-in:[/] {Markup.Escape(completeUrl)}")
                    | None ->
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Open:[/] {Markup.Escape(deviceCode.VerificationUri)}")
                        AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Code:[/] {Markup.Escape(deviceCode.UserCode)}")

                return! pollDeviceCodeAsync config deviceCode
        }

    /// Updates CLI authentication state for apply refresh token while keeping token handling centralized.
    let private applyRefreshToken (bundle: TokenBundle) (refreshed: TokenResponse) (now: Instant) : TokenBundle =
        let expiresIn = refreshed.ExpiresIn |> Option.defaultValue 3600
        let expiresAt = now.Plus(Duration.FromSeconds(float expiresIn))

        let refreshToken =
            refreshed.RefreshToken
            |> Option.defaultValue bundle.RefreshToken

        let scopes =
            refreshed.Scope
            |> Option.defaultValue bundle.Scopes

        let issuer =
            tryGetJwtClaim refreshed.AccessToken "iss"
            |> Option.defaultValue bundle.Issuer

        let subject =
            tryGetJwtClaim refreshed.AccessToken "sub"
            |> Option.orElse bundle.Subject

        { bundle with
            RefreshToken = refreshToken
            AccessToken = refreshed.AccessToken
            AccessTokenExpiresAt = expiresAt
            Issuer = issuer
            Scopes = scopes
            Subject = subject
            UpdatedAt = now
        }

    /// Tries to map refresh token async and returns a GraceError instead of throwing on unsupported input.
    let private tryRefreshTokenAsync (config: OidcCliConfig) (bundle: TokenBundle) =
        task {
            if String.IsNullOrWhiteSpace bundle.RefreshToken then
                return Error "Refresh token missing. Run 'grace authenticate login' again."
            else
                let endpoint = buildEndpoint config.Authority "oauth/token"

                let formValues =
                    [
                        "grant_type", "refresh_token"
                        "client_id", config.ClientId
                        "refresh_token", bundle.RefreshToken
                        "audience", config.Audience
                    ]

                let! response = postFormAsync endpoint formValues

                match response with
                | Error message -> return Error message
                | Ok json ->
                    match parseTokenResponse json with
                    | Error message -> return Error message
                    | Ok refreshed ->
                        let now = getCurrentInstant ()
                        let updated = applyRefreshToken bundle refreshed now
                        return Ok updated
        }

    let private safetyWindow = Duration.FromSeconds(90.0)

    /// Tries to map get interactive token async and returns a GraceError instead of throwing on unsupported input.
    let private tryGetInteractiveTokenAsync (config: OidcCliConfig) =
        task {
            let! storeResult = verifySecureStoreAsync config

            match storeResult with
            | Error message -> return Error message
            | Ok store ->
                return!
                    withTokenLock store (fun () ->
                        task {
                            match tryLoadTokenBundle store with
                            | None -> return Ok None
                            | Some bundle ->
                                let now = getCurrentInstant ()

                                if bundle.AccessTokenExpiresAt > now.Plus(safetyWindow) then
                                    return Ok(Some bundle.AccessToken)
                                else
                                    let! refreshResult = tryRefreshTokenAsync config bundle

                                    match refreshResult with
                                    | Ok updated ->
                                        saveTokenBundle store updated
                                        return Ok(Some updated.AccessToken)
                                    | Error message ->
                                        clearTokenBundle store
                                        return Error message
                        })
        }

    /// Tries to map get access token and returns a GraceError instead of throwing on unsupported input.
    let tryGetAccessToken () =
        task {
            match tryGetGraceTokenFromEnv () with
            | Error message -> return Error message
            | Ok (Some token) -> return Ok(Some token)
            | Ok None ->
                match tryGetEnv Constants.EnvironmentVariables.GraceTokenFile with
                | Some _ ->
                    return
                        Error
                            $"Local token files are no longer supported. Remove {Constants.EnvironmentVariables.GraceTokenFile} and set {Constants.EnvironmentVariables.GraceToken} instead."
                | None ->
                    match tryGetOidcM2mConfig () with
                    | Some m2mConfig ->
                        let endpoint = buildEndpoint m2mConfig.Authority "oauth/token"

                        let formValues =
                            [
                                "grant_type", "client_credentials"
                                "client_id", m2mConfig.ClientId
                                "client_secret", m2mConfig.ClientSecret
                                "audience", m2mConfig.Audience
                            ]
                            |> fun values ->
                                if List.isEmpty m2mConfig.Scopes then
                                    values
                                else
                                    values
                                    @ [
                                        "scope", String.Join(" ", m2mConfig.Scopes)
                                    ]

                        let! response = postFormAsync endpoint formValues

                        match response with
                        | Error message -> return Error message
                        | Ok json ->
                            match parseTokenResponse json with
                            | Error message -> return Error message
                            | Ok token -> return Ok(Some token.AccessToken)
                    | None ->
                        let correlationId = ensureNonEmptyCorrelationId String.Empty
                        let! cliConfigResult = tryGetOidcCliConfig correlationId

                        match cliConfigResult with
                        | Ok None ->
                            return
                                Error
                                    $"Authentication is not configured. Set {Constants.EnvironmentVariables.GraceAuthOidcAuthority}, {Constants.EnvironmentVariables.GraceAuthOidcAudience}, and {Constants.EnvironmentVariables.GraceAuthOidcCliClientId} (or provide GRACE_TOKEN / M2M credentials)."
                        | Ok (Some cliConfig) ->
                            let! tokenResult = tryGetInteractiveTokenAsync cliConfig
                            return tokenResult
                        | Error error -> return Error error.Error
        }

    /// Tries to map get access token for sdk and returns a GraceError instead of throwing on unsupported input.
    let private tryGetAccessTokenForSdk () =
        task {
            let! result = tryGetAccessToken ()

            match result with
            | Ok token -> return token
            | Error _ -> return None
        }

    /// Updates CLI authentication state for configure sdk auth while keeping token handling centralized.
    let configureSdkAuth () = Grace.SDK.Auth.setTokenProvider (fun () -> tryGetAccessTokenForSdk ())

    /// Parses command input into typed values.
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

    /// Formats instant option values into the text shown in Spectre.Console tables or command output.
    let private formatInstantOption (instant: NodaTime.Instant option) =
        match instant with
        | None -> "Never"
        | Some value -> instantToLocalTime value

    /// Formats instant for json values into the text shown in Spectre.Console tables or command output.
    let private formatInstantForJson (instant: NodaTime.Instant) = instant.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)

    /// Formats instant option for json values into the text shown in Spectre.Console tables or command output.
    let private formatInstantOptionForJson (instant: NodaTime.Instant option) = instant |> Option.map formatInstantForJson

    /// Models internal values passed between the parser and auth handlers.
    type internal AuthStatusContext =
        {
            GraceTokenPresent: bool
            GraceTokenValid: bool
            GraceTokenError: string option
            GraceTokenFilePresent: bool
            GraceTokenFileError: string option
            M2mConfigured: bool
            InteractiveConfigured: bool
            InteractiveTokenPresent: bool
            InteractiveExpiresAt: NodaTime.Instant option
            InteractiveSubject: string option
            SecureStoreAvailable: bool
            SecureStoreError: string option
            ConfigError: string option
            Now: NodaTime.Instant
        }

    /// Builds command objects or parameters for execution.
    let internal buildAuthStatus (context: AuthStatusContext) =
        let interactiveTokenFresh =
            context.InteractiveExpiresAt
            |> Option.exists (fun expiresAt -> expiresAt > context.Now.Plus(safetyWindow))

        let activeSource =
            if context.GraceTokenValid then
                "Environment (GRACE_TOKEN)"
            elif context.GraceTokenError.IsSome then
                "Environment (GRACE_TOKEN invalid)"
            elif context.GraceTokenFilePresent then
                "Environment (GRACE_TOKEN_FILE unsupported)"
            elif context.M2mConfigured then
                "M2M (client credentials)"
            elif context.InteractiveConfigured then
                if context.SecureStoreError.IsSome then
                    "Interactive (secure storage unavailable)"
                elif interactiveTokenFresh then
                    "Interactive (cached token)"
                elif context.InteractiveTokenPresent then
                    "Interactive (cached token expired)"
                else
                    "Interactive (no cached token)"
            else
                "None"

        let authenticated =
            match activeSource with
            | "Environment (GRACE_TOKEN)"
            | "Interactive (cached token)" -> true
            | _ -> false

        let statusText = if authenticated then "Authenticated" else "Not authenticated"

        let activeInteractiveExpiresAt =
            if activeSource = "Interactive (cached token)" then
                context.InteractiveExpiresAt
            else
                None

        let activeInteractiveSubject =
            if activeSource = "Interactive (cached token)" then
                context.InteractiveSubject
            else
                None

        let diagnostics =
            [
                match context.GraceTokenError with
                | Some message -> yield { Level = "Error"; Source = Constants.EnvironmentVariables.GraceToken; Message = message }
                | None -> ()

                match context.GraceTokenFileError with
                | Some message -> yield { Level = "Error"; Source = Constants.EnvironmentVariables.GraceTokenFile; Message = message }
                | None -> ()

                match context.SecureStoreError with
                | Some message -> yield { Level = "Error"; Source = "Secure storage"; Message = message }
                | None -> ()

                match context.ConfigError with
                | Some message -> yield { Level = "Error"; Source = "Auth config"; Message = message }
                | None -> ()
            ]

        {
            Authenticated = Some authenticated
            Status = statusText
            ActiveSource = activeSource
            Sources =
                {
                    GraceToken = { Present = Some context.GraceTokenPresent; Valid = Some context.GraceTokenValid; Error = context.GraceTokenError }
                    GraceTokenFile = { Present = Some context.GraceTokenFilePresent; Supported = Some false; Error = context.GraceTokenFileError }
                    M2m = { Configured = Some context.M2mConfigured }
                    Interactive =
                        {
                            Configured = Some context.InteractiveConfigured
                            TokenPresent = Some context.InteractiveTokenPresent
                            SecureStoreAvailable = Some context.SecureStoreAvailable
                            AccessTokenExpiresAt =
                                context.InteractiveExpiresAt
                                |> formatInstantOptionForJson
                            Subject = context.InteractiveSubject
                            Error = context.SecureStoreError
                        }
                }
            AccessTokenExpiresAt =
                activeInteractiveExpiresAt
                |> formatInstantOptionForJson
            Subject = activeInteractiveSubject
            Diagnostics = diagnostics |> List.toArray
        }

    /// Groups the auth command parser, handlers, and output helpers.
    module private LoginOptions =
        let auth =
            (new Option<string>("--auth", Required = false, Description = "Authentication flow: pkce (browser) or device.", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong([| "pkce"; "device" |])

    /// Groups the auth command parser, handlers, and output helpers.
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

        let store =
            new Option<bool>(
                "--store",
                Required = false,
                Description = "Deprecated: local token storage is disabled. Use GRACE_TOKEN instead.",
                Arity = ArgumentArity.Zero
            )

        let includeRevoked =
            new Option<bool>("--include-revoked", Required = false, Description = "Include revoked tokens in the list.", Arity = ArgumentArity.Zero)

        let includeExpired =
            new Option<bool>("--include-expired", Required = false, Description = "Include expired tokens in the list.", Arity = ArgumentArity.Zero)

        let all = new Option<bool>("--all", Required = false, Description = "Include revoked and expired tokens in the list.", Arity = ArgumentArity.Zero)

        let tokenId = new Argument<string>("token-id", Description = "Token id (GUID).")

        let token =
            new Option<string>(
                "--token",
                Required = false,
                Description = "Personal access token (local storage is disabled).",
                Arity = ArgumentArity.ExactlyOne
            )

        let stdin =
            new Option<bool>(
                "--stdin",
                Required = false,
                Description = "Read the token value from standard input (local storage is disabled).",
                Arity = ArgumentArity.Zero
            )

    let private authDevelopmentGuidance = "During development, TestAuth may be available. See docs/Authentication.md for details."

    let private authenticationRequiredMessage = $"Authentication required. Run 'grace authenticate login' and try again. {authDevelopmentGuidance}"

    /// Adds options or child commands to a command definition.
    let private addAuthDevelopmentGuidance (message: string) =
        if String.IsNullOrWhiteSpace message then
            authDevelopmentGuidance
        else
            $"{message} {authDevelopmentGuidance}"

    /// Ensures required command context is present.
    let ensureAccessToken (parseResult: ParseResult) =
        task {
            let correlationId = parseResult |> getCorrelationId
            let! tokenResult = tryGetAccessToken ()

            match tokenResult with
            | Ok (Some _) -> return ()
            | Ok None ->
                Error(GraceError.Create authenticationRequiredMessage correlationId)
                |> renderOutput parseResult
                |> ignore

                raise (OperationCanceledException())
            | Error message ->
                Error(GraceError.Create (addAuthDevelopmentGuidance message) correlationId)
                |> renderOutput parseResult
                |> ignore

                raise (OperationCanceledException())
        }

    /// Executes the login command by binding ParseResult values to the SDK request and CLI output contract.
    type Login() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous login action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                let! cliConfigResult = tryGetOidcCliConfig correlationId

                match cliConfigResult with
                | Ok None ->
                    return
                        Error(
                            GraceError.Create
                                $"Authentication is not configured. Set {Constants.EnvironmentVariables.GraceAuthOidcAuthority}, {Constants.EnvironmentVariables.GraceAuthOidcAudience}, and {Constants.EnvironmentVariables.GraceAuthOidcCliClientId}."
                                correlationId
                        )
                        |> renderOutput parseResult
                | Ok (Some config) ->
                    let desiredAuth =
                        let raw = parseResult.GetValue(LoginOptions.auth)

                        if String.IsNullOrWhiteSpace raw then
                            None
                        elif raw.Equals("device", StringComparison.OrdinalIgnoreCase) then
                            Some LoginMode.Device
                        else
                            Some LoginMode.Pkce

                    let! storeResult = verifySecureStoreAsync config

                    match storeResult with
                    | Error message ->
                        return
                            Error(GraceError.Create $"{message} Use GRACE_TOKEN or configure Auth0 client credentials (M2M)." correlationId)
                            |> renderOutput parseResult
                    | Ok store ->
                        let! tokenResult =
                            match desiredAuth with
                            | Some LoginMode.Device -> tryAcquireTokenWithDeviceFlowAsync config parseResult
                            | Some LoginMode.Pkce -> tryAcquireTokenWithPkceAsync config parseResult
                            | None ->
                                task {
                                    let! pkceResult = tryAcquireTokenWithPkceAsync config parseResult

                                    match pkceResult with
                                    | Ok _ -> return pkceResult
                                    | Error _ -> return! tryAcquireTokenWithDeviceFlowAsync config parseResult
                                }

                        match tokenResult with
                        | Error message ->
                            return
                                Error(GraceError.Create message correlationId)
                                |> renderOutput parseResult
                        | Ok response ->
                            let refreshToken =
                                response.RefreshToken
                                |> Option.defaultValue String.Empty

                            if String.IsNullOrWhiteSpace refreshToken then
                                return
                                    Error(
                                        GraceError.Create
                                            "Refresh token missing. Ensure offline_access scope and refresh token rotation are enabled."
                                            correlationId
                                    )
                                    |> renderOutput parseResult
                            else
                                let bundle = { buildTokenBundle config response with RefreshToken = refreshToken }

                                do! withTokenLock store (fun () -> task { saveTokenBundle store bundle })

                                if parseResult |> hasOutput then
                                    let subject = bundle.Subject |> Option.defaultValue "unknown"
                                    AnsiConsole.MarkupLine($"[{Colors.Important}]Signed in.[/] {Markup.Escape(subject)}")

                                return
                                    Ok(GraceReturnValue.Create "Authenticated." correlationId)
                                    |> renderOutput parseResult
                | Error error -> return Error error |> renderOutput parseResult
            }

    /// Executes the status command by binding ParseResult values to the SDK request and CLI output contract.
    type Status() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous status action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                let graceTokenResult = tryGetGraceTokenFromEnv ()

                let graceTokenPresent =
                    match graceTokenResult with
                    | Ok (Some _) -> true
                    | Ok None -> false
                    | Error _ -> true

                let graceTokenValid =
                    match graceTokenResult with
                    | Ok (Some _) -> true
                    | _ -> false

                let graceTokenError =
                    match graceTokenResult with
                    | Error message -> Some message
                    | _ -> None

                let graceTokenFilePresent = isEnvSet Constants.EnvironmentVariables.GraceTokenFile

                let graceTokenFileError =
                    if graceTokenFilePresent then
                        Some
                            $"Local token file storage is disabled. Unset {Constants.EnvironmentVariables.GraceTokenFile} and use {Constants.EnvironmentVariables.GraceToken} for a PAT."
                    else
                        None

                let m2mConfigured = tryGetOidcM2mConfig () |> Option.isSome

                let! cliConfigResult = tryGetOidcCliConfig correlationId
                let mutable configError: string option = None

                let cliConfig =
                    match cliConfigResult with
                    | Ok value -> value
                    | Error error ->
                        configError <- Some error.Error
                        None

                let mutable interactiveBundle: TokenBundle option = None
                let mutable secureStoreError: string option = None

                match cliConfig with
                | None -> ()
                | Some config ->
                    let! storeResult = verifySecureStoreAsync config

                    match storeResult with
                    | Error message -> secureStoreError <- Some message
                    | Ok store ->
                        let! bundleOpt = withTokenLock store (fun () -> task { return tryLoadTokenBundle store })

                        interactiveBundle <- bundleOpt

                let interactiveConfigured = cliConfig |> Option.isSome
                let interactiveTokenPresent = interactiveBundle |> Option.isSome
                let secureStoreAvailable = interactiveConfigured && secureStoreError.IsNone

                let interactiveExpiresAt =
                    interactiveBundle
                    |> Option.map (fun bundle -> bundle.AccessTokenExpiresAt)

                let interactiveSubject =
                    interactiveBundle
                    |> Option.bind (fun bundle -> bundle.Subject)

                let authStatus =
                    buildAuthStatus
                        {
                            GraceTokenPresent = graceTokenPresent
                            GraceTokenValid = graceTokenValid
                            GraceTokenError = graceTokenError
                            GraceTokenFilePresent = graceTokenFilePresent
                            GraceTokenFileError = graceTokenFileError
                            M2mConfigured = m2mConfigured
                            InteractiveConfigured = interactiveConfigured
                            InteractiveTokenPresent = interactiveTokenPresent
                            InteractiveExpiresAt = interactiveExpiresAt
                            InteractiveSubject = interactiveSubject
                            SecureStoreAvailable = secureStoreAvailable
                            SecureStoreError = secureStoreError
                            ConfigError = configError
                            Now = getCurrentInstant ()
                        }

                if parseResult |> hasOutput then
                    AnsiConsole.MarkupLine($"[{Colors.Important}]{Markup.Escape(authStatus.Status)}[/]")
                    AnsiConsole.WriteLine()
                    AnsiConsole.MarkupLine($"[bold {Colors.Important}]Active source:[/] {Markup.Escape(authStatus.ActiveSource)}")
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]GRACE_TOKEN:[/] {graceTokenPresent}")

                    if graceTokenError.IsSome then
                        AnsiConsole.MarkupLine($"[{Colors.Important}]GRACE_TOKEN error:[/] {Markup.Escape(graceTokenError.Value)}")

                    if graceTokenFileError.IsSome then
                        AnsiConsole.MarkupLine($"[{Colors.Important}]GRACE_TOKEN_FILE:[/] {Markup.Escape(graceTokenFileError.Value)}")

                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]M2M configured:[/] {m2mConfigured}")
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Interactive configured:[/] {interactiveConfigured}")
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Interactive token:[/] {interactiveTokenPresent}")

                    match interactiveExpiresAt with
                    | Some expiresAt ->
                        AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Access token expires:[/] {Markup.Escape(formatInstantOption (Some expiresAt))}")
                    | None -> ()

                    match interactiveSubject with
                    | Some subject -> AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Subject:[/] {Markup.Escape(subject)}")
                    | None -> ()

                    match secureStoreError with
                    | Some message -> AnsiConsole.MarkupLine($"[{Colors.Important}]Secure storage:[/] {Markup.Escape(message)}")
                    | None -> ()

                    match configError with
                    | Some message -> AnsiConsole.MarkupLine($"[{Colors.Important}]Auth config:[/] {Markup.Escape(message)}")
                    | None -> ()

                return
                    Ok(GraceReturnValue.Create authStatus correlationId)
                    |> renderOutput parseResult
            }

    /// Executes the logout command by binding ParseResult values to the SDK request and CLI output contract.
    type Logout() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous logout action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                let! cliConfigResult = tryGetOidcCliConfig correlationId

                match cliConfigResult with
                | Ok None ->
                    return
                        Error(GraceError.Create "Interactive authentication is not configured." correlationId)
                        |> renderOutput parseResult
                | Ok (Some config) ->
                    let! storeResult = verifySecureStoreAsync config

                    match storeResult with
                    | Error message ->
                        return
                            Error(GraceError.Create message correlationId)
                            |> renderOutput parseResult
                    | Ok store ->
                        do! withTokenLock store (fun () -> task { clearTokenBundle store })

                        if parseResult |> hasOutput then
                            AnsiConsole.MarkupLine($"[{Colors.Important}]Signed out.[/]")

                        return
                            Ok(GraceReturnValue.Create "Signed out." correlationId)
                            |> renderOutput parseResult
                | Error error -> return Error error |> renderOutput parseResult
            }

    /// Executes the who am i command by binding ParseResult values to the SDK request and CLI output contract.
    type WhoAmI() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous who am i action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId
                let parameters = CommonParameters(CorrelationId = correlationId)
                let! result = Grace.SDK.Common.getServer<CommonParameters, AuthInfo> (parameters, "authenticate/me")

                match result with
                | Ok graceReturnValue ->
                    if parseResult |> hasOutput then
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Grace user id: {Markup.Escape(graceReturnValue.ReturnValue.GraceUserId)}[/]")

                        if not
                           <| List.isEmpty graceReturnValue.ReturnValue.Claims then
                            let claimList = String.Join(", ", graceReturnValue.ReturnValue.Claims)
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Claims:[/] {Markup.Escape(claimList)}")

                    return Ok graceReturnValue |> renderOutput parseResult
                | Error error -> return Error error |> renderOutput parseResult
            }

    /// Executes the token create command by binding ParseResult values to the SDK request and CLI output contract.
    type TokenCreate() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous token create action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                do! ensureAccessToken parseResult

                let tokenName = parseResult.GetValue(TokenOptions.name)
                let expiresInRaw = parseResult.GetValue(TokenOptions.expiresIn)
                let noExpiry = parseResult.GetValue(TokenOptions.noExpiry)
                let store = parseResult.GetValue(TokenOptions.store)

                if store then
                    return
                        Error(GraceError.Create $"Local token storage is disabled. Set {Constants.EnvironmentVariables.GraceToken} instead." correlationId)
                        |> renderOutput parseResult
                else
                    let expiresInResult =
                        if String.IsNullOrWhiteSpace expiresInRaw then
                            Ok 0L
                        else
                            parseDurationSeconds expiresInRaw

                    match expiresInResult with
                    | Error message ->
                        return
                            Error(GraceError.Create message correlationId)
                            |> renderOutput parseResult
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
                            let storedPath: string option = None

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

    /// Renders token list results only when the selected output mode includes human-readable console text.
    let private renderTokenList (_parseResult: ParseResult) (tokens: Grace.Types.PersonalAccessToken.PersonalAccessTokenSummary list) : unit =
        let table = Table(Border = TableBorder.Rounded)
        table.AddColumn("Name") |> ignore
        table.AddColumn("TokenId") |> ignore
        table.AddColumn("Created") |> ignore
        table.AddColumn("Expires") |> ignore
        table.AddColumn("Last Used") |> ignore
        table.AddColumn("Revoked") |> ignore

        tokens
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

    /// Executes the token list command by binding ParseResult values to the SDK request and CLI output contract.
    type TokenList() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous token list action when System.CommandLine dispatches the parsed command.
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
                        renderTokenList parseResult graceReturnValue.ReturnValue

                    return Ok graceReturnValue |> renderOutput parseResult
                | Error error -> return Error error |> renderOutput parseResult
            }

    /// Executes the token revoke command by binding ParseResult values to the SDK request and CLI output contract.
    type TokenRevoke() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous token revoke action when System.CommandLine dispatches the parsed command.
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

                        return Ok graceReturnValue |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult
            }

    /// Executes the token set command by binding ParseResult values to the SDK request and CLI output contract.
    type TokenSet() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous token set action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                return
                    Error(GraceError.Create $"Local token storage is disabled. Set {Constants.EnvironmentVariables.GraceToken} for a PAT." correlationId)
                    |> renderOutput parseResult
            }

    /// Executes the token clear command by binding ParseResult values to the SDK request and CLI output contract.
    type TokenClear() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous token clear action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                return
                    Error(GraceError.Create $"Local token storage is disabled. Set {Constants.EnvironmentVariables.GraceToken} for a PAT." correlationId)
                    |> renderOutput parseResult
            }

    /// Executes the token status command by binding ParseResult values to the SDK request and CLI output contract.
    type TokenStatus() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous token status action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: Threading.CancellationToken) : Task<int> =
            task {
                let correlationId = parseResult |> getCorrelationId

                let graceTokenResult = tryGetGraceTokenFromEnv ()

                let graceTokenPresent =
                    match graceTokenResult with
                    | Ok (Some _) -> true
                    | Ok None -> false
                    | Error _ -> true

                let graceTokenValid =
                    match graceTokenResult with
                    | Ok (Some _) -> true
                    | _ -> false

                let graceTokenError =
                    match graceTokenResult with
                    | Error message -> Some message
                    | _ -> None

                if parseResult |> hasOutput then
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]GRACE_TOKEN:[/] {graceTokenPresent}")
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]GRACE_TOKEN valid:[/] {graceTokenValid}")

                    match graceTokenError with
                    | Some message -> AnsiConsole.MarkupLine($"[{Colors.Important}]GRACE_TOKEN error:[/] {Markup.Escape(message)}")
                    | None -> ()

                    AnsiConsole.MarkupLine($"[{Colors.Deemphasized}]Local token storage is disabled.[/]")

                return
                    Ok(GraceReturnValue.Create "Token status." correlationId)
                    |> renderOutput parseResult
            }

    let Build =
        let authCommand = new Command("authenticate", Description = "Authenticate with Grace.")
        authCommand.Aliases.Add("authn")

        let loginCommand = new Command("login", Description = "Sign in with Auth0 (PKCE or device flow).")
        loginCommand.Options.Add(LoginOptions.auth)
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

        let tokenSetCommand = new Command("set", Description = "Store a personal access token locally (disabled).")
        tokenSetCommand.Options.Add(TokenOptions.token)
        tokenSetCommand.Options.Add(TokenOptions.stdin)
        tokenSetCommand.Action <- new TokenSet()
        tokenCommand.Subcommands.Add(tokenSetCommand)

        let tokenClearCommand = new Command("clear", Description = "Clear the local personal access token (disabled).")
        tokenClearCommand.Action <- new TokenClear()
        tokenCommand.Subcommands.Add(tokenClearCommand)

        let tokenStatusCommand = new Command("status", Description = "Show personal access token status.")
        tokenStatusCommand.Action <- new TokenStatus()
        tokenCommand.Subcommands.Add(tokenStatusCommand)

        authCommand.Subcommands.Add(tokenCommand)

        authCommand
