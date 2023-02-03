namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Dto.Repository
open Grace.Shared.Parameters.Repository
open System
open System.Collections.Generic

type Repository() =

    /// <summary>
    /// Creates a new repository.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new repository.</param>
    static member public Create(parameters: CreateParameters) =
        postServer<CreateParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.Create)}")
    
    /// <summary>
    /// Creates a new repository.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new repository.</param>
    static member public Init(parameters: InitParameters) =
        postServer<InitParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.Init)}")

    /// <summary>
    /// Sets the name of the repository.
    /// </summary>
    /// <param name="parameters">Values to use when setting the status of the repository.</param>
    static member public SetName(parameters: SetNameParameters) =
        postServer<SetNameParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetName)}")

    /// <summary>
    /// Sets the status of the repository.
    /// </summary>
    /// <param name="parameters">Values to use when setting the status of the repository.</param>
    static member public SetStatus(parameters: StatusParameters) =
        postServer<StatusParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetStatus)}")
    
    /// <summary>
    /// Sets whether the repository is public or private.
    /// </summary>
    /// <param name="parameters">Values to use when setting the repository visibility.</param>
    static member public SetVisibility(parameters: VisibilityParameters) =
        postServer<VisibilityParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetVisibility)}")

    /// <summary>
    /// Sets the repository's description.
    /// </summary>
    /// <param name="parameters">Values to use when setting the repository description.</param>
    static member public SetDescription(parameters: DescriptionParameters) =
        postServer<DescriptionParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetDescription)}")
    
    /// <summary>
    /// Sets the repository default for whether saves should be kept.
    /// </summary>
    /// <param name="parameters">Values to use when setting the repository default for whether saves should be kept.</param>
    static member public SetRecordSaves(parameters: RecordSavesParameters) =
        postServer<RecordSavesParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetRecordSaves)}")
    
    /// <summary>
    /// Sets the default number of days to keep saves.
    /// </summary>
    /// <param name="parameters">Values to use when setting the default save days.</param>
    static member public SetSaveDays(parameters: SaveDaysParameters) =
        postServer<SaveDaysParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetSaveDays)}")

    /// <summary>
    /// Sets the default number of days to keep checkpoints.
    /// </summary>
    /// <param name="parameters">Values to use when setting the default checkpoint retention time.</param>
    static member public SetCheckpointDays(parameters: CheckpointDaysParameters) =
        postServer<CheckpointDaysParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetCheckpointDays)}")

    /// Enables or disables single-step promotion.
    static member public EnableSingleStepPromotion (parameters: EnablePromotionTypeParameters) =
        postServer<EnablePromotionTypeParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.EnableSingleStepPromotion)}")

    /// Enables or disables complex promotion.
    static member public EnableComplexPromotion (parameters: EnablePromotionTypeParameters) =
        postServer<EnablePromotionTypeParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.EnableComplexPromotion)}")

    /// <summary>
    /// Sets the default number of days to keep checkpoints.
    /// </summary>
    /// <param name="parameters">Values to use when setting the default checkpoint retention time.</param>
    static member public SetDefaultServerApiVersion(parameters: DefaultServerApiVersionParameters) =
        postServer<DefaultServerApiVersionParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.SetDefaultServerApiVersion)}")

    /// <summary>
    /// Deletes the repository.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the repository.</param>
    static member public Delete(parameters: DeleteParameters) =
        postServer<DeleteParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.Delete)}")

    /// <summary>
    /// Undeletes the repository.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the owner.</param>
    static member public Undelete(parameters: UndeleteParameters) =
        postServer<UndeleteParameters, String>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.Undelete)}")

    /// Gets the metadata for the repository.
    static member public Get(parameters: RepositoryParameters) =
        postServer<RepositoryParameters, RepositoryDto>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.Get)}")

    /// Gets a list of branches in the repository.
    static member public GetBranches(parameters: GetBranchesParameters) =
        postServer<GetBranchesParameters, IEnumerable<BranchDto>>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.GetBranches)}")

    /// Gets a list of branches in the repository.
    static member public GetBranchesByBranchId(parameters: GetBranchesByBranchIdParameters) =
        postServer<GetBranchesByBranchIdParameters, IEnumerable<BranchDto>>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.GetBranchesByBranchId)}")

    /// Gets a list of references in a repository, which may be in different branches.
    static member public GetReferencesByReferenceId(parameters: GetReferencesByReferenceIdParameters) =
        postServer<GetReferencesByReferenceIdParameters, IEnumerable<ReferenceDto>>(parameters |> ensureCorrelationIdIsSet, $"repository/{nameof(Repository.GetReferencesByReferenceId)}")
