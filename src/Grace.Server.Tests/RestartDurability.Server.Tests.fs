namespace Grace.Server.Tests

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Reminder
open Grace.Types.UploadSession
open Grace.Types.WorkItem
open NodaTime
open NodaTime.Text
open NUnit.Framework
open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// Groups shared helpers for restart durability helpers.
module RestartDurabilityHelpers =
    /// Requires ok and fails the test when missing.
    let requireOkAsync (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    /// Defines content block placement from URI behavior for the surrounding tests used by the server integration restart Durability scenario.
    let contentBlockPlacementFromUri (blobUriWithSasToken: Uri) eTag =
        let pathSegments =
            blobUriWithSasToken
                .AbsolutePath
                .Trim('/')
                .Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map Uri.UnescapeDataString

        let isPathStyleAzurite =
            blobUriWithSasToken.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(blobUriWithSasToken.Host)
               |> fst

        let accountName =
            if isPathStyleAzurite && pathSegments.Length >= 3 then
                pathSegments[0]
            else
                let host = blobUriWithSasToken.Host
                let firstDot = host.IndexOf('.')

                if firstDot > 0 then host.Substring(0, firstDot) else host

        let containerIndex = if isPathStyleAzurite then 1 else 0

        {
            StorageAccountName = accountName
            StorageContainerName = StorageContainerName pathSegments[containerIndex]
            ObjectKey = String.Join("/", pathSegments |> Array.skip (containerIndex + 1))
            ETag = eTag
        }

    /// Gets shared host state from the running test server.
    let getSharedHostState () =
        match App with
        | Some app ->
            {
                App = app
                Client = Client
                GraceServerBaseAddress = graceServerBaseAddress
                CosmosConnectionString = String.Empty
                CosmosDatabaseName = String.Empty
                CosmosContainerName = String.Empty
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

    /// Builds a deterministic repository for integration setup fixture for the server integration restart Durability assertions.
    let createRepositoryAsync repositoryNamePrefix =
        task {
            let repositoryId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Repository.CreateRepositoryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.RepositoryName <- $"{repositoryNamePrefix}-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/create", createJsonContent parameters)
            let! _ = requireOkAsync response

            let storageConnectionString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageConnectionString)

            if not (String.IsNullOrWhiteSpace storageConnectionString) then
                let serviceClient = BlobServiceClient(storageConnectionString)
                let containerClient = serviceClient.GetBlobContainerClient(repositoryId.ToLowerInvariant())
                let! _ = containerClient.CreateIfNotExistsAsync()
                ()

            return repositoryId
        }

    /// Gets owner from the running test server.
    let getOwnerAsync () =
        task {
            let parameters = Parameters.Owner.GetOwnerParameters()
            parameters.OwnerId <- ownerId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/owner/get", createJsonContent parameters)
            let! body = requireOkAsync response
            let returnValue = deserialize<GraceReturnValue<Owner.OwnerDto>> body
            return returnValue.ReturnValue
        }

    /// Gets repository from the running test server.
    let getRepositoryAsync repositoryId =
        task {
            let parameters = Parameters.Repository.GetRepositoryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/get", createJsonContent parameters)
            let! body = requireOkAsync response
            let returnValue = deserialize<GraceReturnValue<Repository.RepositoryDto>> body
            return returnValue.ReturnValue
        }

    /// Builds a deterministic work item for integration setup fixture for the server integration restart Durability assertions.
    let createWorkItemAsync repositoryId title =
        task {
            let workItemId = $"{Guid.NewGuid()}"
            let parameters = Parameters.WorkItem.CreateWorkItemParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemId
            parameters.Title <- title
            parameters.Description <- "restart durability integration test"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/work/create", createJsonContent parameters)
            let! _ = requireOkAsync response
            return workItemId
        }

    /// Gets work item from the running test server.
    let getWorkItemAsync repositoryId workItemIdentifier =
        task {
            let parameters = Parameters.WorkItem.GetWorkItemParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.WorkItemId <- workItemIdentifier
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/work/get", createJsonContent parameters)
            let! body = requireOkAsync response
            let returnValue = deserialize<GraceReturnValue<WorkItemDto>> body
            return returnValue.ReturnValue
        }

    /// Defines pseudo random bytes behavior for the surrounding tests used by the server integration restart Durability scenario.
    let pseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        let mutable state = 0x6284A1D7u

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    /// Defines encode block behavior for the surrounding tests used by the server integration restart Durability scenario.
    let encodeBlock bytes =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test ContentBlock to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Defines manifest for storage pool behavior for the surrounding tests used by the server integration restart Durability scenario.
    let manifestForStoragePool storagePoolId (bytes: byte array) (block: ContentBlockFormat.EncodedContentBlock) =
        let contentBlock = ContentBlock.Create(block.Address, 0L, int64 bytes.Length)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                storagePoolId,
                [ contentBlock ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Defines manifest for behavior for the surrounding tests used by the server integration restart Durability scenario.
    let manifestFor bytes block = manifestForStoragePool (StoragePoolId Constants.DefaultStoragePoolId) bytes block

    /// Defines exact upload scope behavior for the surrounding tests used by the server integration restart Durability scenario.
    let exactUploadScope (sessionId: Guid) = $"/restart-durability/upload-sessions/{sessionId:N}/content.bin"

    /// Builds set storage parameters for route calls.
    let setStorageParameters (parameters: Parameters.Storage.StorageParameters) repositoryId correlationId =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- correlationId

    /// Posts upload session decision to the running test server.
    let postUploadSessionDecisionAsync (route: string) parameters =
        task {
            let! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = requireOkAsync response
            return deserialize<GraceReturnValue<UploadSessionDecision>> body
        }

    /// Uploads content block with SAS through storage test infrastructure.
    let uploadContentBlockWithSasAsync (payload: byte array) (uploadUri: Uri) =
        task {
            let blockBlobClient = BlockBlobClient(uploadUri)
            use payloadStream = new MemoryStream(payload, writable = false)
            let options = BlobUploadOptions()
            options.Conditions <- BlobRequestConditions(IfNoneMatch = Azure.ETag.All)
            let! response = blockBlobClient.UploadAsync(payloadStream, options)
            return response.Value.ETag.ToString()
        }

    /// Builds a deterministic confirmed upload session for integration setup fixture for the server integration restart Durability assertions.
    let createConfirmedUploadSessionAsync repositoryId =
        task {
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 11uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- exactUploadScope sessionId
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "restart-durability-proof"
            start.OperationId <- "start"

            let! startResult = postUploadSessionDecisionAsync "/storage/startManifestUploadSession" start
            Assert.That(startResult.ReturnValue.Session.UploadSessionId, Is.EqualTo(sessionId))
            let manifest = manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId payload block

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- exactUploadScope sessionId
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecisionAsync "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- exactUploadScope sessionId

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = requireOkAsync uploadUriResponse
            Assert.That(uploadUriResponse.Content.Headers.ContentType, Is.Null)

            let uploadUri = Uri uploadUriBody
            let! uploadETag = uploadContentBlockWithSasAsync block.Payload uploadUri

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- exactUploadScope sessionId
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- contentBlockPlacementFromUri uploadUri (Some uploadETag)

            let! confirmResult = postUploadSessionDecisionAsync "/storage/confirmContentBlockUpload" confirm
            Assert.That(confirmResult.ReturnValue.Session.ConfirmedBlockUploads.Length, Is.EqualTo(1))

            return correlationId, sessionId, block, manifest
        }

    /// Defines finalize manifest upload behavior for the surrounding tests used by the server integration restart Durability scenario.
    let finalizeManifestUploadAsync repositoryId correlationId sessionId manifest =
        task {
            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- exactUploadScope sessionId
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest

            let! finalizeResult = postUploadSessionDecisionAsync "/storage/finalizeManifestUpload" finalize
            return finalizeResult.ReturnValue.Session
        }

    /// Formats instant for diagnostics.
    let formatInstant (instant: Instant) = InstantPattern.ExtendedIso.Format instant

    /// Builds a deterministic reminder for integration setup fixture for the server integration restart Durability assertions.
    let createReminderAsync actorName actorId fireAt =
        task {
            let parameters = Parameters.Reminder.CreateReminderParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[0]
            parameters.ActorName <- actorName
            parameters.ActorId <- actorId
            parameters.ReminderType <- "Maintenance"
            parameters.FireAt <- formatInstant fireAt
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/reminder/create", createJsonContent parameters)
            let! body = requireOkAsync response
            let returnValue = deserialize<GraceReturnValue<string>> body

            let matchResult = Regex.Match(returnValue.ReturnValue, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")

            if matchResult.Success then
                return Guid.Parse(matchResult.Value)
            else
                Assert.Fail($"Could not find a reminder id in message: {returnValue.ReturnValue}")
                return Guid.Empty
        }

    /// Gets reminder from the running test server.
    let getReminderAsync reminderId =
        task {
            let parameters = Parameters.Reminder.GetReminderParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryIds[0]
            parameters.ReminderId <- $"{reminderId}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/reminder/get", createJsonContent parameters)
            let! body = requireOkAsync response
            let returnValue = deserialize<GraceReturnValue<ReminderDto>> body
            return returnValue.ReturnValue
        }

/// Owns durable actor state carried across the shared Grace.Server restart fixture.
module internal RestartDurabilityScenario =

    /// Identifies the durable values that must remain usable after restart.
    type Context =
        {
            RepositoryId: string
            RepositoryBeforeRestart: Repository.RepositoryDto
            FirstWorkItemId: string
            FirstWorkItem: WorkItemDto
            UploadCorrelationId: string
            UploadSessionId: Guid
            Block: ContentBlockFormat.EncodedContentBlock
            Manifest: FileManifest
            ReminderActorName: string
            ReminderActorId: string
            ReminderFireAt: Instant
            ReminderId: Guid
        }

    /// Creates and observes durable actor state before the shared Grace.Server restart.
    let prepareAsync () =
        task {
            try
                let! repositoryId = RestartDurabilityHelpers.createRepositoryAsync "restart-proof"
                let! repositoryBeforeRestart = RestartDurabilityHelpers.getRepositoryAsync repositoryId
                let! firstWorkItemId = RestartDurabilityHelpers.createWorkItemAsync repositoryId "before restart"
                let! firstWorkItem = RestartDurabilityHelpers.getWorkItemAsync repositoryId firstWorkItemId

                let! uploadCorrelationId, uploadSessionId, block, manifest = RestartDurabilityHelpers.createConfirmedUploadSessionAsync repositoryId

                let reminderActorName = "DirectoryVersionActor"
                let reminderActorId = $"{Guid.NewGuid()}"
                let reminderFireAt = Instant.FromUtc(2035, 6, 1, 12, 0, 0)
                let! reminderId = RestartDurabilityHelpers.createReminderAsync reminderActorName reminderActorId reminderFireAt

                return
                    {
                        RepositoryId = repositoryId
                        RepositoryBeforeRestart = repositoryBeforeRestart
                        FirstWorkItemId = firstWorkItemId
                        FirstWorkItem = firstWorkItem
                        UploadCorrelationId = uploadCorrelationId
                        UploadSessionId = uploadSessionId
                        Block = block
                        Manifest = manifest
                        ReminderActorName = reminderActorName
                        ReminderActorId = reminderActorId
                        ReminderFireAt = reminderFireAt
                        ReminderId = reminderId
                    }
            with
            | ex -> return raise (InvalidOperationException("Shared Grace.Server restart durable actor preparation failed.", ex))
        }

    /// Confirms the prepared actor state rehydrates and continues correctly after restart.
    let verifyAfterRestartAsync context =
        task {
            let! ownerAfterRestart = RestartDurabilityHelpers.getOwnerAsync ()
            Assert.That(ownerAfterRestart.OwnerId, Is.EqualTo(Guid.Parse(ownerId)))

            let! repositoryAfterRestart = RestartDurabilityHelpers.getRepositoryAsync context.RepositoryId
            Assert.That(repositoryAfterRestart.RepositoryId, Is.EqualTo(context.RepositoryBeforeRestart.RepositoryId))

            let! firstWorkItemAfterRestart = RestartDurabilityHelpers.getWorkItemAsync context.RepositoryId context.FirstWorkItemId

            Assert.That(firstWorkItemAfterRestart.WorkItemId, Is.EqualTo(context.FirstWorkItem.WorkItemId))
            Assert.That(firstWorkItemAfterRestart.WorkItemNumber, Is.EqualTo(context.FirstWorkItem.WorkItemNumber))

            let! secondWorkItemId = RestartDurabilityHelpers.createWorkItemAsync context.RepositoryId "after restart"
            let! secondWorkItem = RestartDurabilityHelpers.getWorkItemAsync context.RepositoryId secondWorkItemId
            Assert.That(secondWorkItem.WorkItemNumber, Is.EqualTo(context.FirstWorkItem.WorkItemNumber + 1L))

            let! finalizedSession =
                RestartDurabilityHelpers.finalizeManifestUploadAsync context.RepositoryId context.UploadCorrelationId context.UploadSessionId context.Manifest

            Assert.That(finalizedSession.FinalizedManifestAddress, Is.EqualTo(Some context.Manifest.ManifestAddress))
            Assert.That(finalizedSession.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            Assert.That(
                finalizedSession.ConfirmedBlockUploads
                |> Array.exists (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = context.Block.Address),
                Is.True
            )

            let! reminderAfterRestart = RestartDurabilityHelpers.getReminderAsync context.ReminderId
            Assert.That(reminderAfterRestart.ReminderId, Is.EqualTo(context.ReminderId))
            Assert.That(reminderAfterRestart.ActorName, Is.EqualTo(context.ReminderActorName))
            Assert.That(reminderAfterRestart.ActorId, Is.EqualTo(context.ReminderActorId))
            Assert.That(reminderAfterRestart.ReminderType, Is.EqualTo(ReminderTypes.Maintenance))
            Assert.That(reminderAfterRestart.ReminderTime, Is.EqualTo(context.ReminderFireAt))
        }

/// Exercises the five ordinary restart expectations around one deliberate Grace.Server restart.
[<TestFixture>]
[<NonParallelizable>]
[<FixtureLifeCycle(LifeCycle.SingleInstance)>]
type SharedGraceServerRestartScenarios() =
    let mutable agentSessionContext: AgentSessionRestartScenario.Context option = None
    let mutable approvalContext: ApprovalRestartScenario.Context option = None
    let mutable webhookContext: WebhookRestartScenario.Context option = None
    let mutable durabilityContext: RestartDurabilityScenario.Context option = None
    let mutable restartEvidence: GraceServerRestartEvidence option = None

    /// Requires preparation state produced by this fixture's one-time setup.
    let requireContext scenarioName context =
        context
        |> Option.defaultWith (fun () -> invalidOp $"Shared Grace.Server restart context was unavailable for {scenarioName} post-restart verification.")

    /// Prepares every scenario against one server process, then performs the fixture's only restart.
    [<OneTimeSetUp>]
    member _.PrepareAndRestartAsync() =
        task {
            try
                let! beforeRestartHealth = Client.GetAsync("/healthz")
                let! beforeRestartHealthBody = beforeRestartHealth.Content.ReadAsStringAsync()
                Assert.That(beforeRestartHealth.StatusCode, Is.EqualTo(HttpStatusCode.OK), beforeRestartHealthBody)
            with
            | ex -> return raise (InvalidOperationException("Shared Grace.Server restart server-health preparation failed.", ex))

            let! preparedAgentSession = AgentSessionRestartScenario.prepareAsync ()
            agentSessionContext <- Some preparedAgentSession

            let! preparedApproval = ApprovalRestartScenario.prepareAsync ()
            approvalContext <- Some preparedApproval

            let! preparedWebhook = WebhookRestartScenario.prepareAsync ()
            webhookContext <- Some preparedWebhook

            let! preparedDurability = RestartDurabilityScenario.prepareAsync ()
            durabilityContext <- Some preparedDurability

            let! observedRestart =
                task {
                    try
                        return!
                            AspireTestHost.restartGraceServerWithEvidenceAsync
                                (RestartDurabilityHelpers.getSharedHostState ())
                                "SharedGraceServerRestartScenarios.PrepareAndRestartAsync"
                    with
                    | ex -> return raise (InvalidOperationException("Shared Grace.Server restart phase failed.", ex))
                }

            restartEvidence <- Some observedRestart
        }

    /// Verifies fresh restart transitions, HTTP readiness, and `/healthz` after the shared restart.
    [<Test>]
    member _.GraceServerRestartRetainsFreshReadinessAndHealthEvidence() =
        task {
            let restart = requireContext "server health" restartEvidence
            use _ = Assert.EnterMultipleScope()

            Assert.That(restart.CommandCompletedAt, Is.GreaterThanOrEqualTo(restart.CommandStartedAt))
            Assert.That(restart.NonReadyEventObservedAt, Is.GreaterThan(restart.CommandCompletedAt))

            Assert.That(
                String.Equals(restart.NonReadyResourceState, "Running", StringComparison.OrdinalIgnoreCase)
                && String.Equals(restart.NonReadyHealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase),
                Is.False,
                $"Expected a fresh non-ready observation, got state={restart.NonReadyResourceState}; health={restart.NonReadyHealthStatus}."
            )

            Assert.That(restart.ResourceEventObservedAt, Is.GreaterThan(restart.NonReadyEventObservedAt))
            Assert.That(restart.ResourceState, Is.EqualTo("Healthy"))
            Assert.That(restart.HttpReadyObservedAt, Is.GreaterThan(restart.ResourceEventObservedAt))

            let! afterRestartHealth = Client.GetAsync("/healthz")
            let! afterRestartHealthBody = afterRestartHealth.Content.ReadAsStringAsync()
            Assert.That(afterRestartHealth.StatusCode, Is.EqualTo(HttpStatusCode.OK), afterRestartHealthBody)
        }

    /// Verifies active Agent Sessions remain process-local across the shared restart.
    [<Test>]
    member _.ActiveAgentSessionsAreProcessLocalAcrossRestart() =
        AgentSessionRestartScenario.verifyAfterRestartAsync (requireContext "Agent Session" agentSessionContext)

    /// Verifies Approval policies are lost while generated requests remain actor-backed across the shared restart.
    [<Test>]
    member _.ApprovalPolicyStoreIsProcessLocalWhileGeneratedRequestsRemainActorBackedAcrossRestart() =
        ApprovalRestartScenario.verifyAfterRestartAsync (requireContext "Approval" approvalContext)

    /// Verifies Webhook rules and HTTP-observable deliveries remain process-local across the shared restart.
    [<Test>]
    member _.WebhookRuleAndHttpObservableDeliveriesAreProcessLocalAcrossRestart() =
        WebhookRestartScenario.verifyAfterRestartAsync (requireContext "Webhook" webhookContext)

    /// Verifies durable actor state rehydrates and continues across the shared restart.
    [<Test>]
    member _.DurableActorStateRehydratesAcrossGraceServerProjectRestart() =
        RestartDurabilityScenario.verifyAfterRestartAsync (requireContext "durable actor state" durabilityContext)
