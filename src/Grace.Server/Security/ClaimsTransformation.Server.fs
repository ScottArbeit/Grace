namespace Grace.Server.Security

open Microsoft.AspNetCore.Authentication
open Microsoft.Extensions.Logging
open System.Security.Claims
open System.Threading.Tasks

/// Adds Grace-specific claims to authenticated principals.
type GraceClaimsTransformation(log: ILogger<GraceClaimsTransformation>) =

    interface IClaimsTransformation with
        member _.TransformAsync(principal: ClaimsPrincipal) =
            task {
                if isNull principal then
                    return principal
                else
                    let identity =
                        principal.Identities
                        |> Seq.tryFind (fun candidate -> candidate.IsAuthenticated)
                        |> Option.orElseWith (fun () -> principal.Identities |> Seq.tryHead)

                    match identity with
                    | None -> return principal
                    | Some targetIdentity ->
                        let claimsToAdd = ClaimMapping.mapClaims principal
                        let claimsToAddCount = claimsToAdd |> List.length

                        if claimsToAddCount > 0 then
                            for claim in claimsToAdd do
                                targetIdentity.AddClaim(claim)

                            log.LogDebug("GraceClaimsTransformation added {ClaimCount} claim(s).", claimsToAddCount)

                        return principal
            }
