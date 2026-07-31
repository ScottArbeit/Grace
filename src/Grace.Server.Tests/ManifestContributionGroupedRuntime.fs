namespace Grace.Server.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Grace.Server.Tests.Services

/// Retains the one fixture-owned Aspire state only while the explicit grouped measurement test invokes accepted leaves.
module internal ManifestContributionGroupedRuntime =

    let mutable private groupedState: TestHostState option = None
    let private repositories = Dictionary<string, string>(StringComparer.Ordinal)

    /// Starts the single grouped Aspire session before any accepted leaf scenario executes.
    let beginSessionAsync bootstrapUserId =
        task {
            if groupedState.IsSome then
                invalidOp "A grouped manifest-contribution session is already active."

            let! state = AspireTestHost.startIsolatedAsync bootstrapUserId
            repositories.Clear()
            groupedState <- Some state
            return state
        }

    /// Returns the grouped session when active, otherwise preserving the standalone leaf startup contract.
    let acquireAsync bootstrapUserId =
        task {
            match groupedState with
            | Some state -> return state
            | None -> return! AspireTestHost.startIsolatedAsync bootstrapUserId
        }

    /// Retains the grouped session between leaves and preserves standalone leaf cleanup otherwise.
    let releaseAsync (state: TestHostState) =
        task {
            match groupedState with
            | Some active when Object.ReferenceEquals(active, state) -> ()
            | _ -> do! AspireTestHost.stopIsolatedAsync state
        }

    /// Stops the grouped session exactly once and clears it even when teardown fails.
    let endSessionAsync () =
        task {
            match groupedState with
            | None -> ()
            | Some state ->
                groupedState <- None
                do! AspireTestHost.stopIsolatedAsync state
        }

    /// Replaces the selected-process test identity before each scenario-local namespace is created.
    let selectBootstrapUser (state: TestHostState) (bootstrapUserId: string) =
        state.Client.DefaultRequestHeaders.Remove("x-grace-user-id")
        |> ignore

        state.Client.DefaultRequestHeaders.Add("x-grace-user-id", bootstrapUserId)

    /// Records the scenario-local Repository identity exposed by an accepted leaf fixture.
    let registerRepository scenarioId repositoryId = repositories[scenarioId] <- string repositoryId

    /// Returns the exact scenario-to-Repository registrations accumulated by the active grouped session.
    let registeredRepositories () =
        repositories
        |> Seq.map (fun pair -> pair.Key, pair.Value)
        |> Seq.toArray
