namespace Grace.Authorization.Tests

open Grace.Server.Security
open Microsoft.AspNetCore.Authentication
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open System
open System.Security.Claims

[<Parallelizable(ParallelScope.All)>]
type ClaimMappingTests() =

    let createPrincipal (claims: Claim list) =
        let identity = ClaimsIdentity(claims, "Bearer")
        ClaimsPrincipal(identity)

    let findValues (claimType: string) (claims: Claim list) =
        claims
        |> List.filter (fun claim -> String.Equals(claim.Type, claimType, StringComparison.Ordinal))
        |> List.map (fun claim -> claim.Value)

    [<Test>]
    member _.ClaimsTransformationDoesNotDuplicateGraceUserId() =
        let principal =
            createPrincipal [ Claim(PrincipalMapper.GraceUserIdClaim, "existing-user")
                              Claim("sub", "subject-1") ]

        let transformer = GraceClaimsTransformation(NullLogger<GraceClaimsTransformation>.Instance)
        let transformed = (transformer :> IClaimsTransformation).TransformAsync(principal).Result

        let graceUserIds =
            transformed.Claims
            |> Seq.filter (fun claim -> claim.Type = PrincipalMapper.GraceUserIdClaim)
            |> Seq.map (fun claim -> claim.Value)
            |> Seq.toList

        Assert.That(graceUserIds, Is.EquivalentTo([ "existing-user" ]))

    [<Test>]
    member _.ClaimMappingIsIdempotent() =
        let principal =
            createPrincipal [ Claim("roles", "Admin")
                              Claim("scp", "repo.write repo.read")
                              Claim("groups", "group-1") ]

        let first = ClaimMapping.mapClaims principal
        let augmented = ClaimsPrincipal(ClaimsIdentity(principal.Claims |> Seq.append first, "Bearer"))
        let second = ClaimMapping.mapClaims augmented

        Assert.That(second, Is.Empty)

    [<Test>]
    member _.DedupesGraceClaimsAndGroups() =
        let principal =
            createPrincipal [ Claim(PrincipalMapper.GraceClaim, "repo.read")
                              Claim(PrincipalMapper.GraceClaim, "repo.read")
                              Claim(PrincipalMapper.GraceGroupIdClaim, "group-1")
                              Claim(PrincipalMapper.GraceGroupIdClaim, "group-1")
                              Claim("roles", "repo.read")
                              Claim("groups", "group-1") ]

        let mapped = ClaimMapping.mapClaims principal

        let graceClaims = findValues PrincipalMapper.GraceClaim mapped |> List.sort
        let graceGroups = findValues PrincipalMapper.GraceGroupIdClaim mapped |> List.sort

        Assert.That(graceClaims, Is.EquivalentTo([]))
        Assert.That(graceGroups, Is.EquivalentTo([]))

    [<Test>]
    member _.SplitsScopesOnSpacesWithoutEmptyEntries() =
        let principal =
            createPrincipal [ Claim("scp", "repo.read  repo.write   ")
                              Claim("scope", "  repo.list") ]

        let mapped = ClaimMapping.mapClaims principal
        let graceClaims = findValues PrincipalMapper.GraceClaim mapped |> List.sort

        Assert.That(graceClaims, Is.EquivalentTo([ "repo.list"; "repo.read"; "repo.write" ]))
