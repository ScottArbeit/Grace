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

/// Groups shared helpers for webhook test helpers.
module private WebhookTestHelpers =

    /// Restarts Grace.Server with a scenario label for process-local webhook assertions.
    let restartGraceServerAsync restartContext =
        let state =
            match App with
            | Some app ->
                {
                    App = app
                    Client = Client
                    GraceServerBaseAddress = graceServerBaseAddress
                    ServiceBusConnectionString = serviceBusConnectionString
                    ServiceBusTopic = serviceBusTopic
                    ServiceBusServerSubscription = serviceBusServerSubscription
                    ServiceBusTestSubscription = serviceBusTestSubscription
                    OperationalFactsTopic = operationalFactsTopic
                    OperationsSqlConnectionString = operationsSqlConnectionString
                }
            | None ->
                Assert.Fail("Aspire test host was not started by the shared setup fixture.")
                Unchecked.defaultof<TestHostState>

        AspireTestHost.restartGraceServerAsync state restartContext

    /// Builds a deterministic authenticated client for integration setup fixture for the server integration webhook assertions.
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

            return! client.PostAsync("/authorize/grant-role", createJsonContent parameters)
        }

    /// Builds rule parameters for route calls.
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

    /// Builds update rule parameters for route calls.
    let updateRuleParameters repositoryId branchId ruleId =
        let parameters = ruleParameters repositoryId branchId
        parameters.WebhookRuleId <- ruleId
        parameters.Name <- $"updated-{Guid.NewGuid():N}"
        parameters

    /// Builds list rule parameters for route calls.
    let listRuleParameters repositoryId branchId =
        let parameters = Parameters.Webhook.ListWebhookRulesParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds list delivery parameters for route calls.
    let listDeliveryParameters repositoryId branchId ruleId =
        let parameters = Parameters.Webhook.ListWebhookDeliveriesParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.WebhookRuleId <- ruleId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds show delivery parameters for route calls.
    let showDeliveryParameters repositoryId branchId deliveryId =
        let parameters = Parameters.Webhook.ShowWebhookDeliveryParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.TargetBranchId <- branchId
        parameters.WebhookDeliveryId <- deliveryId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds a deterministic rule for integration setup fixture for the server integration webhook assertions.
    let createRuleAsync (client: HttpClient) repositoryId branchId =
        task {
            let! createResponse = client.PostAsync("/webhook/rule/create", createJsonContent (ruleParameters repositoryId branchId))

            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            return! deserializeContent<WebhookRule> createResponse
        }

    /// Asserts rule matches scope for integration responses.
    let assertRuleMatchesScope (repositoryId: string) (branchId: string) (rule: WebhookRule) =
        Assert.That(rule.Class, Is.EqualTo(nameof WebhookRule))
        Assert.That(rule.Scope.OwnerId, Is.EqualTo(Guid.Parse(ownerId)))
        Assert.That(rule.Scope.OrganizationId, Is.EqualTo(Guid.Parse(organizationId)))
        Assert.That(rule.Scope.RepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
        Assert.That(rule.Scope.TargetBranchId, Is.EqualTo(Some(Guid.Parse(branchId))))
        Assert.That(rule.EventName, Is.EqualTo(ExternalWebhookEventRegistry.PromotionSetAppliedName))
        Assert.That(rule.EventVersion, Is.EqualTo(1))
        Assert.That(rule.SigningSecretVersion, Is.EqualTo("test-secret-v1"))

/// Covers webhook API scenarios.
[<NonParallelizable>]
type WebhookApiIntegrationTests() =

    /// Resets shared test state before each integration test.
    [<SetUp>]
    member _.SetUp() = WebhookStore.clearForTests ()

    /// Verifies the rule lifecycle and delivery query happy path scenario.
    [<Test>]
    member _.RuleLifecycleAndDeliveryQueryHappyPath() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepositoryAdmin"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = WebhookTestHelpers.createAuthenticatedClient adminUser
            let! created = WebhookTestHelpers.createRuleAsync adminClient repositoryId branchId

            Assert.That(created.Status, Is.EqualTo(WebhookRuleStatus.Disabled))
            Assert.That(created.Url.Url, Is.EqualTo("https://example.com/grace-webhook"))
            Assert.That(created.Url.Safety, Is.EqualTo(OutboundUrlSafety.PublicHttps))
            Assert.That(created.CreatedBy, Is.EqualTo(adminUser))
            WebhookTestHelpers.assertRuleMatchesScope repositoryId branchId created

            let showParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.ShowWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let enableParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.EnableWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let! enableResponse = adminClient.PostAsync("/webhook/rule/enable", createJsonContent enableParameters)
            Assert.That(enableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! enabled = deserializeContent<WebhookRule> enableResponse
            Assert.That(enabled.Status, Is.EqualTo(WebhookRuleStatus.Enabled))
            WebhookTestHelpers.assertRuleMatchesScope repositoryId branchId enabled

            let! showEnabledResponse = adminClient.PostAsync("/webhook/rule/show", createJsonContent showParameters)
            Assert.That(showEnabledResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! shownEnabled = deserializeContent<WebhookRule> showEnabledResponse
            Assert.That(shownEnabled.WebhookRuleId, Is.EqualTo(created.WebhookRuleId))
            Assert.That(shownEnabled.Status, Is.EqualTo(WebhookRuleStatus.Enabled))
            WebhookTestHelpers.assertRuleMatchesScope repositoryId branchId shownEnabled

            let! listRulesResponse =
                adminClient.PostAsync("/webhook/rule/list", createJsonContent (WebhookTestHelpers.listRuleParameters repositoryId branchId))

            Assert.That(listRulesResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! listedRules = deserializeContent<WebhookRule array> listRulesResponse

            let listedRule =
                listedRules
                |> Array.find (fun rule -> rule.WebhookRuleId = created.WebhookRuleId)

            Assert.That(listedRule.Status, Is.EqualTo(WebhookRuleStatus.Enabled))
            WebhookTestHelpers.assertRuleMatchesScope repositoryId branchId listedRule

            let testParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.TestWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            testParameters.DedupeKey <- "test-dedupe-key"
            let! testResponse = adminClient.PostAsync("/webhook/rule/test", createJsonContent testParameters)
            Assert.That(testResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            let! testDelivery = deserializeContent<WebhookDelivery> testResponse
            Assert.That(testDelivery.Status, Is.EqualTo(WebhookDeliveryStatus.Pending))
            Assert.That(testDelivery.WebhookRuleId, Is.EqualTo(created.WebhookRuleId))
            Assert.That(testDelivery.DedupeKey, Is.EqualTo("test-dedupe-key"))
            Assert.That(testDelivery.EventName, Is.EqualTo(ExternalWebhookEventRegistry.PromotionSetAppliedName))
            Assert.That(testDelivery.EventVersion, Is.EqualTo(1))
            Assert.That(testDelivery.AttemptCount, Is.EqualTo(0))

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
            let! shownDelivery = deserializeContent<WebhookDelivery> showDeliveryResponse
            Assert.That(shownDelivery.WebhookDeliveryId, Is.EqualTo(testDelivery.WebhookDeliveryId))
            Assert.That(shownDelivery.WebhookRuleId, Is.EqualTo(created.WebhookRuleId))
            Assert.That(shownDelivery.DedupeKey, Is.EqualTo(testDelivery.DedupeKey))

            let disableParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.DisableWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let deleteParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.DeleteWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let! showResponse = adminClient.PostAsync("/webhook/rule/show", createJsonContent showParameters)
            Assert.That(showResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! shownBeforeDisable = deserializeContent<WebhookRule> showResponse
            Assert.That(shownBeforeDisable.Status, Is.EqualTo(WebhookRuleStatus.Enabled))

            let! disableResponse = adminClient.PostAsync("/webhook/rule/disable", createJsonContent disableParameters)
            Assert.That(disableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! disabled = deserializeContent<WebhookRule> disableResponse
            Assert.That(disabled.Status, Is.EqualTo(WebhookRuleStatus.Disabled))
            Assert.That(disabled.UpdatedAt.IsSome, Is.True)

            let! deleteResponse = adminClient.PostAsync("/webhook/rule/delete", createJsonContent deleteParameters)
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! deleted = deserializeContent<WebhookRule> deleteResponse
            Assert.That(deleted.Status, Is.EqualTo(WebhookRuleStatus.Deleted))
        }

    /// Verifies the webhook rule and HTTP observable deliveries are process local across restart scenario.
    [<Test>]
    [<NonParallelizable>]
    member _.WebhookRuleAndHttpObservableDeliveriesAreProcessLocalAcrossRestart() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepositoryAdmin"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = WebhookTestHelpers.createAuthenticatedClient adminUser
            let! created = WebhookTestHelpers.createRuleAsync adminClient repositoryId branchId

            let enableParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.EnableWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            let! enableResponse = adminClient.PostAsync("/webhook/rule/enable", createJsonContent enableParameters)
            let! enableText = enableResponse.Content.ReadAsStringAsync()
            Assert.That(enableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), enableText)

            let testParameters =
                WebhookTestHelpers.ruleIdParameters<Parameters.Webhook.TestWebhookRuleParameters> repositoryId branchId (created.WebhookRuleId.ToString())

            testParameters.DedupeKey <- $"restart-contract-{Guid.NewGuid():N}"
            let! testResponse = adminClient.PostAsync("/webhook/rule/test", createJsonContent testParameters)
            let! testText = testResponse.Content.ReadAsStringAsync()
            Assert.That(testResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), testText)
            let createdDelivery = deserialize<WebhookDelivery> testText

            let! deliveriesBeforeRestart =
                adminClient.PostAsync(
                    "/webhook/delivery/list",
                    createJsonContent (WebhookTestHelpers.listDeliveryParameters repositoryId branchId (created.WebhookRuleId.ToString()))
                )

            Assert.That(deliveriesBeforeRestart.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! listedBeforeRestart = deserializeContent<WebhookDelivery array> deliveriesBeforeRestart

            Assert.That(
                listedBeforeRestart
                |> Array.map (fun delivery -> delivery.WebhookDeliveryId),
                Does.Contain(createdDelivery.WebhookDeliveryId)
            )

            do! WebhookTestHelpers.restartGraceServerAsync "Webhook.WebhookRuleAndHttpObservableDeliveriesAreProcessLocalAcrossRestart"

            // Contract: webhook rules are process-local. HTTP-observable pending deliveries are intentionally
            // unavailable after restart; retry-scheduled deliveries share the same process-local store by
            // implementation contract, but this public route remains rule-dependent.
            let! rulesAfterRestart =
                adminClient.PostAsync("/webhook/rule/list", createJsonContent (WebhookTestHelpers.listRuleParameters repositoryId branchId))

            let! rulesAfterRestartText = rulesAfterRestart.Content.ReadAsStringAsync()
            Assert.That(rulesAfterRestart.StatusCode, Is.EqualTo(HttpStatusCode.OK), rulesAfterRestartText)
            let listedRulesAfterRestart = deserialize<WebhookRule array> rulesAfterRestartText

            Assert.That(
                listedRulesAfterRestart
                |> Array.exists (fun rule -> rule.WebhookRuleId = created.WebhookRuleId),
                Is.False
            )

            let! deliveriesAfterRestart =
                adminClient.PostAsync(
                    "/webhook/delivery/list",
                    createJsonContent (WebhookTestHelpers.listDeliveryParameters repositoryId branchId (created.WebhookRuleId.ToString()))
                )

            let! deliveriesAfterRestartText = deliveriesAfterRestart.Content.ReadAsStringAsync()
            Assert.That(deliveriesAfterRestart.StatusCode, Is.EqualTo(HttpStatusCode.OK), deliveriesAfterRestartText)
            let listedDeliveriesAfterRestart = deserialize<WebhookDelivery array> deliveriesAfterRestartText

            Assert.That(
                listedDeliveriesAfterRestart
                |> Array.exists (fun delivery -> delivery.WebhookDeliveryId = createdDelivery.WebhookDeliveryId),
                Is.False
            )
        }

    /// Verifies the create rejects unsafe public URL scenario.
    [<Test>]
    member _.CreateRejectsUnsafePublicUrl() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepositoryAdmin"
            Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use adminClient = WebhookTestHelpers.createAuthenticatedClient adminUser
            let parameters = WebhookTestHelpers.ruleParameters repositoryId branchId
            parameters.Url <- "http://localhost:5000/webhook"

            let! response = adminClient.PostAsync("/webhook/rule/create", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the rule list returns only requested authorized scope scenario.
    [<Test>]
    member _.RuleListReturnsOnlyRequestedAuthorizedScope() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = $"{Guid.NewGuid()}"
            let otherBranchId = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"

            let! grant = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepositoryAdmin"
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

    /// Verifies the stored scope blocks body scope spoofing for rule and delivery show scenario.
    [<Test>]
    member _.StoredScopeBlocksBodyScopeSpoofingForRuleAndDeliveryShow() =
        task {
            let repositoryId = repositoryIds[0]
            let allowedBranchId = $"{Guid.NewGuid()}"
            let storedBranchId = $"{Guid.NewGuid()}"
            let adminUser = $"{Guid.NewGuid()}"
            let limitedUser = $"{Guid.NewGuid()}"

            let! grantAdmin = WebhookTestHelpers.grantRoleAsync Client "repo" ownerId organizationId repositoryId "" adminUser "RepositoryAdmin"
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

    /// Verifies the approval notification delivery is not webhook delivery surface scenario.
    [<Test>]
    member _.ApprovalNotificationDeliveryIsNotWebhookDeliverySurface() =
        task {
            use client = WebhookTestHelpers.createAuthenticatedClient $"{Guid.NewGuid()}"
            let! response = client.PostAsync("/approval/notification-delivery/list", createJsonContent (obj ()))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
        }
