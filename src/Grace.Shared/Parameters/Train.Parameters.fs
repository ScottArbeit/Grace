namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open NodaTime
open System

module Train =

    type TrainParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public PromotionTrainId = String.Empty with get, set

    type CreateTrainParameters() =
        inherit TrainParameters()
        member val public TargetBranchId: BranchId = BranchId.Empty with get, set
        member val public BaseReferenceId: ReferenceId = ReferenceId.Empty with get, set
        member val public ScheduledAt: Instant option = None with get, set

    type EnqueueTrainParameters() =
        inherit TrainParameters()
        member val public ChangeId: ChangeId = ChangeId.Empty with get, set

    type DequeueTrainParameters() =
        inherit TrainParameters()
        member val public ChangeId: ChangeId = ChangeId.Empty with get, set

    type BuildTrainParameters() =
        inherit TrainParameters()

    type ApplyTrainParameters() =
        inherit TrainParameters()

    type GetTrainParameters() =
        inherit TrainParameters()
