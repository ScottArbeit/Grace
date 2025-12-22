namespace Grace.Server.Services

open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Services
open Grace.Shared.Utilities
open Grace.Types.Change
open Grace.Types.PatchSet
open Grace.Types.Reference
open Grace.Types.Stack
open Grace.Types.Types
open System
open System.Threading.Tasks

module Restack =

    type RestackResult =
        { UpdatedReferences: ReferenceId array
          Conflicts: PatchFile array }

    let restackStack
        (repositoryId: RepositoryId)
        (stack: StackDto)
        (correlationId: CorrelationId)
        (getChangeRevision: ChangeId -> Task<ChangeRevision option>)
        (getBasedOnReference: BranchId -> Task<ReferenceDto>)
        (applyPatchSet: PatchSetDto -> DirectoryVersionId -> Task<ApplyPatchSet.ApplyResult>)
        =
        task {
            let updatedReferences = System.Collections.Generic.List<ReferenceId>()
            let conflicts = System.Collections.Generic.List<PatchFile>()
            let mutable halted = false

            for layer in stack.Layers |> Array.sortBy (fun layer -> layer.Order) do
                if not halted then
                    let! revisionOpt = getChangeRevision layer.ChangeId

                    match revisionOpt with
                    | None -> ()
                    | Some revision ->
                        let! basedOnReference = getBasedOnReference layer.BranchId

                        let patchSetActor = PatchSet.CreateActorProxy revision.PatchSetId repositoryId correlationId
                        let! patchSetDto = patchSetActor.Get correlationId

                        let! applyResult = applyPatchSet patchSetDto basedOnReference.DirectoryId

                        if applyResult.Conflicts.Length > 0 then
                            conflicts.AddRange(applyResult.Conflicts)
                            halted <- true
                        else
                            updatedReferences.Add(revision.ReferenceId)

            return { UpdatedReferences = updatedReferences.ToArray(); Conflicts = conflicts.ToArray() }
        }
