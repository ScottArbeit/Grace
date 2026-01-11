namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open Orleans
open System
open System.Runtime.Serialization
open System.Runtime.CompilerServices

module Types =

    type TimingFlag =
        | Initial
        | BeforeRetrieveState
        | AfterRetrieveState
        | BeforeSaveState
        | AfterSaveState
        | BeforeStorageQuery
        | AfterStorageQuery
        | BeforeGettingCorrelationIdFromMemoryCache
        | AfterGettingCorrelationIdFromMemoryCache
        | BeforeSettingCorrelationIdInMemoryCache
        | AfterSettingCorrelationIdInMemoryCache
        | Final

    type Timing =
        {
            Time: Instant
            ActorStateName: string
            Flag: TimingFlag
        }

        static member Create (flag: TimingFlag) actorStateName = { Time = getCurrentInstant (); ActorStateName = actorStateName; Flag = flag }
