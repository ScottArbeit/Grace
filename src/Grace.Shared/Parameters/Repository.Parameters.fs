namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System
open System.Collections.Generic

module Repository =

    /// Parameters for many endpoints in the /repository path.
    type RepositoryParameters() = 
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
    
    /// Parameters for the /repository/create endpoint.
    type CreateRepositoryParameters() = 
        inherit RepositoryParameters()

    /// Parameters for the /repository/get endpoint.
    type GetRepositoryParameters() = 
        inherit RepositoryParameters()

    /// Parameters for the /repository/isEmpty endpoint.
    type IsEmptyParameters() = 
        inherit RepositoryParameters()

    /// Parameters for the /repository/init endpoint.
    type InitParameters() = 
        inherit RepositoryParameters()
        member val public GraceConfig = String.Empty with get, set
    
    /// Parameters for the /repository/setVisibility endpoint.
    type SetRepositoryVisibilityParameters() = 
        inherit RepositoryParameters()
        member val public Visibility = String.Empty with get, set
    
    /// Parameters for the /repository/setStatus endpoint.
    type SetRepositoryStatusParameters() = 
        inherit RepositoryParameters()
        member val public Status = String.Empty with get, set
    
    /// Parameters for the /repository/recordSaves endpoint.
    type RecordSavesParameters() = 
        inherit RepositoryParameters()
        member val public RecordSaves = false with get, set
    
    /// Parameters for the /repository/setSaveDays endpoint.
    type SetSaveDaysParameters() = 
        inherit RepositoryParameters()
        member val public SaveDays: Double = Double.MinValue with get, set

    /// Parameters for the /repository/setCheckpointDays endpoint.
    type SetCheckpointDaysParameters() = 
        inherit RepositoryParameters()
        member val public CheckpointDays: Double = Double.MinValue with get, set

    /// Parameters for the /repository/setDirectoryVersionCacheDays endpoint.
    type SetDirectoryVersionCacheDaysParameters() = 
        inherit RepositoryParameters()
        member val public DirectoryVersionCacheDays: Double = Double.MinValue with get, set

    /// Parameters for the /repository/setDiffCacheDays endpoint.
    type SetDiffCacheDaysParameters() = 
        inherit RepositoryParameters()
        member val public DiffCacheDays: Double = Double.MinValue with get, set

    /// Parameters for the /repository/setDescription endpoint.
    type SetRepositoryDescriptionParameters() =
        inherit RepositoryParameters()
        member val public Description = String.Empty with get, set
            
    /// Parameters for the /repository/setDefaultServerApiVersion endpoint.
    type SetDefaultServerApiVersionParameters() =
        inherit RepositoryParameters()
        member val public DefaultServerApiVersion = String.Empty with get, set

    /// Parameters for the /repository/setName endpoint.
    type SetRepositoryNameParameters() = 
        inherit RepositoryParameters()
        member val public NewName = String.Empty with get, set

    /// Parameters for the /repository/delete endpoint.
    type DeleteRepositoryParameters() =
        inherit RepositoryParameters()
        member val public Force = false with get, set
        member val public DeleteReason = String.Empty with get, set

    type EnablePromotionTypeParameters() =
        inherit RepositoryParameters()
        member val public Enabled = false with get, set

    /// Parameters for the /repository/getReferencesByReferenceId endpoint.
    type GetReferencesByReferenceIdParameters() =
        inherit RepositoryParameters()
        member val public ReferenceIds: IEnumerable<ReferenceId> = Array.Empty<ReferenceId>() with get, set
        member val public MaxCount: int = 1 with get, set

    /// Parameters for the /repository/getBranched endpoint.
    type GetBranchesParameters() =
        inherit RepositoryParameters()
        member val public IncludeDeleted = false with get, set
        member val public MaxCount: int = 30 with get, set
        
    /// Parameters for the /repository/getBranchesByBranchId endpoint.
    type GetBranchesByBranchIdParameters() =
        inherit RepositoryParameters()
        member val public BranchIds: IEnumerable<BranchId> = Array.Empty<BranchId>() with get, set
        member val public MaxCount: int = 1 with get, set
        member val public IncludeDeleted = false with get, set

    /// Parameters for the /repository/undelete endpoint.
    type UndeleteRepositoryParameters() =
        inherit RepositoryParameters()
    