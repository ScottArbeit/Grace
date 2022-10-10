namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Organization
open System

type Organization() =

    /// <summary>
    /// Creates a new organization.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new organization.</param>
    static member public Create(parameters: CreateParameters) =
        postServer<CreateParameters, String>(parameters |> ensureCorrelationIdIsSet, $"organization/{nameof(Organization.Create)}")

    /// <summary>
    /// Sets the organization's name.
    /// </summary>
    /// <param name="parameters">Values to use when setting the organization name.</param>
    static member public SetName(parameters: NameParameters) =
        postServer<NameParameters, String>(parameters |> ensureCorrelationIdIsSet, $"organization/{nameof(Organization.SetName)}")

    /// <summary>
    /// Sets the organization's type.
    /// </summary>
    /// <param name="parameters">Values to use when setting the organization's type.</param>
    static member public SetType(parameters: TypeParameters) =
        postServer<TypeParameters, String>(parameters |> ensureCorrelationIdIsSet, $"organization/{nameof(Organization.SetType)}")

    /// <summary>
    /// Sets the organization's visibility in search results.
    /// </summary>
    /// <param name="parameters">Values to use when setting the search visibility.</param>
    static member public SetSearchVisibility(parameters: SearchVisibilityParameters) =
        postServer<SearchVisibilityParameters, String>(parameters |> ensureCorrelationIdIsSet, $"organization/{nameof(Organization.SetSearchVisibility)}")

    /// <summary>
    /// Sets the organization's description.
    /// </summary>
    /// <param name="parameters">Values to use when setting the organization's description.</param>
    static member public SetDescription(parameters: DescriptionParameters) =
        postServer<DescriptionParameters, String>(parameters |> ensureCorrelationIdIsSet, $"organization/{nameof(Organization.SetDescription)}")

    /// <summary>
    /// Deletes the organization.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the organization.</param>
    static member public Delete(parameters: DeleteParameters) =
        postServer<DeleteParameters, String>(parameters |> ensureCorrelationIdIsSet, $"organization/{nameof(Organization.Delete)}")

    /// <summary>
    /// Undeletes the organization.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Undelete(parameters: UndeleteParameters) =
        postServer<UndeleteParameters, String>(parameters |> ensureCorrelationIdIsSet, $"organization/{nameof(Organization.Undelete)}")
