namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Common
open Orleans
open System
open System.Collections.Generic
open System.Runtime.Serialization

module ManifestContributionWorkflow =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionDirection =
        | Increment
        | Decrement

        static member GetKnownTypes() = GetKnownTypes<ManifestContributionDirection>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowLifecycleState =
        | NotStarted
        | InProgress
        | Completed

        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowLifecycleState>()

    [<CLIMutable; GenerateSerializer; CustomComparison; CustomEquality>]
    type ManifestContributionWorkflowRange =
        {
            StoragePoolId: StoragePoolId
            ContentBlockAddress: ContentBlockAddress
            OrdinalStart: int
            OrdinalCount: int
        }

        interface IComparable with
            member this.CompareTo other =
                match other with
                | :? ManifestContributionWorkflowRange as otherRange ->
                    compare
                        (this.StoragePoolId, this.ContentBlockAddress, this.OrdinalStart, this.OrdinalCount)
                        (otherRange.StoragePoolId, otherRange.ContentBlockAddress, otherRange.OrdinalStart, otherRange.OrdinalCount)
                | _ -> invalidArg (nameof other) "Cannot compare ManifestContributionWorkflowRange with a different type."

        override this.Equals(other: obj) =
            match other with
            | :? ManifestContributionWorkflowRange as otherRange ->
                this.StoragePoolId = otherRange.StoragePoolId
                && this.ContentBlockAddress = otherRange.ContentBlockAddress
                && this.OrdinalStart = otherRange.OrdinalStart
                && this.OrdinalCount = otherRange.OrdinalCount
            | _ -> false

        override this.GetHashCode() = HashCode.Combine(this.StoragePoolId, this.ContentBlockAddress, this.OrdinalStart, this.OrdinalCount)

    [<GenerateSerializer>]
    type StartManifestContributionWorkflow =
        {
            OperationId: ManifestContributionWorkflowOperationId
            RepositoryId: RepositoryId
            ManifestAddress: ManifestAddress
            Direction: ManifestContributionDirection
            Ranges: ManifestContributionWorkflowRange array
        }

    [<GenerateSerializer>]
    type ManifestContributionWorkflowRangeProgress =
        {
            OperationId: ManifestContributionWorkflowOperationId
            RepositoryId: RepositoryId
            ManifestAddress: ManifestAddress
            Range: ManifestContributionWorkflowRange
        }

    [<GenerateSerializer>]
    type ManifestContributionWorkflowRangeFailure =
        {
            OperationId: ManifestContributionWorkflowOperationId
            RepositoryId: RepositoryId
            ManifestAddress: ManifestAddress
            Range: ManifestContributionWorkflowRange
            Message: string
        }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowCommand =
        | Start of StartManifestContributionWorkflow
        | RecordRangeSucceeded of ManifestContributionWorkflowRangeProgress
        | RecordRangeFailed of ManifestContributionWorkflowRangeFailure

        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowCommand>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowEventType =
        | WorkflowStarted of start: StartManifestContributionWorkflow
        | RangeSucceeded of ManifestContributionWorkflowRangeProgress
        | RangeFailed of ManifestContributionWorkflowRangeFailure

        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowEventType>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ManifestContributionWorkflowIntent =
        | AdjustRangeActiveManifestCount of range: ManifestContributionWorkflowRange * delta: int

        static member GetKnownTypes() = GetKnownTypes<ManifestContributionWorkflowIntent>()

    [<GenerateSerializer>]
    type ManifestContributionWorkflowEvent = { Event: ManifestContributionWorkflowEventType; Metadata: EventMetadata }

    [<GenerateSerializer>]
    type ManifestContributionWorkflowDto =
        {
            Class: string
            RepositoryId: RepositoryId
            ManifestAddress: ManifestAddress
            Direction: ManifestContributionDirection
            Ranges: ManifestContributionWorkflowRange array
            CompletedRanges: Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>
            FailedRanges: Dictionary<ManifestContributionWorkflowRange, string>
            LifecycleState: ManifestContributionWorkflowLifecycleState
            LastOperationId: ManifestContributionWorkflowOperationId option
        }

        static member Default =
            {
                Class = nameof ManifestContributionWorkflowDto
                RepositoryId = RepositoryId.Empty
                ManifestAddress = String.Empty
                Direction = ManifestContributionDirection.Increment
                Ranges = Array.empty
                CompletedRanges = Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>()
                FailedRanges = Dictionary<ManifestContributionWorkflowRange, string>()
                LifecycleState = ManifestContributionWorkflowLifecycleState.NotStarted
                LastOperationId = None
            }

        static member private Lifecycle ranges completedRanges =
            if Array.isEmpty ranges then
                ManifestContributionWorkflowLifecycleState.NotStarted
            elif completedRanges = Array.length ranges then
                ManifestContributionWorkflowLifecycleState.Completed
            else
                ManifestContributionWorkflowLifecycleState.InProgress

        static member private CloneCompleted(completedRanges: Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>) =
            Dictionary<ManifestContributionWorkflowRange, ManifestContributionWorkflowOperationId>(completedRanges)

        static member private CloneFailed(failedRanges: Dictionary<ManifestContributionWorkflowRange, string>) =
            Dictionary<ManifestContributionWorkflowRange, string>(failedRanges)

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
