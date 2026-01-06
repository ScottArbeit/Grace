namespace Grace.Server

open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Queue
open Grace.Types.Types
open System.Threading.Tasks

module ConflictPipeline =
    let addConflict
        (candidateId: CandidateId)
        (repositoryId: RepositoryId)
        (conflict: Grace.Types.PromotionGroup.ConflictAnalysis)
        (metadata: EventMetadata)
        (correlationId: CorrelationId)
        =
        task {
            let candidateActorProxy = IntegrationCandidate.CreateActorProxy candidateId repositoryId correlationId

            return! candidateActorProxy.Handle (CandidateCommand.AddConflict conflict) metadata
        }

    let recordResolution
        (candidateId: CandidateId)
        (repositoryId: RepositoryId)
        (receipt: ConflictReceipt)
        (metadata: EventMetadata)
        (correlationId: CorrelationId)
        =
        task {
            let receiptActorProxy = ConflictReceipt.CreateActorProxy receipt.ConflictReceiptId repositoryId correlationId

            match! receiptActorProxy.Handle (ConflictReceiptCommand.Create receipt) metadata with
            | Error error -> return Error error
            | Ok _ ->
                let candidateActorProxy = IntegrationCandidate.CreateActorProxy candidateId repositoryId correlationId

                return! candidateActorProxy.Handle (CandidateCommand.AddConflictReceipt receipt.ConflictReceiptId) metadata
        }
