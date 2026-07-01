namespace Grace.Server.Security

open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Auth
open Microsoft.Extensions.Configuration
open System

/// Contains Grace Server external auth config behavior and supporting helpers.
module ExternalAuthConfig =
    /// Represents oidc auth config used by Grace Server APIs and background services.
    type OidcAuthConfig = { Authority: string; Audience: string }

    /// Gets try get config value data needed by the server flow.
    let private tryGetConfigValue (configuration: IConfiguration) (name: string) =
        if isNull configuration then
            None
        else
            let value = configuration[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    /// Normalizes normalize authority data for stable server comparisons.
    let private normalizeAuthority (authority: string) =
        let trimmed = authority.Trim()

        if trimmed.EndsWith("/", StringComparison.Ordinal) then
            trimmed
        else
            $"{trimmed}/"

    /// Gets try get oidc config data needed by the server flow.
    let tryGetOidcConfig (configuration: IConfiguration) =
        match tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAuthority,
              tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAudience
            with
        | Some authority, Some audience -> Some { Authority = normalizeAuthority authority; Audience = audience.Trim() }
        | _ -> None

    /// Gets try get oidc client config data needed by the server flow.
    let tryGetOidcClientConfig (configuration: IConfiguration) =
        match tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAuthority,
              tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcAudience,
              tryGetConfigValue configuration EnvironmentVariables.GraceAuthOidcCliClientId
            with
        | Some authority, Some audience, Some cliClientId ->
            Some { Authority = normalizeAuthority authority; Audience = audience.Trim(); CliClientId = cliClientId.Trim() }
        | _ -> None

    /// Determines whether oidc configured.
    let isOidcConfigured (configuration: IConfiguration) = tryGetOidcConfig configuration |> Option.isSome
