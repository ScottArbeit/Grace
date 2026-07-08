namespace Grace.Server

open Asp.Versioning
open Asp.Versioning.ApiExplorer
open Azure.Core
open Azure.Identity
open Azure.Monitor.OpenTelemetry.Exporter
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Giraffe
open Giraffe.EndpointRouting
open Grace.Actors
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Utilities
open Grace.Server
open Grace.Server.Middleware
open Grace.Server.ReminderService
open Grace.Server.Security
open Grace.Server.Security.TestAuth
open Grace.Shared.Converters
open Grace.Shared.Parameters
open Grace.Types.Automation
open Grace.Types.Common
open Grace.Types.Authorization
open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authorization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpLogging
open Microsoft.AspNetCore.Mvc
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Hosting.Internal
open Microsoft.Extensions.Logging
open Microsoft.OpenApi
open NodaTime
open OpenTelemetry
open OpenTelemetry.Exporter
open OpenTelemetry.Instrumentation.AspNetCore
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Orleans
open Orleans.Persistence.Cosmos
open Orleans.Serialization
open Orleans.Serialization.NodaTime
open Swashbuckle.AspNetCore.Swagger
open System
open System.Linq
open System.Reflection
open System.Text.Json
open System.Collections.Generic
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Linq
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharpPlus
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open Microsoft.IdentityModel.Tokens
open System.Security.Claims

