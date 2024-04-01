namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto.Owner
open Grace.Shared.Parameters.Owner
open Grace.Shared.Types
open System
open System.Threading.Tasks

type Owner() =

    /// <summary>
    /// Creates a new owner.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new owner.</param>
    static member public Create(parameters: CreateOwnerParameters) =
        postServer<CreateOwnerParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"owner/{nameof (Owner.Create)}"
        )

    /// <summary>
    /// Sets the owner's name.
    /// </summary>
    /// <param name="parameters">Values to use when setting the owner name.</param>
    static member public Get(parameters: GetOwnerParameters) =
        postServer<GetOwnerParameters, OwnerDto> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.Get)}")

    /// <summary>
    /// Sets the owner's name.
    /// </summary>
    /// <param name="parameters">Values to use when setting the owner name.</param>
    static member public SetName(parameters: SetOwnerNameParameters) =
        postServer<SetOwnerNameParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"owner/{nameof (Owner.SetName)}"
        )

    /// <summary>
    /// Sets the owner's type.
    /// </summary>
    /// <param name="parameters">Values to use when setting the owner type.</param>
    static member public SetType(parameters: SetOwnerTypeParameters) =
        postServer<SetOwnerTypeParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"owner/{nameof (Owner.SetType)}"
        )

    /// <summary>
    /// Sets the owner's visibility in search results.
    /// </summary>
    /// <param name="parameters">Values to use when setting the search visibility.</param>
    static member public SetSearchVisibility(parameters: SetOwnerSearchVisibilityParameters) =
        postServer<SetOwnerSearchVisibilityParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"owner/{nameof (Owner.SetSearchVisibility)}"
        )

    /// <summary>
    /// Sets the owner's description.
    /// </summary>
    /// <param name="parameters">Values to use when setting the owner's description.</param>
    static member public SetDescription(parameters: SetOwnerDescriptionParameters) =
        postServer<SetOwnerDescriptionParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"owner/{nameof (Owner.SetDescription)}"
        )

    /// <summary>
    /// Deletes the owner.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Delete(parameters: DeleteOwnerParameters) =
        postServer<DeleteOwnerParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"owner/{nameof (Owner.Delete)}"
        )

    /// <summary>
    /// Undeletes the owner.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Undelete(parameters: UndeleteOwnerParameters) =
        postServer<UndeleteOwnerParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"owner/{nameof (Owner.Undelete)}"
        )
