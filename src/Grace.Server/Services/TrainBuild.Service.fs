namespace Grace.Server.Services

open Grace.Server.Services
open Grace.Shared.Utilities
open Grace.Types.Change
open Grace.Types.PatchSet
open Grace.Types.Train
open Grace.Types.Types
open System
open System.Threading.Tasks

module TrainBuild =

    type TrainBuildResult =
        { HeadDirectoryId: DirectoryVersionId
          Conflicts: PatchFile array }

    let buildTrain
        (train: TrainDto)
        (baseDirectoryId: DirectoryVersionId)
        (getChangeRevision: ChangeId -> Task<ChangeRevision option>)
        (getPatchSet: PatchSetId -> Task<PatchSetDto>)
        (applyPatchSet: PatchSetDto -> DirectoryVersionId -> Task<ApplyPatchSet.ApplyResult>)
        =
        task {
            let conflicts = System.Collections.Generic.List<PatchFile>()
            let mutable currentDirectoryId = baseDirectoryId
            let mutable halted = false

            for changeId in train.Queue do
                if not halted then
                    let! revisionOpt = getChangeRevision changeId

                    match revisionOpt with
                    | None -> ()
                    | Some revision ->
                        let! patchSet = getPatchSet revision.PatchSetId
                        let! applyResult = applyPatchSet patchSet currentDirectoryId

                        if applyResult.Conflicts.Length > 0 then
                            conflicts.AddRange(applyResult.Conflicts)
                            halted <- true
                        else if applyResult.RootDirectoryId <> DirectoryVersionId.Empty then
                            currentDirectoryId <- applyResult.RootDirectoryId

            return { HeadDirectoryId = currentDirectoryId; Conflicts = conflicts.ToArray() }
        }