/// Contains Grace Server application behavior and supporting helpers.
module Application =

    /// Represents cosmos warmup used by Grace Server APIs and background services.
    type CosmosWarmup(cosmosClient: CosmosClient, log: ILogger<CosmosWarmup>) =
        interface IHostedService with
            /// Starts the host service after configuration bootstrap has completed.
            member _.StartAsync(ct: CancellationToken) : Task =
                log.LogInformation("Waiting for Cosmos DB emulator to be ready...")

                /// Implements rec for the server request pipeline.
                let rec loop i =
                    task {
                        if i > 120 || ct.IsCancellationRequested then
                            log.LogError("Cosmos DB emulator was not ready in time.")
                            return ()
                        else
                            try
                                let! _ = cosmosClient.ReadAccountAsync()
                                log.LogInformation("Cosmos DB emulator ready.")
                                return ()
                            with
                            | ex ->
                                log.LogWarning("Cosmos DB emulator not ready, retry {Try}...", i)
                                do! Task.Delay(1000, ct)
                                return! loop (i + 1)
                    }

                loop 1 :> Task

            /// Stops the host service during graceful server shutdown.
            member _.StopAsync(_ct: CancellationToken) : Task = Task.CompletedTask

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    /// Represents startup used by Grace Server APIs and background services.
    type Startup(configuration: IConfiguration) =

        do
            ApplicationContext.setConfiguration configuration

            ApplicationContext.configurePubSubSettings ()
            |> ApplicationContext.setPubSubSettings

        let notLoggedIn = RequestErrors.UNAUTHORIZED "Basic" "Some Realm" "You must be logged in."

        let mustBeLoggedIn = requiresAuthentication notLoggedIn

        let graceServerVersion =
            BuildInfo
                .fromAssembly(
                    Assembly.GetExecutingAssembly()
                )
                .InformationalVersion

        /// Implements repository resource from context for the server request pipeline.
        let repositoryResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Repository(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId)
            }

        /// Implements artifact repository permission from query for the server request pipeline.
        let artifactRepositoryPermissionFromQuery (context: HttpContext) =
            task {
                let correlationId = Services.getCorrelationId context

                /// Gets try get query guid data needed by the server flow.
                let tryGetQueryGuid queryParameterName =
                    match context.Request.Query.TryGetValue queryParameterName with
                    | true, values when
                        values.Count > 0
                        && not (String.IsNullOrWhiteSpace values[0])
                        ->
                        let mutable parsed = Guid.Empty

                        if
                            Guid.TryParse(values[0], &parsed)
                            && parsed <> Guid.Empty
                        then
                            Ok parsed
                        else
                            Error(GraceError.Create $"{queryParameterName} must be a non-empty GUID." correlationId)
                    | _ -> Error(GraceError.Create $"{queryParameterName} is required." correlationId)

                match tryGetQueryGuid "ownerId", tryGetQueryGuid "organizationId", tryGetQueryGuid "repositoryId" with
                | Ok ownerId, Ok organizationId, Ok repositoryId ->
                    return Ok(Operation.RepositoryRead, Resource.Repository(ownerId, organizationId, repositoryId))
                | Error error, _, _
                | _, Error error, _
                | _, _, Error error -> return Error error
            }

        /// Implements owner resource from context for the server request pipeline.
        let ownerResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Owner graceIds.OwnerId
            }

        /// Implements organization resource from context for the server request pipeline.
        let organizationResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Organization(graceIds.OwnerId, graceIds.OrganizationId)
            }

        /// Implements branch resource from context for the server request pipeline.
        let branchResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Branch(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, graceIds.BranchId)
            }

        /// Implements upload path resources from context for the server request pipeline.
        let uploadPathResourcesFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetUploadMetadataForFilesParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context

                return StorageAuthorizationResources.uploadMetadataResources graceIds.OwnerId graceIds.OrganizationId graceIds.RepositoryId parameters
            }

        /// Builds authorization resources for issuing upload URIs.
        let uploadUriResourcesFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetUploadUriParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context

                return StorageAuthorizationResources.uploadUriResources graceIds.OwnerId graceIds.OrganizationId graceIds.RepositoryId parameters
            }

        /// Implements download path resource from context for the server request pipeline.
        let downloadPathResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                use! document = JsonDocument.ParseAsync(context.Request.Body, cancellationToken = context.RequestAborted)

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context
                let mutable relativePath = String.Empty

                if document.RootElement.ValueKind = JsonValueKind.Object then
                    let mutable property = Unchecked.defaultof<JsonElement>

                    if
                        document.RootElement.TryGetProperty("RelativePath", &property)
                        && property.ValueKind = JsonValueKind.String
                    then
                        relativePath <- property.GetString()

                let parameters = Storage.GetDownloadUriParameters(RelativePath = relativePath)

                return StorageAuthorizationResources.downloadUriResource graceIds.OwnerId graceIds.OrganizationId graceIds.RepositoryId parameters
            }

        /// Implements annotate path resource from context for the server request pipeline.
        let annotatePathResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Branch.AnnotateParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let correlationId = Services.getCorrelationId context

                if Object.ReferenceEquals(parameters, null) then
                    return Error(GraceError.Create "Annotate parameters must not be null." correlationId)
                elif String.IsNullOrWhiteSpace parameters.Path then
                    return Error(GraceError.Create "Annotation Path must be a relative file path." correlationId)
                else
                    let graceIds = Services.getGraceIds context
                    let normalizedPath = normalizeFilePath parameters.Path
                    return Ok(Operation.PathRead, Resource.Path(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, normalizedPath))
            }

        /// Implements invalid content block address error for the server request pipeline.
        let invalidContentBlockAddressError correlationId =
            GraceError.Create "ContentBlockAddress must be a 64-character hexadecimal BLAKE3 value." correlationId

        /// Validates validate content block address for resource inputs before server processing continues.
        let validateContentBlockAddressForResource correlationId contentBlockAddress =
            match ContentAddress.tryNormalizeBlake3Address contentBlockAddress with
            | Some normalizedAddress -> Ok normalizedAddress
            | None -> Error(invalidContentBlockAddressError correlationId)

        /// Implements content block upload path resource from context for the server request pipeline.
        let contentBlockUploadPathResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetContentBlockUploadUriParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let correlationId = Services.getCorrelationId context
                let graceIds = Services.getGraceIds context

                match validateContentBlockAddressForResource correlationId parameters.ContentBlockAddress with
                | Error error -> return Error error
                | Ok contentBlockAddress ->
                    parameters.ContentBlockAddress <- contentBlockAddress

                    return
                        Ok(
                            Operation.PathWrite,
                            StorageAuthorizationResources.contentBlockUploadResource graceIds.OwnerId graceIds.OrganizationId graceIds.RepositoryId parameters
                        )
            }

        /// Implements content block download path resource from context for the server request pipeline.
        let contentBlockDownloadPathResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetContentBlockDownloadUriParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let correlationId = Services.getCorrelationId context
                let graceIds = Services.getGraceIds context

                match validateContentBlockAddressForResource correlationId parameters.ContentBlockAddress with
                | Error error -> return Error error
                | Ok contentBlockAddress ->
                    parameters.ContentBlockAddress <- contentBlockAddress

                    match
                        StorageAuthorizationResources.tryContentBlockDownloadResource
                            correlationId
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters
                        with
                    | Error error -> return Error error
                    | Ok resource -> return Ok(Operation.PathRead, resource)
            }

        /// Implements upload session path resource from context for the server request pipeline.
        let uploadSessionPathResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.UploadSessionStorageParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context

                return StorageAuthorizationResources.uploadSessionResource graceIds.OwnerId graceIds.OrganizationId graceIds.RepositoryId parameters
            }

        /// Handles the Grace Server compose handlers request.
        let composeHandlers (first: HttpHandler) (second: HttpHandler) : HttpHandler = fun next context -> first (second next) context

        /// Handles the Grace Server require system admin request.
        let requireSystemAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.SystemAdmin (fun _ -> task { return Resource.System })

        /// Handles the Grace Server require system operate or admin request.
        let requireSystemOperateOrAdmin: HttpHandler =
            AuthorizationMiddleware.requiresAnyPermission
                [
                    Operation.SystemAdmin
                    Operation.SystemOperate
                ]
                (fun _ -> task { return Resource.System })

        /// Handles the Grace Server require owner admin request.
        let requireOwnerAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OwnerAdmin ownerResourceFromContext

        /// Handles the Grace Server require owner write or admin request.
        let requireOwnerWriteOrAdmin: HttpHandler =
            AuthorizationMiddleware.requiresAnyPermission
                [
                    Operation.OwnerAdmin
                    Operation.OwnerWrite
                ]
                ownerResourceFromContext

        /// Handles the Grace Server require owner read request.
        let requireOwnerRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OwnerRead ownerResourceFromContext

        /// Handles the Grace Server require organization admin request.
        let requireOrganizationAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OrganizationAdmin organizationResourceFromContext

        /// Handles the Grace Server require organization write or admin request.
        let requireOrganizationWriteOrAdmin: HttpHandler =
            AuthorizationMiddleware.requiresAnyPermission
                [
                    Operation.OrganizationAdmin
                    Operation.OrganizationWrite
                ]
                organizationResourceFromContext

        /// Handles the Grace Server require organization read request.
        let requireOrganizationRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OrganizationRead organizationResourceFromContext

        /// Handles the Grace Server require repository admin request.
        let requireRepositoryAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.RepositoryAdmin repositoryResourceFromContext

        /// Handles the Grace Server require repository write or admin request.
        let requireRepositoryWriteOrAdmin: HttpHandler =
            AuthorizationMiddleware.requiresAnyPermission
                [
                    Operation.RepositoryAdmin
                    Operation.RepositoryWrite
                ]
                repositoryResourceFromContext

        /// Handles the Grace Server require repository read request.
        let requireRepositoryRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.RepositoryRead repositoryResourceFromContext

        /// Handles the Grace Server require repository write request.
        let requireRepositoryWrite: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.RepositoryWrite repositoryResourceFromContext

        /// Handles the Grace Server require artifact repository read request.
        let requireArtifactRepositoryRead: HttpHandler = AuthorizationMiddleware.requiresPermissionResolved artifactRepositoryPermissionFromQuery

        /// Handles the Grace Server require branch admin request.
        let requireBranchAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.BranchAdmin branchResourceFromContext

        /// Handles the Grace Server require branch write or admin request.
        let requireBranchWriteOrAdmin: HttpHandler =
            AuthorizationMiddleware.requiresAnyPermission
                [
                    Operation.BranchAdmin
                    Operation.BranchWrite
                ]
                branchResourceFromContext

        /// Handles the Grace Server require branch read request.
        let requireBranchRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.BranchRead branchResourceFromContext

        /// Handles the Grace Server require branch write request.
        let requireBranchWrite: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.BranchWrite branchResourceFromContext

        /// Implements approval policy resource from context for the server request pipeline.
        let approvalPolicyResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Approval.ApprovalPolicyParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let scope = ApprovalCommon.scopeFromPolicyParameters parameters
                return ApprovalCommon.resourceFromApprovalScope scope
            }

        /// Implements approval request list resource from context for the server request pipeline.
        let approvalRequestListResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Approval.ApprovalRequestParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let scope = ApprovalCommon.scopeFromRequestParameters parameters
                return ApprovalCommon.resourceFromApprovalScope scope
            }

        /// Implements webhook rule resource from context for the server request pipeline.
        let webhookRuleResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Webhook.WebhookRuleParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let scope = WebhookCommon.scopeFromRuleParameters parameters
                return WebhookCommon.resourceFromWebhookScope scope
            }

        /// Implements webhook delivery list resource from context for the server request pipeline.
        let webhookDeliveryListResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Webhook.WebhookDeliveryParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let scope = WebhookCommon.scopeFromDeliveryParameters parameters
                return WebhookCommon.resourceFromWebhookScope scope
            }

        /// Handles the Grace Server require approval policy manage request.
        let requireApprovalPolicyManage: HttpHandler =
            AuthorizationMiddleware.requiresPermission Operation.ApprovalPolicyManage approvalPolicyResourceFromContext

        /// Handles the Grace Server require approval policy show request.
        let requireApprovalPolicyShow: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalPolicy.resolveStoredPolicyForManage<Approval.ShowApprovalPolicyParameters>

        /// Handles the Grace Server require approval policy update request.
        let requireApprovalPolicyUpdate: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalPolicy.resolveStoredPolicyForManage<Approval.UpdateApprovalPolicyParameters>

        /// Handles the Grace Server require approval policy enable request.
        let requireApprovalPolicyEnable: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalPolicy.resolveStoredPolicyForManage<Approval.EnableApprovalPolicyParameters>

        /// Handles the Grace Server require approval policy disable request.
        let requireApprovalPolicyDisable: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalPolicy.resolveStoredPolicyForManage<Approval.DisableApprovalPolicyParameters>

        /// Handles the Grace Server require approval policy delete request.
        let requireApprovalPolicyDelete: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalPolicy.resolveStoredPolicyForManage<Approval.DeleteApprovalPolicyParameters>

        /// Handles the Grace Server require approval request read request.
        let requireApprovalRequestRead: HttpHandler =
            AuthorizationMiddleware.requiresPermission Operation.ApprovalRequestRead approvalRequestListResourceFromContext

        /// Handles the Grace Server require approval request show request.
        let requireApprovalRequestShow: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalRequest.resolveStoredRequestForRead<Approval.ShowApprovalRequestParameters>

        /// Handles the Grace Server require approval request history request.
        let requireApprovalRequestHistory: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalRequest.resolveStoredRequestForRead<Approval.ApprovalRequestHistoryParameters>

        /// Handles the Grace Server require approval request approve request.
        let requireApprovalRequestApprove: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalRequest.resolveStoredRequestForRespond<Approval.ApproveApprovalRequestParameters>

        /// Handles the Grace Server require approval request reject request.
        let requireApprovalRequestReject: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved ApprovalRequest.resolveStoredRequestForRespond<Approval.RejectApprovalRequestParameters>

        /// Handles the Grace Server require webhook rule manage request.
        let requireWebhookRuleManage: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.WebhookManage webhookRuleResourceFromContext

        /// Handles the Grace Server require webhook rule show request.
        let requireWebhookRuleShow: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved WebhookRule.resolveStoredRuleForManage<Webhook.ShowWebhookRuleParameters>

        /// Handles the Grace Server require webhook rule update request.
        let requireWebhookRuleUpdate: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved WebhookRule.resolveStoredRuleForManage<Webhook.UpdateWebhookRuleParameters>

        /// Handles the Grace Server require webhook rule enable request.
        let requireWebhookRuleEnable: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved WebhookRule.resolveStoredRuleForManage<Webhook.EnableWebhookRuleParameters>

        /// Handles the Grace Server require webhook rule disable request.
        let requireWebhookRuleDisable: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved WebhookRule.resolveStoredRuleForManage<Webhook.DisableWebhookRuleParameters>

        /// Handles the Grace Server require webhook rule delete request.
        let requireWebhookRuleDelete: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved WebhookRule.resolveStoredRuleForManage<Webhook.DeleteWebhookRuleParameters>

        /// Handles the Grace Server require webhook rule test request.
        let requireWebhookRuleTest: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved WebhookRule.resolveStoredRuleForManage<Webhook.TestWebhookRuleParameters>

        /// Handles the Grace Server require webhook delivery read request.
        let requireWebhookDeliveryRead: HttpHandler =
            AuthorizationMiddleware.requiresPermission Operation.WebhookDeliveryRead webhookDeliveryListResourceFromContext

        /// Handles the Grace Server require webhook delivery show request.
        let requireWebhookDeliveryShow: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved WebhookDelivery.resolveStoredDeliveryForRead<Webhook.ShowWebhookDeliveryParameters>

        /// Handles the Grace Server require path write request.
        let requirePathWrite: HttpHandler = AuthorizationMiddleware.requiresPermissions Operation.PathWrite uploadPathResourcesFromContext

        /// Handles the Grace Server require path write for upload uri request.
        let requirePathWriteForUploadUri: HttpHandler = AuthorizationMiddleware.requiresPermissions Operation.PathWrite uploadUriResourcesFromContext

        /// Handles the Grace Server require path read request.
        let requirePathRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.PathRead downloadPathResourceFromContext

        /// Handles the Grace Server require annotate path read request.
        let requireAnnotatePathRead: HttpHandler = AuthorizationMiddleware.requiresPermissionResolved annotatePathResourceFromContext

        /// Handles the Grace Server require path write for content block upload uri request.
        let requirePathWriteForContentBlockUploadUri: HttpHandler = AuthorizationMiddleware.requiresPermissionResolved contentBlockUploadPathResourceFromContext

        /// Handles the Grace Server require path read for content block download uri request.
        let requirePathReadForContentBlockDownloadUri: HttpHandler =
            AuthorizationMiddleware.requiresPermissionResolved contentBlockDownloadPathResourceFromContext

        /// Handles the Grace Server require path write for upload session request.
        let requirePathWriteForUploadSession: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.PathWrite uploadSessionPathResourceFromContext

        let activeAgentSessionsByAgentKey = ConcurrentDictionary<string, AgentSessionInfo>()
        let activeAgentSessionsBySessionId = ConcurrentDictionary<string, string>()
        let agentSessionOperationCache = ConcurrentDictionary<string, AgentSessionOperationResult>()
        let bootstrappedAgentKeys = ConcurrentDictionary<string, byte>()

        /// Parses try parse guid input into the server model.
        let tryParseGuid (value: string) =
            let mutable parsed = Guid.Empty

            if String.IsNullOrWhiteSpace value |> not
               && Guid.TryParse(value, &parsed)
               && parsed <> Guid.Empty then
                Some parsed
            else
                Option.None

        /// Resolves resolve repository id data from request or repository state.
        let resolveRepositoryId (graceIds: GraceIds) (repositoryIdFromParameters: string) =
            if graceIds.RepositoryId <> RepositoryId.Empty then
                graceIds.RepositoryId
            else
                repositoryIdFromParameters
                |> tryParseGuid
                |> Option.defaultValue RepositoryId.Empty

        /// Normalizes normalize operation id data for stable server comparisons.
        let normalizeOperationId (operationId: string) (operationName: string) (correlationId: CorrelationId) =
            if String.IsNullOrWhiteSpace operationId then
                $"{operationName}-{correlationId}"
            else
                operationId.Trim()

        /// Normalizes normalize session source data for stable server comparisons.
        let normalizeSessionSource (source: string) = if String.IsNullOrWhiteSpace source then "cli" else source.Trim()

        /// Converts server authentication data into agent key.
        let toAgentKey (repositoryId: RepositoryId) (agentId: string) =
            if repositoryId = RepositoryId.Empty
               || String.IsNullOrWhiteSpace agentId then
                String.Empty
            else
                $"{repositoryId:N}:{agentId.Trim().ToLowerInvariant()}"

        /// Normalizes normalize replay identity data for stable server comparisons.
        let normalizeReplayIdentity (identity: string) =
            if String.IsNullOrWhiteSpace identity then
                "unknown"
            else
                identity.Trim().ToLowerInvariant()

        /// Converts server authentication data into replay key.
        let toReplayKey (repositoryId: RepositoryId) (identity: string) (operationId: string) =
            $"{repositoryId:N}|{normalizeReplayIdentity identity}|{normalizeReplayIdentity operationId}"

        /// Writes agent session parameters from grace ids onto the current response or server state.
        let setAgentSessionParametersFromGraceIds (graceIds: GraceIds) (parameters: Common.AgentSessionParameters) =
            if graceIds.OwnerId <> OwnerId.Empty then
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OwnerName <- $"{graceIds.OwnerName}"

            if graceIds.OrganizationId <> OrganizationId.Empty then
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.OrganizationName <- $"{graceIds.OrganizationName}"

            if graceIds.RepositoryId <> RepositoryId.Empty then
                parameters.RepositoryId <- graceIds.RepositoryIdString
                parameters.RepositoryName <- $"{graceIds.RepositoryName}"

        /// Captures the persisted agent-session result, including operation id and idempotent replay status.
        let createOperationResult (session: AgentSessionInfo) (message: string) (operationId: string) (wasReplay: bool) =
            { AgentSessionOperationResult.Default with Session = session; Message = message; OperationId = operationId; WasIdempotentReplay = wasReplay }

        /// Adds repository and request-supplied scope identifiers to the event metadata for an agent-session command.
        let buildAgentSessionMetadata
            (context: HttpContext)
            (repositoryId: RepositoryId)
            (parameters: Common.AgentSessionParameters)
            (session: AgentSessionInfo)
            =
            let metadata = Services.createMetadata context

            if repositoryId <> RepositoryId.Empty then
                metadata.Properties[ nameof RepositoryId ] <- $"{repositoryId}"

            if String.IsNullOrWhiteSpace parameters.OwnerId
               |> not then
                metadata.Properties[ nameof OwnerId ] <- parameters.OwnerId

            if String.IsNullOrWhiteSpace parameters.OrganizationId
               |> not then
                metadata.Properties[ nameof OrganizationId ] <- parameters.OrganizationId

            if String.IsNullOrWhiteSpace session.AgentId |> not then
                metadata.Properties[ "ActorId" ] <- session.AgentId

            if String.IsNullOrWhiteSpace session.SessionId |> not then
                metadata.Properties[ "SessionId" ] <- session.SessionId

            metadata

        /// Implements emit agent session event for the server request pipeline.
        let emitAgentSessionEvent
            (context: HttpContext)
            (eventType: AutomationEventType)
            (metadata: EventMetadata)
            (operationResult: AgentSessionOperationResult)
            =
            task {
                match EventingPublisher.tryCreateAgentSessionEnvelope eventType metadata operationResult with
                | Some envelope -> do! Notification.routeAutomationEvent context.RequestServices envelope
                | None -> ()
            }

        /// Gets try get active session by agent key data needed by the server flow.
        let tryGetActiveSessionByAgentKey (agentKey: string) =
            match activeAgentSessionsByAgentKey.TryGetValue(agentKey) with
            | true, session when session.LifecycleState = AgentSessionLifecycleState.Active -> Some(agentKey, session)
            | _ -> Option.None

        /// Attempts to remove session by agent key and returns an option or result instead of throwing.
        let tryRemoveSessionByAgentKey (agentKey: string) =
            let mutable removedSession = AgentSessionInfo.Default

            activeAgentSessionsByAgentKey.TryRemove(agentKey, &removedSession)
            |> ignore

        /// Attempts to remove session lookup and returns an option or result instead of throwing.
        let tryRemoveSessionLookup (sessionId: string) =
            let mutable removedAgentKey = String.Empty

            activeAgentSessionsBySessionId.TryRemove(sessionId, &removedAgentKey)
            |> ignore

        /// Resolves try resolve active session data from request or repository state.
        let tryResolveActiveSession (repositoryId: RepositoryId) (agentId: string) (sessionId: string) (workItemIdOrNumber: string) =
            let repositoryPrefix = $"{repositoryId:N}:"

            if String.IsNullOrWhiteSpace sessionId |> not then
                match activeAgentSessionsBySessionId.TryGetValue(sessionId) with
                | true, agentKey when agentKey.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase) -> tryGetActiveSessionByAgentKey agentKey
                | _ -> Option.None
            elif String.IsNullOrWhiteSpace agentId |> not then
                let agentKey = toAgentKey repositoryId agentId
                tryGetActiveSessionByAgentKey agentKey
            elif String.IsNullOrWhiteSpace workItemIdOrNumber
                 |> not then
                activeAgentSessionsByAgentKey
                |> Seq.tryPick (fun keyValue ->
                    if
                        keyValue.Key.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase)
                        && keyValue.Value.LifecycleState = AgentSessionLifecycleState.Active
                        && keyValue.Value.WorkItemIdOrNumber.Equals(workItemIdOrNumber, StringComparison.OrdinalIgnoreCase)
                    then
                        Some(keyValue.Key, keyValue.Value)
                    else
                        Option.None)
            else
                Option.None

        /// Handles the Grace Server start agent session request.
        let startAgentSession: HttpHandler =
            fun (_next: HttpFunc) (context: HttpContext) ->
                task {
                    let graceIds = Services.getGraceIds context
                    let correlationId = Services.getCorrelationId context

                    let! parameters =
                        context
                        |> Services.parse<Common.StartAgentSessionParameters>

                    setAgentSessionParametersFromGraceIds graceIds (parameters :> Common.AgentSessionParameters)

                    if String.IsNullOrWhiteSpace parameters.AgentId then
                        return!
                            context
                            |> Services.result400BadRequest (GraceError.Create "AgentId is required to start an agent session." correlationId)
                    elif String.IsNullOrWhiteSpace parameters.WorkItemIdOrNumber then
                        return!
                            context
                            |> Services.result400BadRequest (GraceError.Create "WorkItemIdOrNumber is required to start an agent session." correlationId)
                    else
                        let repositoryId = resolveRepositoryId graceIds parameters.RepositoryId

                        if repositoryId = RepositoryId.Empty then
                            return!
                                context
                                |> Services.result400BadRequest (GraceError.Create "RepositoryId is required to start an agent session." correlationId)
                        else
                            let operationId = normalizeOperationId parameters.OperationId "start" correlationId
                            let replayKey = toReplayKey repositoryId parameters.AgentId operationId

                            match agentSessionOperationCache.TryGetValue(replayKey) with
                            | true, replayResult ->
                                let replay = { replayResult with WasIdempotentReplay = true }

                                return!
                                    context
                                    |> Services.result200Ok (GraceReturnValue.Create replay correlationId)
                            | _ ->
                                let agentKey = toAgentKey repositoryId parameters.AgentId
                                let source = normalizeSessionSource parameters.Source

                                match tryGetActiveSessionByAgentKey agentKey with
                                | Some (_, existingSession) ->
                                    if existingSession.WorkItemIdOrNumber.Equals(parameters.WorkItemIdOrNumber, StringComparison.OrdinalIgnoreCase) then
                                        let replayResult =
                                            createOperationResult existingSession "Work session is already active for this work item." operationId true

                                        agentSessionOperationCache[replayKey] <- replayResult

                                        return!
                                            context
                                            |> Services.result200Ok (GraceReturnValue.Create replayResult correlationId)
                                    else
                                        let graceError =
                                            GraceError.Create
                                                ($"An active session already exists for work item '{existingSession.WorkItemIdOrNumber}'. Stop it before starting another.")
                                                correlationId

                                        return! context |> Services.result400BadRequest graceError
                                | None ->
                                    let now = getCurrentInstant ()

                                    let newSession =
                                        { AgentSessionInfo.Default with
                                            SessionId =
                                                (if String.IsNullOrWhiteSpace parameters.OperationId then
                                                     Guid.NewGuid().ToString("N")
                                                 else
                                                     parameters.OperationId.Trim())
                                            AgentId = parameters.AgentId
                                            AgentDisplayName = parameters.AgentDisplayName
                                            WorkItemIdOrNumber = parameters.WorkItemIdOrNumber
                                            PromotionSetId = parameters.PromotionSetId
                                            Source = source
                                            LifecycleState = AgentSessionLifecycleState.Active
                                            StartedAt = Some now
                                            LastUpdatedAt = Some now
                                        }

                                    activeAgentSessionsByAgentKey[agentKey] <- newSession
                                    activeAgentSessionsBySessionId[newSession.SessionId] <- agentKey

                                    let operationResult = createOperationResult newSession "Agent work session started." operationId false

                                    agentSessionOperationCache[replayKey] <- operationResult

                                    let metadata = buildAgentSessionMetadata context repositoryId (parameters :> Common.AgentSessionParameters) newSession

                                    do! emitAgentSessionEvent context AutomationEventType.AgentWorkStarted metadata operationResult

                                    if bootstrappedAgentKeys.TryAdd(agentKey, 0uy) then
                                        do! emitAgentSessionEvent context AutomationEventType.AgentBootstrapped metadata operationResult

                                    return!
                                        context
                                        |> Services.result200Ok (GraceReturnValue.Create operationResult correlationId)
                }

        /// Handles the Grace Server stop agent session request.
        let stopAgentSession: HttpHandler =
            fun (_next: HttpFunc) (context: HttpContext) ->
                task {
                    let graceIds = Services.getGraceIds context
                    let correlationId = Services.getCorrelationId context

                    let! parameters =
                        context
                        |> Services.parse<Common.StopAgentSessionParameters>

                    setAgentSessionParametersFromGraceIds graceIds (parameters :> Common.AgentSessionParameters)

                    let repositoryId = resolveRepositoryId graceIds parameters.RepositoryId

                    if repositoryId = RepositoryId.Empty then
                        return!
                            context
                            |> Services.result400BadRequest (GraceError.Create "RepositoryId is required to stop an agent session." correlationId)
                    else
                        let operationId = normalizeOperationId parameters.OperationId "stop" correlationId

                        let replayIdentity =
                            if String.IsNullOrWhiteSpace parameters.AgentId
                               |> not then
                                parameters.AgentId
                            elif String.IsNullOrWhiteSpace parameters.SessionId
                                 |> not then
                                parameters.SessionId
                            elif String.IsNullOrWhiteSpace parameters.WorkItemIdOrNumber
                                 |> not then
                                parameters.WorkItemIdOrNumber
                            else
                                "unknown"

                        let replayKey = toReplayKey repositoryId replayIdentity operationId

                        match agentSessionOperationCache.TryGetValue(replayKey) with
                        | true, replayResult ->
                            let replay = { replayResult with WasIdempotentReplay = true }

                            return!
                                context
                                |> Services.result200Ok (GraceReturnValue.Create replay correlationId)
                        | _ ->
                            match tryResolveActiveSession repositoryId parameters.AgentId parameters.SessionId parameters.WorkItemIdOrNumber with
                            | Some (agentKey, activeSession) ->
                                if String.IsNullOrWhiteSpace parameters.WorkItemIdOrNumber
                                   |> not
                                   && not
                                      <| activeSession.WorkItemIdOrNumber.Equals(parameters.WorkItemIdOrNumber, StringComparison.OrdinalIgnoreCase) then
                                    return!
                                        context
                                        |> Services.result400BadRequest (
                                            GraceError.Create
                                                ($"Active session targets work item '{activeSession.WorkItemIdOrNumber}', but stop requested '{parameters.WorkItemIdOrNumber}'.")
                                                correlationId
                                        )
                                else
                                    let now = getCurrentInstant ()

                                    let stoppedSession =
                                        { activeSession with
                                            LifecycleState = AgentSessionLifecycleState.Stopped
                                            LastUpdatedAt = Some now
                                            StoppedAt = Some now
                                        }

                                    tryRemoveSessionByAgentKey agentKey
                                    tryRemoveSessionLookup activeSession.SessionId

                                    let stopMessage =
                                        if String.IsNullOrWhiteSpace parameters.StopReason then
                                            "Agent work session stopped."
                                        else
                                            $"Agent work session stopped: {parameters.StopReason}"

                                    let operationResult = createOperationResult stoppedSession stopMessage operationId false

                                    agentSessionOperationCache[replayKey] <- operationResult

                                    let metadata = buildAgentSessionMetadata context repositoryId (parameters :> Common.AgentSessionParameters) stoppedSession

                                    do! emitAgentSessionEvent context AutomationEventType.AgentWorkStopped metadata operationResult

                                    return!
                                        context
                                        |> Services.result200Ok (GraceReturnValue.Create operationResult correlationId)
                            | None ->
                                let now = getCurrentInstant ()

                                let inactiveSession =
                                    { AgentSessionInfo.Default with
                                        SessionId = parameters.SessionId
                                        AgentId = parameters.AgentId
                                        AgentDisplayName = parameters.AgentDisplayName
                                        WorkItemIdOrNumber = parameters.WorkItemIdOrNumber
                                        Source = "cli"
                                        LifecycleState = AgentSessionLifecycleState.Inactive
                                        LastUpdatedAt = Some now
                                        StoppedAt = Some now
                                    }

                                let operationResult =
                                    createOperationResult inactiveSession "No active session matched this request. Nothing to stop." operationId true

                                agentSessionOperationCache[replayKey] <- operationResult

                                return!
                                    context
                                    |> Services.result200Ok (GraceReturnValue.Create operationResult correlationId)
                }

        /// Handles the Grace Server get agent session status request.
        let getAgentSessionStatus: HttpHandler =
            fun (_next: HttpFunc) (context: HttpContext) ->
                task {
                    let graceIds = Services.getGraceIds context
                    let correlationId = Services.getCorrelationId context

                    let! parameters =
                        context
                        |> Services.parse<Common.GetAgentSessionStatusParameters>

                    setAgentSessionParametersFromGraceIds graceIds (parameters :> Common.AgentSessionParameters)

                    let repositoryId = resolveRepositoryId graceIds parameters.RepositoryId

                    if repositoryId = RepositoryId.Empty then
                        return!
                            context
                            |> Services.result400BadRequest (GraceError.Create "RepositoryId is required to get agent session status." correlationId)
                    else
                        let now = getCurrentInstant ()

                        let result =
                            match tryResolveActiveSession repositoryId parameters.AgentId parameters.SessionId parameters.WorkItemIdOrNumber with
                            | Some (_, activeSession) ->
                                createOperationResult
                                    activeSession
                                    "Active agent session found."
                                    (normalizeOperationId String.Empty "status" correlationId)
                                    false
                            | None ->
                                let inactiveSession =
                                    { AgentSessionInfo.Default with
                                        SessionId = parameters.SessionId
                                        AgentId = parameters.AgentId
                                        AgentDisplayName = parameters.AgentDisplayName
                                        WorkItemIdOrNumber = parameters.WorkItemIdOrNumber
                                        LifecycleState = AgentSessionLifecycleState.Inactive
                                        LastUpdatedAt = Some now
                                    }

                                createOperationResult
                                    inactiveSession
                                    "No active agent session matched this request."
                                    (normalizeOperationId String.Empty "status" correlationId)
                                    false

                        return!
                            context
                            |> Services.result200Ok (GraceReturnValue.Create result correlationId)
                }

        /// Handles the Grace Server get active agent session request.
        let getActiveAgentSession: HttpHandler =
            fun (_next: HttpFunc) (context: HttpContext) ->
                task {
                    let graceIds = Services.getGraceIds context
                    let correlationId = Services.getCorrelationId context

                    let! parameters =
                        context
                        |> Services.parse<Common.GetActiveAgentSessionParameters>

                    setAgentSessionParametersFromGraceIds graceIds (parameters :> Common.AgentSessionParameters)

                    let repositoryId = resolveRepositoryId graceIds parameters.RepositoryId

                    if repositoryId = RepositoryId.Empty then
                        return!
                            context
                            |> Services.result400BadRequest (GraceError.Create "RepositoryId is required to get the active agent session." correlationId)
                    else
                        let now = getCurrentInstant ()

                        let result =
                            match tryResolveActiveSession repositoryId parameters.AgentId String.Empty parameters.WorkItemIdOrNumber with
                            | Some (_, activeSession) ->
                                createOperationResult
                                    activeSession
                                    "Active agent session found."
                                    (normalizeOperationId String.Empty "active" correlationId)
                                    false
                            | None ->
                                let inactiveSession =
                                    { AgentSessionInfo.Default with
                                        AgentId = parameters.AgentId
                                        AgentDisplayName = parameters.AgentDisplayName
                                        WorkItemIdOrNumber = parameters.WorkItemIdOrNumber
                                        LifecycleState = AgentSessionLifecycleState.Inactive
                                        LastUpdatedAt = Some now
                                    }

                                createOperationResult
                                    inactiveSession
                                    "No active agent session matched this request."
                                    (normalizeOperationId String.Empty "active" correlationId)
                                    false

                        return!
                            context
                            |> Services.result200Ok (GraceReturnValue.Create result correlationId)
                }

        /// Handles the Grace Server list active agent sessions request.
        let listActiveAgentSessions: HttpHandler =
            fun (_next: HttpFunc) (context: HttpContext) ->
                task {
                    let graceIds = Services.getGraceIds context
                    let correlationId = Services.getCorrelationId context

                    let! parameters =
                        context
                        |> Services.parse<Common.ListActiveAgentSessionsParameters>

                    setAgentSessionParametersFromGraceIds graceIds (parameters :> Common.AgentSessionParameters)

                    let repositoryId = resolveRepositoryId graceIds parameters.RepositoryId

                    if repositoryId = RepositoryId.Empty then
                        return!
                            context
                            |> Services.result400BadRequest (GraceError.Create "RepositoryId is required to list active agent sessions." correlationId)
                    else
                        let repositoryPrefix = $"{repositoryId:N}:"

                        let maximumSessionCount =
                            if parameters.MaximumSessionCount <= 0 then
                                25
                            else
                                parameters.MaximumSessionCount

                        let sessions =
                            activeAgentSessionsByAgentKey
                            |> Seq.filter (fun keyValue ->
                                keyValue.Key.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase)
                                && keyValue.Value.LifecycleState = AgentSessionLifecycleState.Active)
                            |> Seq.map (fun keyValue -> keyValue.Value)
                            |> Seq.sortByDescending (fun session ->
                                match session.LastUpdatedAt with
                                | Some instant -> instant.ToUnixTimeTicks()
                                | None -> Int64.MinValue)
                            |> Seq.truncate maximumSessionCount
                            |> Seq.toList

                        let result =
                            { AgentSessionListResult.Default with
                                Sessions = sessions
                                Count = sessions.Length
                                Message = $"Found {sessions.Length} active agent session(s)."
                            }

                        return!
                            context
                            |> Services.result200Ok (GraceReturnValue.Create result correlationId)
                }

        let endpoints =
            [
                GET [ route
                          "/"
                          (warbler (fun _ ->
                              htmlString
                                  $"<h1>Hello From Grace Server {graceServerVersion}!</h1><br/><p>The current server time is: {getCurrentInstantExtended ()}.</p>"))
                      |> addMetadata (AllowAnonymousAttribute())
                      route
                          "/healthz"
                          (warbler (fun _ ->
                              htmlString $"<h1>Grace server seems healthy!</h1><br/><p>The current server time is: {getCurrentInstantExtended ()}.</p>"))
                      |> addMetadata (AllowAnonymousAttribute()) ]
                PUT []
                subRoute
                    "/approval/policy"
                    [
                        POST [ route "/create" (composeHandlers requireApprovalPolicyManage ApprovalPolicy.Create)
                               |> addMetadata typeof<Approval.CreateApprovalPolicyParameters>

                               route "/list" (composeHandlers requireApprovalPolicyManage ApprovalPolicy.List)
                               |> addMetadata typeof<Approval.ListApprovalPoliciesParameters>

                               route "/show" (composeHandlers requireApprovalPolicyShow ApprovalPolicy.Show)
                               |> addMetadata typeof<Approval.ShowApprovalPolicyParameters>

                               route "/update" (composeHandlers requireApprovalPolicyUpdate ApprovalPolicy.Update)
                               |> addMetadata typeof<Approval.UpdateApprovalPolicyParameters>

                               route "/enable" (composeHandlers requireApprovalPolicyEnable ApprovalPolicy.Enable)
                               |> addMetadata typeof<Approval.EnableApprovalPolicyParameters>

                               route "/disable" (composeHandlers requireApprovalPolicyDisable ApprovalPolicy.Disable)
                               |> addMetadata typeof<Approval.DisableApprovalPolicyParameters>

                               route "/delete" (composeHandlers requireApprovalPolicyDelete ApprovalPolicy.Delete)
                               |> addMetadata typeof<Approval.DeleteApprovalPolicyParameters>

                               route "/evaluate" (composeHandlers requireApprovalPolicyManage ApprovalPolicy.Evaluate)
                               |> addMetadata typeof<Approval.EvaluateApprovalPolicyParameters> ]
                    ]
                subRoute
                    "/approval/request"
                    [
                        POST [ route "/list" (composeHandlers requireApprovalRequestRead ApprovalRequest.List)
                               |> addMetadata typeof<Approval.ListApprovalRequestsParameters>

                               route "/_seedGenerated" ApprovalRequest.SeedGenerated
                               |> addMetadata typeof<Approval.SeedGeneratedApprovalRequestParameters>

                               route "/show" (composeHandlers requireApprovalRequestShow ApprovalRequest.Show)
                               |> addMetadata typeof<Approval.ShowApprovalRequestParameters>

                               route "/approve" (composeHandlers requireApprovalRequestApprove ApprovalRequest.Approve)
                               |> addMetadata typeof<Approval.ApproveApprovalRequestParameters>

                               route "/reject" (composeHandlers requireApprovalRequestReject ApprovalRequest.Reject)
                               |> addMetadata typeof<Approval.RejectApprovalRequestParameters>

                               route "/history" (composeHandlers requireApprovalRequestHistory ApprovalRequest.History)
                               |> addMetadata typeof<Approval.ApprovalRequestHistoryParameters> ]
                    ]
                subRoute
                    "/webhook/rule"
                    [
                        POST [ route "/create" (composeHandlers requireWebhookRuleManage WebhookRule.Create)
                               |> addMetadata typeof<Webhook.CreateWebhookRuleParameters>

                               route "/list" (composeHandlers requireWebhookRuleManage WebhookRule.List)
                               |> addMetadata typeof<Webhook.ListWebhookRulesParameters>

                               route "/show" (composeHandlers requireWebhookRuleShow WebhookRule.Show)
                               |> addMetadata typeof<Webhook.ShowWebhookRuleParameters>

                               route "/update" (composeHandlers requireWebhookRuleUpdate WebhookRule.Update)
                               |> addMetadata typeof<Webhook.UpdateWebhookRuleParameters>

                               route "/enable" (composeHandlers requireWebhookRuleEnable WebhookRule.Enable)
                               |> addMetadata typeof<Webhook.EnableWebhookRuleParameters>

                               route "/disable" (composeHandlers requireWebhookRuleDisable WebhookRule.Disable)
                               |> addMetadata typeof<Webhook.DisableWebhookRuleParameters>

                               route "/delete" (composeHandlers requireWebhookRuleDelete WebhookRule.Delete)
                               |> addMetadata typeof<Webhook.DeleteWebhookRuleParameters>

                               route "/test" (composeHandlers requireWebhookRuleTest WebhookRule.Test)
                               |> addMetadata typeof<Webhook.TestWebhookRuleParameters> ]
                    ]
                subRoute
                    "/webhook/delivery"
                    [
                        POST [ route "/list" (composeHandlers requireWebhookDeliveryRead WebhookDelivery.List)
                               |> addMetadata typeof<Webhook.ListWebhookDeliveriesParameters>

                               route "/show" (composeHandlers requireWebhookDeliveryShow WebhookDelivery.Show)
                               |> addMetadata typeof<Webhook.ShowWebhookDeliveryParameters> ]
                    ]
                subRoute
                    "/branch"
                    [
                        POST [ route "/assign" (composeHandlers requireBranchWriteOrAdmin Branch.Assign)
                               |> addMetadata typeof<Branch.AssignParameters>

                               route "/annotate" (composeHandlers requireBranchRead (composeHandlers requireAnnotatePathRead Branch.Annotate))
                               |> addMetadata typeof<Branch.AnnotateParameters>

                               (route "/checkpoint" (composeHandlers requireBranchWriteOrAdmin Branch.Checkpoint)
                                |> addMetadata typeof<Branch.CreateReferenceParameters>)

                               route "/commit" (composeHandlers requireBranchWriteOrAdmin Branch.Commit)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/create" (composeHandlers requireRepositoryWriteOrAdmin Branch.Create)
                               |> addMetadata typeof<Branch.CreateBranchParameters>

                               route "/createExternal" (composeHandlers requireBranchWriteOrAdmin Branch.CreateExternal)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/delete" Branch.Delete
                               |> addMetadata typeof<Branch.DeleteBranchParameters>

                               route "/enableAssign" Branch.EnableAssign
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableAutoRebase" Branch.EnableAutoRebase
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableCheckpoint" Branch.EnableCheckpoint
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableCommit" (composeHandlers requireBranchAdmin Branch.EnableCommit)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableExternal" Branch.EnableExternal
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enablePromotion" Branch.EnablePromotion
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableSave" Branch.EnableSave
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableTag" Branch.EnableTag
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/setPromotionMode" Branch.SetPromotionMode
                               |> addMetadata typeof<Branch.SetPromotionModeParameters>

                               route "/get" (composeHandlers requireBranchRead Branch.Get)
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getEvents" Branch.GetEvents
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getExternals" Branch.GetExternals
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getCheckpoints" Branch.GetCheckpoints
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getCommits" Branch.GetCommits
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getDiffsForReferenceType" Branch.GetDiffsForReferenceType
                               |> addMetadata typeof<Branch.GetDiffsForReferenceTypeParameters>

                               route "/getParentBranch" Branch.GetParentBranch
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getPromotions" Branch.GetPromotions
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getRecursiveSize" Branch.GetRecursiveSize
                               |> addMetadata typeof<Branch.ListContentsParameters>

                               route "/getReference" Branch.GetReference
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getReferences" Branch.GetReferences
                               |> addMetadata typeof<Branch.GetReferencesParameters>

                               route "/getSaves" Branch.GetSaves
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getTags" Branch.GetTags
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getVersion" Branch.GetVersion
                               |> addMetadata typeof<Branch.GetBranchVersionParameters>

                               route "/listContents" Branch.ListContents
                               |> addMetadata typeof<Branch.ListContentsParameters>

                               route "/promote" (composeHandlers requireBranchWriteOrAdmin Branch.Promote)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/rebase" Branch.Rebase
                               |> addMetadata typeof<Branch.RebaseParameters>

                               route "/revealReference" (composeHandlers requireBranchWriteOrAdmin Branch.RevealReference)
                               |> addMetadata typeof<Branch.RevealReferenceParameters>

                               route "/save" (composeHandlers requireBranchWriteOrAdmin Branch.Save)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/tag" (composeHandlers requireBranchWriteOrAdmin Branch.Tag)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/updateParentBranch" Branch.UpdateParentBranch
                               |> addMetadata typeof<Branch.UpdateParentBranchParameters> ]
                    ]
                subRoute
                    "/diff"
                    [
                        POST [ route "/getDiff" Diff.GetDiff
                               |> addMetadata typeof<Diff.GetDiffParameters>

                               route "/getDiffBySha256Hash" Diff.GetDiffBySha256Hash
                               |> addMetadata typeof<Diff.GetDiffBySha256HashParameters>

                               route "/getDiffByBlake3Hash" Diff.GetDiffByBlake3Hash
                               |> addMetadata typeof<Diff.GetDiffByBlake3HashParameters>

                               route "/populate" Diff.Populate
                               |> addMetadata typeof<Diff.PopulateParameters> ]
                    ]
                subRoute
                    "/directory"
                    [
                        POST [ route "/create" (composeHandlers requireRepositoryWriteOrAdmin DirectoryVersion.Create)
                               |> addMetadata typeof<DirectoryVersion.CreateParameters>

                               route "/get" DirectoryVersion.Get
                               |> addMetadata typeof<DirectoryVersion.GetParameters>

                               route "/getByDirectoryIds" DirectoryVersion.GetByDirectoryIds
                               |> addMetadata typeof<DirectoryVersion.GetByDirectoryIdsParameters>

                               route "/getBySha256Hash" DirectoryVersion.GetBySha256Hash
                               |> addMetadata typeof<DirectoryVersion.GetBySha256HashParameters>

                               route "/getByBlake3Hash" DirectoryVersion.GetByBlake3Hash
                               |> addMetadata typeof<DirectoryVersion.GetByBlake3HashParameters>

                               route "/getDirectoryVersionsRecursive" DirectoryVersion.GetDirectoryVersionsRecursive
                               |> addMetadata typeof<DirectoryVersion.GetParameters>

                               route "/getZipFile" DirectoryVersion.GetZipFile
                               |> addMetadata typeof<DirectoryVersion.GetZipFileParameters>

                               route "/saveDirectoryVersions" (composeHandlers requireRepositoryWriteOrAdmin DirectoryVersion.SaveDirectoryVersions)
                               |> addMetadata typeof<DirectoryVersion.SaveDirectoryVersionsParameters> ]
                    ]
                subRoute "/notifications" [ GET [] ]
                subRoute
                    "/organization"
                    [
                        POST [ route "/create" (composeHandlers requireOwnerWriteOrAdmin Organization.Create)
                               |> addMetadata typeof<Organization.CreateOrganizationParameters>

                               route "/delete" Organization.Delete
                               |> addMetadata typeof<Organization.DeleteOrganizationParameters>

                               route "/get" (composeHandlers requireOrganizationRead Organization.Get)
                               |> addMetadata typeof<Organization.GetOrganizationParameters>

                               route "/listRepositories" Organization.ListRepositories
                               |> addMetadata typeof<Organization.GetOrganizationParameters>

                               route "/setDescription" Organization.SetDescription
                               |> addMetadata typeof<Organization.SetOrganizationDescriptionParameters>

                               route "/setName" (composeHandlers requireOrganizationAdmin Organization.SetName)
                               |> addMetadata typeof<Organization.SetOrganizationNameParameters>

                               route "/setSearchVisibility" Organization.SetSearchVisibility
                               |> addMetadata typeof<Organization.SetOrganizationSearchVisibilityParameters>

                               route "/setType" Organization.SetType
                               |> addMetadata typeof<Organization.SetOrganizationTypeParameters>

                               route "/undelete" Organization.Undelete
                               |> addMetadata typeof<Organization.UndeleteOrganizationParameters> ]
                    ]
                subRoute
                    "/owner"
                    [
                        POST [ route "/create" (composeHandlers requireSystemOperateOrAdmin Owner.Create)
                               |> addMetadata typeof<Owner.CreateOwnerParameters>

                               route "/delete" Owner.Delete
                               |> addMetadata typeof<Owner.DeleteOwnerParameters>

                               route "/get" (composeHandlers requireOwnerRead Owner.Get)
                               |> addMetadata typeof<Owner.GetOwnerParameters>

                               route "/listOrganizations" Owner.ListOrganizations
                               |> addMetadata typeof<Owner.GetOwnerParameters>

                               route "/setDescription" Owner.SetDescription
                               |> addMetadata typeof<Owner.SetOwnerDescriptionParameters>

                               route "/setName" (composeHandlers requireOwnerAdmin Owner.SetName)
                               |> addMetadata typeof<Owner.SetOwnerNameParameters>

                               route "/setSearchVisibility" Owner.SetSearchVisibility
                               |> addMetadata typeof<Owner.SetOwnerSearchVisibilityParameters>

                               route "/setType" Owner.SetType
                               |> addMetadata typeof<Owner.SetOwnerTypeParameters>

                               route "/undelete" Owner.Undelete
                               |> addMetadata typeof<Owner.UndeleteOwnerParameters> ]
                    ]
                subRoute
                    "/agent"
                    [
                        POST [ route "/session/start" (composeHandlers requireRepositoryWrite startAgentSession)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.StartAgentSessionParameters>

                               route "/session/stop" (composeHandlers requireRepositoryWrite stopAgentSession)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.StopAgentSessionParameters>

                               route "/session/status" (composeHandlers requireRepositoryRead getAgentSessionStatus)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.GetAgentSessionStatusParameters>

                               route "/session/active" (composeHandlers requireRepositoryRead getActiveAgentSession)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.GetActiveAgentSessionParameters>

                               route "/session/listActive" (composeHandlers requireRepositoryRead listActiveAgentSessions)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.ListActiveAgentSessionsParameters> ]
                    ]
                subRoute
                    "/work"
                    [
                        POST [ route "/create" (composeHandlers requireRepositoryWriteOrAdmin WorkItem.Create)
                               |> addMetadata typeof<WorkItem.CreateWorkItemParameters>

                               route "/get" (composeHandlers requireRepositoryRead WorkItem.Get)
                               |> addMetadata typeof<WorkItem.GetWorkItemParameters>

                               route "/update" (composeHandlers requireRepositoryWrite WorkItem.Update)
                               |> addMetadata typeof<WorkItem.UpdateWorkItemParameters>

                               route "/add-summary" (composeHandlers requireRepositoryWrite WorkItem.AddSummary)
                               |> addMetadata typeof<WorkItem.AddSummaryParameters>

                               route "/link/reference" (composeHandlers requireRepositoryWrite WorkItem.LinkReference)
                               |> addMetadata typeof<WorkItem.LinkReferenceParameters>

                               route "/link/artifact" (composeHandlers requireRepositoryWrite WorkItem.LinkArtifact)
                               |> addMetadata typeof<WorkItem.LinkArtifactParameters>

                               route "/link/promotion-set" (composeHandlers requireRepositoryWrite WorkItem.LinkPromotionSet)
                               |> addMetadata typeof<WorkItem.LinkPromotionSetParameters>

                               route "/links/list" (composeHandlers requireRepositoryRead WorkItem.GetLinks)
                               |> addMetadata typeof<WorkItem.GetWorkItemLinksParameters>

                               route "/attachments/list" (composeHandlers requireRepositoryRead WorkItem.ListAttachments)
                               |> addMetadata typeof<WorkItem.ListWorkItemAttachmentsParameters>

                               route "/attachments/show" (composeHandlers requireRepositoryRead WorkItem.ShowAttachment)
                               |> addMetadata typeof<WorkItem.ShowWorkItemAttachmentParameters>

                               route "/attachments/download" (composeHandlers requireRepositoryRead WorkItem.DownloadAttachment)
                               |> addMetadata typeof<WorkItem.DownloadWorkItemAttachmentParameters>

                               route "/links/remove/reference" (composeHandlers requireRepositoryWrite WorkItem.RemoveReferenceLink)
                               |> addMetadata typeof<WorkItem.RemoveReferenceLinkParameters>

                               route "/links/remove/promotion-set" (composeHandlers requireRepositoryWrite WorkItem.RemovePromotionSetLink)
                               |> addMetadata typeof<WorkItem.RemovePromotionSetLinkParameters>

                               route "/links/remove/artifact" (composeHandlers requireRepositoryWrite WorkItem.RemoveArtifactLink)
                               |> addMetadata typeof<WorkItem.RemoveArtifactLinkParameters>

                               route "/links/remove/artifact-type" (composeHandlers requireRepositoryWrite WorkItem.RemoveArtifactTypeLinks)
                               |> addMetadata typeof<WorkItem.RemoveArtifactTypeLinksParameters> ]
                    ]
                subRoute
                    "/policy"
                    [
                        POST [ route "/current" Policy.GetCurrent
                               |> addMetadata typeof<Policy.GetPolicyParameters>

                               route "/acknowledge" Policy.Acknowledge
                               |> addMetadata typeof<Policy.AcknowledgePolicyParameters>

                               route "/_seedSnapshot" Policy.SeedSnapshot
                               |> addMetadata typeof<Policy.SeedPolicySnapshotParameters> ]
                    ]
                subRoute
                    "/review"
                    [
                        POST [ route "/notes" Review.GetNotes
                               |> addMetadata typeof<Review.GetReviewNotesParameters>

                               route "/candidate/resolve" Review.ResolveCandidateIdentity
                               |> addMetadata typeof<Review.ResolveCandidateIdentityParameters>

                               route "/candidate/get" Review.GetCandidate
                               |> addMetadata typeof<Review.CandidateProjectionParameters>

                               route "/candidate/required-actions" Review.GetCandidateRequiredActions
                               |> addMetadata typeof<Review.CandidateProjectionParameters>

                               route "/candidate/attestations" Review.GetCandidateAttestations
                               |> addMetadata typeof<Review.CandidateProjectionParameters>

                               route "/report/get" Review.GetReviewReport
                               |> addMetadata typeof<Review.CandidateProjectionParameters>

                               route "/candidate/retry" Review.RetryCandidate
                               |> addMetadata typeof<Review.CandidateProjectionParameters>

                               route "/candidate/cancel" Review.CancelCandidate
                               |> addMetadata typeof<Review.CandidateProjectionParameters>

                               route "/candidate/gate-rerun" Review.RerunCandidateGate
                               |> addMetadata typeof<Review.CandidateGateRerunParameters>

                               route "/checkpoint" Review.Checkpoint
                               |> addMetadata typeof<Review.ReviewCheckpointParameters>

                               route "/resolve" Review.ResolveFinding
                               |> addMetadata typeof<Review.ResolveFindingParameters>

                               route "/deepen" Review.Deepen
                               |> addMetadata typeof<Review.DeepenReviewParameters> ]
                    ]
                subRoute
                    "/queue"
                    [
                        POST [ route "/status" Queue.Status
                               |> addMetadata typeof<Queue.QueueStatusParameters>

                               route "/enqueue" Queue.Enqueue
                               |> addMetadata typeof<Queue.EnqueueParameters>

                               route "/pause" Queue.Pause
                               |> addMetadata typeof<Queue.QueueActionParameters>

                               route "/resume" Queue.Resume
                               |> addMetadata typeof<Queue.QueueActionParameters>

                               route "/dequeue" Queue.Dequeue
                               |> addMetadata typeof<Queue.PromotionSetActionParameters> ]
                    ]
                subRoute
                    "/promotion-set"
                    [
                        POST [ route "/create" (composeHandlers requireRepositoryWriteOrAdmin PromotionSet.Create)
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.CreatePromotionSetParameters>

                               route "/get" PromotionSet.Get
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.GetPromotionSetParameters>

                               route "/get-events" PromotionSet.GetEvents
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.GetPromotionSetEventsParameters>

                               route "/update-input-promotions" PromotionSet.UpdateInputPromotions
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.UpdatePromotionSetInputPromotionsParameters>

                               route "/recompute" PromotionSet.Recompute
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.RecomputePromotionSetParameters>

                               route "/apply" PromotionSet.Apply
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.ApplyPromotionSetParameters>

                               routef "/%O/resolve-conflicts" PromotionSet.ResolveConflicts
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.ResolvePromotionSetConflictsParameters>

                               route "/delete" PromotionSet.Delete
                               |> addMetadata typeof<Grace.Shared.Parameters.PromotionSet.DeletePromotionSetParameters> ]
                    ]
                subRoute
                    "/validation-set"
                    [
                        POST [ route "/create" (composeHandlers requireRepositoryWriteOrAdmin ValidationSet.Create)
                               |> addMetadata typeof<Grace.Shared.Parameters.Validation.CreateValidationSetParameters>

                               route "/get" ValidationSet.Get
                               |> addMetadata typeof<Grace.Shared.Parameters.Validation.GetValidationSetParameters>

                               route "/update" ValidationSet.Update
                               |> addMetadata typeof<Grace.Shared.Parameters.Validation.UpdateValidationSetParameters>

                               route "/delete" ValidationSet.Delete
                               |> addMetadata typeof<Grace.Shared.Parameters.Validation.DeleteValidationSetParameters> ]
                    ]
                subRoute
                    "/validation-result"
                    [
                        POST [ route "/record" (composeHandlers requireRepositoryWriteOrAdmin ValidationResult.Record)
                               |> addMetadata typeof<Grace.Shared.Parameters.Validation.RecordValidationResultParameters> ]
                    ]
                subRoute
                    "/artifact"
                    [
                        POST [ route "/create" (composeHandlers requireRepositoryWriteOrAdmin Artifact.Create)
                               |> addMetadata typeof<Grace.Shared.Parameters.Artifact.CreateArtifactParameters> ]

                        GET [ routef "/%O/download-uri" (fun artifactId -> composeHandlers requireArtifactRepositoryRead (Artifact.GetDownloadUri artifactId)) ]
                    ]
                subRoute
                    "/repository"
                    [
                        POST [ route "/create" (composeHandlers requireOrganizationWriteOrAdmin Repository.Create)
                               |> addMetadata typeof<Repository.CreateRepositoryParameters>

                               route "/delete" (composeHandlers requireRepositoryAdmin Repository.Delete)
                               |> addMetadata typeof<Repository.DeleteRepositoryParameters>

                               route "/exists" Repository.Exists
                               |> addMetadata typeof<Repository.RepositoryParameters>

                               route "/get" (composeHandlers requireRepositoryRead Repository.Get)
                               |> addMetadata typeof<Repository.RepositoryParameters>

                               route "/getBranches" Repository.GetBranches
                               |> addMetadata typeof<Repository.GetBranchesParameters>

                               route "/getBranchesByBranchId" Repository.GetBranchesByBranchId
                               |> addMetadata typeof<Repository.GetBranchesByBranchIdParameters>

                               route "/getReferencesByReferenceId" Repository.GetReferencesByReferenceId
                               |> addMetadata typeof<Repository.GetReferencesByReferenceIdParameters>

                               route "/isEmpty" Repository.IsEmpty
                               |> addMetadata typeof<Repository.IsEmptyParameters>

                               route "/setAllowsLargeFiles" Repository.SetAllowsLargeFiles
                               |> addMetadata typeof<Repository.SetAllowsLargeFilesParameters>

                               route "/setAnonymousAccess" Repository.SetAnonymousAccess
                               |> addMetadata typeof<Repository.SetAnonymousAccessParameters>

                               route "/setCheckpointDays" Repository.SetCheckpointDays
                               |> addMetadata typeof<Repository.SetCheckpointDaysParameters>

                               route "/setDiffCacheDays" Repository.SetDiffCacheDays
                               |> addMetadata typeof<Repository.SetDiffCacheDaysParameters>

                               route "/setDirectoryVersionCacheDays" Repository.SetDirectoryVersionCacheDays
                               |> addMetadata typeof<Repository.SetDirectoryVersionCacheDaysParameters>

                               route "/setDefaultServerApiVersion" Repository.SetDefaultServerApiVersion
                               |> addMetadata typeof<Repository.SetDefaultServerApiVersionParameters>

                               route "/setConflictResolutionPolicy" Repository.SetConflictResolutionPolicy
                               |> addMetadata typeof<Repository.SetConflictResolutionPolicyParameters>

                               route "/setDescription" (composeHandlers requireRepositoryAdmin Repository.SetDescription)
                               |> addMetadata typeof<Repository.SetRepositoryDescriptionParameters>

                               route "/setLogicalDeleteDays" Repository.SetLogicalDeleteDays
                               |> addMetadata typeof<Repository.SetLogicalDeleteDaysParameters>

                               route "/setName" Repository.SetName
                               |> addMetadata typeof<Repository.SetRepositoryNameParameters>

                               route "/setRecordSaves" Repository.SetRecordSaves
                               |> addMetadata typeof<Repository.RecordSavesParameters>

                               route "/setSaveDays" Repository.SetSaveDays
                               |> addMetadata typeof<Repository.SetSaveDaysParameters>

                               route "/setStatus" Repository.SetStatus
                               |> addMetadata typeof<Repository.SetRepositoryStatusParameters>

                               route "/setVisibility" (composeHandlers requireRepositoryAdmin Repository.SetVisibility)
                               |> addMetadata typeof<Repository.SetRepositoryVisibilityParameters>

                               route "/undelete" Repository.Undelete
                               |> addMetadata typeof<Repository.UndeleteRepositoryParameters> ]
                    ]
                subRoute
                    "/storage"
                    [
                        POST [ route "/getUploadMetadataForFiles" (composeHandlers requirePathWrite Storage.GetUploadMetadataForFiles)
                               |> addMetadata typeof<Storage.GetUploadMetadataForFilesParameters>

                               route "/discoverContentBlocks" (composeHandlers requireRepositoryRead Storage.DiscoverContentBlocks)
                               |> addMetadata typeof<Storage.DiscoverContentBlocksParameters>

                               route "/startManifestUploadSession" (composeHandlers requirePathWriteForUploadSession Storage.StartManifestUploadSession)
                               |> addMetadata typeof<Storage.StartManifestUploadSessionParameters>

                               route "/issueDedupeDiscovery" (composeHandlers requirePathWriteForUploadSession Storage.IssueDedupeDiscovery)
                               |> addMetadata typeof<Storage.IssueDedupeDiscoveryParameters>

                               route "/claimReuseRanges" (composeHandlers requirePathWriteForUploadSession Storage.ClaimReuseRanges)
                               |> addMetadata typeof<Storage.ClaimReuseRangesParameters>

                               route "/registerContentBlockUpload" (composeHandlers requirePathWriteForUploadSession Storage.RegisterContentBlockUpload)
                               |> addMetadata typeof<Storage.RegisterContentBlockUploadParameters>

                               route "/confirmContentBlockUpload" (composeHandlers requirePathWriteForUploadSession Storage.ConfirmContentBlockUpload)
                               |> addMetadata typeof<Storage.ConfirmContentBlockUploadParameters>

                               route "/finalizeManifestUpload" (composeHandlers requirePathWriteForUploadSession Storage.FinalizeManifestUpload)
                               |> addMetadata typeof<Storage.FinalizeManifestUploadParameters>

                               route "/getDownloadUri" (composeHandlers requirePathRead Storage.GetDownloadUri)
                               |> addMetadata typeof<Storage.GetDownloadUriParameters>

                               route "/getContentBlockUploadUri" (composeHandlers requirePathWriteForContentBlockUploadUri Storage.GetContentBlockUploadUri)
                               |> addMetadata typeof<Storage.GetContentBlockUploadUriParameters>

                               route
                                   "/getContentBlockDownloadUri"
                                   (composeHandlers requirePathReadForContentBlockDownloadUri Storage.GetContentBlockDownloadUri)
                               |> addMetadata typeof<Storage.GetContentBlockDownloadUriParameters>

                               route "/getUploadUri" (composeHandlers requirePathWriteForUploadUri Storage.GetUploadUris)
                               |> addMetadata typeof<Storage.GetUploadUriParameters> ]
                    ]
                subRoute
                    "/authorize"
                    [
                        POST [ route "/grant-role" Access.GrantRole
                               |> addMetadata typeof<Access.GrantRoleParameters>

                               route "/revoke-role" Access.RevokeRole
                               |> addMetadata typeof<Access.RevokeRoleParameters>

                               route "/list-role-assignments" Access.ListRoleAssignments
                               |> addMetadata typeof<Access.ListRoleAssignmentsParameters>

                               route "/show" Access.ShowRoleAssignments
                               |> addMetadata typeof<Access.ShowRoleAssignmentsParameters>

                               route "/upsert-path-permission" Access.UpsertPathPermission
                               |> addMetadata typeof<Access.UpsertPathPermissionParameters>

                               route "/remove-path-permission" Access.RemovePathPermission
                               |> addMetadata typeof<Access.RemovePathPermissionParameters>

                               route "/list-path-permissions" Access.ListPathPermissions
                               |> addMetadata typeof<Access.ListPathPermissionsParameters>

                               route "/check-permission" Access.CheckPermission
                               |> addMetadata typeof<Access.CheckPermissionParameters> ]
                        GET [ route "/list-roles" Access.ListRoles ]
                    ]
                subRoute
                    "/authenticate"
                    [
                        GET [ route "/me" Auth.Me
                              route "/oidc/config" (Auth.OidcConfig configuration)
                              |> addMetadata (AllowAnonymousAttribute())
                              route "/login" Auth.Login
                              |> addMetadata (AllowAnonymousAttribute())
                              routef "/login/%s" (fun providerId -> Auth.LoginProvider providerId)
                              |> addMetadata (AllowAnonymousAttribute())
                              route "/logout" Auth.Logout ]
                        POST [ route "/token/create" (Auth.TokenCreate configuration)
                               |> addMetadata typeof<Auth.CreatePersonalAccessTokenParameters>
                               route "/token/list" (Auth.TokenList configuration)
                               |> addMetadata typeof<Auth.ListPersonalAccessTokensParameters>
                               route "/token/revoke" (Auth.TokenRevoke configuration)
                               |> addMetadata typeof<Auth.RevokePersonalAccessTokenParameters> ]
                    ]
                subRoute
                    "/reminder"
                    [
                        POST [ route "/list" Reminder.List
                               |> addMetadata typeof<Reminder.ListRemindersParameters>

                               route "/get" Reminder.Get
                               |> addMetadata typeof<Reminder.GetReminderParameters>

                               route "/delete" Reminder.Delete
                               |> addMetadata typeof<Reminder.DeleteReminderParameters>

                               route "/updateTime" Reminder.UpdateTime
                               |> addMetadata typeof<Reminder.UpdateReminderTimeParameters>

                               route "/reschedule" Reminder.Reschedule
                               |> addMetadata typeof<Reminder.RescheduleReminderParameters>

                               route "/create" (composeHandlers requireRepositoryWriteOrAdmin Reminder.Create)
                               |> addMetadata typeof<Reminder.CreateReminderParameters> ]
                    ]
                subRoute
                    "/admin"
                    [
                        POST [
#if DEBUG
                               route "/deleteAllFromCosmosDB" (composeHandlers requireSystemAdmin Storage.DeleteAllFromCosmosDB)
                               route "/deleteAllRemindersFromCosmosDB" (composeHandlers requireSystemAdmin Storage.DeleteAllRemindersFromCosmosDB)
#endif
                                ]
                    ]
            ]

        let notFoundHandler = "Not Found" |> text |> RequestErrors.notFound

        let mutable currentWorkingSet = String.Empty
        let mutable maxWorkingSet = String.Empty
        let mutable lastMetricsUpdateTime = Instant.MinValue
        let mutable threadCount = String.Empty

        /// Implements enrich telemetry for the server request pipeline.
        let enrichTelemetry (activity: Activity) (request: HttpRequest) = //(eventName: string) (obj: Object) =
            let currentProcess = Process.GetCurrentProcess()
            let context = request.HttpContext

            if (lastMetricsUpdateTime + Duration.FromSeconds 10.0) < getCurrentInstant () then
                currentWorkingSet <- currentProcess.WorkingSet64.ToString("N0")
                maxWorkingSet <- currentProcess.PeakWorkingSet64.ToString("N0")
                lastMetricsUpdateTime <- getCurrentInstant ()
                threadCount <- currentProcess.Threads.Count.ToString("N0")

            let user = context.User

            if user.Identity.IsAuthenticated then
                activity
                    .AddTag("enduser.id", Security.TelemetryEnrichment.safeEndUserId user)
                    .AddTag("enduser.claims", Security.TelemetryEnrichment.safeClaimsTag user)
                |> ignore

            activity
                .AddTag("working_set", currentWorkingSet)
                .AddTag("max_working_set", maxWorkingSet)
                .AddTag("thread_count", threadCount)
                .AddTag("http.client_ip", context.Connection.RemoteIpAddress)
                .AddTag("enduser.is_authenticated", user.Identity.IsAuthenticated)
            |> ignore

        /// Registers Grace services, authentication, Orleans, storage, and hosted background workers.
        member _.ConfigureServices(services: IServiceCollection) =
            let mutable configurationObj: obj = null

            if
                not
                <| memoryCache.TryGetValue(Constants.MemoryCache.GraceConfiguration, &configurationObj)
            then
                invalidOp "Grace configuration not found in memory cache."

            let configuration = configurationObj :?> IConfigurationRoot

            let azureMonitorConnectionString =
                let connectionString = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.ApplicationInsightsConnectionString

                if String.IsNullOrWhiteSpace connectionString then
                    configuration["Grace:ApplicationInsightsConnectionString"]
                else
                    connectionString

            ApplicationContext.setActorStateStorageProvider ActorStateStorageProvider.AzureCosmosDb

            // OpenTelemetry trace attribute specifications: https://github.com/open-telemetry/opentelemetry-specification/tree/main/specification/trace/semantic_conventions
            let globalOpenTelemetryAttributes = Dictionary<string, obj>()
            globalOpenTelemetryAttributes.Add("host.name", Environment.MachineName)
            globalOpenTelemetryAttributes.Add("process.pid", Environment.ProcessId)

            globalOpenTelemetryAttributes.Add(
                "process.starttime",
                Process
                    .GetCurrentProcess()
                    .StartTime.ToUniversalTime()
                    .ToString("u")
            )

            globalOpenTelemetryAttributes.Add("process.executable.name", Process.GetCurrentProcess().ProcessName)
            globalOpenTelemetryAttributes.Add("process.runtime.version", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)

            let openApiInfo = new OpenApiInfo()
            openApiInfo.Description <- "Grace is a version control system. Code and documentation can be found at https://gracevcs.com."
            openApiInfo.Title <- "Grace Server API"
            openApiInfo.Version <- "v0.2"
            openApiInfo.Contact <- new OpenApiContact()
            openApiInfo.Contact.Name <- "Scott Arbeit"
            openApiInfo.Contact.Email <- "scott.arbeit@outlook.com"
            openApiInfo.Contact.Url <- Uri("https://gracevcs.com")

            // Telemetry configuration
            let graceServerAppId = "grace-server-integration-test"

            let tracingOtlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT_URL")
            let otel = services.AddOpenTelemetry()

            otel
                .ConfigureResource(fun resourceBuilder ->
                    resourceBuilder
                        .AddService(graceServerAppId)
                        .AddTelemetrySdk()
                        .AddAttributes(globalOpenTelemetryAttributes)
                    |> ignore)

                .WithMetrics(fun metricsBuilder ->
                    metricsBuilder
                        .AddAspNetCoreInstrumentation()
                        .AddMeter("Microsoft.AspNetCore.Hosting")
                        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                        .AddPrometheusExporter(fun prometheusOptions -> prometheusOptions.ScrapeEndpointPath <- "/metrics")
                    |> ignore

                    if
                        not
                        <| String.IsNullOrWhiteSpace(azureMonitorConnectionString)
                    then
                        logToConsole "OpenTelemetry: Configuring Azure Monitor metrics exporter"

                        metricsBuilder.AddAzureMonitorMetricExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString)
                        |> ignore)
                .WithTracing(fun traceBuilder ->
                    traceBuilder
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource(graceServerAppId)
                    |> ignore

                    if
                        not
                        <| String.IsNullOrWhiteSpace(tracingOtlpEndpoint)
                    then
                        logToConsole $"OpenTelemetry: Configuring OTLP exporter to {tracingOtlpEndpoint}"

                        traceBuilder.AddOtlpExporter(fun options -> options.Endpoint <- Uri(tracingOtlpEndpoint))
                        |> ignore
                    else
                        traceBuilder.AddConsoleExporter() |> ignore

                    if
                        not
                        <| String.IsNullOrWhiteSpace(azureMonitorConnectionString)
                    then
                        logToConsole "OpenTelemetry: Configuring Azure Monitor trace exporter"

                        traceBuilder.AddAzureMonitorTraceExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString)
                        |> ignore)
            |> ignore

            let isTesting =
                match Environment.GetEnvironmentVariable("GRACE_TESTING") with
                | null -> false
                | value ->
                    value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

            let oidcConfig = ExternalAuthConfig.tryGetOidcConfig configuration
            let hasOidc = oidcConfig |> Option.isSome

            let authBuilder =
                services.AddAuthentication (fun options ->
                    options.DefaultScheme <- "GraceAuth"
                    options.DefaultChallengeScheme <- "GraceAuth")

            let schemeBuilder =
                authBuilder
                    .AddPolicyScheme(
                        "GraceAuth",
                        "GraceAuth",
                        fun options ->
                            options.ForwardDefaultSelector <-
                                fun context ->
                                    let authorization = context.Request.Headers.Authorization.ToString()

                                    let testUserId =
                                        context
                                            .Request
                                            .Headers[ TestAuth.UserIdHeader ]
                                            .ToString()

                                    AuthSchemeSelection.selectScheme isTesting hasOidc authorization testUserId
                    )
                    .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuth.PersonalAccessTokenAuthHandler>(
                        PersonalAccessTokenAuth.SchemeName,
                        fun _ -> ()
                    )

            if isTesting then
                schemeBuilder.AddScheme<AuthenticationSchemeOptions, GraceTestAuthHandler>(TestAuth.SchemeName, (fun _ -> ()))
                |> ignore
            else
                schemeBuilder |> ignore

            match oidcConfig with
            | Some config ->
                authBuilder.AddJwtBearer(
                    JwtBearerDefaults.AuthenticationScheme,
                    fun options ->
                        options.Authority <- config.Authority
                        options.Audience <- config.Audience

                        options.TokenValidationParameters <-
                            TokenValidationParameters(
                                ValidateIssuer = true,
                                ValidateAudience = true,
                                NameClaimType = "name",
                                RoleClaimType = "roles",
                                ValidAudience = config.Audience
                            )
                )
                |> ignore
            | None -> ()

            services.AddAuthorization (fun options ->
                options.FallbackPolicy <-
                    AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build())
            |> ignore

            services.AddTransient<IClaimsTransformation, GraceClaimsTransformation>()
            |> ignore

            services.AddSingleton<IGracePermissionEvaluator, GracePermissionEvaluator>()
            |> ignore

            services.AddW3CLogging (fun options ->
                options.FileName <- "Grace.Server.log-"

                let tempPath =
                    match Environment.GetEnvironmentVariable("TEMP") with
                    | value when not (String.IsNullOrWhiteSpace value) -> value
                    | _ -> Path.GetTempPath()

                options.LogDirectory <- Path.Combine(tempPath, "Grace.Server.Logs"))
            |> ignore

            services
                .AddHostedService<CosmosWarmup>()
                .AddGiraffe()
                // Next line adds the Json serializer that Giraffe uses internally.
                .AddSingleton<Json.ISerializer>(
                    Json.Serializer(Constants.JsonSerializerOptions)
                )
                .AddSingleton<IPartitionKeyProvider, GracePartitionKeyProvider>()
                .AddSingleton<IApprovalPolicySnapshotResolver, PromotionSet.ApprovalPolicySnapshotResolver>()
                .AddRouting()
                .AddLogging()
                .AddHostedService<ReminderService>()
                .AddHostedService<Notification.Subscriber.GraceEventSubscriptionService>()
                .AddSingleton<WebhookDispatch.IOutboundWebhookTransport, WebhookDispatch.HttpOutboundWebhookTransport>()
                .AddHostedService<WebhookRetryHostedService>()
                .AddHttpLogging()
                .AddOrleans(fun siloBuilder ->
                    siloBuilder.Services.AddSerializer (fun serializerBuilder ->
                        serializerBuilder.AddNodaTimeSerializers()
                        |> ignore)
                    |> ignore)
            |> ignore

            services.AddSingleton<CosmosClient> (fun serviceProvider ->
                let cosmosConnectionString = configuration.GetValue<string>(getConfigKey Constants.EnvironmentVariables.AzureCosmosDBConnectionString)

                let options =
                    new CosmosClientOptions(
                        ConnectionMode = ConnectionMode.Gateway,
                        UseSystemTextJsonSerializerWithOptions = Constants.JsonSerializerOptions,
                        HttpClientFactory =
                            (fun () ->
                                let httpHandler = new HttpClientHandler()
                                httpHandler.ServerCertificateCustomValidationCallback <- HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                                new HttpClient(httpHandler, disposeHandler = true)),
                        LimitToEndpoint = true // prevents discovery probes that can trigger TLS issues on emulator
                    )

                options.ServerCertificateCustomValidationCallback <- Func<X509Certificate2, X509Chain, SslPolicyErrors, bool>(fun _ _ _ -> true)

                if AzureEnvironment.useManagedIdentity then
                    let endpoint =
                        AzureEnvironment.tryGetCosmosEndpointUri ()
                        |> Option.defaultWith (fun () -> invalidOp "Azure Cosmos DB endpoint must be configured when using a managed identity.")

                    new CosmosClient(endpoint.AbsoluteUri, defaultAzureCredential.Value, options)
                else
                    if String.IsNullOrWhiteSpace cosmosConnectionString then
                        invalidOp "Azure Cosmos DB connection string is required when managed identity is disabled."

                    new CosmosClient(cosmosConnectionString, options))
            |> ignore

            let apiVersioningBuilder =
                services.AddApiVersioning (fun options ->
                    options.ReportApiVersions <- true
                    options.DefaultApiVersion <- ApiVersion(ApiContractVersion.CurrentReleasedDate)
                    options.AssumeDefaultVersionWhenUnspecified <- true
                    // Use whatever reader you want
                    options.ApiVersionReader <-
                        ApiVersionReader.Combine(
                            new UrlSegmentApiVersionReader(),
                            new HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey),
                            new MediaTypeApiVersionReader(Constants.ServerApiVersionHeaderKey)
                        ))

            apiVersioningBuilder.AddApiExplorer (fun options ->
                // add the versioned api explorer, which also adds IApiVersionDescriptionProvider service
                // note: the specified format code will format the version as "'v'major[.minor][-status]"
                options.GroupNameFormat <- "'v'VVV"
                options.DefaultApiVersion <- ApiVersion(ApiContractVersion.CurrentReleasedDate)
                options.AssumeDefaultVersionWhenUnspecified <- true
                options.ApiVersionParameterSource <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey)

                // note: this option is only necessary when versioning by url segment. the SubstitutionFormat
                // can also be used to control the format of the API version in route templates
                options.SubstituteApiVersionInUrl <- true)
            |> ignore

            services
                .AddSignalR(fun options -> options.EnableDetailedErrors <- true)
                //.AddStackExchangeRedis(
                //    "localhost:6379",
                //    fun options ->
                //        options.Configuration.ChannelPrefix <- StackExchange.Redis.RedisChannel.Literal("grace-server")
                //        options.Configuration.AbortOnConnectFail <- false
                //)
                .AddJsonProtocol(fun options -> options.PayloadSerializerOptions <- Constants.JsonSerializerOptions)
            |> ignore

            services.AddSingleton<ReviewModels.IReviewModelProvider>(fun _ -> ReviewModels.createProvider configuration)
            |> ignore

            services.AddSingleton<Grace.Types.PromotionSetConflictModel.IConflictResolutionModelProvider> (fun _ ->
                Grace.Types.PromotionSetConflictModel.createProvider configuration)
            |> ignore

            logToConsole $"Exiting ConfigureServices."

        // List all services to the log.
        //services |> Seq.iter (fun service -> logToConsole $"Service: {service.ServiceType}.")

        /// Builds the ASP.NET Core middleware and endpoint pipeline for Grace Server.
        member _.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
            let blobServiceClient = Context.blobServiceClient
            let containers = blobServiceClient.GetBlobContainers()

            let diffContainerName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.DiffContainerName

            if not
               <| containers.Any(fun c -> c.Name = diffContainerName) then
                logToConsole $"Creating blob container: {diffContainerName}."

                blobServiceClient.CreateBlobContainer(diffContainerName, PublicAccessType.None)
                |> ignore

            if env.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore

            app
                //.UseMiddleware<FakeMiddleware>()
                .UseMiddleware<HttpSecurityHeadersMiddleware>()
                .UseW3CLogging()
                .UseMiddleware<CorrelationIdMiddleware>()
                .UseMiddleware<LogRequestHeadersMiddleware>()
                .UseStaticFiles()
                .UseMiddleware<ApiVersionAliasMiddleware>()
                .UseMiddleware<SdkLifecycleMiddleware>()
                .UseRouting()
                .UseAuthentication()
                .UseMiddleware<LogAuthorizationFailureMiddleware>()
                .UseAuthorization()
                .Use(
                    Func<HttpContext, RequestDelegate, Task> (fun (context: HttpContext) (next: RequestDelegate) ->
                        task {
                            let gateDecision = MetricsAuthorization.decideBeforePermissionCheck context.Request.Path (PrincipalMapper.tryGetUserId context.User)

                            match gateDecision with
                            | PassThrough -> return! next.Invoke(context)
                            | RequirePermissionCheck ->
                                let includeReason = Environment.GetEnvironmentVariable("GRACE_TESTING") = "1"
                                let principals = PrincipalMapper.getPrincipals context.User
                                let claims = PrincipalMapper.getEffectiveClaims context.User
                                let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                                let! permissionDecision = evaluator.CheckAsync(principals, claims, Operation.SystemAdmin, Resource.System)
                                let finalDecision = MetricsAuthorization.decideAfterPermissionCheck includeReason permissionDecision

                                match finalDecision with
                                | PassThrough -> return! next.Invoke(context)
                                | RejectRequest (statusCode, message) ->
                                    context.Response.StatusCode <- statusCode
                                    do! context.Response.WriteAsync(message)
                                | RequirePermissionCheck -> invalidOp "Metrics authorization produced an unexpected repeated permission check."
                            | RejectRequest (statusCode, message) ->
                                context.Response.StatusCode <- statusCode
                                do! context.Response.WriteAsync(message)
                        }
                        :> Task)
                )
                .UseStatusCodePages()
                //.UseMiddleware<TimingMiddleware>()
                .UseMiddleware<ValidateIdsMiddleware>()
                .UseEndpoints(fun endpointBuilder ->
                    // Add Giraffe (Web API) endpoints
                    endpointBuilder.MapGiraffeEndpoints(endpoints)

                    // Add Prometheus scraping endpoint
                    endpointBuilder.MapPrometheusScrapingEndpoint()
                    |> ignore

                    // Add SignalR hub endpoints
                    endpointBuilder.MapHub<Notification.NotificationHub>("/notifications")
                    |> ignore)

                // If we get here, we didn't find a route.
                .UseGiraffe(
                    notFoundHandler
                )

            // Set the global ApplicationContext.
            ApplicationContext.serviceProvider <- app.ApplicationServices
            ApplicationContext.Set().Wait()

            logToConsole $"Grace Server started successfully."
