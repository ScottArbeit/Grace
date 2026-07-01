namespace Grace.Server.Security

open System
open System.Security.Claims

/// Contains Grace Server telemetry enrichment behavior and supporting helpers.
module TelemetryEnrichment =

    let private allowedClaimTypes =
        Set.ofList [ PrincipalMapper.GraceUserIdClaim
                     PrincipalMapper.GraceGroupIdClaim
                     PrincipalMapper.GraceClaim
                     "roles"
                     ClaimTypes.Role ]

    /// Computes redact claim value data used by Grace Server.
    let private redactClaimValue (claimType: string) (value: string) =
        if claimType = PrincipalMapper.GraceUserIdClaim then "REDACTED"
        elif String.IsNullOrWhiteSpace value then String.Empty
        else value.Trim()

    /// Implements safe end user id for the server request pipeline.
    let safeEndUserId (principal: ClaimsPrincipal) =
        PrincipalMapper.tryGetUserId principal
        |> Option.map (fun _ -> "REDACTED")
        |> Option.defaultValue "anonymous"

    /// Implements safe claims tag for the server request pipeline.
    let safeClaimsTag (principal: ClaimsPrincipal) =
        if isNull principal || isNull principal.Claims then
            String.Empty
        else
            principal.Claims
            |> Seq.filter (fun claim -> allowedClaimTypes.Contains claim.Type)
            |> Seq.map (fun claim -> $"{claim.Type}:{redactClaimValue claim.Type claim.Value}")
            |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
            |> String.concat ";"
