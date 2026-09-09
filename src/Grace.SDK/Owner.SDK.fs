namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Owner
open Grace.Types.Owner
open Grace.Types.Common
open Grace.Types.UsageObservation
open Grace.Shared.Parameters.Repository
open System
open System.Threading.Tasks

/// SDK entry point for owner profile, visibility, lifecycle, and lookup endpoints.
type Owner() =

    /// Reads one retained declaration using explicit historical scope, preserving all quantity and timestamp digits.
    static member GetDirectoryVersionObservation
        (
            observationId: Guid,
            parameters: GetRepositoryParameters
        ) : Task<GraceResult<DirectoryVersionSizeObservation>> =
        task {
            /// Requires a supplied nonempty GUID without reading current configuration.
            let validId (value: string) =
                match Guid.TryParse value with
                | true, id -> id <> Guid.Empty
                | _ -> false

            if isNull (box parameters) then
                return Error(GraceError.Create "Explicit owner, organization and repository IDs are required." "")
            elif observationId = Guid.Empty
                 || not (
                     validId parameters.OwnerId
                     && validId parameters.OrganizationId
                     && validId parameters.RepositoryId
                 )
                 || [
                     parameters.OwnerName
                     parameters.OrganizationName
                     parameters.RepositoryName
                    ]
                    |> List.exists (String.IsNullOrEmpty >> not) then
                return
                    Error(
                        GraceError.Create
                            "Use a nonempty ObservationId and explicit owner, organization and repository GUIDs without name selectors."
                            parameters.CorrelationId
                    )
            else
                let query =
                    [
                        "OwnerId", parameters.OwnerId
                        "OrganizationId", parameters.OrganizationId
                        "RepositoryId", parameters.RepositoryId
                    ]
                    |> List.map (fun (key, value) -> $"{key}={Uri.EscapeDataString value}")
                    |> String.concat "&"

                return!
                    getServer<GetRepositoryParameters, DirectoryVersionSizeObservation> (
                        ensureCorrelationIdIsSet parameters,
                        $"owner/usage/directory-version-observations/{observationId:D}?{query}"
                    )
        }

    /// <summary>
    /// Registers an owner profile that can contain organizations and repositories.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new owner.</param>
    static member public Create(parameters: CreateOwnerParameters) =
        postServer<CreateOwnerParameters, String> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.Create)}")

    /// <summary>
    /// Gets the owner's information.
    /// </summary>
    /// <param name="parameters">Values to use when retrieving the owner information.</param>
    static member public Get(parameters: GetOwnerParameters) =
        postServer<GetOwnerParameters, OwnerDto> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.Get)}")

    /// <summary>
    /// Sets the owner's name.
    /// </summary>
    /// <param name="parameters">Values to use when setting the owner name.</param>
    static member public SetName(parameters: SetOwnerNameParameters) =
        postServer<SetOwnerNameParameters, String> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.SetName)}")

    /// <summary>
    /// Sets the owner's type.
    /// </summary>
    /// <param name="parameters">Values to use when setting the owner type.</param>
    static member public SetType(parameters: SetOwnerTypeParameters) =
        postServer<SetOwnerTypeParameters, String> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.SetType)}")

    /// <summary>
    /// Sets the owner's visibility in search results.
    /// </summary>
    /// <param name="parameters">Values to use when setting the search visibility.</param>
    static member public SetSearchVisibility(parameters: SetOwnerSearchVisibilityParameters) =
        postServer<SetOwnerSearchVisibilityParameters, String> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.SetSearchVisibility)}")

    /// <summary>
    /// Sets the owner's description.
    /// </summary>
    /// <param name="parameters">Values to use when setting the owner's description.</param>
    static member public SetDescription(parameters: SetOwnerDescriptionParameters) =
        postServer<SetOwnerDescriptionParameters, String> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.SetDescription)}")

    /// <summary>
    /// Deletes the owner.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Delete(parameters: DeleteOwnerParameters) =
        postServer<DeleteOwnerParameters, String> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.Delete)}")

    /// <summary>
    /// Undeletes the owner.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Undelete(parameters: UndeleteOwnerParameters) =
        postServer<UndeleteOwnerParameters, String> (parameters |> ensureCorrelationIdIsSet, $"owner/{nameof (Owner.Undelete)}")
