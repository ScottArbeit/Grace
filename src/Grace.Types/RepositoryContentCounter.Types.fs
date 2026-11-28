namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Orleans
open System
open System.Runtime.Serialization

/// Contains repository content counter helpers.
module RepositoryContentCounter =

    /// Represents repository content counter lifecycle state.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterLifecycleState =
        | NotReferenced
        | Referenced

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterLifecycleState>()

    /// Identifies the bounded counter operation recorded by the latest completed change.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterChangeOperation =
        | Added
        | Removed

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterChangeOperation>()

    /// Records the latest completed count transition without retaining a lifetime operation history.
    [<GenerateSerializer>]
    type RepositoryContentCounterCompletedChange =
        {
            [<Id(0u)>]
            OperationId: RepositoryContentCounterOperationId
            [<Id(1u)>]
            Operation: RepositoryContentCounterChangeOperation
            [<Id(2u)>]
            PreviousCount: ReferenceCount
            [<Id(3u)>]
            CurrentCount: ReferenceCount
            [<Id(4u)>]
            Revision: int64
        }

    /// One storage meter change committed with a zero crossing that the repository's storage meter has not released yet.
    /// The revision is the counter revision of the crossing, so (StoragePoolId, ManifestAddress, Revision) names it once.
    [<GenerateSerializer>]
    type RepositoryStorageMeterChange =
        {
            [<Id(0u)>]
            Revision: int64
            [<Id(1u)>]
            LogicalBytesDelta: int64
        }

    /// Represents repository content counter command.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterCommand =
        | AddReference of
            operationId: RepositoryContentCounterOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            logicalSize: int64
        | RemoveReference of
            operationId: RepositoryContentCounterOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterCommand>()

    /// Carries one repair-only positive logical count replacement guarded by an exact actor revision.
    [<GenerateSerializer>]
    type RepositoryContentCounterRepairCommand =
        {
            [<Id(0u)>]
            OperationId: RepositoryContentCounterOperationId
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            StoragePoolId: StoragePoolId
            [<Id(3u)>]
            ManifestAddress: ManifestAddress
            [<Id(4u)>]
            ExpectedRevision: int64
            [<Id(5u)>]
            RebuiltCount: ReferenceCount
        }

    /// Represents repository content counter event type.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterEventType =
        | ReferenceAdded of
            operationId: RepositoryContentCounterOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            logicalSize: int64
        | ReferenceRemoved of operationId: RepositoryContentCounterOperationId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterEventType>()

    /// Represents repository content counter intent.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterIntent =
        | IncrementManifestReferenceCount of
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            counterRevision: int64
        | DecrementManifestReferenceCount of
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            counterRevision: int64

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterIntent>()

    /// Represents the repository content counter event contract.
    [<GenerateSerializer>]
    type RepositoryContentCounterEvent =
        {
            [<Id(0u)>]
            Event: RepositoryContentCounterEventType
            [<Id(1u)>]
            Metadata: EventMetadata
        }

    /// Represents repository content counter dto.
    [<GenerateSerializer>]
    type RepositoryContentCounterDto =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            StoragePoolId: StoragePoolId
            [<Id(3u)>]
            ManifestAddress: ManifestAddress
            [<Id(4u)>]
            Count: ReferenceCount
            [<Id(5u)>]
            Revision: int64
            [<Id(6u)>]
            LastCompletedChange: RepositoryContentCounterCompletedChange option
            /// Retains one exact add until its dependent workflow and Library receipt are durable.
            [<Id(7u)>]
            PendingTrackedAdd: RepositoryContentCounterCompletedChange option
            /// The manifest's logical size, recorded by the first add so every release subtracts the same number.
            [<Id(8u)>]
            LogicalSize: int64
            /// Zero crossings committed with the count that the repository's storage meter has not released yet.
            [<Id(9u)>]
            PendingMeterChanges: RepositoryStorageMeterChange list
        }

        /// Preserves the established read name while the durable snapshot stores the contract's `Count` field.
        member this.ReferenceCount = this.Count

        /// Derives lifecycle from the bounded current count instead of persisting duplicate state.
        member this.LifecycleState =
            if this.Count = 0L then
                RepositoryContentCounterLifecycleState.NotReferenced
            else
                RepositoryContentCounterLifecycleState.Referenced

        /// Projects the latest operation identity for existing callers without persisting another field.
        member this.LastOperationId =
            this.LastCompletedChange
            |> Option.map (fun change -> change.OperationId)

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof RepositoryContentCounterDto
                RepositoryId = RepositoryId.Empty
                StoragePoolId = String.Empty
                ManifestAddress = String.Empty
                Count = 0L
                Revision = 0L
                LastCompletedChange = None
                PendingTrackedAdd = None
                LogicalSize = 0L
                PendingMeterChanges = []
            }

        /// Appends the storage meter change for a count that crossed zero at `revision`, using the recorded size.
        static member WithMeterChangeForCrossing previousCount nextCount revision (counter: RepositoryContentCounterDto) =
            let delta =
                if previousCount = 0L && nextCount > 0L then counter.LogicalSize
                elif previousCount > 0L && nextCount = 0L then -counter.LogicalSize
                else 0L

            if delta = 0L then
                counter
            else
                { counter with
                    PendingMeterChanges =
                        counter.PendingMeterChanges
                        @ [
                            { Revision = revision; LogicalBytesDelta = delta }
                        ]
                }

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto counterEvent current =
            match counterEvent.Event with
            | RepositoryContentCounterEventType.ReferenceAdded (operationId, repositoryId, storagePoolId, manifestAddress, logicalSize) ->
                let nextCount = current.Count + 1L

                { current with
                    RepositoryId =
                        if current.RepositoryId = RepositoryId.Empty then
                            repositoryId
                        else
                            current.RepositoryId
                    StoragePoolId =
                        if String.IsNullOrWhiteSpace current.StoragePoolId then
                            storagePoolId
                        else
                            current.StoragePoolId
                    ManifestAddress =
                        if String.IsNullOrWhiteSpace current.ManifestAddress then
                            manifestAddress
                        else
                            current.ManifestAddress
                    Count = nextCount
                    Revision = current.Revision + 1L
                    LogicalSize = if current.LogicalSize = 0L then logicalSize else current.LogicalSize
                    LastCompletedChange =
                        Some
                            {
                                OperationId = operationId
                                Operation = RepositoryContentCounterChangeOperation.Added
                                PreviousCount = current.Count
                                CurrentCount = nextCount
                                Revision = current.Revision + 1L
                            }
                }
                |> RepositoryContentCounterDto.WithMeterChangeForCrossing current.Count nextCount (current.Revision + 1L)
            | RepositoryContentCounterEventType.ReferenceRemoved operationId ->
                let nextReferenceCount = max 0L (current.Count - 1L)

                { current with
                    Count = nextReferenceCount
                    Revision = current.Revision + 1L
                    LastCompletedChange =
                        Some
                            {
                                OperationId = operationId
                                Operation = RepositoryContentCounterChangeOperation.Removed
                                PreviousCount = current.Count
                                CurrentCount = nextReferenceCount
                                Revision = current.Revision + 1L
                            }
                }
                |> RepositoryContentCounterDto.WithMeterChangeForCrossing current.Count nextReferenceCount (current.Revision + 1L)

    /// Represents repository content counter decision.
    [<GenerateSerializer>]
    type RepositoryContentCounterDecision =
        {
            [<Id(0u)>]
            Counter: RepositoryContentCounterDto
            [<Id(1u)>]
            OperationId: RepositoryContentCounterOperationId
            [<Id(2u)>]
            Events: RepositoryContentCounterEvent list
            [<Id(3u)>]
            Intents: RepositoryContentCounterIntent list
            [<Id(4u)>]
            WasIdempotentReplay: bool
            [<Id(5u)>]
            Message: string
        }
