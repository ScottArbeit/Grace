namespace Grace

open Grace.SDK
open Grace.Shared
open Grace.Shared.Parameters
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Collections.Concurrent
open System.Security.Cryptography
open FSharpPlus.Data.NonEmptySeq

module Load =

    let numberOfRepositories = 100
    let numberOfBranches = 800
    let numberOfEvents = 100000

    let showResult<'T> (r: GraceResult<'T>) =
        match r with
        | Ok result -> () //logToConsole (sprintf "%s - CorrelationId: %s" (result.Properties["EventType"]) result.CorrelationId)
        | Error error -> logToConsole $"{error}"

    let parallelOptions = ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount * 4)

    [<EntryPoint>]
    let main args =
        (task {
            let startTime = getCurrentInstant ()
            let cancellationToken = new CancellationToken()

            let suffixes = ConcurrentDictionary<int, string>()

            for i in { 0 .. Math.Max(numberOfRepositories, numberOfBranches) } do
                suffixes[i] <- Random.Shared.Next(Int32.MaxValue).ToString("X8")

            let repositoryIds = ConcurrentDictionary<int, RepositoryId>()
            let parentBranchIds = ConcurrentDictionary<int, BranchId>()

            let ids = ConcurrentDictionary<int, OwnerId * OrganizationId * RepositoryId * BranchId>()

            let ownerId = Guid.NewGuid()
            let ownerName = $"Owner{suffixes[0]}"
            let organizationId = Guid.NewGuid()
            let organizationName = $"Organization{suffixes[0]}"

            let! r = Owner.Create(Owner.CreateOwnerParameters(OwnerId = $"{ownerId}", OwnerName = ownerName, CorrelationId = generateCorrelationId ()))

            showResult r

            let! r =
                Organization.Create(
                    Organization.CreateOrganizationParameters(
                        OwnerId = $"{ownerId}",
                        OrganizationId = $"{organizationId}",
                        OrganizationName = organizationName,
                        CorrelationId = generateCorrelationId ()
                    )
                )

            showResult r

            do!
                Parallel.ForEachAsync(
                    { 0 .. (numberOfRepositories - 1) },
                    parallelOptions,
                    (fun (i: int) (cancellationToken: CancellationToken) ->
                        ValueTask(
                            task {
                                do! Task.Delay(Random.Shared.Next(25))
                                let repositoryId = Guid.NewGuid()
                                let repositoryName = $"Repository{suffixes[i]}"

                                let! repo =
                                    Repository.Create(
                                        Repository.CreateRepositoryParameters(
                                            OwnerId = $"{ownerId}",
                                            OrganizationId = $"{organizationId}",
                                            RepositoryId = $"{repositoryId}",
                                            RepositoryName = repositoryName,
                                            CorrelationId = generateCorrelationId ()
                                        )
                                    )

                                repositoryIds.AddOrUpdate(i, repositoryId, (fun _ _ -> repositoryId)) |> ignore

                                match repo with
                                | Ok r ->
                                    logToConsole $"Added repository {i}; repositoryId: {repositoryId}; repositoryName: {repositoryName}."

                                    let! mainBranch =
                                        Branch.Get(
                                            Branch.GetBranchParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchName = Constants.InitialBranchName,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    match mainBranch with
                                    | Ok branch ->
                                        logToConsole $"Adding parentBranchId {i}; branch.ReturnValue.BranchId: {branch.ReturnValue.BranchId}."

                                        parentBranchIds.AddOrUpdate(i, branch.ReturnValue.BranchId, (fun _ _ -> branch.ReturnValue.BranchId))
                                        |> ignore
                                    | Error error -> logToConsole $"Error getting main: {error}"
                                | Error error -> logToConsole $"Error creating repository: {error}"

                                showResult repo
                            }
                        ))
                )

            do!
                Parallel.ForEachAsync(
                    { 0 .. (numberOfBranches - 1) },
                    parallelOptions,
                    (fun (i: int) (cancellationToken: CancellationToken) ->
                        ValueTask(
                            task {
                                do! Task.Delay(Random.Shared.Next(50))
                                let branchId = Guid.NewGuid()
                                let branchName = $"Branch{suffixes[i]}"
                                let repositoryIndex = Random.Shared.Next(repositoryIds.Count)
                                let repositoryId = repositoryIds[repositoryIndex]
                                let parentBranchId = parentBranchIds[repositoryIndex]

                                try
                                    let! r =
                                        Branch.Create(
                                            Branch.CreateBranchParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                BranchName = branchName,
                                                ParentBranchId = $"{parentBranchId}",
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r

                                    match r with
                                    | Ok r ->
                                        ids.AddOrUpdate(
                                            i,
                                            (ownerId, organizationId, repositoryId, branchId),
                                            (fun _ _ -> (ownerId, organizationId, repositoryId, branchId))
                                        )
                                        |> ignore

                                        logToConsole $"Added to ids: {(ownerId, organizationId, repositoryId, branchId)}; Count: {ids.Count}."
                                    | Error error -> logToConsole $"Error creating branch {i}: {error}."
                                with ex ->
                                    logToConsole $"i: {i}; exception; repositoryId: {repositoryId}; parentBranchId: {parentBranchId}."
                            }
                        ))
                )

            let setupTime = getCurrentInstant ()
            logToConsole $"Setup complete. numberOfRepositories: {numberOfRepositories}; numberOfBranches: {numberOfBranches}; ids.Count: {ids.Count}."

            do!
                Parallel.ForEachAsync(
                    { 0 .. (numberOfEvents - 1) },
                    parallelOptions,
                    (fun (i: int) (cancellationToken: CancellationToken) ->
                        ValueTask(
                            task {
                                if i % 250 = 0 then
                                    logToConsole
                                        $"Processing event {i} of {numberOfEvents}; Transactions/sec: {float i / (getCurrentInstant () - setupTime).TotalSeconds:F3}"

                                let rnd = Random.Shared.Next(ids.Count)
                                let (ownerId, organizationId, repositoryId, branchId) = ids[rnd]
                                let sha256 = SHA256.Create()
                                let byteArray = Array.init 64 (fun _ -> byte (Random.Shared.Next(256)))
                                let sha256Hash = byteArrayToString (sha256.ComputeHash(byteArray).AsSpan())

                                match Random.Shared.Next(0, 13) with
                                | 0 ->
                                    let! r =
                                        Branch.Save(
                                            Branch.CreateReferenceParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                Message = $"Save - {DateTime.UtcNow}",
                                                DirectoryVersionId = Guid.NewGuid(),
                                                Sha256Hash = sha256Hash,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 1 ->
                                    let! r =
                                        Branch.Checkpoint(
                                            Branch.CreateReferenceParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                Message = $"Checkpoint - {DateTime.UtcNow}",
                                                DirectoryVersionId = Guid.NewGuid(),
                                                Sha256Hash = sha256Hash,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 2 ->
                                    let! r =
                                        Branch.Commit(
                                            Branch.CreateReferenceParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                Message = $"Commit - {DateTime.UtcNow}",
                                                DirectoryVersionId = Guid.NewGuid(),
                                                Sha256Hash = sha256Hash,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 3 ->
                                    let! r =
                                        Branch.Tag(
                                            Branch.CreateReferenceParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                Message = $"Tag - {DateTime.UtcNow}",
                                                DirectoryVersionId = Guid.NewGuid(),
                                                Sha256Hash = sha256Hash,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 4 ->
                                    let! r =
                                        Branch.Get(
                                            Branch.GetBranchParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 5 ->
                                    let! r =
                                        Branch.GetReferences(
                                            Branch.GetReferencesParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                MaxCount = 30,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 6 ->
                                    let! r =
                                        Branch.GetCommits(
                                            Branch.GetReferencesParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                MaxCount = 30,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 7 ->
                                    let! r =
                                        Branch.GetTags(
                                            Branch.GetReferencesParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                MaxCount = 30,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 8 ->
                                    let! r =
                                        Repository.GetBranches(
                                            Repository.GetBranchesParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                MaxCount = 30,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 9 ->
                                    let! r =
                                        Repository.Get(
                                            Repository.GetRepositoryParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 10 ->
                                    let! r =
                                        Repository.GetBranchesByBranchId(
                                            Repository.GetBranchesByBranchIdParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchIds = [| branchId |],
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 11 ->
                                    let! r =
                                        Branch.GetCheckpoints(
                                            Branch.GetReferencesParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                MaxCount = 30,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | 12 ->
                                    let! r =
                                        Branch.GetSaves(
                                            Branch.GetReferencesParameters(
                                                OwnerId = $"{ownerId}",
                                                OrganizationId = $"{organizationId}",
                                                RepositoryId = $"{repositoryId}",
                                                BranchId = $"{branchId}",
                                                MaxCount = 30,
                                                CorrelationId = generateCorrelationId ()
                                            )
                                        )

                                    showResult r
                                | _ -> ()
                            }
                        ))
                )

            let mainProcessingTime = getCurrentInstant ()
            logToConsole "Main processing complete."
            logToConsole $"Processing time:  {mainProcessingTime - setupTime}."
            logToConsole $"Transactions/sec: {float numberOfEvents / (mainProcessingTime - setupTime).TotalSeconds}"
            logToConsole "Starting tear down."

            // Tear down
            let deleteParallelOptions = ParallelOptions(MaxDegreeOfParallelism = 8)
            let mutable deleteCount = 0

            do!
                Parallel.ForEachAsync(
                    ids.Values,
                    deleteParallelOptions,
                    (fun id (cancellationToken: CancellationToken) ->
                        ValueTask(
                            task {
                                let (ownerId, organizationId, repositoryId, branchId) = id

                                let! result =
                                    Branch.Delete(
                                        Branch.DeleteBranchParameters(
                                            OwnerId = $"{ownerId}",
                                            OrganizationId = $"{organizationId}",
                                            RepositoryId = $"{repositoryId}",
                                            BranchId = $"{branchId}",
                                            CorrelationId = generateCorrelationId ()
                                        )
                                    )

                                showResult result
                                let deleteCount = Interlocked.Increment(&deleteCount)

                                if deleteCount % 100 = 0 then
                                    logToConsole $"Deleted {deleteCount} of {ids.Count} branches."
                            //do! Task.Delay(Random.Shared.Next(100))
                            }
                        ))
                )

            deleteCount <- 0

            do!
                Parallel.ForEachAsync(
                    ids.Values
                        .Select(fun (ownerId, organizationId, repositoryId, branchId) -> (ownerId, organizationId, repositoryId))
                        .Distinct(),
                    deleteParallelOptions,
                    (fun id (cancellationToken: CancellationToken) ->
                        ValueTask(
                            task {
                                let (ownerId, organizationId, repositoryId) = id

                                let! result =
                                    Repository.Delete(
                                        Repository.DeleteRepositoryParameters(
                                            OwnerId = $"{ownerId}",
                                            OrganizationId = $"{organizationId}",
                                            RepositoryId = $"{repositoryId}",
                                            DeleteReason = "performance test",
                                            CorrelationId = generateCorrelationId ()
                                        )
                                    )

                                showResult result
                                let deleteCount = Interlocked.Increment(&deleteCount)

                                if deleteCount % 100 = 0 then
                                    logToConsole $"Deleted {deleteCount} of {numberOfRepositories} repositories."

                            //do! Task.Delay(Random.Shared.Next(50))
                            }
                        ))
                )

            let! r =
                Organization.Delete(
                    Organization.DeleteOrganizationParameters(
                        OwnerId = $"{ownerId}",
                        OrganizationId = $"{organizationId}",
                        DeleteReason = "performance test",
                        CorrelationId = generateCorrelationId ()
                    )
                )

            showResult r

            let! r =
                Owner.Delete(Owner.DeleteOwnerParameters(OwnerId = $"{ownerId}", DeleteReason = "performance test", CorrelationId = generateCorrelationId ()))

            showResult r

            let endTime = getCurrentInstant ()
            logToConsole "Tear down complete."

            printfn $"Number of events: {numberOfEvents}."
            printfn $"Setup time:       {setupTime - startTime}."
            printfn $"Processing time:  {mainProcessingTime - setupTime}."
            printfn $"Tear down:        {endTime - mainProcessingTime}."
            printfn $"Total:            {endTime - startTime}."
            printfn $"Transactions/sec: {float numberOfEvents / (mainProcessingTime - setupTime).TotalSeconds}"
            return 0
        })
            .Result
