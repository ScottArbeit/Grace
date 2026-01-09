namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module WorkItem =
    /// Base parameters for WorkItem endpoints.
    type WorkItemParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public WorkItemId = String.Empty with get, set

    /// Parameters for /work/create.
    type CreateWorkItemParameters() =
        inherit WorkItemParameters()
        member val public Title = String.Empty with get, set
        member val public Description = String.Empty with get, set

    /// Parameters for /work/{id}.
    type GetWorkItemParameters() =
        inherit WorkItemParameters()

    /// Parameters for /work/{id}/update.
    type UpdateWorkItemParameters() =
        inherit WorkItemParameters()
        member val public Title = String.Empty with get, set
        member val public Description = String.Empty with get, set
        member val public Status = String.Empty with get, set
        member val public Constraints = String.Empty with get, set
        member val public Notes = String.Empty with get, set
        member val public ArchitecturalNotes = String.Empty with get, set
        member val public MigrationNotes = String.Empty with get, set

    /// Parameters for /work/{id}/link/reference.
    type LinkReferenceParameters() =
        inherit WorkItemParameters()
        member val public ReferenceId = String.Empty with get, set

    /// Parameters for /work/{id}/link/promotion-group.
    type LinkPromotionGroupParameters() =
        inherit WorkItemParameters()
        member val public PromotionGroupId = String.Empty with get, set
