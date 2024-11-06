namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Dto.Repository
open Grace.Shared.Parameters.Repository
open Grace.Shared.Utilities
open Grace.Shared.Types
open System
open System.Collections.Generic
open System.Threading.Tasks

[<AutoOpen>]

type Repository() =

    /// <summary>
    /// Creates a new repository.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new repository.</param>
    static member public Create(parameters: CreateRepositoryParameters) =
        logToConsole $"Creating repository: RepositoryId: {parameters.RepositoryId}; RepositoryName: {parameters.RepositoryName}."
        postServer<CreateRepositoryParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.Create)}")

    /// <summary>
    /// Creates a new repository.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new repository.</param>
    static member public Init(parameters: InitParameters) =
        postServer<InitParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.Init)}")

    /// Checks to see if a repository is empty and ready for initialization.
    static member public IsEmpty(parameters: IsEmptyParameters) =
        postServer<IsEmptyParameters, bool> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.IsEmpty)}")

    /// <summary>
    /// Sets the name of the repository.
    /// </summary>
    /// <param name="parameters">Values to use when setting the status of the repository.</param>
    static member public SetName(parameters: SetRepositoryNameParameters) =
        postServer<SetRepositoryNameParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetName)}")

    /// <summary>
    /// Sets the status of the repository.
    /// </summary>
    /// <param name="parameters">Values to use when setting the status of the repository.</param>
    static member public SetStatus(parameters: SetRepositoryStatusParameters) =
        postServer<SetRepositoryStatusParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetStatus)}")

    /// <summary>
    /// Sets whether the repository is public or private.
    /// </summary>
    /// <param name="parameters">Values to use when setting the repository visibility.</param>
    static member public SetVisibility(parameters: SetRepositoryVisibilityParameters) =
        postServer<SetRepositoryVisibilityParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetVisibility)}")

    /// <summary>
    /// Sets the repository's description.
    /// </summary>
    /// <param name="parameters">Values to use when setting the repository description.</param>
    static member public SetDescription(parameters: SetRepositoryDescriptionParameters) =
        postServer<SetRepositoryDescriptionParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetDescription)}")

    /// <summary>
    /// Sets the repository default for whether saves should be kept.
    /// </summary>
    /// <param name="parameters">Values to use when setting the repository default for whether saves should be kept.</param>
    static member public SetRecordSaves(parameters: RecordSavesParameters) =
        postServer<RecordSavesParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetRecordSaves)}")

    /// Sets the number of days to keep logical deletes in this repository.
    static member public SetLogicalDeleteDays(parameters: SetLogicalDeleteDaysParameters) =
        postServer<SetLogicalDeleteDaysParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetLogicalDeleteDays)}")

    /// <summary>
    /// Sets the number of days to keep saves in this repository.
    /// </summary>
    /// <param name="parameters">Values to use when setting the default save days.</param>
    static member public SetSaveDays(parameters: SetSaveDaysParameters) =
        postServer<SetSaveDaysParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetSaveDays)}")

    /// <summary>
    /// Sets the number of days to keep checkpoints in this repository.
    /// </summary>
    /// <param name="parameters">Values to use when setting the default checkpoint retention time.</param>
    static member public SetCheckpointDays(parameters: SetCheckpointDaysParameters) =
        postServer<SetCheckpointDaysParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetCheckpointDays)}")

    /// Sets the number of days to keep diff results cached in the database.
    static member public SetDiffCacheDays(parameters: SetDiffCacheDaysParameters) =
        postServer<SetDiffCacheDaysParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.SetDiffCacheDays)}")

    /// Sets the number of days to keep recursive directory version contents in the database.
    static member public SetDirectoryVersionCacheDays(parameters: SetDirectoryVersionCacheDaysParameters) =
        postServer<SetDirectoryVersionCacheDaysParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"repository/{nameof (Repository.SetDirectoryVersionCacheDays)}"
        )

    /// <summary>
    /// Sets the default version of the Server API that clients should use when accessing this repository.
    /// </summary>
    /// <param name="parameters">Values to use when setting the default checkpoint retention time.</param>
    static member public SetDefaultServerApiVersion(parameters: SetDefaultServerApiVersionParameters) =
        postServer<SetDefaultServerApiVersionParameters, String> (
            parameters |> ensureCorrelationIdIsSet,
            $"repository/{nameof (Repository.SetDefaultServerApiVersion)}"
        )

    /// <summary>
    /// Deletes the repository.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the repository.</param>
    static member public Delete(parameters: DeleteRepositoryParameters) =
        postServer<DeleteRepositoryParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.Delete)}")

    /// <summary>
    /// Undeletes the repository.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Undelete(parameters: UndeleteRepositoryParameters) =
        postServer<UndeleteRepositoryParameters, String> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.Undelete)}")

    /// Gets the metadata for the repository.
    static member public Get(parameters: RepositoryParameters) =
        postServer<RepositoryParameters, RepositoryDto> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.Get)}")

    /// Gets a list of branches in the repository.
    static member public GetBranches(parameters: GetBranchesParameters) =
        postServer<GetBranchesParameters, IEnumerable<BranchDto>> (parameters |> ensureCorrelationIdIsSet, $"repository/{nameof (Repository.GetBranches)}")

    /// Gets a list of branches in the repository.
    static member public GetBranchesByBranchId(parameters: GetBranchesByBranchIdParameters) =
        postServer<GetBranchesByBranchIdParameters, IEnumerable<BranchDto>> (
            parameters |> ensureCorrelationIdIsSet,
            $"repository/{nameof (Repository.GetBranchesByBranchId)}"
        )

    /// Gets a list of references in a repository, which may be in different branches.
    static member public GetReferencesByReferenceId(parameters: GetReferencesByReferenceIdParameters) =
        postServer<GetReferencesByReferenceIdParameters, IEnumerable<ReferenceDto>> (
            parameters |> ensureCorrelationIdIsSet,
            $"repository/{nameof (Repository.GetReferencesByReferenceId)}"
        )
