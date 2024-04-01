namespace Grace.Actors

open Dapr.Actors
open Grace.Actors.Interfaces
open Grace.Actors.Constants
open Grace.Actors.Storage
open Grace.Shared
open Grace.Shared.Types
open System
open System.Threading.Tasks

module RepositoryPermission =

    let ActorName = ActorName.RepositoryPermission

    type RepositoryPermissionCommand = Set of PathPermission

//[<Serializable>]
//type RepositoryPermissionEvent =
//    {
//        Event: RepositoryPermissionEventType
//        Metadata: EventMetadata
//    }

//type IRepositoryPermission =
//    inherit IActor
//    abstract member Exists: unit -> Task<bool>
//    abstract member
