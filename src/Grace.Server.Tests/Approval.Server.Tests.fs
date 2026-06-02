namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Types
open Grace.Types.Webhooks
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Threading.Tasks

module private ApprovalTestHelpers =

    let createAuthenticatedClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    let createClientWithGroup (userId: string) (groupId: string) =
        let client = createAuthenticatedClient userId
        client.DefaultRequestHeaders.Add("x-grace-groups", groupId)
        client

    let createUnauthenticatedClient () =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client

    let grantRoleAsync
        (client: HttpClient)
        (scopeKind: string)
        (ownerId: string)
        (organizationId: string)
        (repositoryId: string)
        (branchId: string)
        (principalId: string)
        (roleId: string)
        =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/access/grantRole", createJsonContent parameters)
        }

    let createPolicyParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Approval.CreateApprovalPolicyParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.Name <- $"policy-{Guid.NewGuid():N}"
        parameters.Subject <- "promotion"
        parameters.RequiredResponder <- "role:ApprovalResponder"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let showPolicyParameters (repositoryId: string) (branchId: string) (policyId: string) =
        let parameters = Parameters.Approval.ShowApprovalPolicyParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.ApprovalPolicyId <- policyId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let updatePolicyParameters (repositoryId: string) (branchId: string) (policyId: string) =
        let parameters = createPolicyParameters repositoryId branchId
        parameters.ApprovalPolicyId <- policyId
        parameters.Name <- $"updated-{Guid.NewGuid():N}"
        parameters

    let listPolicyParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Approval.ListApprovalPoliciesParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let listRequestParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Approval.ListApprovalRequestsParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let requestParameters<'T when 'T :> Parameters.Approval.ApprovalRequestParameters and 'T: (new: unit -> 'T)>
        (repositoryId: string)
        (branchId: string)
        (requestId: string)
        =
        let parameters = new 'T()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.ApprovalRequestId <- requestId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let seedRequestWithAttempt (repositoryId: string) (branchId: string) (selector: string) attempt =
        task {
            let requestId = Guid.NewGuid()

            let parameters = Parameters.Approval.SeedGeneratedApprovalRequestParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.TargetBranchId <- branchId
            parameters.ApprovalRequestId <- requestId.ToString()
            parameters.ApprovalPolicyId <- Guid.NewGuid().ToString()
            parameters.ApprovalPolicyVersion <- 1
            parameters.Subject <- "promotion"
            parameters.RequiredResponder <- selector
            parameters.CorrelationId <- generateCorrelationId ()

            match attempt with
            | Some value -> parameters.StepsComputationAttempt <- Nullable value
            | None -> ()

            let! response = Client.PostAsync("/approval/request/_seedGenerated", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseText)
            let! stored = deserializeContent<ApprovalRequest> response
            Assert.That(stored.ApprovalRequestId, Is.Not.EqualTo(Guid.Empty))
            return stored.ApprovalRequestId.ToString()
        }

    let seedRequest (repositoryId: string) (branchId: string) (selector: string) = seedRequestWithAttempt repositoryId branchId selector None

    let createPolicyAsync (client: HttpClient) repositoryId branchId =
        task {
            let! createResponse = client.PostAsync("/approval/policy/create", createJsonContent (createPolicyParameters repositoryId branchId))

            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            return! deserializeContent<ApprovalPolicy> createResponse
        }

