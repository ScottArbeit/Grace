namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Common
open Orleans
open System
open System.Collections.Generic
open System.Runtime.Serialization

/// Contains manifest contribution workflow helpers.
module ManifestContributionWorkflow =

    /// Represents manifest contribution direction.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionDirection =
        | Increment
        | Decrement

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ManifestContributionDirection>()

    /// Represents manifest contribution workflow lifecycle state.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowLifecycleState =
        | NotStarted
        | InProgress
        | Completed

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowLifecycleState>()

    /// Represents manifest contribution workflow range.
    [<CLIMutable; GenerateSerializer; CustomComparison; CustomEquality>]
    type ManifestContributionWorkflowRange =
        {
            [<Id(0u)>]
            StoragePoolId: StoragePoolId

            [<Id(1u)>]
            ContentBlockAddress: ContentBlockAddress

            [<Id(2u)>]
            OrdinalStart: int

            [<Id(3u)>]
            OrdinalCount: int
        }

        interface IComparable with
            /// Orders values by their stable identity string so sorted collections remain deterministic.
            member this.CompareTo other =
                match other with
                | :? ManifestContributionWorkflowRange as otherRange ->
                    compare
                        (this.StoragePoolId, this.ContentBlockAddress, this.OrdinalStart, this.OrdinalCount)
                        (otherRange.StoragePoolId, otherRange.ContentBlockAddress, otherRange.OrdinalStart, otherRange.OrdinalCount)
                | _ -> invalidArg (nameof other) "Cannot compare ManifestContributionWorkflowRange with a different type."

        /// Compares the domain identity fields that define whether two values refer to the same Grace object.
        override this.Equals(other: obj) =
            match other with
            | :? ManifestContributionWorkflowRange as otherRange ->
                this.StoragePoolId = otherRange.StoragePoolId
                && this.ContentBlockAddress = otherRange.ContentBlockAddress
                && this.OrdinalStart = otherRange.OrdinalStart
                && this.OrdinalCount = otherRange.OrdinalCount
            | _ -> false

        /// Computes a hash code from the same domain identity fields used by equality.
        override this.GetHashCode() = HashCode.Combine(this.StoragePoolId, this.ContentBlockAddress, this.OrdinalStart, this.OrdinalCount)

    /// Represents start manifest contribution workflow.
    [<GenerateSerializer>]
    type StartManifestContributionWorkflow =
        {
            [<Id(0u)>]
            OperationId: ManifestContributionWorkflowOperationId

            [<Id(1u)>]
            RepositoryId: RepositoryId

            [<Id(2u)>]
            StoragePoolId: StoragePoolId

            [<Id(3u)>]
            ManifestAddress: ManifestAddress

            [<Id(4u)>]
            Direction: ManifestContributionDirection

            [<Id(5u)>]
            Ranges: ManifestContributionWorkflowRange array
        }

    /// Represents manifest contribution workflow range progress.
    [<GenerateSerializer>]
    type ManifestContributionWorkflowRangeProgress =
        {
            [<Id(0u)>]
            OperationId: ManifestContributionWorkflowOperationId

            [<Id(1u)>]
            RepositoryId: RepositoryId

            [<Id(2u)>]
            StoragePoolId: StoragePoolId

            [<Id(3u)>]
            ManifestAddress: ManifestAddress

            [<Id(4u)>]
            Range: ManifestContributionWorkflowRange
        }

    /// Represents manifest contribution workflow range failure.
    [<GenerateSerializer>]
    type ManifestContributionWorkflowRangeFailure =
        {
            [<Id(0u)>]
            OperationId: ManifestContributionWorkflowOperationId

            [<Id(1u)>]
            RepositoryId: RepositoryId

            [<Id(2u)>]
            StoragePoolId: StoragePoolId

            [<Id(3u)>]
            ManifestAddress: ManifestAddress

            [<Id(4u)>]
            Range: ManifestContributionWorkflowRange

            [<Id(5u)>]
            Message: string
        }

    /// Represents manifest contribution workflow command.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowCommand =
        | Start of
            operationId: ManifestContributionWorkflowOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            direction: ManifestContributionDirection *
            ranges: ManifestContributionWorkflowRange array
        | RecordRangeSucceeded of
            operationId: ManifestContributionWorkflowOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            range: ManifestContributionWorkflowRange
        | RecordRangeFailed of
            operationId: ManifestContributionWorkflowOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            range: ManifestContributionWorkflowRange *
            message: string

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowCommand>()

    /// Represents manifest contribution workflow event type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowEventType =
        | WorkflowStarted of start: StartManifestContributionWorkflow
        | RangeSucceeded of ManifestContributionWorkflowRangeProgress
        | RangeFailed of ManifestContributionWorkflowRangeFailure

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowEventType>()

    /// Represents manifest contribution workflow intent.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowIntent =
        | AdjustRangeActiveManifestCount of range: ManifestContributionWorkflowRange * delta: int

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowIntent>()

    /// Represents the manifest contribution workflow event contract.
    [<GenerateSerializer>]
    type ManifestContributionWorkflowEvent = { Event: ManifestContributionWorkflowEventType; Metadata: EventMetadata }

    /// Represents manifest contribution workflow dto.
    [<GenerateSerializer>]
    type ManifestContributionWorkflowDto =
        {
            Class: string
            RepositoryId: RepositoryId
            StoragePoolId: StoragePoolId
            ManifestAddress: ManifestAddress
            Direction: ManifestContributionDirection
            Ranges: ManifestContributionWorkflowRange array
            CompletedRanges: Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>
            FailedRanges: Dictionary<ManifestContributionWorkflowRange, string>
            LifecycleState: ManifestContributionWorkflowLifecycleState
            LastOperationId: ManifestContributionWorkflowOperationId option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ManifestContributionWorkflowDto
                RepositoryId = RepositoryId.Empty
                StoragePoolId = String.Empty
                ManifestAddress = String.Empty
                Direction = ManifestContributionDirection.Increment
                Ranges = Array.empty
                CompletedRanges = Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>()
                FailedRanges = Dictionary<ManifestContributionWorkflowRange, string>()
                LifecycleState = ManifestContributionWorkflowLifecycleState.NotStarted
                LastOperationId = None
            }

        /// Summarizes the workflow timestamps that describe clone and promotion progress.
        static member private Lifecycle ranges completedRanges =
            if Array.isEmpty ranges then
                ManifestContributionWorkflowLifecycleState.NotStarted
            elif completedRanges = Array.length ranges then
                ManifestContributionWorkflowLifecycleState.Completed
            else
                ManifestContributionWorkflowLifecycleState.InProgress

        /// Marks the workflow clone phase as completed with its completion timestamp.
        static member private CloneCompleted(completedRanges: Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>) =
            Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>(completedRanges)

        /// Marks the workflow clone phase as failed while retaining the failure reason.
        static member private CloneFailed(failedRanges: Dictionary<ManifestContributionWorkflowRange, string>) =
            Dictionary<ManifestContributionWorkflowRange, string>(failedRanges)

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto workflowEvent current =
            match workflowEvent.Event with
            | ManifestContributionWorkflowEventType.WorkflowStarted start ->
                let lifecycle =
                    if Array.isEmpty start.Ranges then
                        ManifestContributionWorkflowLifecycleState.Completed
                    else
                        ManifestContributionWorkflowLifecycleState.InProgress

                { current with
                    RepositoryId = start.RepositoryId
                    StoragePoolId = start.StoragePoolId
                    ManifestAddress = start.ManifestAddress
                    Direction = start.Direction
                    Ranges = Array.copy start.Ranges
                    CompletedRanges = Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>()
                    FailedRanges = Dictionary<ManifestContributionWorkflowRange, string>()
                    LifecycleState = lifecycle
                    LastOperationId = Some start.OperationId
                }
            | ManifestContributionWorkflowEventType.RangeSucceeded progress ->
                let completed = ManifestContributionWorkflowDto.CloneCompleted current.CompletedRanges
                completed[progress.Range] <- progress.OperationId

                let failed = ManifestContributionWorkflowDto.CloneFailed current.FailedRanges
                failed.Remove(progress.Range) |> ignore

                { current with
                    CompletedRanges = completed
                    FailedRanges = failed
                    LifecycleState = ManifestContributionWorkflowDto.Lifecycle current.Ranges completed.Count
                    LastOperationId = Some progress.OperationId
                }
            | ManifestContributionWorkflowEventType.RangeFailed failure ->
                let failed = ManifestContributionWorkflowDto.CloneFailed current.FailedRanges
                failed[failure.Range] <- failure.Message

                { current with
                    FailedRanges = failed
                    LifecycleState = ManifestContributionWorkflowDto.Lifecycle current.Ranges current.CompletedRanges.Count
                    LastOperationId = Some failure.OperationId
                }

    /// Represents manifest contribution workflow decision.
    [<GenerateSerializer>]
    type ManifestContributionWorkflowDecision =
        {
            Workflow: ManifestContributionWorkflowDto
            OperationId: ManifestContributionWorkflowOperationId
            Events: ManifestContributionWorkflowEvent list
            Intents: ManifestContributionWorkflowIntent list
            WasIdempotentReplay: bool
            Message: string
        }
