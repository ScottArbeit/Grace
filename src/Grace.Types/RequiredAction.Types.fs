namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Collections.Generic
open System.Runtime.Serialization

module RequiredAction =
    /// The taxonomy for required actions.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RequiredActionType =
        | AcknowledgePolicyChange
        | ProvideHumanApproval
        | ResolveFinding
        | RunGate
        | FixGateFailure
        | ResolveConflict
        | ReAckDueToBaselineDrift
        | ProvideMigrationNotes

        static member GetKnownTypes() = GetKnownTypes<RequiredActionType>()

    /// Machine-readable required action description.
    [<GenerateSerializer>]
    type RequiredActionDto =
        {
            RequiredActionType: RequiredActionType
            TargetId: string option
            Reason: string
            Parameters: Dictionary<string, string>
            SuggestedCliCommand: string option
            SuggestedApiCall: string option
        }