[<NonParallelizable>]
type ApprovalApiIntegrationTests() =

    [<SetUp>]
    member _.SetUp() = ApprovalStore.clearForTests ()

    [<Test>]
    member _.PolicyLifecycleHappyPath() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = ApprovalTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = ApprovalTestHelpers.createAuthenticatedClient adminUser

            let! createResponse =
                adminClient.PostAsync("/approval/policy/create", createJsonContent (ApprovalTestHelpers.createPolicyParameters repositoryId branchId))

            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! created = deserializeContent<ApprovalPolicy> createResponse
            Assert.That(created.Status, Is.EqualTo(ApprovalPolicyStatus.Disabled))

            let showParameters = ApprovalTestHelpers.showPolicyParameters repositoryId branchId (created.ApprovalPolicyId.ToString())
            let! enableResponse = adminClient.PostAsync("/approval/policy/enable", createJsonContent showParameters)
            Assert.That(enableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! showResponse = adminClient.PostAsync("/approval/policy/show", createJsonContent showParameters)
            Assert.That(showResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! evaluateResponse =
                adminClient.PostAsync("/approval/policy/evaluate", createJsonContent (ApprovalTestHelpers.createPolicyParameters repositoryId branchId))

            Assert.That(evaluateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! disableResponse = adminClient.PostAsync("/approval/policy/disable", createJsonContent showParameters)
            Assert.That(disableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! deleteResponse = adminClient.PostAsync("/approval/policy/delete", createJsonContent showParameters)
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.NoPublicApprovalRequestCreateRouteExists() =
        task {
            use client = ApprovalTestHelpers.createAuthenticatedClient $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/approval/request/create", createJsonContent (obj ()))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
        }

    [<Test>]
    member _.PolicyListReturnsOnlyRequestedAuthorizedScope() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = $"{Guid.NewGuid()}"
            let otherBranchId = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = ApprovalTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = ApprovalTestHelpers.createAuthenticatedClient adminUser
            let! allowedPolicy = ApprovalTestHelpers.createPolicyAsync adminClient repositoryId allowedBranchId
            let! otherPolicy = ApprovalTestHelpers.createPolicyAsync adminClient repositoryId otherBranchId

            let! listResponse =
                adminClient.PostAsync("/approval/policy/list", createJsonContent (ApprovalTestHelpers.listPolicyParameters repositoryId allowedBranchId))

            Assert.That(listResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! policies = deserializeContent<ApprovalPolicy array> listResponse

            Assert.That(
                policies
                |> Array.map (fun policy -> policy.ApprovalPolicyId),
                Is.EquivalentTo([| allowedPolicy.ApprovalPolicyId |])
            )

            Assert.That(
                policies
                |> Array.exists (fun policy -> policy.ApprovalPolicyId = otherPolicy.ApprovalPolicyId),
                Is.False
            )
        }

    [<Test>]
    member _.PolicyStoredScopeBlocksBodyScopeSpoofingForShowAndUpdate() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = $"{Guid.NewGuid()}"
            let storedBranchId = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"
            let limitedUser = $"{Guid.NewGuid()}"

            let! grantAdmin = ApprovalTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = ApprovalTestHelpers.createAuthenticatedClient adminUser
            let! storedPolicy = ApprovalTestHelpers.createPolicyAsync adminClient repositoryId storedBranchId

            let! grantLimited = ApprovalTestHelpers.grantRoleAsync Client "branch" ownerId organizationId repositoryId allowedBranchId limitedUser "BranchAdmin"

            Assert.That(grantLimited.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use limitedClient = ApprovalTestHelpers.createAuthenticatedClient limitedUser

            let spoofedShow = ApprovalTestHelpers.showPolicyParameters repositoryId allowedBranchId (storedPolicy.ApprovalPolicyId.ToString())

            let! showResponse = limitedClient.PostAsync("/approval/policy/show", createJsonContent spoofedShow)
            Assert.That(showResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let spoofedUpdate = ApprovalTestHelpers.updatePolicyParameters repositoryId allowedBranchId (storedPolicy.ApprovalPolicyId.ToString())

            let! updateResponse = limitedClient.PostAsync("/approval/policy/update", createJsonContent spoofedUpdate)
            Assert.That(updateResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    [<Test>]
    member _.PolicyUpdateRejectsScopeChangeEvenForStoredScopeManager() =
        task {
            let repositoryId = repositoryIds[0]
            let storedBranchId = repositoryDefaultBranchIds[0]
            let otherBranchId = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grantAdmin = ApprovalTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = ApprovalTestHelpers.createAuthenticatedClient adminUser
            let! storedPolicy = ApprovalTestHelpers.createPolicyAsync adminClient repositoryId storedBranchId

            let movedUpdate = ApprovalTestHelpers.updatePolicyParameters repositoryId otherBranchId (storedPolicy.ApprovalPolicyId.ToString())

            let! updateResponse = adminClient.PostAsync("/approval/policy/update", createJsonContent movedUpdate)
            Assert.That(updateResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.RequestListReturnsOnlyRequestedAuthorizedScope() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = $"{Guid.NewGuid()}"
            let otherBranchId = $"{Guid.NewGuid()}"
            let userId = $"{Guid.NewGuid()}"
            let! allowedRequestId = ApprovalTestHelpers.seedRequest repositoryId allowedBranchId $"user:{userId}"
            let! otherRequestId = ApprovalTestHelpers.seedRequest repositoryId otherBranchId $"user:{Guid.NewGuid()}"

            let! grantReader = ApprovalTestHelpers.grantRoleAsync Client "branch" ownerId organizationId repositoryId allowedBranchId userId "ApprovalResponder"

            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use client = ApprovalTestHelpers.createAuthenticatedClient userId

            let! listResponse =
                client.PostAsync("/approval/request/list", createJsonContent (ApprovalTestHelpers.listRequestParameters repositoryId allowedBranchId))

            let! listResponseText = listResponse.Content.ReadAsStringAsync()
            Assert.That(listResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), listResponseText)

            let requests = deserialize<ApprovalRequest array> listResponseText

            Assert.That(
                requests
                |> Array.map (fun request -> request.ApprovalRequestId.ToString()),
                Is.EquivalentTo([| allowedRequestId |])
            )

            Assert.That(
                requests
                |> Array.exists (fun request -> request.ApprovalRequestId.ToString() = otherRequestId),
                Is.False
            )
        }

    [<Test>]
    member _.ResponseRejectsMissingRespondPermissionEvenWhenSelectorMatches() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let userId = $"{Guid.NewGuid()}"
            let! requestId = ApprovalTestHelpers.seedRequest repositoryId branchId $"user:{userId}"

            let! grant = ApprovalTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" userId "RepoReader"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use client = ApprovalTestHelpers.createAuthenticatedClient userId
            let parameters = ApprovalTestHelpers.requestParameters<Parameters.Approval.ApproveApprovalRequestParameters> repositoryId branchId requestId
            let! response = client.PostAsync("/approval/request/approve", createJsonContent parameters)

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    [<Test>]
    member _.ResponseRejectsResponderRoleCallerWhenSelectorDoesNotMatch() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let userId = $"{Guid.NewGuid()}"
            let! requestId = ApprovalTestHelpers.seedRequest repositoryId branchId "group:release-managers"

            let! grant = ApprovalTestHelpers.grantRoleAsync Client "branch" ownerId organizationId repositoryId branchId userId "ApprovalResponder"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use client = ApprovalTestHelpers.createAuthenticatedClient userId
            let parameters = ApprovalTestHelpers.requestParameters<Parameters.Approval.RejectApprovalRequestParameters> repositoryId branchId requestId
            let! response = client.PostAsync("/approval/request/reject", createJsonContent parameters)

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.BodySuppliedScopeEscalationDoesNotBypassStoredScope() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = repositoryDefaultBranchIds[0]
            let storedBranchId = $"{Guid.NewGuid()}"
            let userId = $"{Guid.NewGuid()}"
            let! requestId = ApprovalTestHelpers.seedRequest repositoryId storedBranchId $"user:{userId}"

            let! grant = ApprovalTestHelpers.grantRoleAsync Client "branch" ownerId organizationId repositoryId allowedBranchId userId "ApprovalResponder"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use client = ApprovalTestHelpers.createAuthenticatedClient userId
            let parameters = ApprovalTestHelpers.requestParameters<Parameters.Approval.ApproveApprovalRequestParameters> repositoryId allowedBranchId requestId
            let! response = client.PostAsync("/approval/request/approve", createJsonContent parameters)
            let! responseText = response.Content.ReadAsStringAsync()

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), responseText)
        }

    [<Test>]
    member _.DuplicateResponseIsIdempotentAfterTerminalDecision() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let userId = $"{Guid.NewGuid()}"
            let! requestId = ApprovalTestHelpers.seedRequest repositoryId branchId $"user:{userId}"

            let! grant = ApprovalTestHelpers.grantRoleAsync Client "branch" ownerId organizationId repositoryId branchId userId "ApprovalResponder"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use client = ApprovalTestHelpers.createAuthenticatedClient userId
            let parameters = ApprovalTestHelpers.requestParameters<Parameters.Approval.RejectApprovalRequestParameters> repositoryId branchId requestId
            let! first = client.PostAsync("/approval/request/reject", createJsonContent parameters)
            let! firstText = first.Content.ReadAsStringAsync()
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK), firstText)

            let! duplicate = client.PostAsync("/approval/request/reject", createJsonContent parameters)
            Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }
