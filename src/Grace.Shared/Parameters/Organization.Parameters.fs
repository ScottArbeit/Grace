namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Organization =

    type OrganizationParameters() = 
        inherit CommonParameters()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set

    type CreateParameters() = 
        inherit OrganizationParameters()

    type NameParameters() =
        inherit OrganizationParameters()
        member val public NewName: string = String.Empty with get, set
    
    type TypeParameters() =
        inherit OrganizationParameters()
        member val public OrganizationType: string = String.Empty with get, set
        
    type SearchVisibilityParameters() =
        inherit OrganizationParameters()
        member val public SearchVisibility: string = String.Empty with get, set
        
    type DescriptionParameters() =
        inherit OrganizationParameters()
        member val public Description: string = String.Empty with get, set

    type DeleteParameters() =
        inherit OrganizationParameters()
        member val public Force: bool = false with get, set
        member val public DeleteReason: string = String.Empty with get, set

    type UndeleteParameters() =
        inherit OrganizationParameters()
    
    type GetParameters() =
        inherit OrganizationParameters()

    type ListRepositoriesParameters() =
        inherit OrganizationParameters()
