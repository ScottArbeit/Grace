namespace Grace.Server.Security

open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Auth
open Microsoft.Extensions.Configuration
open System

module ExternalAuthConfig =
    type OidcAuthConfig = { Authority: string; Audience: string }

    let private tryGetConfigValue (configuration: IConfiguration) (name: string) =
        if isNull configuration then
            None
        else
            let value = configuration[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let private normalizeAuthority (authority: string) =
        let trimmed = authority.Trim()

        if trimmed.EndsWith("/", StringComparison.Ordinal) then
            trimmed
        else
            $"{trimmed}/"

    let tryGetOidcConfig (configuration: IConfiguration) =
        match tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAuthority,
              tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAudience
            with
        | Some authority, Some audience -> Some { Authority = normalizeAuthority authority; Audience = audience.Trim() }
        | _ -> None

    let tryGetOidcClientConfig (configuration: IConfiguration) =
        match tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAuthority,
              tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAudience,
              tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcCliClientId
            with
        | Some authority, Some audience, Some cliClientId ->
            Some { Authority = normalizeAuthority authority; Audience = audience.Trim(); CliClientId = cliClientId.Trim() }
        | _ -> None

    let isOidcConfigured (configuration: IConfiguration) = tryGetOidcConfig configuration |> Option.isSome

    let warnIfMicrosoftConfigPresent (configuration: IConfiguration) =
        let deprecated =
            [
                EnvironmentVariables.GraceAuthMicrosoftClientId
                EnvironmentVariables.GraceAuthMicrosoftClientSecret
                EnvironmentVariables.GraceAuthMicrosoftTenantId
                EnvironmentVariables.GraceAuthMicrosoftAuthority
                EnvironmentVariables.GraceAuthMicrosoftApiScope
                EnvironmentVariables.GraceAuthMicrosoftCliClientId
            ]

        let isSet name =
            tryGetConfigValue configuration name
            |> Option.isSome

        if deprecated |> List.exists isSet then
            logToConsole "Deprecated Microsoft auth settings are ignored. Configure grace__auth__oidc__authority and grace__auth__oidc__audience instead."
