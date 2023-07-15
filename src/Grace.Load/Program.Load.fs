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

    let numberOfRepositories = 20
    let numberOfEvents = 200

    let showResult<'T> (r: GraceResult<'T>) =
        match r with
        | Ok result -> () //logToConsole (sprintf "%s - CorrelationId: %s" (result.Properties["EventType"]) result.CorrelationId)
        | Error error -> logToConsole $"{error}"
    
    let g() = $"{Guid.NewGuid()}"

    [<EntryPoint>]
    let main args =
        (task {
            ThreadPool.SetMinThreads(100, 100) |> ignore
            let startTime = getCurrentInstant()
            let cancellationToken = new CancellationToken()

            let suffixes = ConcurrentDictionary<int, string>()
            let ownerIds = ConcurrentDictionary<int, OwnerId>()
            let organizationIds = ConcurrentDictionary<int, OrganizationId>()
            let repositoryIds = ConcurrentDictionary<int, RepositoryId>()
            let parentBranchIds = ConcurrentDictionary<int, BranchId>()
            let ids = ConcurrentDictionary<int, OwnerId * OrganizationId * RepositoryId * BranchId>()

            for i in {0..numberOfRepositories} do
                suffixes[i] <- Random.Shared.Next(Int32.MaxValue).ToString("X8")

            do! Parallel.ForEachAsync({0..numberOfRepositories}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let ownerId = Guid.NewGuid()
                    let ownerName = $"Owner{suffixes[i]}"
                    let! r = Owner.Create(Owner.CreateOwnerParameters(OwnerId = $"{ownerId}", OwnerName = ownerName, CorrelationId = g()))
                    ownerIds.AddOrUpdate(i, ownerId, (fun _ _ -> ownerId)) |> ignore
                    showResult r
                })
            ))

            do! Parallel.ForEachAsync({0..numberOfRepositories}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let organizationId = Guid.NewGuid()
                    let organizationName = $"Organization{suffixes[i]}"
                    let! r = Organization.Create(Organization.CreateOrganizationParameters(OwnerId = $"{ownerIds[i]}", OrganizationId = $"{organizationId}", OrganizationName = organizationName, CorrelationId = g()))
                    organizationIds.AddOrUpdate(i, organizationId, (fun _ _ -> organizationId)) |> ignore
                    showResult r
                })
            ))

            do! Parallel.ForEachAsync({0..numberOfRepositories}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let repositoryId = Guid.NewGuid()
                    let repositoryName = $"Repository{suffixes[i]}"
                    let! repo = Repository.Create(Repository.CreateRepositoryParameters(OwnerId = $"{ownerIds[i]}", OrganizationId = $"{organizationIds[i]}", RepositoryId = $"{repositoryId}", RepositoryName = repositoryName, CorrelationId = g()))
                    repositoryIds.AddOrUpdate(i, repositoryId, (fun _ _ -> repositoryId)) |> ignore
                    match repo with
                    | Ok r ->
                        parentBranchIds.AddOrUpdate(i, Guid.Parse(r.Properties[nameof(BranchId)]), (fun _ _ -> Guid.Parse(r.Properties[nameof(BranchId)]))) |> ignore
                    | Error error -> ()
                    showResult repo
                })
            ))

            do! Parallel.ForEachAsync({0..numberOfRepositories}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let branchId = Guid.NewGuid()
                    let branchName = $"Branch{suffixes[i]}"
                    let! r = Branch.Create(Branch.CreateBranchParameters(OwnerId = $"{ownerIds[i]}", OrganizationId = $"{organizationIds[i]}", RepositoryId = $"{repositoryIds[i]}", BranchId = $"{branchId}", BranchName = branchName, ParentBranchId = $"{parentBranchIds[i]}", CorrelationId = g()))
                    showResult r
                    match r with
                    | Ok r ->
                        ids.AddOrUpdate(i, (ownerIds[i], organizationIds[i], repositoryIds[i], branchId), (fun _ _ -> (ownerIds[i], organizationIds[i], repositoryIds[i], branchId))) |> ignore
                    | Error error -> ()
                })
            ))

            let setupTime = getCurrentInstant()
            logToConsole "Setup complete."

            do! Parallel.ForEachAsync({0..numberOfEvents}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let (ownerId, organizationId, repositoryId, branchId) = ids[i % ids.Count]
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
            do! Parallel.ForEachAsync({0..numberOfRepositories}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let (ownerId, organizationId, repositoryId, branchId) = ids[i]

                    let! r = Branch.Delete(Branch.DeleteBranchParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}", CorrelationId = g()))
                    showResult r

                    let! result = Repository.Delete(Repository.DeleteRepositoryParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", DeleteReason = "performance test", CorrelationId = g()))
                    showResult result

                    let! r = Organization.Delete(Organization.DeleteOrganizationParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", DeleteReason = "performance test", CorrelationId = g()))
                    showResult r

                    let! r = Owner.Delete(Owner.DeleteOwnerParameters(OwnerId = $"{ownerId}", DeleteReason = "performance test", CorrelationId = g()))
                    showResult r
                })
            ))

            let endTime = getCurrentInstant()
            logToConsole "Tear down complete."

            printfn $"Setup time:       {setupTime - startTime}."
            printfn $"Processing time:  {mainProcessingTime - setupTime}."
            printfn $"Tear down:        {endTime - mainProcessingTime}."
            printfn $"Total:            {endTime - startTime}."
            printfn $"Transactions/sec: {float numberOfEvents / (mainProcessingTime - setupTime).TotalSeconds}"
            return 0            
        }).Result
