namespace Grace

open Grace.SDK
open Grace.Shared.Parameters
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks

module Load =

    let rnd = Random()

    let showResult<'T> (r: GraceResult<'T>) =
        match r with
        | Ok result -> printfn $"{result}"
        | Error error -> printfn $"{error}"

    [<EntryPoint>]
    let main args =
        (task {
            let startTime = getCurrentInstant()
            let cancellationToken = new CancellationToken()
            let parallelOptions = ParallelOptions(MaxDegreeOfParallelism = Environment.ProcessorCount * 2, CancellationToken = cancellationToken)
            Parallel.ForEachAsync({0 .. 20}, parallelOptions, (fun (i: int) (cancellationToken: CancellationToken) ->
                (task {
                    let suffix = rnd.Next(65536).ToString("X4")
                    let ownerId = Guid.NewGuid().ToString()
                    let ownerName = $"Owner{suffix}"
                    let organizationId = Guid.NewGuid().ToString()
                    let organizationName = $"Organization{suffix}"
                    let repositoryId = Guid.NewGuid().ToString()
                    let repositoryName = $"Repository{suffix}"
                    let branchId = Guid.NewGuid().ToString()
                    let branchName = $"Branch{suffix}"

                    let! r = Owner.Create(Owner.CreateParameters(OwnerId = ownerId, OwnerName = ownerName))
                    showResult r

                    let! r = Organization.Create(Organization.CreateParameters(OwnerName = ownerName, OrganizationId = organizationId, OrganizationName = organizationName))
                    showResult r
                    
                    let! repo = Repository.Create(Repository.CreateParameters(OwnerName = ownerName, OrganizationName = organizationName, RepositoryId = repositoryId, RepositoryName = repositoryName))
                    showResult repo

                    match repo with
                    | Ok repo ->
                        let! r = Branch.Create(Branch.CreateParameters(OwnerName = ownerName, OrganizationName = organizationName, RepositoryName = repositoryName, BranchId = branchId, BranchName = branchName, ParentBranchId = repo.Properties[nameof(BranchId)]))
                        showResult r
                    | Error error -> ()

                    for i in 1..16 do
                        match rnd.Next(4) with
                        | 0 -> 
                            let! r = Branch.Save(Branch.CreateReferenceParameters(OwnerName = ownerName, OrganizationName = organizationName, RepositoryName = repositoryName, 
                                        BranchName = branchName, Message = $"Save - {DateTime.UtcNow}", DirectoryId = Guid.Empty))
                            showResult r
                        | 1 ->
                            let! r = Branch.Checkpoint(Branch.CreateReferenceParameters(OwnerName = ownerName, OrganizationName = organizationName, RepositoryName = repositoryName, 
                                        BranchName = branchName, Message = $"Checkpoint - {DateTime.UtcNow}", DirectoryId = Guid.Empty))
                            showResult r
                        | 2 ->
                            let! r = Branch.Commit(Branch.CreateReferenceParameters(OwnerName = ownerName, OrganizationName = organizationName, RepositoryName = repositoryName, 
                                        BranchName = branchName, Message = $"Commit - {DateTime.UtcNow}", DirectoryId = Guid.Empty))
                            showResult r
                        | 3 ->
                            let! r = Branch.Tag(Branch.CreateReferenceParameters(OwnerName = ownerName, OrganizationName = organizationName, RepositoryName = repositoryName, 
                                        BranchName = branchName, Message = $"Tag - {DateTime.UtcNow}", DirectoryId = Guid.Empty))
                            showResult r
                        | _ -> ()
                }).Wait()
                ValueTask.CompletedTask
            )).Wait()

            let endTime = getCurrentInstant()
            printfn $"Elapsed: {endTime - startTime}."
            return 0            
        }).Result
