namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Branch =
    
    type BranchParameters() = 
        inherit CommonParameters()
        member val public BranchId = String.Empty with get, set
        member val public BranchName = String.Empty with get, set
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set

    type BranchQueryParameters() = 
        inherit BranchParameters()
        member val public Sha256Hash: Sha256Hash  = String.Empty with get, set
        member val public ReferenceId = String.Empty with get, set

    type CreateParameters() =
        inherit BranchParameters()
        member val public ParentBranchId = String.Empty with get, set
        member val public ParentBranchName = String.Empty with get, set

    type RebaseParameters() =
        inherit BranchParameters()
        member val public BasedOn: ReferenceId = ReferenceId.Empty with get, set

    type CreateReferenceParameters() =
        inherit BranchParameters()
        member val public DirectoryId: DirectoryId = Guid.Empty with get, set
        member val public Sha256Hash: Sha256Hash  = String.Empty with get, set
        member val public Message = String.Empty with get, set

    type SetNameParameters() =
        inherit BranchParameters()
        member val public NewName = String.Empty with get, set

    type EnableFeatureParameters() =
        inherit BranchParameters()
        member val public Enabled = true with get, set

    type DeleteParameters() =
        inherit BranchParameters()
        
    type GetReferenceParameters() =
        inherit BranchQueryParameters()

    type GetReferencesParameters() =
        inherit BranchParameters()
        member val public FullSha = false with get, set
        member val public MaxCount = 50 with get, set

    type GetDiffsForReferenceTypeParameters() =
        inherit BranchParameters()
        member val public ReferenceType = String.Empty with get, set
        member val public MaxCount = 50 with get, set

    type GetDiffsForReferencesParameters() =
        inherit BranchParameters()
        member val public References = String.Empty with get, set
        member val public MaxCount = 50 with get, set
        
    type GetParameters() = 
        inherit BranchQueryParameters()

    type SwitchParameters() = 
        inherit BranchQueryParameters()

    type GetVersionParameters() =
        inherit BranchQueryParameters()

