namespace Grace.Actors

open System
open System.Threading.Tasks

/// Starts Library wakes without letting them affect a change that is already committed. A wake is advisory: clients
/// poll anyway, so a wake that fails or never finishes costs only latency, never a change.
module LibraryWake =

    /// Names the test-only setting that makes every wake for one repository fail, so hosted tests can prove a broker
    /// failure never reaches the caller. It applies only while GRACE_TESTING is "1".
    [<Literal>]
    let FaultRepositoryIdVariable = "GRACE_TEST_LIBRARY_WAKE_FAULT_REPOSITORY_ID"

    /// Returns the repository whose wakes a hosted test asked to fail, if any.
    let private faultRepositoryId =
        lazy
            (if Environment.GetEnvironmentVariable("GRACE_TESTING") = "1" then
                 match Guid.TryParse(Environment.GetEnvironmentVariable FaultRepositoryIdVariable) with
                 | true, repositoryId -> Some repositoryId
                 | _ -> None
             else
                 None)

    /// Reports whether a hosted test asked wakes for this repository to fail.
    let shouldFail (repositoryId: Guid) = faultRepositoryId.Value = Some repositoryId

    /// Starts one send and returns at once, without waiting for it. A failure, including one thrown before the send's
    /// first await, goes to onFailure and never to the caller. If reporting the failure fails too, that is dropped.
    let start (send: unit -> Task) (onFailure: exn -> unit) =
        let sending =
            task {
                try
                    do! send ()
                with
                | ex ->
                    try
                        onFailure ex
                    with
                    | _ -> ()
            }

        sending |> ignore
