namespace Grace.Server.Security

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.Extensions.Configuration
open System
open System.Collections.Generic
open System.Security.Claims

/// Contains Grace Cache registration identity and server-approved configuration boundaries.
module CacheServiceIdentity =

    /// Represents the server configuration that gates Grace Cache service registration.
    type CacheRegistrationConfig = { Enabled: bool; ServicePrincipalIds: string list; AllowedScopes: string list; AllowedCapabilities: string list }

    /// Represents the non-secret Cache registration configuration summary suitable for status output.
    type CacheRegistrationConfigSummary = { Enabled: bool; ServicePrincipalCount: int; AllowedScopeCount: int; AllowedCapabilities: string list }

    /// Represents the authorization decision for a Grace Cache service registration attempt.
    type CacheRegistrationDecision =
        | Allowed of servicePrincipalId: string
        | Denied of reason: string

    let private listSeparators = [| ';' |]

    /// Reads a non-empty configuration value using Grace's environment-compatible key normalization.
    let private tryGetConfigValue (configuration: IConfiguration) (name: string) =
        if isNull configuration then
            None
        else
            let value = configuration[getConfigKey name]

            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    /// Parses a semicolon-delimited configuration list while removing empty entries and duplicate values.
    let private parseConfiguredList (comparer: StringComparer) (value: string option) =
        match value with
        | None -> []
        | Some configured ->
            let seen = HashSet<string>(comparer)

            configured.Split(listSeparators, StringSplitOptions.RemoveEmptyEntries)
            |> Seq.map (fun item -> item.Trim())
            |> Seq.filter (fun item -> not (String.IsNullOrWhiteSpace item))
            |> Seq.filter seen.Add
            |> Seq.toList

    /// Parses the cache registration enablement flag so invalid startup configuration fails early.
    let private parseEnabled (value: string option) =
        match value with
        | None -> Ok false
        | Some configured ->
            match configured.Trim().ToLowerInvariant() with
            | "1"
            | "true"
            | "yes" -> Ok true
            | "0"
            | "false"
            | "no" -> Ok false
            | _ -> Error $"{EnvironmentVariables.GraceCacheRegistrationEnabled} must be true or false."

    /// Builds the Grace Cache registration configuration and reports missing server-side approval data.
    let tryGetRegistrationConfig (configuration: IConfiguration) =
        match parseEnabled (tryGetConfigValue configuration EnvironmentVariables.GraceCacheRegistrationEnabled) with
        | Error error -> Error [ error ]
        | Ok enabled ->
            let servicePrincipalIds =
                EnvironmentVariables.GraceCacheRegistrationServicePrincipalIds
                |> tryGetConfigValue configuration
                |> parseConfiguredList StringComparer.Ordinal

            let allowedScopes =
                EnvironmentVariables.GraceCacheRegistrationAllowedScopes
                |> tryGetConfigValue configuration
                |> parseConfiguredList StringComparer.Ordinal

            let allowedCapabilities =
                EnvironmentVariables.GraceCacheRegistrationAllowedCapabilities
                |> tryGetConfigValue configuration
                |> parseConfiguredList StringComparer.OrdinalIgnoreCase

            let errors = ResizeArray<string>()

            if enabled && servicePrincipalIds.IsEmpty then
                errors.Add
                    $"{EnvironmentVariables.GraceCacheRegistrationServicePrincipalIds} must contain at least one OIDC service principal when Cache registration is enabled."

            if enabled && allowedScopes.IsEmpty then
                errors.Add
                    $"{EnvironmentVariables.GraceCacheRegistrationAllowedScopes} must contain at least one server-approved scope when Cache registration is enabled."

            if enabled && allowedCapabilities.IsEmpty then
                errors.Add
                    $"{EnvironmentVariables.GraceCacheRegistrationAllowedCapabilities} must contain at least one server-approved capability when Cache registration is enabled."

            if errors.Count > 0 then
                Error(errors |> Seq.toList)
            else
                Ok { Enabled = enabled; ServicePrincipalIds = servicePrincipalIds; AllowedScopes = allowedScopes; AllowedCapabilities = allowedCapabilities }

    /// Requires valid Cache registration configuration before a service exposes registration listeners.
    let requireValidRegistrationConfig configuration =
        match tryGetRegistrationConfig configuration with
        | Ok config -> config
        | Error errors -> invalidOp (String.Join(" ", errors))

    /// Creates a non-secret Cache registration configuration summary for logs or status output.
    let summarizeConfig (config: CacheRegistrationConfig) =
        {
            Enabled = config.Enabled
            ServicePrincipalCount = config.ServicePrincipalIds.Length
            AllowedScopeCount = config.AllowedScopes.Length
            AllowedCapabilities = config.AllowedCapabilities
        }

    /// Determines whether a requested Cache registration scope was approved by server configuration.
    let isScopeAllowed (config: CacheRegistrationConfig) (scope: string) =
        config.AllowedScopes
        |> List.exists (fun allowed -> String.Equals(allowed, scope, StringComparison.Ordinal))

    /// Determines whether a requested Cache registration capability was approved by server configuration.
    let isCapabilityAllowed (config: CacheRegistrationConfig) (capability: string) =
        config.AllowedCapabilities
        |> List.exists (fun allowed -> String.Equals(allowed, capability, StringComparison.OrdinalIgnoreCase))

    /// Gets the configured OIDC service principal ID from the claims presented by an authenticated Cache service.
    let tryGetServicePrincipalId (principal: ClaimsPrincipal) =
        if isNull principal then
            None
        else
            [
                "azp"
                "client_id"
                "appid"
                "sub"
                PrincipalMapper.GraceUserIdClaim
            ]
            |> Seq.tryPick (fun claimType ->
                principal.Claims
                |> Seq.tryFind (fun claim -> String.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun claim -> claim.Value.Trim())
                |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value)))

    /// Determines whether the request was authenticated by the JWT bearer scheme used for OIDC service credentials.
    let isJwtBearerPrincipal (authenticatedScheme: string) (principal: ClaimsPrincipal) =
        if isNull principal
           || isNull principal.Identity
           || String.IsNullOrWhiteSpace authenticatedScheme then
            false
        else
            let identity = principal.Identity

            identity.IsAuthenticated
            && String.Equals(authenticatedScheme, JwtBearerDefaults.AuthenticationScheme, StringComparison.Ordinal)

    /// Determines whether the JWT carries the Auth0 client-credentials marker required for Cache service registration.
    let hasClientCredentialsGrant (principal: ClaimsPrincipal) =
        if isNull principal then
            false
        else
            principal.Claims
            |> Seq.exists (fun claim ->
                String.Equals(claim.Type, "gty", StringComparison.OrdinalIgnoreCase)
                && String.Equals(claim.Value, "client-credentials", StringComparison.OrdinalIgnoreCase))

    /// Authorizes a Cache registration request against the configured service identity, scopes, and capabilities.
    let decideRegistration
        (config: CacheRegistrationConfig)
        (authenticatedScheme: string)
        (principal: ClaimsPrincipal)
        (requestedScopes: string list)
        (requestedCapabilities: string list)
        =
        if not config.Enabled then
            Denied "Cache registration is disabled."
        elif not (isJwtBearerPrincipal authenticatedScheme principal) then
            Denied "Cache registration requires OIDC JWT bearer authentication."
        elif not (hasClientCredentialsGrant principal) then
            Denied "Cache registration requires an OIDC client-credentials service token."
        else
            match tryGetServicePrincipalId principal with
            | None -> Denied "Cache registration requires a service principal claim."
            | Some servicePrincipalId when
                not
                    (
                        config.ServicePrincipalIds
                        |> List.exists (fun configured -> String.Equals(configured, servicePrincipalId, StringComparison.Ordinal))
                    )
                ->
                Denied "Cache registration service principal is not approved by server configuration."
            | Some _ when requestedScopes.IsEmpty -> Denied "Cache registration requires at least one server-approved scope."
            | Some _ when requestedCapabilities.IsEmpty -> Denied "Cache registration requires at least one server-approved capability."
            | Some servicePrincipalId ->
                let unapprovedScope =
                    requestedScopes
                    |> List.tryFind (isScopeAllowed config >> not)

                let unapprovedCapability =
                    requestedCapabilities
                    |> List.tryFind (isCapabilityAllowed config >> not)

                match unapprovedScope, unapprovedCapability with
                | Some _, _ -> Denied "Cache registration requested a scope that is not approved by server configuration."
                | _, Some _ -> Denied "Cache registration requested a capability that is not approved by server configuration."
                | None, None -> Allowed servicePrincipalId
