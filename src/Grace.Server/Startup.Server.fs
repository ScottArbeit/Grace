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
open Grace.Types.Types
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
open Microsoft.IdentityModel.Tokens
open System.Security.Claims

module Application =

    type CosmosWarmup(cosmosClient: CosmosClient, log: ILogger<CosmosWarmup>) =
        interface IHostedService with
            member _.StartAsync(ct: CancellationToken) : Task =
                log.LogInformation("Waiting for Cosmos DB emulator to be ready...")

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

            member _.StopAsync(_ct: CancellationToken) : Task = Task.CompletedTask

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    type Startup(configuration: IConfiguration) =

        do
            ApplicationContext.setConfiguration configuration

            ApplicationContext.configurePubSubSettings ()
            |> ApplicationContext.setPubSubSettings

        let notLoggedIn = RequestErrors.UNAUTHORIZED "Basic" "Some Realm" "You must be logged in."

        let mustBeLoggedIn = requiresAuthentication notLoggedIn

        let graceServerVersion =
            FileVersionInfo
                .GetVersionInfo(
                    Assembly.GetExecutingAssembly().Location
                )
                .FileVersion

        let repositoryResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Repository(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId)
            }

        let ownerResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Owner graceIds.OwnerId
            }

        let organizationResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Organization(graceIds.OwnerId, graceIds.OrganizationId)
            }

        let branchResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return Resource.Branch(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, graceIds.BranchId)
            }

        let uploadPathResourcesFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetUploadMetadataForFilesParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context

                let resources =
                    parameters.FileVersions
                    |> Seq.map (fun fileVersion -> Resource.Path(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, fileVersion.RelativePath))
                    |> Seq.toList

                return resources
            }

        let uploadUriResourcesFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetUploadUriParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context

                let resources =
                    parameters.FileVersions
                    |> Seq.map (fun fileVersion -> Resource.Path(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, fileVersion.RelativePath))
                    |> Seq.toList

                return resources
            }

        let downloadPathResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetDownloadUriParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context

                return Resource.Path(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, parameters.FileVersion.RelativePath)
            }

        let composeHandlers (first: HttpHandler) (second: HttpHandler) : HttpHandler = fun next context -> first (second next) context

        let requireSystemAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.SystemAdmin (fun _ -> task { return Resource.System })

        let requireOwnerAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OwnerAdmin ownerResourceFromContext

        let requireOwnerRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OwnerRead ownerResourceFromContext

        let requireOrgAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OrgAdmin organizationResourceFromContext

        let requireOrgRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.OrgRead organizationResourceFromContext

        let requireRepoAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.RepoAdmin repositoryResourceFromContext

        let requireRepoRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.RepoRead repositoryResourceFromContext

        let requireRepoWrite: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.RepoWrite repositoryResourceFromContext

        let requireBranchAdmin: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.BranchAdmin branchResourceFromContext

        let requireBranchRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.BranchRead branchResourceFromContext

        let requireBranchWrite: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.BranchWrite branchResourceFromContext

        let requirePathWrite: HttpHandler = AuthorizationMiddleware.requiresPermissions Operation.PathWrite uploadPathResourcesFromContext

        let requirePathWriteForUploadUri: HttpHandler = AuthorizationMiddleware.requiresPermissions Operation.PathWrite uploadUriResourcesFromContext

        let requirePathRead: HttpHandler = AuthorizationMiddleware.requiresPermission Operation.PathRead downloadPathResourceFromContext

        let activeAgentSessionsByAgentKey = ConcurrentDictionary<string, AgentSessionInfo>()
        let activeAgentSessionsBySessionId = ConcurrentDictionary<string, string>()
        let agentSessionOperationCache = ConcurrentDictionary<string, AgentSessionOperationResult>()
        let bootstrappedAgentKeys = ConcurrentDictionary<string, byte>()

        let tryParseGuid (value: string) =
            let mutable parsed = Guid.Empty

            if String.IsNullOrWhiteSpace value |> not
               && Guid.TryParse(value, &parsed)
               && parsed <> Guid.Empty then
                Some parsed
            else
                Option.None

        let resolveRepositoryId (graceIds: GraceIds) (repositoryIdFromParameters: string) =
            if graceIds.RepositoryId <> RepositoryId.Empty then
                graceIds.RepositoryId
            else
                repositoryIdFromParameters
                |> tryParseGuid
                |> Option.defaultValue RepositoryId.Empty

        let normalizeOperationId (operationId: string) (operationName: string) (correlationId: CorrelationId) =
            if String.IsNullOrWhiteSpace operationId then
                $"{operationName}-{correlationId}"
            else
                operationId.Trim()

        let normalizeSessionSource (source: string) = if String.IsNullOrWhiteSpace source then "cli" else source.Trim()

        let toAgentKey (repositoryId: RepositoryId) (agentId: string) =
            if repositoryId = RepositoryId.Empty
               || String.IsNullOrWhiteSpace agentId then
                String.Empty
            else
                $"{repositoryId:N}:{agentId.Trim().ToLowerInvariant()}"

        let normalizeReplayIdentity (identity: string) =
            if String.IsNullOrWhiteSpace identity then
                "unknown"
            else
                identity.Trim().ToLowerInvariant()

        let toReplayKey (repositoryId: RepositoryId) (identity: string) (operationId: string) =
            $"{repositoryId:N}|{normalizeReplayIdentity identity}|{normalizeReplayIdentity operationId}"

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

        let createOperationResult (session: AgentSessionInfo) (message: string) (operationId: string) (wasReplay: bool) =
            { AgentSessionOperationResult.Default with Session = session; Message = message; OperationId = operationId; WasIdempotentReplay = wasReplay }

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

        let tryGetActiveSessionByAgentKey (agentKey: string) =
            match activeAgentSessionsByAgentKey.TryGetValue(agentKey) with
            | true, session when session.LifecycleState = AgentSessionLifecycleState.Active -> Some(agentKey, session)
            | _ -> Option.None

        let tryRemoveSessionByAgentKey (agentKey: string) =
            let mutable removedSession = AgentSessionInfo.Default

            activeAgentSessionsByAgentKey.TryRemove(agentKey, &removedSession)
            |> ignore

        let tryRemoveSessionLookup (sessionId: string) =
            let mutable removedAgentKey = String.Empty

            activeAgentSessionsBySessionId.TryRemove(sessionId, &removedAgentKey)
            |> ignore

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
                    "/branch"
                    [
                        POST [ route "/assign" Branch.Assign
                               |> addMetadata typeof<Branch.AssignParameters>

                               route "/checkpoint" Branch.Checkpoint
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/commit" (composeHandlers requireBranchWrite Branch.Commit)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/create" Branch.Create
                               |> addMetadata typeof<Branch.CreateBranchParameters>

                               route "/createExternal" Branch.CreateExternal
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

                               route "/promote" Branch.Promote
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/rebase" Branch.Rebase
                               |> addMetadata typeof<Branch.RebaseParameters>

                               route "/save" Branch.Save
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/tag" Branch.Tag
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

                               route "/populate" Diff.Populate
                               |> addMetadata typeof<Diff.PopulateParameters> ]
                    ]
                subRoute
                    "/directory"
                    [
                        POST [ route "/create" DirectoryVersion.Create
                               |> addMetadata typeof<DirectoryVersion.CreateParameters>

                               route "/get" DirectoryVersion.Get
                               |> addMetadata typeof<DirectoryVersion.GetParameters>

                               route "/getByDirectoryIds" DirectoryVersion.GetByDirectoryIds
                               |> addMetadata typeof<DirectoryVersion.GetByDirectoryIdsParameters>

                               route "/getBySha256Hash" DirectoryVersion.GetBySha256Hash
                               |> addMetadata typeof<DirectoryVersion.GetBySha256HashParameters>

                               route "/getDirectoryVersionsRecursive" DirectoryVersion.GetDirectoryVersionsRecursive
                               |> addMetadata typeof<DirectoryVersion.GetParameters>

                               route "/getZipFile" DirectoryVersion.GetZipFile
                               |> addMetadata typeof<DirectoryVersion.GetZipFileParameters>

                               route "/saveDirectoryVersions" DirectoryVersion.SaveDirectoryVersions
                               |> addMetadata typeof<DirectoryVersion.SaveDirectoryVersionsParameters> ]
                    ]
                subRoute "/notifications" [ GET [] ]
                subRoute
                    "/organization"
                    [
                        POST [ route "/create" Organization.Create
                               |> addMetadata typeof<Organization.CreateOrganizationParameters>

                               route "/delete" Organization.Delete
                               |> addMetadata typeof<Organization.DeleteOrganizationParameters>

                               route "/get" (composeHandlers requireOrgRead Organization.Get)
                               |> addMetadata typeof<Organization.GetOrganizationParameters>

                               route "/listRepositories" Organization.ListRepositories
                               |> addMetadata typeof<Organization.GetOrganizationParameters>

                               route "/setDescription" Organization.SetDescription
                               |> addMetadata typeof<Organization.SetOrganizationDescriptionParameters>

                               route "/setName" (composeHandlers requireOrgAdmin Organization.SetName)
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
                        POST [ route "/create" Owner.Create
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
                        POST [ route "/session/start" (composeHandlers requireRepoWrite startAgentSession)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.StartAgentSessionParameters>

                               route "/session/stop" (composeHandlers requireRepoWrite stopAgentSession)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.StopAgentSessionParameters>

                               route "/session/status" (composeHandlers requireRepoRead getAgentSessionStatus)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.GetAgentSessionStatusParameters>

                               route "/session/active" (composeHandlers requireRepoRead getActiveAgentSession)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.GetActiveAgentSessionParameters>

                               route "/session/listActive" (composeHandlers requireRepoRead listActiveAgentSessions)
                               |> addMetadata typeof<Grace.Shared.Parameters.Common.ListActiveAgentSessionsParameters> ]
                    ]
                subRoute
                    "/work"
                    [
                        POST [ route "/create" (composeHandlers requireRepoWrite WorkItem.Create)
                               |> addMetadata typeof<WorkItem.CreateWorkItemParameters>

                               route "/get" WorkItem.Get
                               |> addMetadata typeof<WorkItem.GetWorkItemParameters>

                               route "/update" WorkItem.Update
                               |> addMetadata typeof<WorkItem.UpdateWorkItemParameters>

                               route "/add-summary" WorkItem.AddSummary
                               |> addMetadata typeof<WorkItem.AddSummaryParameters>

                               route "/link/reference" WorkItem.LinkReference
                               |> addMetadata typeof<WorkItem.LinkReferenceParameters>

                               route "/link/artifact" WorkItem.LinkArtifact
                               |> addMetadata typeof<WorkItem.LinkArtifactParameters>

                               route "/link/promotion-set" WorkItem.LinkPromotionSet
                               |> addMetadata typeof<WorkItem.LinkPromotionSetParameters>

                               route "/links/list" WorkItem.GetLinks
                               |> addMetadata typeof<WorkItem.GetWorkItemLinksParameters>

                               route "/attachments/list" WorkItem.ListAttachments
                               |> addMetadata typeof<WorkItem.ListWorkItemAttachmentsParameters>

                               route "/attachments/show" WorkItem.ShowAttachment
                               |> addMetadata typeof<WorkItem.ShowWorkItemAttachmentParameters>

                               route "/attachments/download" WorkItem.DownloadAttachment
                               |> addMetadata typeof<WorkItem.DownloadWorkItemAttachmentParameters>

                               route "/links/remove/reference" WorkItem.RemoveReferenceLink
                               |> addMetadata typeof<WorkItem.RemoveReferenceLinkParameters>

                               route "/links/remove/promotion-set" WorkItem.RemovePromotionSetLink
                               |> addMetadata typeof<WorkItem.RemovePromotionSetLinkParameters>

                               route "/links/remove/artifact" WorkItem.RemoveArtifactLink
                               |> addMetadata typeof<WorkItem.RemoveArtifactLinkParameters>

                               route "/links/remove/artifact-type" WorkItem.RemoveArtifactTypeLinks
                               |> addMetadata typeof<WorkItem.RemoveArtifactTypeLinksParameters> ]
                    ]
                subRoute
                    "/policy"
                    [
                        POST [ route "/current" Policy.GetCurrent
                               |> addMetadata typeof<Policy.GetPolicyParameters>

                               route "/acknowledge" Policy.Acknowledge
                               |> addMetadata typeof<Policy.AcknowledgePolicyParameters> ]
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
                        POST [ route "/create" PromotionSet.Create
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
                        POST [ route "/create" ValidationSet.Create
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
                        POST [ route "/record" ValidationResult.Record
                               |> addMetadata typeof<Grace.Shared.Parameters.Validation.RecordValidationResultParameters> ]
                    ]
                subRoute
                    "/artifact"
                    [
                        POST [ route "/create" Artifact.Create
                               |> addMetadata typeof<Grace.Shared.Parameters.Artifact.CreateArtifactParameters> ]

                        GET [ routef "/%O/download-uri" Artifact.GetDownloadUri ]
                    ]
                subRoute
                    "/repository"
                    [
                        POST [ route "/create" Repository.Create
                               |> addMetadata typeof<Repository.CreateRepositoryParameters>

                               route "/delete" (composeHandlers requireRepoAdmin Repository.Delete)
                               |> addMetadata typeof<Repository.DeleteRepositoryParameters>

                               route "/exists" Repository.Exists
                               |> addMetadata typeof<Repository.RepositoryParameters>

                               route "/get" (composeHandlers requireRepoRead Repository.Get)
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

                               route "/setDescription" (composeHandlers requireRepoAdmin Repository.SetDescription)
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

                               route "/setVisibility" (composeHandlers requireRepoAdmin Repository.SetVisibility)
                               |> addMetadata typeof<Repository.SetRepositoryVisibilityParameters>

                               route "/undelete" Repository.Undelete
                               |> addMetadata typeof<Repository.UndeleteRepositoryParameters> ]
                    ]
                subRoute
                    "/storage"
                    [
                        POST [ route "/getUploadMetadataForFiles" (composeHandlers requirePathWrite Storage.GetUploadMetadataForFiles)
                               |> addMetadata typeof<Storage.GetUploadMetadataForFilesParameters>

                               route "/getDownloadUri" (composeHandlers requirePathRead Storage.GetDownloadUri)
                               |> addMetadata typeof<Storage.GetDownloadUriParameters>

                               route "/getUploadUri" (composeHandlers requirePathWriteForUploadUri Storage.GetUploadUris)
                               |> addMetadata typeof<Storage.GetUploadUriParameters> ]
                    ]
                subRoute
                    "/access"
                    [
                        POST [ route "/grantRole" Access.GrantRole
                               |> addMetadata typeof<Access.GrantRoleParameters>

                               route "/revokeRole" Access.RevokeRole
                               |> addMetadata typeof<Access.RevokeRoleParameters>

                               route "/listRoleAssignments" Access.ListRoleAssignments
                               |> addMetadata typeof<Access.ListRoleAssignmentsParameters>

                               route "/upsertPathPermission" Access.UpsertPathPermission
                               |> addMetadata typeof<Access.UpsertPathPermissionParameters>

                               route "/removePathPermission" Access.RemovePathPermission
                               |> addMetadata typeof<Access.RemovePathPermissionParameters>

                               route "/listPathPermissions" Access.ListPathPermissions
                               |> addMetadata typeof<Access.ListPathPermissionsParameters>

                               route "/checkPermission" Access.CheckPermission
                               |> addMetadata typeof<Access.CheckPermissionParameters> ]
                        GET [ route "/listRoles" Access.ListRoles ]
                    ]
                subRoute
                    "/auth"
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

                               route "/create" Reminder.Create
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
                let claimsList = stringBuilderPool.Get()

                try
                    if not <| isNull user.Claims then
                        for claim in user.Claims do
                            claimsList.Append($"{claim.Type}:{claim.Value};")
                            |> ignore

                    if claimsList.Length > 1 then
                        claimsList.Remove(claimsList.Length - 1, 1)
                        |> ignore

                    activity
                        .AddTag("enduser.id", user.Identity.Name)
                        .AddTag("enduser.claims", claimsList.ToString())
                    |> ignore
                finally
                    stringBuilderPool.Return(claimsList)

            activity
                .AddTag("working_set", currentWorkingSet)
                .AddTag("max_working_set", maxWorkingSet)
                .AddTag("thread_count", threadCount)
                .AddTag("http.client_ip", context.Connection.RemoteIpAddress)
                .AddTag("enduser.is_authenticated", user.Identity.IsAuthenticated)
            |> ignore

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

            if isTesting then
                services
                    .AddAuthentication(fun options ->
                        options.DefaultScheme <- "GraceAuth"
                        options.DefaultChallengeScheme <- "GraceAuth")
                    .AddPolicyScheme(
                        "GraceAuth",
                        "GraceAuth",
                        fun options ->
                            options.ForwardDefaultSelector <-
                                fun context ->
                                    let authorization = context.Request.Headers.Authorization.ToString()

                                    if
                                        not (String.IsNullOrWhiteSpace authorization)
                                        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                    then
                                        let token = authorization.Substring("Bearer ".Length).Trim()

                                        if token.StartsWith(TokenPrefix, StringComparison.Ordinal) then
                                            PersonalAccessTokenAuth.SchemeName
                                        else
                                            TestAuth.SchemeName
                                    else
                                        TestAuth.SchemeName
                    )
                    .AddScheme<AuthenticationSchemeOptions, GraceTestAuthHandler>(TestAuth.SchemeName, (fun _ -> ()))
                    .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuth.PersonalAccessTokenAuthHandler>(
                        PersonalAccessTokenAuth.SchemeName,
                        fun _ -> ()
                    )
                |> ignore
            else
                ExternalAuthConfig.warnIfMicrosoftConfigPresent configuration

                let oidcConfig = ExternalAuthConfig.tryGetOidcConfig configuration
                let hasOidc = oidcConfig |> Option.isSome

                let authBuilder =
                    services.AddAuthentication (fun options ->
                        options.DefaultScheme <- "GraceAuth"
                        options.DefaultChallengeScheme <- "GraceAuth")

                authBuilder
                    .AddPolicyScheme(
                        "GraceAuth",
                        "GraceAuth",
                        fun options ->
                            options.ForwardDefaultSelector <-
                                fun context ->
                                    let authorization = context.Request.Headers.Authorization.ToString()

                                    if
                                        not (String.IsNullOrWhiteSpace authorization)
                                        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                    then
                                        let token = authorization.Substring("Bearer ".Length).Trim()

                                        if token.StartsWith(TokenPrefix, StringComparison.Ordinal) then
                                            PersonalAccessTokenAuth.SchemeName
                                        else if hasOidc then
                                            JwtBearerDefaults.AuthenticationScheme
                                        else
                                            PersonalAccessTokenAuth.SchemeName
                                    else if hasOidc then
                                        JwtBearerDefaults.AuthenticationScheme
                                    else
                                        PersonalAccessTokenAuth.SchemeName
                    )
                    .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuth.PersonalAccessTokenAuthHandler>(
                        PersonalAccessTokenAuth.SchemeName,
                        fun _ -> ()
                    )
                |> ignore

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
                .AddRouting()
                .AddLogging()
                .AddHostedService<ReminderService>()
                .AddHostedService<Notification.Subscriber.GraceEventSubscriptionService>()
                .AddHttpLogging()
                .AddOrleans(fun siloBuilder ->
                    siloBuilder.Services.AddSerializer (fun serializerBuilder ->
                        serializerBuilder.AddNodaTimeSerializers()
                        |> ignore)
                    |> ignore)
            |> ignore

            services.AddSingleton<CosmosClient> (fun serviceProvider ->
                let cosmosConnectionString = configuration.GetValue<string>(getConfigKey Constants.EnvironmentVariables.AzureCosmosDBConnectionString)

                // Force SNI = "localhost" while we connect to 127.0.0.1.
                let httpHandler =
                    // Create and configure SslClientAuthenticationOptions
                    let sslOptions = SslClientAuthenticationOptions()
                    sslOptions.TargetHost <- "localhost" // SNI host_name must be DNS per RFC 6066
                    sslOptions.RemoteCertificateValidationCallback <- RemoteCertificateValidationCallback(fun _ _ _ _ -> true)
                    new SocketsHttpHandler(SslOptions = sslOptions)

                let options =
                    new CosmosClientOptions(
                        ConnectionMode = ConnectionMode.Gateway,
                        UseSystemTextJsonSerializerWithOptions = Constants.JsonSerializerOptions,
                        HttpClientFactory = (fun () -> new HttpClient(httpHandler, disposeHandler = true)),
                        LimitToEndpoint = true // prevents discovery probes that can trigger TLS issues on emulator
                    )

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
                    options.DefaultApiVersion <- new ApiVersion(1, 0)
                    options.AssumeDefaultVersionWhenUnspecified <- true
                    // Use whatever reader you want
                    options.ApiVersionReader <-
                        ApiVersionReader.Combine(
                            new UrlSegmentApiVersionReader(),
                            new HeaderApiVersionReader("x-api-version"),
                            new MediaTypeApiVersionReader("x-api-version")
                        ))

            apiVersioningBuilder.AddApiExplorer (fun options ->
                // add the versioned api explorer, which also adds IApiVersionDescriptionProvider service
                // note: the specified format code will format the version as "'v'major[.minor][-status]"
                options.GroupNameFormat <- "'v'VVV"
                options.DefaultApiVersion <- ApiVersion(DateOnly(2023, 10, 1))
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
                .UseRouting()
                .UseAuthentication()
                .UseAuthorization()
                .Use(
                    Func<HttpContext, RequestDelegate, Task> (fun (context: HttpContext) (next: RequestDelegate) ->
                        task {
                            if context.Request.Path.StartsWithSegments(PathString("/metrics")) then
                                let includeReason = Environment.GetEnvironmentVariable("GRACE_TESTING") = "1"

                                match PrincipalMapper.tryGetUserId context.User with
                                | None ->
                                    context.Response.StatusCode <- StatusCodes.Status401Unauthorized
                                    do! context.Response.WriteAsync("Authentication required.")
                                | Some _ ->
                                    let principals = PrincipalMapper.getPrincipals context.User
                                    let claims = PrincipalMapper.getEffectiveClaims context.User
                                    let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                                    let! decision = evaluator.CheckAsync(principals, claims, Operation.SystemAdmin, Resource.System)

                                    match decision with
                                    | Allowed _ -> return! next.Invoke(context)
                                    | Denied reason ->
                                        context.Response.StatusCode <- StatusCodes.Status403Forbidden

                                        let message =
                                            if
                                                includeReason
                                                && not (String.IsNullOrWhiteSpace reason)
                                            then
                                                reason
                                            else
                                                "Forbidden."

                                        do! context.Response.WriteAsync(message)
                            else
                                return! next.Invoke(context)
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
