namespace Grace.Actors

open Grace.Types.Library
open System.Threading.Tasks

/// Applies the failure-only Library wake envelope protocol in its durable effect order.
module LibraryNotifications =

    /// Sends one envelope, retaining it only after terminal send failure and advancing before clearing recovered state.
    let attempt send advance persist clear hasRetained (envelope: FailedGraceEventEnvelope) =
        task {
            let! sent =
                task {
                    try
                        do! send envelope
                        return true
                    with
                    | _ -> return false
                }

            if sent then
                do! advance ()

                if hasRetained then do! clear ()

                return true
            else
                if not hasRetained then do! persist envelope

                return false
        }
