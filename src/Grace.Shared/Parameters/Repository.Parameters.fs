namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System
open System.Collections.Generic

module Repository =

    type RepositoryParameters() = 
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
    
    type CreateParameters() = 
        inherit RepositoryParameters()

    type InitParameters() = 
        inherit RepositoryParameters()
        member val public GraceConfig = String.Empty with get, set
    
    type VisibilityParameters() = 
        inherit RepositoryParameters()
        member val public Visibility = String.Empty with get, set
    
    type StatusParameters() = 
        inherit RepositoryParameters()
        member val public Status = String.Empty with get, set
    
    type RecordSavesParameters() = 
        inherit RepositoryParameters()
        member val public RecordSaves = false with get, set
    
    type SaveDaysParameters() = 
        inherit RepositoryParameters()
        member val public SaveDays = Double.MinValue with get, set

    type CheckpointDaysParameters() = 
        inherit RepositoryParameters()
        member val public CheckpointDays = Double.MinValue with get, set

    type DescriptionParameters() =
        inherit RepositoryParameters()
        member val public Description = String.Empty with get, set
            
    type DefaultServerApiVersionParameters() =
        inherit RepositoryParameters()
        member val public DefaultServerApiVersion = String.Empty with get, set

    type SetNameParameters() = 
        inherit RepositoryParameters()
        member val public NewName = String.Empty with get, set
    
    type DeleteParameters() =
        inherit RepositoryParameters()
        member val public Force = false with get, set
        member val public DeleteReason = String.Empty with get, set

    type EnableMergeTypeParameters() =
        inherit RepositoryParameters()
        member val public Enabled = false with get, set

    type GetReferencesByReferenceIdParameters() =
        inherit RepositoryParameters()
        member val public ReferenceIds: IEnumerable<ReferenceId> = Array.Empty<ReferenceId>() with get, set

    type GetBranchesParameters() =
        inherit RepositoryParameters()
        member val public IncludeDeleted = false with get, set
        
    type GetBranchesByBranchIdParameters() =
        inherit RepositoryParameters()
        member val public BranchIds: IEnumerable<BranchId> = Array.Empty<BranchId>() with get, set
        member val public IncludeDeleted = false with get, set

    type UndeleteParameters() =
        inherit RepositoryParameters()
    