namespace Grace.Server.Tests

open Grace.Server.Security
open NUnit.Framework
open System.Security.Claims

[<Parallelizable(ParallelScope.All)>]
type ClaimMappingTests() =

    let createPrincipal (claims: Claim list) =
        let identity = ClaimsIdentity(claims, "Bearer")
        ClaimsPrincipal(identity)

    let findValues (claimType: string) (claims: Claim list) =
        claims
        |> List.filter (fun claim -> claim.Type = claimType)
        |> List.map (fun claim -> claim.Value)
        |> Set.ofList

    [<Test>]
    member _.MapsGraceUserIdFromTenantAndObjectId() =
        let principal = createPrincipal [ Claim("tid", "tenant-1"); Claim("oid", "object-1") ]

        let mapped = ClaimMapping.mapClaims principal
        let userIds = findValues PrincipalMapper.GraceUserIdClaim mapped
        Assert.That(userIds, Is.EquivalentTo([ "tenant-1:object-1" ]))

    [<Test>]
    member _.MapsGraceUserIdFromIssuerAndSubject() =
        let principal =
            createPrincipal
                [ Claim("iss", "https://login.microsoftonline.com/common/v2.0")
                  Claim("sub", "subject-1") ]

        let mapped = ClaimMapping.mapClaims principal
        let userIds = findValues PrincipalMapper.GraceUserIdClaim mapped
        Assert.That(userIds, Is.EquivalentTo([ "https://login.microsoftonline.com/common/v2.0|subject-1" ]))

    [<Test>]
    member _.DoesNotOverrideExistingGraceUserId() =
        let principal =
            createPrincipal
                [ Claim(PrincipalMapper.GraceUserIdClaim, "existing-user")
                  Claim("tid", "tenant-2")
                  Claim("oid", "object-2") ]

        let mapped = ClaimMapping.mapClaims principal
        let userIds = findValues PrincipalMapper.GraceUserIdClaim mapped
        Assert.That(userIds.Count, Is.EqualTo(0))

    [<Test>]
    member _.MapsRolesScopesAndGroups() =
        let principal =
            createPrincipal
                [ Claim("roles", "Admin")
                  Claim("scp", "repo.write repo.read")
                  Claim("groups", "group-1") ]

        let mapped = ClaimMapping.mapClaims principal
        let graceClaims = findValues PrincipalMapper.GraceClaim mapped
        let graceGroups = findValues PrincipalMapper.GraceGroupIdClaim mapped

        Assert.That(graceClaims, Is.EquivalentTo([ "Admin"; "repo.write"; "repo.read" ]))
        Assert.That(graceGroups, Is.EquivalentTo([ "group-1" ]))
