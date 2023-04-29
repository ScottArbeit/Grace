namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Directory
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Diagnostics

module Directory =

    let GetActorId (directoryId: DirectoryId) = ActorId($"{directoryId}")
 
    type DirectoryVersionActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.DirectoryVersion
        let log = host.LoggerFactory.CreateLogger(actorName)
        let dtoStateName = "DirectoryVersionState"
        let directoryVersionCacheStateName = "DirectoryVersionCacheState"

        let mutable methodStartTime = Instant.MinValue
        let mutable directoryVersion = DirectoryVersion.Default
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null

        override this.OnActivateAsync() =
            let stateManager = this.StateManager
            log.LogInformation("{CurrentInstant} Activated {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
            task {
                try
                    let! retrievedDto = Storage.RetrieveState<DirectoryVersion> stateManager dtoStateName
                    match retrievedDto with
                        | Some retrievedDto -> directoryVersion <- retrievedDto
                        | None -> ()
                with ex ->
                    let exc = createExceptionResponse ex
                    log.LogError("{CurrentInstant} Error in {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended(), exc.ToString())
            } :> Task

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            //log.LogInformation("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId())
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration = getCurrentInstant().Minus(actorStartTime)
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName} Id: {Id}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
            logScope.Dispose()
            Task.CompletedTask

        member private this.SetReminderToDeleteCachedState() =
            this.RegisterReminderAsync("DeleteCachedState", Array.empty<byte>, TimeSpan.FromDays(1.0), TimeSpan.Zero)

            //try
            //    let result = this.RegisterReminderAsync("DeleteCachedState", Array.empty<byte>, TimeSpan.FromDays(1.0), TimeSpan.Zero)
            //    ()
            //with ex ->
            //    log.LogError("{CurrentInstant}: Error in {methodName}. Exception: {exception}", getCurrentInstantExtended(), nameof(this.SetReminderToDeleteCachedState), createExceptionResponse ex)

        interface IRemindable with
            member this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                let stateManager = this.StateManager
                match reminderName with
                | "DeleteCachedState" ->
                    task {
                        let! deleteSucceeded = Storage.DeleteState stateManager directoryVersionCacheStateName
                        ()
                    } :> Task
                | _ -> Task.CompletedTask

        interface IDirectoryVersionActor with
            member this.Exists() = (directoryVersion.CreatedAt > Instant.MinValue) |> returnTask

            member this.Delete(correlationId) = GraceResult.Error (GraceError.Create "Not implemented" correlationId) |> returnTask

            member this.Get() = directoryVersion  |> returnTask

            member this.GetCreatedAt() = directoryVersion.CreatedAt |> returnTask

            member this.GetDirectories() = directoryVersion.Directories |> returnTask

            member this.GetFiles() = directoryVersion.Files |> returnTask

            member this.GetSha256Hash() = directoryVersion.Sha256Hash |> returnTask

            member this.GetSize() = directoryVersion.Size |> returnTask

            member this.GetSizeRecursive() = 
                let stateManager = this.StateManager
                task {
                    if directoryVersion.RecursiveSize = Constants.InitialDirectorySize then
                        // If it hasn't yet been calculated, calculate it.
                        let tasks =
                            directoryVersion.Directories
                            |> Seq.map(fun directoryId ->
                                            task {
                                                let actorId = GetActorId directoryId
                                                let subdirectoryActor = this.ProxyFactory.CreateActorProxy<IDirectoryVersionActor>(actorId, ActorName.DirectoryVersion)
                                                return! subdirectoryActor.GetSizeRecursive()
                                            })
                        Task.WaitAll(tasks.Cast<Task>().ToArray())
                        let recursiveSize =
                            (tasks
                            |> Seq.map (fun task -> task.Result)
                            |> Seq.sum)
                            + directoryVersion.Size
                        directoryVersion <- {directoryVersion with RecursiveSize = recursiveSize}
                        do! Storage.SaveState stateManager dtoStateName directoryVersion
                        return recursiveSize
                    else
                        // If it's already been calculated, just return it.
                        return directoryVersion.RecursiveSize
                }

            member this.GetDirectoryVersionsRecursive() =
                let stateManager = this.StateManager
                task {
                    try
                        //logToConsole $"In GetDirectoryVersionsRecursive."
                        let! cachedSubdirectoryVersions = Storage.RetrieveState<List<DirectoryVersion>> stateManager directoryVersionCacheStateName
                        //let cachedSubdirectoryVersions = None
                        match cachedSubdirectoryVersions with
                        | Some subdirectoryVersions -> 
                            logToConsole $"In DirectoryVersionActor.GetDirectoryVersionsRecursive({this.Id.GetId()}). SubdirectoryVersions already cached."
                            return subdirectoryVersions
                        | None ->
                            logToConsole $"In DirectoryVersionActor.GetDirectoryVersionsRecursive({this.Id.GetId()}). SubdirectoryVersions not cached; generating the list."
                            let subdirectoryVersions = ConcurrentQueue<DirectoryVersion>()
                            subdirectoryVersions.Enqueue(directoryVersion)
                            do! Parallel.ForEachAsync(directoryVersion.Directories, Constants.ParallelOptions, (fun directoryId ct ->
                                ValueTask(task {
                                    let actorId = GetActorId directoryId
                                    let subdirectoryActor = this.ProxyFactory.CreateActorProxy<IDirectoryVersionActor>(actorId, ActorName.DirectoryVersion)
                                    let! subdirectoryContents = subdirectoryActor.GetDirectoryVersionsRecursive()
                                    for directoryVersion in subdirectoryContents do
                                        subdirectoryVersions.Enqueue(directoryVersion)
                                })))
                            //let tasks =
                            //    directoryVersion.Directories
                            //    |> Seq.map(fun directoryId ->
                            //                    task {
                            //                        let actorId = GetActorId directoryId
                            //                        let subdirectoryActor = this.ProxyFactory.CreateActorProxy<IDirectoryVersionActor>(actorId, ActorName.Directory)
                            //                        return! subdirectoryActor.GetDirectoryVersionsRecursive()
                            //                    })
                            //Task.WaitAll(tasks.Cast<Task>().ToArray())
                            //tasks |> Seq.iter (fun task -> subdirectoryVersions.AddRange(task.Result))
                            let subdirectoryVersionsList = subdirectoryVersions.ToList()
                            do! Storage.SaveState stateManager directoryVersionCacheStateName subdirectoryVersionsList
                            logToConsole $"In DirectoryVersionActor.GetDirectoryVersionsRecursive(); storing subdirectoryVersion list."
                            let! _ = this.SetReminderToDeleteCachedState()
                            logToConsole $"In DirectoryVersionActor.GetDirectoryVersionsRecursive(); set delete reminder."
                            return subdirectoryVersionsList
                    with ex ->
                        log.LogError("{CurrentInstant}: Error in {methodName}. Exception: {exception}", getCurrentInstantExtended(), nameof(this.SetReminderToDeleteCachedState), createExceptionResponse ex)
                        return List<DirectoryVersion>()
                }

            member this.Create (newDirectoryVersion: DirectoryVersion) correlationId =
                let stateManager = this.StateManager
                task {
                    // If the CreatedDate isn't set to Instant.MinValue, we know it's already populated..
                    if directoryVersion.CreatedAt > Instant.MinValue then
                        return Error (GraceError.Create (DirectoryError.getErrorMessage DirectoryAlreadyExists) correlationId)
                    elif newDirectoryVersion.Size <> uint64 (newDirectoryVersion.Files.Sum(fun file -> int64 file.Size)) then
                        return Error (GraceError.Create (DirectoryError.getErrorMessage InvalidSize) correlationId)
                    else
                        do! Storage.SaveState stateManager dtoStateName newDirectoryVersion
                        directoryVersion <- newDirectoryVersion
                        return Ok (GraceReturnValue.Create "Directory created." correlationId)
                }
