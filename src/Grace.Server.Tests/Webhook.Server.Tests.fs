namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Webhooks
open NUnit.Framework
open System
open System.Net
open System.Net.Http

module private WebhookTestHelpers =

    let createAuthenticatedClient (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
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

    let ruleParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Webhook.CreateWebhookRuleParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.Name <- $"webhook-{Guid.NewGuid():N}"
        parameters.EventName <- ExternalWebhookEventRegistry.PromotionSetAppliedName
        parameters.EventVersion <- 1
        parameters.Url <- "https://example.com/grace-webhook"
        parameters.SigningSecretVersion <- "test-secret-v1"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let ruleIdParameters<'T when 'T :> Parameters.Webhook.WebhookRuleParameters and 'T: (new: unit -> 'T)>
        (repositoryId: string)
        (branchId: string)
        (ruleId: string)
        =
        let parameters = new 'T()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.WebhookRuleId <- ruleId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let updateRuleParameters repositoryId branchId ruleId =
        let parameters = ruleParameters repositoryId branchId
        parameters.WebhookRuleId <- ruleId
        parameters.Name <- $"updated-{Guid.NewGuid():N}"
        parameters

    let listRuleParameters repositoryId branchId =
        let parameters = Parameters.Webhook.ListWebhookRulesParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let listDeliveryParameters repositoryId branchId ruleId =
        let parameters = Parameters.Webhook.ListWebhookDeliveriesParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.WebhookRuleId <- ruleId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let showDeliveryParameters repositoryId branchId deliveryId =
        let parameters = Parameters.Webhook.ShowWebhookDeliveryParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.WebhookDeliveryId <- deliveryId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createRuleAsync (client: HttpClient) repositoryId branchId =
        task {
            let! createResponse = client.PostAsync("/webhook/rule/create", createJsonContent (ruleParameters repositoryId branchId))

            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            return! deserializeContent<WebhookRule> createResponse
        }

