namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Owner =

    /// Common parameters for endpoints in the /owner path.
    type OwnerParameters() = 
        inherit CommonParameters()
        /// The Id of the owner.
        member val public OwnerId: string = String.Empty with get, set
        /// The name of the owner.
        member val public OwnerName: string = String.Empty with get, set

    /// Parameters for the /owner/create endpoint.
    type CreateOwnerParameters() = 
        inherit OwnerParameters()

    /// Parameters for the /owner/setName endpoint.
    type SetOwnerNameParameters() =
        inherit OwnerParameters()
        /// The new name for the owner.
        member val public NewName: string = String.Empty with get, set

    /// Parameters for the /owner/setType endpoint.
    type SetOwnerTypeParameters() =
        inherit OwnerParameters()
        /// The new type for the owner. Must be one of the OwnerType cases.
        member val public OwnerType: string = String.Empty with get, set

    /// Parameters for the /owner/setSearchVisibility endpoint.
    type SetOwnerSearchVisibilityParameters() =
        inherit OwnerParameters()
        /// The new search visibility for the owner. Must be one of the SearchVisibility cases.
        member val public SearchVisibility: string = String.Empty with get, set
        
    /// Parameters for the /owner/setDescription endpoint.
    type SetOwnerDescriptionParameters() =
        inherit OwnerParameters()
        /// The new description for the owner.
        member val public Description: string = String.Empty with get, set
        
    /// Parameters for the /owner/get endpoint.
    type GetOwnerParameters() =
        inherit OwnerParameters()
        /// If true, include deleted owners in the response.
        member val public IncludeDeleted: bool = false with get, set

    /// Parameters for the /owner/delete endpoint.
    type DeleteOwnerParameters() =
        inherit OwnerParameters()
        /// If true, force the deletion of the owner even if it has repositories.
        member val public Force: bool = false with get, set
        /// The reason for the deletion.
        member val public DeleteReason: string = String.Empty with get, set

    /// Parameters for the /owner/undelete endpoint.
    type UndeleteOwnerParameters() =
        inherit OwnerParameters()

    /// Parameters for the /owner/listOrganizations endpoint.
    type ListOrganizationsParameters() =
        inherit OwnerParameters()