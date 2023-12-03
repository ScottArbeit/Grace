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

module Load =

    let numberOfRepositories = 500
    let numberOfEvents = 5000

    let showResult<'T> (r: GraceResult<'T>) =
        match r with
        | Ok result -> () //logToConsole (sprintf "%s - CorrelationId: %s" (result.Properties["EventType"]) result.CorrelationId)
        | Error error -> logToConsole $"{error}"
    
    let g() = $"{Guid.NewGuid()}"
    let ParallelOptions = ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount * 2)

    [<EntryPoint>]
    let main args =
        (task {
            //ThreadPool.SetMinThreads(100, 100) |> ignore
            let startTime = getCurrentInstant()
            let cancellationToken = new CancellationToken()

            let suffixes = ConcurrentDictionary<int, string>()
            for i in {0..numberOfRepositories} do
                suffixes[i] <- Random.Shared.Next(Int32.MaxValue).ToString("X8")

            let repositoryIds = ConcurrentDictionary<int, RepositoryId>()
            let parentBranchIds = ConcurrentDictionary<int, BranchId>()
            let ids = ConcurrentDictionary<int, OwnerId * OrganizationId * RepositoryId * BranchId>()

            let ownerId = Guid.NewGuid()
            let ownerName = $"Owner{suffixes[0]}"
            let organizationId = Guid.NewGuid()
            let organizationName = $"Organization{suffixes[0]}"
            
            let! r = Owner.Create(Owner.CreateOwnerParameters(OwnerId = $"{ownerId}", OwnerName = ownerName, CorrelationId = g()))
            showResult r

            let! r = Organization.Create(Organization.CreateOrganizationParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", OrganizationName = organizationName, CorrelationId = g()))
            showResult r

            do! Parallel.ForEachAsync({0..numberOfRepositories}, ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    do! Task.Delay(Random.Shared.Next(25))
                    let repositoryId = Guid.NewGuid()
                    let repositoryName = $"Repository{suffixes[i]}"
                    let! repo = Repository.Create(Repository.CreateRepositoryParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", RepositoryName = repositoryName, CorrelationId = g()))
                    repositoryIds.AddOrUpdate(i, repositoryId, (fun _ _ -> repositoryId)) |> ignore
                    match repo with
                    | Ok r ->
                        logToConsole $"Adding parentBranchId {i}; r.Properties[nameof(BranchId)]: {r.Properties[nameof(BranchId)]}."
                        parentBranchIds.AddOrUpdate(i, Guid.Parse(r.Properties[nameof(BranchId)]), (fun _ _ -> Guid.Parse(r.Properties[nameof(BranchId)]))) |> ignore
                    | Error error -> ()
                    showResult repo
                })
            ))

            do! Parallel.ForEachAsync({0..numberOfRepositories}, ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    try
                        do! Task.Delay(Random.Shared.Next(25))
                        let branchId = Guid.NewGuid()
                        let branchName = $"Branch{suffixes[i]}"
                        let! r = Branch.Create(Branch.CreateBranchParameters(OwnerId = $"{ownerId}",
                            OrganizationId = $"{organizationId}",
                            RepositoryId = $"{repositoryIds[i]}",
                            BranchId = $"{branchId}",
                            BranchName = branchName,
                            ParentBranchId = $"{parentBranchIds[i]}", CorrelationId = g()))
                        showResult r
                        match r with
                        | Ok r ->
                            ids.AddOrUpdate(i, (ownerId, organizationId, repositoryIds[i], branchId), (fun _ _ -> (ownerId, organizationId, repositoryIds[i], branchId))) |> ignore
                        | Error error ->
                            logToConsole $"Error creating branch {i}: {error}."
                    with ex ->
                        logToConsole $"i: {i}; exception; repositoryIds[i]: {repositoryIds.ContainsKey(i)}; parentBranchIds[i]: {parentBranchIds.ContainsKey(i)}."
                })
            ))

            let setupTime = getCurrentInstant()
            logToConsole "Setup complete."

            do! Parallel.ForEachAsync({0..numberOfEvents}, ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let rnd = Random.Shared.Next(ids.Count)
                    let (ownerId, organizationId, repositoryId, branchId) = ids[rnd]
                    match Random.Shared.Next(4) with
                    | 0 -> 
                        let! r = Branch.Save(Branch.CreateReferenceParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}", 
                            Message = $"Save - {DateTime.UtcNow}", DirectoryId = Guid.NewGuid(), CorrelationId = g()))
                        showResult r
                    | 1 ->
                        let! r = Branch.Checkpoint(Branch.CreateReferenceParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}",
                            Message = $"Checkpoint - {DateTime.UtcNow}", DirectoryId = Guid.NewGuid(), CorrelationId = g()))
                        showResult r
                    | 2 ->
                        let! r = Branch.Commit(Branch.CreateReferenceParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}",
                            Message = $"Commit - {DateTime.UtcNow}", DirectoryId = Guid.NewGuid(), CorrelationId = g()))
                        showResult r
                    | 3 ->
                        let! r = Branch.Tag(Branch.CreateReferenceParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}",
                            Message = $"Tag - {DateTime.UtcNow}", DirectoryId = Guid.NewGuid(), CorrelationId = g()))
                        showResult r
                    | _ -> ()
                })
            ))

            let mainProcessingTime = getCurrentInstant()
            logToConsole "Main processing complete."

            // Tear down
            do! Parallel.ForEachAsync({0..numberOfRepositories}, ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let (ownerId, organizationId, repositoryId, branchId) = ids[i]

                    let! r = Branch.Delete(Branch.DeleteBranchParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}", CorrelationId = g()))
                    showResult r
                    do! Task.Delay(Random.Shared.Next(25))

                    let! result = Repository.Delete(Repository.DeleteRepositoryParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", DeleteReason = "performance test", CorrelationId = g()))
                    showResult result
                    do! Task.Delay(Random.Shared.Next(25))
                })
            ))

            let! r = Organization.Delete(Organization.DeleteOrganizationParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", DeleteReason = "performance test", CorrelationId = g()))
            showResult r
            do! Task.Delay(Random.Shared.Next(25))

            let! r = Owner.Delete(Owner.DeleteOwnerParameters(OwnerId = $"{ownerId}", DeleteReason = "performance test", CorrelationId = g()))
            showResult r
            do! Task.Delay(Random.Shared.Next(25))

            let endTime = getCurrentInstant()
            logToConsole "Tear down complete."

            printfn $"Setup time:       {setupTime - startTime}."
            printfn $"Processing time:  {mainProcessingTime - setupTime}."
            printfn $"Tear down:        {endTime - mainProcessingTime}."
            printfn $"Total:            {endTime - startTime}."
            printfn $"Transactions/sec: {float numberOfEvents / (mainProcessingTime - setupTime).TotalSeconds}"
            return 0            
        }).Result