[<NonParallelizable>]
type WebhookApiIntegrationTests() =

    [<SetUp>]
    member _.SetUp() = WebhookStore.clearForTests ()

    [<Test>]
    member _.RuleLifecycleAndDeliveryQueryHappyPath() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = WebhookTestHelpers.createAuthenticatedClient adminUser
            let! created = WebhookTestHelpers.createRuleAsync adminClient repositoryId branchId

            Assert.That(created.Status, Is.EqualTo(WebhookRuleStatus.Disabled))
            Assert.That(created.Url.Url, Is.EqualTo("https://example.com/grace-webhook"))
            Assert.That(created.Url.Safety, Is.EqualTo(OutboundUrlSafety.PublicHttps))

            let showParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.ShowWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let enableParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.EnableWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let! enableResponse = adminClient.PostAsync("/webhook/rule/enable", createJsonContent enableParameters)
            Assert.That(enableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let testParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.TestWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            testParameters.DedupeKey <- "test-dedupe-key"
            let! testResponse = adminClient.PostAsync("/webhook/rule/test", createJsonContent testParameters)
            Assert.That(testResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! testDelivery = deserializeContent<WebhookDelivery> testResponse
            Assert.That(testDelivery.Status, Is.EqualTo(WebhookDeliveryStatus.Pending))
            Assert.That(testDelivery.WebhookRuleId, Is.EqualTo(created.WebhookRuleId))
            Assert.That(testDelivery.DedupeKey, Is.EqualTo("test-dedupe-key"))

            let! listResponse =
                adminClient.PostAsync(
                    "/webhook/delivery/list",
                    createJsonContent (WebhookTestHelpers.listDeliveryParameters repositoryId branchId (created.WebhookRuleId.ToString()))
                )

            Assert.That(listResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! deliveries = deserializeContent<WebhookDelivery array> listResponse

            Assert.That(
                deliveries
                |> Array.map (fun delivery -> delivery.WebhookDeliveryId),
                Is.EquivalentTo([| testDelivery.WebhookDeliveryId |])
            )

            let! showDeliveryResponse =
                adminClient.PostAsync(
                    "/webhook/delivery/show",
                    createJsonContent (WebhookTestHelpers.showDeliveryParameters repositoryId branchId (testDelivery.WebhookDeliveryId.ToString()))
                )

            Assert.That(showDeliveryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let disableParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.DisableWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let deleteParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.DeleteWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let! showResponse = adminClient.PostAsync("/webhook/rule/show", createJsonContent showParameters)
            Assert.That(showResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! disableResponse = adminClient.PostAsync("/webhook/rule/disable", createJsonContent disableParameters)
            Assert.That(disableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! deleteResponse = adminClient.PostAsync("/webhook/rule/delete", createJsonContent deleteParameters)
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    member _.CreateRejectsUnsafePublicUrl() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = WebhookTestHelpers.createAuthenticatedClient adminUser
            let parameters = WebhookTestHelpers.ruleParameters repositoryId branchId
            parameters.Url <- "http://localhost:5000/webhook"

            let! response = adminClient.PostAsync("/webhook/rule/create", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.RuleListReturnsOnlyRequestedAuthorizedScope() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = $"{Guid.NewGuid()}"
            let otherBranchId = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = WebhookTestHelpers.createAuthenticatedClient adminUser
            let! allowedRule = WebhookTestHelpers.createRuleAsync adminClient repositoryId allowedBranchId
            let! otherRule = WebhookTestHelpers.createRuleAsync adminClient repositoryId otherBranchId

            let! listResponse =
                adminClient.PostAsync("/webhook/rule/list", createJsonContent (WebhookTestHelpers.listRuleParameters repositoryId allowedBranchId))

            Assert.That(listResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! rules = deserializeContent<WebhookRule array> listResponse

            Assert.That(
                rules
                |> Array.map (fun rule -> rule.WebhookRuleId),
                Is.EquivalentTo([| allowedRule.WebhookRuleId |])
            )

            Assert.That(
                rules
                |> Array.exists (fun rule -> rule.WebhookRuleId = otherRule.WebhookRuleId),
                Is.False
            )
        }

    [<Test>]
    member _.StoredScopeBlocksBodyScopeSpoofingForRuleAndDeliveryShow() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = $"{Guid.NewGuid()}"
            let storedBranchId = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"
            let limitedUser = $"{Guid.NewGuid()}"

            let! grantAdmin = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepoAdmin"
            Assert.That(grantAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = WebhookTestHelpers.createAuthenticatedClient adminUser
            let! storedRule = WebhookTestHelpers.createRuleAsync adminClient repositoryId storedBranchId

            let testParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.TestWebhookRuleParameters>
                    repositoryId
                    storedBranchId
                    (storedRule.WebhookRuleId.ToString())

            let! testResponse = adminClient.PostAsync("/webhook/rule/test", createJsonContent testParameters)
            Assert.That(testResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! storedDelivery = deserializeContent<WebhookDelivery> testResponse

            let! grantLimited = WebhookTestHelpers.grantRoleAsync Client "branch" ownerId organizationId repositoryId allowedBranchId limitedUser "BranchAdmin"

            Assert.That(grantLimited.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use limitedClient = WebhookTestHelpers.createAuthenticatedClient limitedUser

            let spoofedShow =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.ShowWebhookRuleParameters>
                    repositoryId
                    allowedBranchId
                    (storedRule.WebhookRuleId.ToString())

            let! showResponse = limitedClient.PostAsync("/webhook/rule/show", createJsonContent spoofedShow)
            Assert.That(showResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let spoofedUpdate = WebhookTestHelpers.updateRuleParameters repositoryId allowedBranchId (storedRule.WebhookRuleId.ToString())

            let! updateResponse = limitedClient.PostAsync("/webhook/rule/update", createJsonContent spoofedUpdate)
            Assert.That(updateResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let spoofedDeliveryShow = WebhookTestHelpers.showDeliveryParameters repositoryId allowedBranchId (storedDelivery.WebhookDeliveryId.ToString())

            let! deliveryShowResponse = limitedClient.PostAsync("/webhook/delivery/show", createJsonContent spoofedDeliveryShow)
            Assert.That(deliveryShowResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
        }

    [<Test>]
    member _.ApprovalNotificationDeliveryIsNotWebhookDeliverySurface() =
        task {
            use client = WebhookTestHelpers.createAuthenticatedClient $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/approval/notification-delivery/list", createJsonContent (obj ()))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
        }
