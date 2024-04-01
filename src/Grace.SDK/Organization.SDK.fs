namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto.Organization
open Grace.Shared.Parameters.Organization
open System

type Organization() =

    /// <summary>
    /// Creates a new organization.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new organization.</param>
    static member public Create(parameters: CreateOrganizationParameters) =
        postServer<CreateOrganizationParameters, String> (parameters |> ensureCorrelationIdIsSet, $"organization/{nameof (Organization.Create)}")

    /// <summary>
    /// Sets the organization's name.
    /// </summary>
    /// <param name="parameters">Values to use when setting the organization name.</param>
    static member public Get(parameters: GetOrganizationParameters) =
        postServer<GetOrganizationParameters, OrganizationDto> (parameters |> ensureCorrelationIdIsSet, $"organization/{nameof (Organization.Get)}")

    /// <summary>
    /// Sets the organization's name.
    /// </summary>
    /// <param name="parameters">Values to use when setting the organization name.</param>
    static member public SetName(parameters: SetOrganizationNameParameters) =
        postServer<SetOrganizationNameParameters, String> (parameters |> ensureCorrelationIdIsSet, $"organization/{nameof (Organization.SetName)}")

    /// <summary>
    /// Sets the organization's type.
    /// </summary>
    /// <param name="parameters">Values to use when setting the organization's type.</param>
    static member public SetType(parameters: SetOrganizationTypeParameters) =
        postServer<SetOrganizationTypeParameters, String> (parameters |> ensureCorrelationIdIsSet, $"organization/{nameof (Organization.SetType)}")

    /// <summary>
    /// Sets the organization's visibility in search results.
    /// </summary>
    /// <param name="parameters">Values to use when setting the search visibility.</param>
    static member public SetSearchVisibility(parameters: SetOrganizationSearchVisibilityParameters) =
        postServer<SetOrganizationSearchVisibilityParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"organization/{nameof (Organization.SetSearchVisibility)}"
        )

    /// <summary>
    /// Sets the organization's description.
    /// </summary>
    /// <param name="parameters">Values to use when setting the organization's description.</param>
    static member public SetDescription(parameters: SetOrganizationDescriptionParameters) =
        postServer<SetOrganizationDescriptionParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"organization/{nameof (Organization.SetDescription)}"
        )

    /// <summary>
    /// Deletes the organization.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the organization.</param>
    static member public Delete(parameters: DeleteOrganizationParameters) =
        postServer<DeleteOrganizationParameters, String> (parameters |> ensureCorrelationIdIsSet, $"organization/{nameof (Organization.Delete)}")

    /// <summary>
    /// Undeletes the organization.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Undelete(parameters: UndeleteOrganizationParameters) =
        postServer<UndeleteOrganizationParameters, String> (parameters |> ensureCorrelationIdIsSet, $"organization/{nameof (Organization.Undelete)}")
