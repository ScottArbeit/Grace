namespace Grace.Actors

open System
open System.Threading.Tasks

/// Starts Library wakes without letting them affect a change that is already committed. A wake is advisory: clients
/// poll anyway, so a wake that fails or never finishes costs only latency, never a change.
module LibraryWake =

    /// Names the test-only setting that makes every wake for one repository never finish, so hosted tests can prove a
    /// change never waits for its wake. It applies only while GRACE_TESTING is "1".
    [<Literal>]
    let FaultRepositoryIdVariable = "GRACE_TEST_LIBRARY_WAKE_STALL_REPOSITORY_ID"

    /// Returns the repository whose wakes a hosted test asked to fail, if any.
    let private faultRepositoryId =
        lazy
            (if Environment.GetEnvironmentVariable("GRACE_TESTING") = "1" then
                 match Guid.TryParse(Environment.GetEnvironmentVariable FaultRepositoryIdVariable) with
                 | true, repositoryId -> Some repositoryId
                 | _ -> None
             else
                 None)

    /// Reports whether a hosted test asked wakes for this repository to never finish.
    let shouldStall (repositoryId: Guid) = faultRepositoryId.Value = Some repositoryId

    /// A wake that never finishes, for the hosted stall test.
    let stalled () : Task = TaskCompletionSource().Task

    /// Starts one send on the thread pool and returns at once. Nothing runs on the caller's scheduler, so neither building
    /// the message, a slow client start nor logging a failure can hold up an Orleans grain turn. A failure, including one
    /// thrown before the send's first await, goes to onFailure and never to the caller. If reporting the failure fails
    /// too, that is dropped.
    let start (send: unit -> Task) (onFailure: exn -> unit) =
        Task.Run(
            Func<Task> (fun () ->
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
                :> Task)
        )
        |> ignore
