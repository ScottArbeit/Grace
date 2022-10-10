namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open System

module Owner =

    type OwnerParameters() = 
        inherit CommonParameters()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set

    type CreateParameters() = 
        inherit OwnerParameters()

    type SetNameParameters() =
        inherit OwnerParameters()
        member val public NewName: string = String.Empty with get, set

    type SetTypeParameters() =
        inherit OwnerParameters()
        member val public OwnerType: string = String.Empty with get, set

    type SetSearchVisibilityParameters() =
        inherit OwnerParameters()
        member val public SearchVisibility: string = String.Empty with get, set
        
    type SetDescriptionParameters() =
        inherit OwnerParameters()
        member val public Description: string = String.Empty with get, set
        
    type GetParameters() =
        inherit OwnerParameters()

    type DeleteParameters() =
        inherit OwnerParameters()
        member val public Force: bool = false with get, set
        member val public DeleteReason: string = String.Empty with get, set

    type UndeleteParameters() =
        inherit OwnerParameters()

    type ListOrganizationsParameters() =
        inherit OwnerParameters()