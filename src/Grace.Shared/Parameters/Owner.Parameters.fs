namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Owner =

    /// Parameters for many endpoints in the /owner path.
    type OwnerParameters() = 
        inherit CommonParameters()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set

    /// Parameters for the /owner/create endpoint.
    type CreateOwnerParameters() = 
        inherit OwnerParameters()

    /// Parameters for the /owner/setName endpoint.
    type SetOwnerNameParameters() =
        inherit OwnerParameters()
        member val public NewName: string = String.Empty with get, set

    /// Parameters for the /owner/setType endpoint.
    type SetOwnerTypeParameters() =
        inherit OwnerParameters()
        member val public OwnerType: string = String.Empty with get, set

    /// Parameters for the /owner/setSearchVisibility endpoint.
    type SetOwnerSearchVisibilityParameters() =
        inherit OwnerParameters()
        member val public SearchVisibility: string = String.Empty with get, set
        
    /// Parameters for the /owner/setDescription endpoint.
    type SetOwnerDescriptionParameters() =
        inherit OwnerParameters()
        member val public Description: string = String.Empty with get, set
        
    /// Parameters for the /owner/get endpoint.
    type GetOwnerParameters() =
        inherit OwnerParameters()

    /// Parameters for the /owner/delete endpoint.
    type DeleteOwnerParameters() =
        inherit OwnerParameters()
        member val public Force: bool = false with get, set
        member val public DeleteReason: string = String.Empty with get, set

    /// Parameters for the /owner/undelete endpoint.
    type UndeleteOwnerParameters() =
        inherit OwnerParameters()

    /// Parameters for the /owner/listOrganizations endpoint.
    type ListOrganizationsParameters() =
        inherit OwnerParameters()