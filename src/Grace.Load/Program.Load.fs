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

module Load =

    let numberOfRepositories = 15
    let numberOfEvents = 500

    let showResult<'T> (r: GraceResult<'T>) =
        match r with
        | Ok result -> logToConsole (sprintf "%s - CorrelationId: %s" (result.Properties["EventType"]) result.CorrelationId)
        | Error error -> logToConsole $"{error}"
    
    let g() = $"{Guid.NewGuid()}"

    [<EntryPoint>]
    let main args =
        (task {
            ThreadPool.SetMinThreads(100, 100) |> ignore
            let startTime = getCurrentInstant()
            let cancellationToken = new CancellationToken()

            let ids = ConcurrentDictionary<int, OwnerId * OrganizationId * RepositoryId * BranchId>()

            do! Parallel.ForEachAsync({0..numberOfRepositories}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let suffix = Random.Shared.Next(Int32.MaxValue).ToString("X8")

                    let ownerId = Guid.NewGuid()
                    let ownerName = $"Owner{suffix}{i:X2}"
                    let! r = Owner.Create(Owner.CreateParameters(OwnerId = $"{ownerId}", OwnerName = ownerName, CorrelationId = g()))
                    showResult r

                    let organizationId = Guid.NewGuid()
                    let organizationName = $"Organization{suffix}{i:X2}"
                    let! r = Organization.Create(Organization.CreateParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", OrganizationName = organizationName, CorrelationId = g()))
                    showResult r

                    let repositoryId = Guid.NewGuid()
                    let repositoryName = $"Repository{suffix}{i:X2}"
                    let! repo = Repository.Create(Repository.CreateParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", RepositoryName = repositoryName, CorrelationId = g()))
                    showResult repo

                    let branchId = Guid.NewGuid()
                    let branchName = $"Branch{suffix}{i:X2}"
                    match repo with
                    | Ok repo ->
                        let! r = Branch.Create(Branch.CreateParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}", BranchName = branchName, ParentBranchId = repo.Properties[nameof(BranchId)], CorrelationId = g()))
                        showResult r
                        match r with
                        | Ok r ->
                            ids.AddOrUpdate(i, (ownerId, organizationId, repositoryId, branchId), (fun _ _ -> (ownerId, organizationId, repositoryId, branchId))) |> ignore
                        | Error error -> ()
                    | Error error -> ()
                })
            ))

            let setupTime = getCurrentInstant()

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

            // Tear down
            do! Parallel.ForEachAsync({0..numberOfRepositories}, Constants.ParallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                ValueTask(task {
                    let (ownerId, organizationId, repositoryId, branchId) = ids[i]

                    let! r = Branch.Delete(Branch.DeleteParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", BranchId = $"{branchId}", CorrelationId = g()))
                    showResult r

                    let! result = Repository.Delete(Repository.DeleteParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", RepositoryId = $"{repositoryId}", DeleteReason = "performance test", CorrelationId = g()))
                    showResult result

                    let! r = Organization.Delete(Organization.DeleteParameters(OwnerId = $"{ownerId}", OrganizationId = $"{organizationId}", DeleteReason = "performance test", CorrelationId = g()))
                    showResult r

                    let! r = Owner.Delete(Owner.DeleteParameters(OwnerId = $"{ownerId}", DeleteReason = "performance test", CorrelationId = g()))
                    showResult r
                })
            ))

            let endTime = getCurrentInstant()
            printfn $"Setup time:       {setupTime - startTime}."
            printfn $"Processing time:  {mainProcessingTime - setupTime}."
            printfn $"Tear down:        {endTime - mainProcessingTime}."
            printfn $"Total:            {endTime - startTime}."
            printfn $"Transactions/sec: {float numberOfEvents / (mainProcessingTime - setupTime).TotalSeconds}"
            return 0            
        }).Result
