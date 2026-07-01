namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

/// Groups Orleans actor helpers for global lock keys, proxies, state, or workflow transitions.
module GlobalLock =

    /// Stores durable state for lock state.
    type LockState =
        {
            IsLocked: bool
            LockedBy: string option
            LockedAt: Instant option
        }

        /// Coordinates unlocked logic for the GlobalLock actor.
        static member Unlocked = { IsLocked = false; LockedBy = None; LockedAt = None }

    let log = loggerFactory.CreateLogger("GlobalLock.Actor")

    /// Implements the Orleans grain for global lock actor.
    type GlobalLockActor() =
        inherit Grain()

        static let actorName = ActorName.GlobalLock

        let log = loggerFactory.CreateLogger("GlobalLock.Actor")

        let mutable actorStartTime = Instant.MinValue

        let mutable lockState = LockState.Unlocked
        let mutable instanceName = String.Empty

        interface IGlobalLockActor with
            /// Attempts to acquire the global lock for an owner and expiration window.
            member this.AcquireLock(lockedBy: string) =
                if lockState.IsLocked then
                    false |> returnTask
                else
                    lockState <- { IsLocked = true; LockedBy = Some lockedBy; LockedAt = Some(getCurrentInstant ()) }
                    instanceName <- lockedBy
                    true |> returnTask

            /// Releases the global lock when the caller owns the active lease.
            member this.ReleaseLock(releasedBy: string) =
                match lockState.LockedBy with
                | Some lockedBy ->
                    if lockState.IsLocked && lockedBy = releasedBy then
                        lockState <- LockState.Unlocked
                        instanceName <- lockedBy
                        Ok() |> returnTask
                    else
                        Error "Not locked by the calling instance."
                        |> returnTask
                | None ->
                    Error "Cannot release the lock. The lock has not been acquired."
                    |> returnTask //blah

            /// Reports whether the global lock currently has an active owner.
            member this.IsLocked() = lockState.IsLocked |> returnTask
