namespace Grace.Server.Tests

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.MaterializationPlan
open Grace.Types.Reminder
open Grace.Types.UploadSession
open Grace.Types.WorkItem
open NodaTime
open NodaTime.Text
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// Groups shared helpers for restart durability helpers.
module private RestartDurabilityHelpers =
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

    /// Restarts grace server to verify durability across process restarts.
    let restartGraceServerAsync () = AspireTestHost.restartGraceServerAsync (getSharedHostState ())

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

    /// Builds the public half of a static P-256 Cache identity key for HTTP enrollment requests.
    let cacheIdentityPublicKey (privateKey: ECDsa) =
        let parameters = privateKey.ExportParameters(false)
        CacheIdentityPublicKey.Create(ArtifactGrant.Base64Url.encode parameters.Q.X, ArtifactGrant.Base64Url.encode parameters.Q.Y)

    /// Sends a valid Cache enrollment request through the HTTP and Orleans actor boundary.
    let enrollCacheAsync (privateKey: ECDsa) (repositoryId: string) (displayName: string) (endpoint: string) =
        task {
            let organization = Guid.Parse organizationId

            let request =
                {
                    Class = nameof CacheEnrollmentRequest
                    DisplayName = displayName
                    BoundaryKind = CacheBoundaryKind.Organization
                    OwnerId = Guid.Parse ownerId
                    OrganizationId = Some organization
                    RepositoryScopes =
                        List<CacheRepositoryScope>(
                            [
                                CacheRepositoryScope.Create(organization, Guid.Parse repositoryId)
                            ]
                        )
                    PublicKey = cacheIdentityPublicKey privateKey
                    Endpoint = endpoint
                    AllowHttpEndpoint = true
                    SoftwareVersion = "1.0.0"
                    ProtocolVersion = "v1"
                    PrefetchSupported = true
                }

            let! response = Client.PostAsync("/cache/enroll", createJsonContent request)
            let! body = requireOkAsync response
            let result = deserialize<GraceReturnValue<CacheRegistrationResult>> body
            return request, result.ReturnValue
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

    /// Sends a canonical static-key refresh request after restart and returns the lifecycle result from the rehydrated actor.
    let refreshCacheAsync (privateKey: ECDsa) (cacheId: Guid) (endpoint: string) =
        task {
            let observedAt = getCurrentInstant ()

            let unsignedRequest =
                {
                    Class = nameof CacheRegistrationRefreshRequest
                    CacheId = cacheId
                    Endpoint = endpoint
                    Health = CacheHealthStatus.Healthy
                    SoftwareVersion = "1.0.1"
                    ProtocolVersion = "v1"
                    PrefetchSupported = false
                    ObservedAt = observedAt
                    Proof = Unchecked.defaultof<SignedCacheRequestProof>
                }

            let request =
                { unsignedRequest with
                    Proof =
                        CacheRegistrationProof.createProof
                            privateKey
                            cacheId
                            CacheRegistrationProof.RefreshOperation
                            (CacheRegistrationProof.refreshRequestDigest unsignedRequest)
                            observedAt
                }

            let! response = Client.PostAsync("/cache/refresh", createJsonContent request)
            let! body = requireOkAsync response
            let result = deserialize<GraceReturnValue<CacheRegistrationResult>> body
            return result.ReturnValue
        }

    /// Requests a cache-required plan so the server's materialization path selects from the reactivated Cache registration actor.
    let requestCacheRequiredPlanAsync repositoryId =
        task {
            use holderKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            let request =
                MaterializationPlanRequest
                    .Create(
                        MaterializationTargetSelector.ForBranch(BranchName Constants.InitialBranchName),
                        MaterializationExecutionMode.CacheRequired,
                        MaterializationCacheSelection.Required,
                        [
                            MaterializationArtifactKind.DirectoryVersionZip
                            MaterializationArtifactKind.RecursiveDirectoryMetadata
                        ]
                    )
                    .WithHolderPublicKey(ArtifactGrant.exportHolderPublicKey holderKey)

            let parameters = Parameters.Materialization.PlanParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()
            parameters.Request <- request

            let! response = Client.PostAsync("/materialization/plan", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            return response.StatusCode, body
        }

/// Covers restart durability server scenarios.
[<NonParallelizable>]
type RestartDurabilityServer() =

    /// Verifies the grace server project resource restarts and returns healthy responses scenario.
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

    /// Verifies the durable actor state rehydrates across grace server project restart scenario.
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

    /// Verifies valid HTTP enrollment keeps every immutable request field through the Orleans actor call and persists before success.
    [<Test>]
    [<Order(3)>]
    member _.CacheEnrollmentTransfersValidatedFieldsThroughTheActorBoundary() =
        task {
            let! invalidResponse = Client.PostAsync("/cache/enroll", createJsonContent Unchecked.defaultof<CacheEnrollmentRequest>)

            Assert.That(invalidResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            use privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let displayName = $"enrollment-transfer-{Guid.NewGuid():N}"
            let endpoint = $"http://cache-{Guid.NewGuid():N}.example.test"
            let! request, result = RestartDurabilityHelpers.enrollCacheAsync privateKey repositoryIds[0] displayName endpoint

            let registration =
                match result.Registration with
                | Some value -> value
                | None ->
                    Assert.Fail("Successful Cache enrollment did not return the persisted registration.")
                    Unchecked.defaultof<CacheRegistration>

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(result.Status, Is.EqualTo(CacheRegistrationRefreshStatus.Enrolled))
                    Assert.That(registration.CacheId, Is.Not.EqualTo(Guid.Empty))
                    Assert.That(registration.DisplayName, Is.EqualTo(request.DisplayName))
                    Assert.That(registration.BoundaryKind, Is.EqualTo(request.BoundaryKind))
                    Assert.That(registration.OwnerId, Is.EqualTo(request.OwnerId))
                    Assert.That(registration.OrganizationId, Is.EqualTo(request.OrganizationId))
                    Assert.That(registration.RepositoryScopes, Has.Length.EqualTo(request.RepositoryScopes.Count))
                    Assert.That(registration.RepositoryScopes[0], Is.EqualTo(request.RepositoryScopes[0]))
                    Assert.That(registration.PublicKey, Is.EqualTo(request.PublicKey))
                    Assert.That(registration.Endpoint, Is.EqualTo(request.Endpoint))
                    Assert.That(registration.AllowHttpEndpoint, Is.EqualTo(request.AllowHttpEndpoint))
                    Assert.That(registration.Health, Is.EqualTo(CacheHealthStatus.Unhealthy))
                    Assert.That(registration.SoftwareVersion, Is.EqualTo(request.SoftwareVersion))
                    Assert.That(registration.ProtocolVersion, Is.EqualTo(request.ProtocolVersion))
                    Assert.That(registration.PrefetchSupported, Is.EqualTo(request.PrefetchSupported)))
            )

            use typedContent = createJsonContent request
            let! requestJson = typedContent.ReadAsStringAsync()

            let rawJson =
                requestJson.TrimEnd().TrimEnd('}')
                + ",\"Health\":1}"

            use rawContent = new StringContent(rawJson, Encoding.UTF8, "application/json")
            let! rawResponse = Client.PostAsync("/cache/enroll", rawContent)
            let! rawBody = RestartDurabilityHelpers.requireOkAsync rawResponse

            let rawResult =
                deserialize<GraceReturnValue<CacheRegistrationResult>> rawBody
                |> fun value -> value.ReturnValue

            match rawResult.Registration with
            | Some rawRegistration -> Assert.That(rawRegistration.Health, Is.EqualTo(CacheHealthStatus.Unhealthy))
            | None -> Assert.Fail("Enrollment with an ignored raw Health member did not return its persisted registration.")
        }

    /// Verifies a persisted static cache identity remains ineligible before liveness refresh and rehydrates for signed refresh.
    [<Test>]
    [<Order(4)>]
    member _.StaticCacheRegistrationStartsUnhealthyAndRehydratesForSignedRefresh() =
        task {
            use privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let! selectedRepositoryId = RestartDurabilityHelpers.createRepositoryAsync "restart-cache-selected"
            let displayName = $"restart-cache-{Guid.NewGuid():N}"
            let endpoint = $"http://restart-cache-{Guid.NewGuid():N}.example.test"

            let! _, enrolled = RestartDurabilityHelpers.enrollCacheAsync privateKey selectedRepositoryId displayName endpoint
            Assert.That(enrolled.Status, Is.EqualTo(CacheRegistrationRefreshStatus.Enrolled))

            let cacheId =
                match enrolled.Registration with
                | Some registration ->
                    Assert.That(registration.PublicKey, Is.EqualTo(RestartDurabilityHelpers.cacheIdentityPublicKey privateKey))
                    registration.CacheId
                | None ->
                    Assert.Fail("Cache enrollment did not return the persisted registration.")
                    Guid.Empty

            let! initialStatus, initialBody = RestartDurabilityHelpers.requestCacheRequiredPlanAsync selectedRepositoryId
            Assert.That(initialStatus, Is.EqualTo(HttpStatusCode.ServiceUnavailable), initialBody)

            let initialError = deserialize<GraceError> initialBody
            Assert.That(initialError.Error, Does.Contain("No eligible Cache registration is currently available."))

            do! RestartDurabilityHelpers.restartGraceServerAsync ()

            let! refreshed = RestartDurabilityHelpers.refreshCacheAsync privateKey cacheId endpoint
            Assert.That(refreshed.Status, Is.EqualTo(CacheRegistrationRefreshStatus.RefreshNotDue))

            let rehydratedRegistration =
                match refreshed.Registration with
                | Some registration -> registration
                | None ->
                    Assert.Fail("Signed refresh after restart did not return the rehydrated registration.")
                    Unchecked.defaultof<CacheRegistration>

            Assert.That(rehydratedRegistration.CacheId, Is.EqualTo(cacheId))
            Assert.That(rehydratedRegistration.PublicKey, Is.EqualTo(RestartDurabilityHelpers.cacheIdentityPublicKey privateKey))
            Assert.That(rehydratedRegistration.RepositoryScopes, Has.Length.EqualTo(1))

            Assert.That(
                rehydratedRegistration.RepositoryScopes[0]
                    .RepositoryId,
                Is.EqualTo(Guid.Parse selectedRepositoryId)
            )

            let! unrelatedRepositoryId = RestartDurabilityHelpers.createRepositoryAsync "restart-cache-unrelated"
            let! unrelatedStatus, unrelatedBody = RestartDurabilityHelpers.requestCacheRequiredPlanAsync unrelatedRepositoryId

            Assert.That(unrelatedStatus, Is.EqualTo(HttpStatusCode.ServiceUnavailable), unrelatedBody)

            let unrelatedError = deserialize<GraceError> (unrelatedBody)
            Assert.That(unrelatedError.Error, Does.Contain("No eligible Cache registration is currently available."))
        }
