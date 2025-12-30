namespace Grace.Server.Security

open Grace.Shared.Constants
open Grace.Shared.Utilities
open Microsoft.Extensions.Configuration
open System

module ExternalAuthConfig =

    [<Literal>]
    let MicrosoftProviderId = "microsoft"

    [<Literal>]
    let MicrosoftScheme = "Microsoft"

    [<Literal>]
    let MicrosoftDisplayName = "Microsoft"

    type MicrosoftAuthConfig =
        { ClientId: string
          ClientSecret: string option
          TenantId: string
          Authority: string
          ApiScope: string
          CliClientId: string option }

    type AuthProvider = { Id: string; DisplayName: string; Scheme: string }

    let private tryGetConfigValue (configuration: IConfiguration) (name: string) =
        if isNull configuration then
            None
        else
            let value = configuration[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let private getTenantId (configuration: IConfiguration) =
        tryGetConfigValue configuration EnvironmentVariables.GraceAuthMicrosoftTenantId
        |> Option.defaultValue "common"

    let private getAuthority (configuration: IConfiguration) (tenantId: string) =
        tryGetConfigValue configuration EnvironmentVariables.GraceAuthMicrosoftAuthority
        |> Option.defaultValue ($"https://login.microsoftonline.com/{tenantId}")

    let private getApiScope (configuration: IConfiguration) (clientId: string) =
        tryGetConfigValue configuration EnvironmentVariables.GraceAuthMicrosoftApiScope
        |> Option.defaultValue ($"api://{clientId}/access")

    let tryGetMicrosoftConfig (configuration: IConfiguration) =
        match tryGetConfigValue configuration EnvironmentVariables.GraceAuthMicrosoftClientId with
        | None -> None
        | Some clientId ->
            let tenantId = getTenantId configuration
            let authority = getAuthority configuration tenantId
            let apiScope = getApiScope configuration clientId
            let clientSecret = tryGetConfigValue configuration EnvironmentVariables.GraceAuthMicrosoftClientSecret
            let cliClientId = tryGetConfigValue configuration EnvironmentVariables.GraceAuthMicrosoftCliClientId

            Some
                { ClientId = clientId
                  ClientSecret = clientSecret
                  TenantId = tenantId
                  Authority = authority
                  ApiScope = apiScope
                  CliClientId = cliClientId }

    let isMicrosoftWebConfigured (configuration: IConfiguration) =
        match tryGetMicrosoftConfig configuration with
        | Some config ->
            match config.ClientSecret with
            | Some secret when not (String.IsNullOrWhiteSpace secret) -> true
            | _ -> false
        | None -> false

    let getEnabledProviders (configuration: IConfiguration) =
        [ if isMicrosoftWebConfigured configuration then
              { Id = MicrosoftProviderId
                DisplayName = MicrosoftDisplayName
                Scheme = MicrosoftScheme } ]
