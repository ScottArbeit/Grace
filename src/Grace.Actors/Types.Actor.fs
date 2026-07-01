namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Types.Common
open Grace.Shared.Utilities
open NodaTime
open Orleans
open System
open System.Runtime.Serialization
open System.Runtime.CompilerServices

/// Groups Orleans actor helpers for types keys, proxies, state, or workflow transitions.
module Types =

    /// Wraps timing flag records exchanged by actor queries or projections.
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

    /// Wraps timing records exchanged by actor queries or projections.
    type Timing =
        {
            Time: Instant
            ActorStateName: string
            Flag: TimingFlag
        }

        /// Captures the timing flag, actor state name, and timestamp for a timing entry.
        static member Create (flag: TimingFlag) actorStateName = { Time = getCurrentInstant (); ActorStateName = actorStateName; Flag = flag }
