namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http

[<NonParallelizable>]
type AccessBootstrapEnabledTests() =

    let createOwner (client: HttpClient) =
        task {
            let ownerId = $"{Guid.NewGuid()}"
            let ownerParameters = Parameters.Owner.CreateOwnerParameters()
            ownerParameters.OwnerId <- ownerId
            ownerParameters.OwnerName <- $"BootstrapOwner{Guid.NewGuid():N}"
            ownerParameters.CorrelationId <- generateCorrelationId ()

            let! response = client.PostAsync("/owner/create", createJsonContent ownerParameters)
            response.EnsureSuccessStatusCode() |> ignore

            return ownerId
        }

    let createGrantRoleParameters (roleId: string) (principalId: string) =
        let parameters = Parameters.Access.GrantRoleParameters()
        parameters.ScopeKind <- "system"
        parameters.PrincipalType <- "User"
        parameters.PrincipalId <- principalId
        parameters.RoleId <- roleId
        parameters.Source <- "test"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    [<Test>]
    member _.BootstrapSeedsSystemAdminWhenConfigured() =
        task {
            use client = createClient testUserId
            let! _ownerId = createOwner client

            let parameters = createGrantRoleParameters "SystemAdmin" $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/access/grantRole", createJsonContent parameters)

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }
