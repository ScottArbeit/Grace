namespace Grace.Server.Bootstrap.Tests

open Grace.Server.Tests
open Grace.Shared
open Grace.Shared.Utilities
open NUnit.Framework
open System
open System.Net
open System.Net.Http

[<NonParallelizable>]
type AccessBootstrapTests() =

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

    [<Test>]
    member _.BootstrapDisabledByDefaultReturnsForbidden() =
        task {
            let! hostState = AspireTestHost.startAsync None
            let mutable captured: exn option = None

            try
                let client = hostState.Client
                client.DefaultRequestHeaders.Add("x-grace-user-id", $"{Guid.NewGuid()}")

                let! _ownerId = createOwner client

                let parameters = createGrantRoleParameters "SystemAdmin" $"{Guid.NewGuid()}"
                let! response = client.PostAsync("/access/grantRole", createJsonContent parameters)

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
            with
            | ex -> captured <- Some ex

            hostState.Client.Dispose()
            do! AspireTestHost.stopAsync (Some hostState.App)

            match captured with
            | Some ex -> raise ex
            | None -> ()
        }

    [<Test>]
    member _.BootstrapSeedsSystemAdminWhenConfigured() =
        task {
            let bootstrapUserId = $"{Guid.NewGuid()}"
            let! hostState = AspireTestHost.startAsync (Some bootstrapUserId)
            let mutable captured: exn option = None

            try
                let client = hostState.Client
                client.DefaultRequestHeaders.Add("x-grace-user-id", bootstrapUserId)

                let! _ownerId = createOwner client

                let parameters = createGrantRoleParameters "SystemAdmin" $"{Guid.NewGuid()}"
                let! response = client.PostAsync("/access/grantRole", createJsonContent parameters)

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            with
            | ex -> captured <- Some ex

            hostState.Client.Dispose()
            do! AspireTestHost.stopAsync (Some hostState.App)

            match captured with
            | Some ex -> raise ex
            | None -> ()
        }
