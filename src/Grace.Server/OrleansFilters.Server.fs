namespace Grace.Server

open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open System
open System.Diagnostics
open System.Threading.Tasks

module Orleans =

    type GracePartitionKeyProvider() =
        interface Orleans.Persistence.Cosmos.IPartitionKeyProvider with
            member _.GetPartitionKey(grainType: string, grainId: GrainId) =
                ValueTask<string>(
                    task {
                        let correlationid = getCorrelationId ()
                        let repositoryId = getRepositoryId ()

                        //let! repositoryId =
                        //    match grainType with
                        //    | "Repository" ->
                        //        let grain = grainFactory.GetGrain<IRepositoryActor>(grainId.GetGuidKey())
                        //        grain.AsReference<IHasRepositoryId>().GetRepositoryId correlationid
                        //    | "Branch" ->
                        //        let grain = grainFactory.GetGrain<IBranchActor>(grainId.GetGuidKey())
                        //        grain.AsReference<IHasRepositoryId>().GetRepositoryId correlationid
                        //    | "Reference" ->
                        //        let grain = grainFactory.GetGrain<IReferenceActor>(grainId.GetGuidKey())
                        //        grain.AsReference<IHasRepositoryId>().GetRepositoryId correlationid
                        //    | "DirectoryVersion" ->
                        //        let grain = grainFactory.GetGrain<IDirectoryVersionActor>(grainId.GetGuidKey())
                        //        grain.AsReference<IHasRepositoryId>().GetRepositoryId correlationid
                        //    | _ ->
                        //        // For other grain types, use the grain type as the partition key.
                        //        Guid.Empty |> returnTask

                        if repositoryId = Guid.Empty then
                            logToConsole
                                $"GracePartitionKeyProvider: correlationId: {correlationid}; grainType: {grainType}; grainId: {grainId} - No repository found, using grain type as partition key."

                            return grainType
                        else
                            logToConsole
                                $"GracePartitionKeyProvider: correlationId: {correlationid}; grainType: {grainType}, grainId: {grainId} - Using repository ID {repositoryId} as partition key."

                            return $"{repositoryId}"
                    }
                )

    /// Centralizes pre‑ and post‑invoke behavior for all grains.
    type CorrelationLoggingFilter(loggerFactory: ILoggerFactory) =
        interface IIncomingGrainCallFilter with
            member _.Invoke(context: IIncomingGrainCallContext) =
                task {
                    let correlationId = getCorrelationId ()
                    let actorName = getActorName ()

                    let sb = stringBuilderPool.Get()

                    if not <| isNull (RequestContext.Entries) then
                        RequestContext.Keys
                        |> Seq.iter (fun key ->
                            logToConsole $"RequestContext: {key}"

                            let blah =
                                match RequestContext.Get(key) with
                                | :? string as s -> s
                                | _ -> String.Empty

                            sb.Append($"{key} = {blah}; ") |> ignore)

                        log.LogInformation("RequestContext: {RequestContext}", sb)
                        stringBuilderPool.Return(sb)

                    let grainType = $"{context.TargetId.Type}"
                    let grainId = $"{context.TargetId.Key}"
                    let log = loggerFactory.CreateLogger(grainType)
                    use _scope = log.BeginScope("Grain {GrainType}/{GrainId}", grainType, grainId)

                    log.LogTrace(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Reminder {ActorName}.{MethodName}; GrainId: {Id}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        actorName,
                        context.InterfaceMethod.Name,
                        grainId
                    )

                    let actorStartTime = getCurrentInstant ()

                    try
                        // Invoke the grain method.
                        do! context.Invoke()
                    with ex ->
                        // Log the exception if it occurs during the grain method invocation.
                        let duration_ms = getDurationRightAligned_ms actorStartTime

                        log.LogError(
                            ex,
                            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Exception in {ActorName}.{MethodName}; GrainId: {Id}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            actorName,
                            context.InterfaceMethod.Name,
                            grainId
                        )

                    let command = getCurrentCommand ()

                    let duration_ms = getDurationRightAligned_ms actorStartTime
                    //if not <| String.IsNullOrEmpty(actorName) then
                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName};{Command} GrainId: {Id}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        actorName,
                        context.InterfaceMethod.Name,
                        command,
                        grainId
                    )
                }
