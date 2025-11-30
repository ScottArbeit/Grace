namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open System

module Branch =

    /// Parameters for many endpoints in the /branch path.
    type BranchParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public BranchId = String.Empty with get, set
        member val public BranchName: BranchName = String.Empty with get, set

    /// Base class for parameters for branch queries.
    type BranchQueryParameters() =
        inherit BranchParameters()
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public ReferenceId = String.Empty with get, set

    /// Parameters for the /branch/create endpoint.
    type CreateBranchParameters() =
        inherit BranchParameters()
        member val public ParentBranchId = String.Empty with get, set
        member val public ParentBranchName: BranchName = String.Empty with get, set
        member val public InitialPermissions: ReferenceType seq = [ Commit; Checkpoint; Save; Tag ] with get, set

    /// Parameters for the /branch/assign endpoint.
    type AssignParameters() =
        inherit BranchParameters()
        member val public DirectoryVersionId: DirectoryVersionId = Guid.Empty with get, set
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public Message = String.Empty with get, set

    /// Parameters for the /branch/rebase endpoint.
    type RebaseParameters() =
        inherit BranchParameters()
        member val public BasedOn: ReferenceId = ReferenceId.Empty with get, set

    /// Parameters for the various /branch/create[reference] endpoints.
    type CreateReferenceParameters() =
        inherit BranchParameters()
        member val public DirectoryVersionId: DirectoryVersionId = Guid.Empty with get, set
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public Message = String.Empty with get, set

    /// Parameters for the /branch/setName endpoint.
    type SetBranchNameParameters() =
        inherit BranchParameters()
        member val public NewName = String.Empty with get, set

    /// Parameters for the various /branch/enable[feature] endpoints.
    type EnableFeatureParameters() =
        inherit BranchParameters()
        member val public Enabled = false with get, set

    /// Parameters for the /branch/setPromotionMode endpoint.
    type SetPromotionModeParameters() =
        inherit BranchParameters()
        member val public PromotionMode = String.Empty with get, set

    /// Parameters for the /branch/delete endpoint.
    type DeleteBranchParameters() =
        inherit BranchParameters()
        member val public Force: bool = false with get, set
        member val public DeleteReason: DeleteReason = String.Empty with get, set
        member val public ReassignChildBranches: bool = false with get, set
        member val public NewParentBranchId: string = String.Empty with get, set
        member val public NewParentBranchName: string = String.Empty with get, set

    /// Parameters for the /branch/updateParentBranch endpoint.
    type UpdateParentBranchParameters() =
        inherit BranchParameters()
        member val public NewParentBranchId: string = String.Empty with get, set
        member val public NewParentBranchName: string = String.Empty with get, set

    /// Parameters for the /branch/getReference endpoint.
    type GetReferenceParameters() =
        inherit BranchQueryParameters()

    /// Parameters for the /branch/getReferences and /branch/get[reference] endpoints.
    type GetReferencesParameters() =
        inherit BranchParameters()
        member val public FullSha = false with get, set
        member val public MaxCount = 50 with get, set

    type GetLatestReferencesByReferenceTypeParameters() =
        inherit BranchParameters()
        member val public ReferenceTypes: ReferenceType array = [| Promotion; Commit; Checkpoint; Save |] with get, set

    /// Parameters for the /branch/getDiffsForReferenceType endpoint.
    type GetDiffsForReferenceTypeParameters() =
        inherit BranchParameters()
        member val public ReferenceType = String.Empty with get, set
        member val public MaxCount = 50 with get, set

    /// Parameters for the /branch/getDiffsForReferences endpoint.
    type GetDiffsForReferencesParameters() =
        inherit BranchParameters()
        member val public References = String.Empty with get, set
        member val public MaxCount = 50 with get, set

    /// Parameters for the /branch/get endpoint.
    type GetBranchParameters() =
        inherit BranchQueryParameters()
        member val public IncludeDeleted = false with get, set

    type ListContentsParameters() =
        inherit BranchQueryParameters()
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public ReferenceId = String.Empty with get, set
        member val public Pattern = String.Empty with get, set
        member val public ShowDirectories = true with get, set
        member val public ShowFiles = true with get, set
        member val public ForceRecompute = false with get, set

    /// Parameters for the /branch/switch endpoint.
    type SwitchParameters() =
        inherit BranchQueryParameters()

    /// Parameters for the /branch/getVersion endpoint.
    type GetBranchVersionParameters() =
        inherit BranchQueryParameters()
        member val public IncludeDeleted = false with get, set
