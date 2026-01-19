namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Json

[<Parallelizable(ParallelScope.All)>]
type AccessCheckPermissionTests() =

    let createClient (userId: string) (groups: string list) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)

        if not groups.IsEmpty then
            client.DefaultRequestHeaders.Add("x-grace-groups", String.Join(";", groups))

        client

    let checkPermissionAsync (client: HttpClient) ownerId organizationId repositoryId resourceKind operation principalType principalId =
        task {
            let parameters = Parameters.Access.CheckPermissionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ResourceKind <- resourceKind
            parameters.Operation <- operation
            parameters.Path <- ""
            parameters.PrincipalType <- principalType
            parameters.PrincipalId <- principalId
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/access/checkPermission", createJsonContent parameters)
        }

    [<Test>]
    member _.SelfCheckWithoutPrincipalSpecifiedReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response = checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "" ""
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.ExplicitSelfPrincipalReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response =
                checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "User" userId

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.GroupPrincipalMemberReturnsOk() =
        task {
            let userId = $"{Guid.NewGuid()}"
            let groupId = $"{Guid.NewGuid()}"
            use client = createClient userId [ groupId ]

            let! response =
                checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "Group" groupId

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.OtherPrincipalRequiresAdmin() =
        task {
            let userId = $"{Guid.NewGuid()}"
            use client = createClient userId []

            let! response =
                checkPermissionAsync client $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" $"{Guid.NewGuid()}" "repo" "RepoRead" "User" $"{Guid.NewGuid()}"

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }
