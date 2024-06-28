namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Organization =

    /// Common parameters for endpoints in the /organization path.
    type OrganizationParameters() =
        inherit CommonParameters()
        /// The Id of the Owner.
        member val public OwnerId: string = String.Empty with get, set
        /// The name of the Owner.
        member val public OwnerName: string = String.Empty with get, set
        /// The Id of the Organization.
        member val public OrganizationId: string = String.Empty with get, set
        /// The name of the Organization.
        member val public OrganizationName: string = String.Empty with get, set

    /// Parameters for the /organization/create endpoint.
    type CreateOrganizationParameters() =
        inherit OrganizationParameters()

    /// Parameters for the /organization/get endpoint.
    type GetOrganization() =
        inherit OrganizationParameters()
        /// If true, deleted Organizations will be included in the response.
        member val public IncludeDeleted: bool = false with get, set

    /// Parameters for the /organization/setName endpoint.
    type SetOrganizationNameParameters() =
        inherit OrganizationParameters()
        /// The new name of the Organization.
        member val public NewName: string = String.Empty with get, set

    /// Parameters for the /organization/setType endpoint.
    type SetOrganizationTypeParameters() =
        inherit OrganizationParameters()
        /// The new type of the Organization. Must be one of the OrganizationType enum values.
        member val public OrganizationType: string = String.Empty with get, set

    /// Parameters for the /organization/setSearchVisibility endpoint.
    type SetOrganizationSearchVisibilityParameters() =
        inherit OrganizationParameters()
        /// The new search visibility of the Organization. Must be one of the SearchVisibility enum values.
        member val public SearchVisibility: string = String.Empty with get, set

    /// Parameters for the /organization/setDescription endpoint.
    type SetOrganizationDescriptionParameters() =
        inherit OrganizationParameters()
        /// The new description of the Organization.
        member val public Description: string = String.Empty with get, set

    /// Parameters for the /organization/delete endpoint.
    type DeleteOrganizationParameters() =
        inherit OrganizationParameters()
        /// If true, the Organization will be deleted even if it has Repositories.
        member val public Force: bool = false with get, set
        /// The reason for deleting the Organization.
        member val public DeleteReason: string = String.Empty with get, set

    /// Parameters for the /organization/undelete endpoint.
    type UndeleteOrganizationParameters() =
        inherit OrganizationParameters()

    /// Parameters for the /organization/get endpoint.
    type GetOrganizationParameters() =
        inherit OrganizationParameters()
        member val public IncludeDeleted = false with get, set

    /// Parameters for the /organization/listRepositories endpoint.
    type ListRepositoriesParameters() =
        inherit OrganizationParameters()
