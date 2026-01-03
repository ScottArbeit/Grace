namespace Grace.Server

open Grace.Actors.Constants
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting
open Orleans.Persistence
open Orleans.Runtime
open System
open System.Diagnostics
open System.Linq
open System.Threading.Tasks
open FSharpPlus.Data
open System.Collections.Generic
open Grace.Shared.Parameters

module Orleans =

    /// Provides partition keys for grains based on their type and context when Orleans is used with Cosmos DB.
    type GracePartitionKeyProvider() =
        let log = loggerFactory.CreateLogger("OrleansFilters.Server")

        interface Cosmos.IPartitionKeyProvider with
            member _.GetPartitionKey(grainType: string, grainId: GrainId) =
                ValueTask<string>(
                    task {
                        //logToConsole $"****GracePartitionKeyProvider: grainType: {grainType}; grainId: {grainId}."

                        let orleansContext =
                            match memoryCache.GetOrleansContextEntry(grainId) with
                            | Some orleansContext -> orleansContext
                            | None -> Dictionary<string, obj>()

                        //orleansContext
                        //|> Seq.iter (fun kvp -> logToConsole $"**** - {kvp.Key}: {kvp.Value}")

                        let organizationId () = $"{orleansContext[nameof OrganizationId]}"
                        let repositoryId () = $"{orleansContext[nameof RepositoryId]}"

                        let partitionKey =
                            match grainType with
                            | StateName.Branch -> repositoryId ()
                            | StateName.Diff -> repositoryId ()
                            | StateName.DirectoryAppearance -> repositoryId ()
                            | StateName.DirectoryVersion -> repositoryId ()
                            | StateName.FileAppearance -> repositoryId ()
                            | StateName.NamedSection -> repositoryId ()
                            | StateName.Organization -> StateName.Organization
                            | StateName.Owner -> StateName.Owner
                            | StateName.PersonalAccessToken -> grainId.Key.ToString()
                            | StateName.Reference -> repositoryId ()
                            | StateName.Reminder -> StateName.Reminder
                            | StateName.Repository -> organizationId ()
                            | StateName.RepositoryPermission -> repositoryId ()
                            | StateName.AccessControl -> grainId.Key.ToString()
                            | StateName.User -> StateName.User
                            | _ -> raise (ArgumentException($"Unknown grain type in {nameof GracePartitionKeyProvider}: {grainType}"))

                        let correlationid = getCorrelationId ()

                        //logToConsole
                        //    $"****GracePartitionKeyProvider: correlationId: {correlationid}; grainType: {grainType}; grainId: {grainId}; partitionKey: {partitionKey}."

                        return partitionKey
                    }
                )

    /// Centralizes pre‑ and post‑invoke behavior for all grains.
    type CorrelationLoggingFilter() =
        let log = loggerFactory.CreateLogger("OrleansFilters.Server")

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
