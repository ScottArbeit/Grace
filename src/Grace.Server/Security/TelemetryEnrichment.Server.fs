namespace Grace.Server.Security

open System
open System.Security.Claims

module TelemetryEnrichment =

    let private allowedClaimTypes =
        Set.ofList [ PrincipalMapper.GraceUserIdClaim
                     PrincipalMapper.GraceGroupIdClaim
                     PrincipalMapper.GraceClaim
                     "roles"
                     ClaimTypes.Role ]

    let private redactClaimValue (claimType: string) (value: string) =
        if claimType = PrincipalMapper.GraceUserIdClaim then "REDACTED"
        elif String.IsNullOrWhiteSpace value then String.Empty
        else value.Trim()

    let safeEndUserId (principal: ClaimsPrincipal) =
        PrincipalMapper.tryGetUserId principal
        |> Option.map (fun _ -> "REDACTED")
        |> Option.defaultValue "anonymous"

    let safeClaimsTag (principal: ClaimsPrincipal) =
        if isNull principal || isNull principal.Claims then
            String.Empty
        else
            principal.Claims
            |> Seq.filter (fun claim -> allowedClaimTypes.Contains claim.Type)
            |> Seq.map (fun claim -> $"{claim.Type}:{redactClaimValue claim.Type claim.Value}")
            |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
            |> String.concat ";"
