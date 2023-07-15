namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Organization =

    /// Parameters for many endpoints in the /organization path.
    type OrganizationParameters() = 
        inherit CommonParameters()
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set

    /// Parameters for the /organization/create endpoint.
    type CreateOrganizationParameters() = 
        inherit OrganizationParameters()

    /// Parameters for the /organization/setName endpoint.
    type SetOrganizationNameParameters() =
        inherit OrganizationParameters()
        member val public NewName: string = String.Empty with get, set
    
    /// Parameters for the /organization/setType endpoint.
    type SetOrganizationTypeParameters() =
        inherit OrganizationParameters()
        member val public OrganizationType: string = String.Empty with get, set
        
    /// Parameters for the /organization/setSearchVisibility endpoint.
    type SetOrganizationSearchVisibilityParameters() =
        inherit OrganizationParameters()
        member val public SearchVisibility: string = String.Empty with get, set
        
    /// Parameters for the /organization/setDescription endpoint.
    type SetOrganizationDescriptionParameters() =
        inherit OrganizationParameters()
        member val public Description: string = String.Empty with get, set

    /// Parameters for the /organization/delete endpoint.
    type DeleteOrganizationParameters() =
        inherit OrganizationParameters()
        member val public Force: bool = false with get, set
        member val public DeleteReason: string = String.Empty with get, set

    /// Parameters for the /organization/undelete endpoint.
    type UndeleteOrganizationParameters() =
        inherit OrganizationParameters()
    
    /// Parameters for the /organization/get endpoint.
    type GetOrganizationParameters() =
        inherit OrganizationParameters()

    /// Parameters for the /organization/listRepositories endpoint.
    type ListRepositoriesParameters() =
        inherit OrganizationParameters()
