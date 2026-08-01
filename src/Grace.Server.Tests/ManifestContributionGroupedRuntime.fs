namespace Grace.Server.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Grace.Server.Tests.Services

/// Retains the one fixture-owned Aspire state only while the explicit grouped measurement test invokes accepted leaves.
module internal ManifestContributionGroupedRuntime =

    let mutable private groupedState: TestHostState option = None
    let mutable private groupedBootstrapUserId: string option = None
    let private repositories = Dictionary<string, string>(StringComparer.Ordinal)
    let private assetIndexOffsets = Dictionary<string, int>(StringComparer.Ordinal)

    /// Starts the single grouped Aspire session before any accepted leaf scenario executes.
    let beginSessionAsync bootstrapUserId =
        task {
            if groupedState.IsSome then
                invalidOp "A grouped manifest-contribution session is already active."

            let! state = AspireTestHost.startIsolatedAsync bootstrapUserId
            repositories.Clear()
            assetIndexOffsets.Clear()
            groupedState <- Some state
            groupedBootstrapUserId <- Some bootstrapUserId
            state.Client.DefaultRequestHeaders.Add("x-grace-user-id", bootstrapUserId)
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
                groupedBootstrapUserId <- None
                do! AspireTestHost.stopIsolatedAsync state
        }

    /// Retains the startup-granted grouped identity while preserving each standalone leaf's selected identity.
    let selectBootstrapUser (state: TestHostState) (bootstrapUserId: string) =
        let selectedUserId =
            match groupedState, groupedBootstrapUserId with
            | Some active, Some groupedUserId when Object.ReferenceEquals(active, state) -> groupedUserId
            | _ -> bootstrapUserId

        state.Client.DefaultRequestHeaders.Remove("x-grace-user-id")
        |> ignore

        state.Client.DefaultRequestHeaders.Add("x-grace-user-id", selectedUserId)

    /// Records the scenario-local Repository identity and reserves its grouped physical-asset namespace.
    let registerRepository scenarioId repositoryId =
        let repositoryKey = string repositoryId
        repositories[scenarioId] <- repositoryKey

        if
            groupedState.IsSome
            && not (assetIndexOffsets.ContainsKey repositoryKey)
        then
            assetIndexOffsets[repositoryKey] <- (assetIndexOffsets.Count + 1) * 10_000

    /// Keeps deterministic leaf asset indexes unchanged outside grouped execution and disjoint within it.
    let selectAssetIndex repositoryId index =
        match assetIndexOffsets.TryGetValue(string repositoryId) with
        | true, offset when groupedState.IsSome -> offset + index
        | _ -> index

    /// Returns the exact scenario-to-Repository registrations accumulated by the active grouped session.
    let registeredRepositories () =
        repositories
        |> Seq.map (fun pair -> pair.Key, pair.Value)
        |> Seq.toArray
