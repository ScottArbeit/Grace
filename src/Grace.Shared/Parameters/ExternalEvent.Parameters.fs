namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module ExternalEvent =

    /// Base parameters for canonical external-event admin endpoints.
    type ExternalEventParameters() =
        inherit CommonParameters()

    /// Parameters for resending a canonical external event by its event ID.
    type ResendExternalEventParameters() =
        inherit ExternalEventParameters()
        /// The canonical external event ID to rebuild and resend.
        member val public EventId = String.Empty with get, set
