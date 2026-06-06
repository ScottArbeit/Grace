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

module private RestartDurabilityHelpers =
    let requireOkAsync (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return body
        }

    let getSharedHostState () =
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
            }
        | None ->
            Assert.Fail("Aspire test host was not started by the shared setup fixture.")
            Unchecked.defaultof<TestHostState>

    let restartGraceServerAsync () = AspireTestHost.restartGraceServerAsync (getSharedHostState ())

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

    let pseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        let mutable state = 0x6284A1D7u

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    let encodeBlock bytes =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test ContentBlock to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    let manifestFor (bytes: byte array) (block: ContentBlockFormat.EncodedContentBlock) =
        let contentBlock = ContentBlock.Create(block.Address, 0L, int64 bytes.Length)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                [ contentBlock ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let setStorageParameters (parameters: Parameters.Storage.StorageParameters) repositoryId correlationId =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- correlationId

    let postUploadSessionDecisionAsync (route: string) parameters =
        task {
            let! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = requireOkAsync response
            return deserialize<GraceReturnValue<UploadSessionDecision>> body
        }

    let uploadContentBlockWithSasAsync (payload: byte array) (uploadUri: Uri) =
        task {
            let blockBlobClient = BlockBlobClient(uploadUri)
            use payloadStream = new MemoryStream(payload, writable = false)
            let options = BlobUploadOptions()
            options.Conditions <- BlobRequestConditions(IfNoneMatch = Azure.ETag.All)
            let! response = blockBlobClient.UploadAsync(payloadStream, options)
            return response.Value.ETag.ToString()
        }

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
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "restart-durability-proof"
            start.OperationId <- "start"

            let! startResult = postUploadSessionDecisionAsync "/storage/startManifestUploadSession" start
            Assert.That(startResult.ReturnValue.Session.UploadSessionId, Is.EqualTo(sessionId))

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecisionAsync "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = requireOkAsync uploadUriResponse
            Assert.That(uploadUriResponse.Content.Headers.ContentType, Is.Null)

            let! uploadETag = uploadContentBlockWithSasAsync block.Payload (Uri uploadUriBody)

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- { ObjectKey = $"cas/content-blocks/{block.Address}"; ETag = Some uploadETag }

            let! confirmResult = postUploadSessionDecisionAsync "/storage/confirmContentBlockUpload" confirm
            Assert.That(confirmResult.ReturnValue.Session.ConfirmedBlockUploads.Length, Is.EqualTo(1))

            return correlationId, sessionId, block, manifest
        }

    let finalizeManifestUploadAsync repositoryId correlationId sessionId manifest =
        task {
            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- "/"
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest

            let! finalizeResult = postUploadSessionDecisionAsync "/storage/finalizeManifestUpload" finalize
            return finalizeResult.ReturnValue.Session
        }

    let formatInstant (instant: Instant) = InstantPattern.ExtendedIso.Format instant

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

[<NonParallelizable>]
type RestartDurabilityServer() =

    [<Test>]
    [<Order(1)>]
    member _.GraceServerProjectResourceRestartsAndReturnsHealthyResponses() =
        task {
            let! before = Client.GetAsync("/healthz")
            Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            do! RestartDurabilityHelpers.restartGraceServerAsync ()

            let! after = Client.GetAsync("/healthz")
            Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        }

    [<Test>]
    [<Order(2)>]
    member _.DurableActorStateRehydratesAcrossGraceServerProjectRestart() =
        task {
            let! repositoryId = RestartDurabilityHelpers.createRepositoryAsync "restart-proof"
            let! repositoryBeforeRestart = RestartDurabilityHelpers.getRepositoryAsync repositoryId
            let! firstWorkItemId = RestartDurabilityHelpers.createWorkItemAsync repositoryId "before restart"
            let! firstWorkItem = RestartDurabilityHelpers.getWorkItemAsync repositoryId firstWorkItemId

            let! uploadCorrelationId, uploadSessionId, block, manifest = RestartDurabilityHelpers.createConfirmedUploadSessionAsync repositoryId

            let reminderActorName = "DirectoryVersionActor"
            let reminderActorId = $"{Guid.NewGuid()}"
            let reminderFireAt = Instant.FromUtc(2035, 6, 1, 12, 0, 0)
            let! reminderId = RestartDurabilityHelpers.createReminderAsync reminderActorName reminderActorId reminderFireAt

            do! RestartDurabilityHelpers.restartGraceServerAsync ()

            let! ownerAfterRestart = RestartDurabilityHelpers.getOwnerAsync ()
            Assert.That(ownerAfterRestart.OwnerId, Is.EqualTo(Guid.Parse(ownerId)))

            let! repositoryAfterRestart = RestartDurabilityHelpers.getRepositoryAsync repositoryId
            Assert.That(repositoryAfterRestart.RepositoryId, Is.EqualTo(repositoryBeforeRestart.RepositoryId))

            let! firstWorkItemAfterRestart = RestartDurabilityHelpers.getWorkItemAsync repositoryId firstWorkItemId

            Assert.That(firstWorkItemAfterRestart.WorkItemId, Is.EqualTo(firstWorkItem.WorkItemId))
            Assert.That(firstWorkItemAfterRestart.WorkItemNumber, Is.EqualTo(firstWorkItem.WorkItemNumber))

            let! secondWorkItemId = RestartDurabilityHelpers.createWorkItemAsync repositoryId "after restart"
            let! secondWorkItem = RestartDurabilityHelpers.getWorkItemAsync repositoryId secondWorkItemId
            Assert.That(secondWorkItem.WorkItemNumber, Is.EqualTo(firstWorkItem.WorkItemNumber + 1L))

            let! finalizedSession = RestartDurabilityHelpers.finalizeManifestUploadAsync repositoryId uploadCorrelationId uploadSessionId manifest

            Assert.That(finalizedSession.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
            Assert.That(finalizedSession.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            Assert.That(
                finalizedSession.ConfirmedBlockUploads
                |> Array.exists (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = block.Address),
                Is.True
            )

            let! reminderAfterRestart = RestartDurabilityHelpers.getReminderAsync reminderId
            Assert.That(reminderAfterRestart.ReminderId, Is.EqualTo(reminderId))
            Assert.That(reminderAfterRestart.ActorName, Is.EqualTo(reminderActorName))
            Assert.That(reminderAfterRestart.ActorId, Is.EqualTo(reminderActorId))
            Assert.That(reminderAfterRestart.ReminderType, Is.EqualTo(ReminderTypes.Maintenance))
            Assert.That(reminderAfterRestart.ReminderTime, Is.EqualTo(reminderFireAt))
        }
