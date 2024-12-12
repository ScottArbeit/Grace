namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module GlobalLock =

    type LockState =
        { IsLocked: bool
          LockedBy: string option
          LockedAt: Instant option }

        static member Unlocked = { IsLocked = false; LockedBy = None; LockedAt = None }

    let GetActorId (globalLockName: string) = ActorId(globalLockName)

    let log = loggerFactory.CreateLogger("GlobalLock.Actor")

    type GlobalLockActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.GlobalLock

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null

        let mutable lockState = LockState.Unlocked
        let mutable instanceName = String.Empty

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            log.LogInformation(
                "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; InstanceName: {instanceName}; Finished {ActorName}.{MethodName}. IsLocked: {isLocked}.",
                getCurrentInstantExtended (),
                getMachineName,
                duration_ms,
                instanceName,
                actorName,
                context.MethodName,
                lockState.IsLocked
            )

            logScope.Dispose()
            Task.CompletedTask

        interface IGlobalLockActor with
            member this.AcquireLock(lockedBy: string) =
                if lockState.IsLocked then
                    false |> returnTask
                else
                    lockState <- { IsLocked = true; LockedBy = Some lockedBy; LockedAt = Some(getCurrentInstant ()) }
                    instanceName <- lockedBy
                    true |> returnTask

            member this.ReleaseLock(releasedBy: string) =
                match lockState.LockedBy with
                | Some lockedBy ->
                    if lockState.IsLocked && lockedBy = releasedBy then
                        lockState <- LockState.Unlocked
                        instanceName <- lockedBy
                        Ok() |> returnTask
                    else
                        Error "Not locked by the calling instance." |> returnTask
                | None -> Error "Cannot release the lock. The lock has not been acquired." |> returnTask

            member this.IsLocked = lockState.IsLocked |> returnTask
