namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Diff
open Grace.Shared.Dto.Reference
open Grace.Shared.Parameters.Branch
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.Threading.Tasks

/// The Branch module provides a set of functions for interacting with branches in the Grace API.
type Branch() =

    /// Creates a new branch.
    static member public Create(parameters: CreateBranchParameters) =
        postServer<CreateBranchParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Create)}")

    /// Rebases a branch on a promotion from the parent branch.
    static member public Rebase(parameters: RebaseParameters) =
        postServer<RebaseParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Rebase)}")

    /// Assigns a specific version of the repository to be the next promotion in a branch.
    static member public Assign(parameters: AssignParameters) =
        postServer<AssignParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Assign)}")

    /// Creates a promotion reference in the parent branch of this branch.
    static member public Promote(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Promote)}")

    /// Creates a commit reference in this branch.
    static member public Commit(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Commit)}")

    /// Creates a checkpoint reference in this branch.
    static member public Checkpoint(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Checkpoint)}")

    /// Creates a save reference in this branch.
    static member public Save(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Save)}")

    /// Creates a tag reference in this branch.
    static member public Tag(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Tag)}")

    /// Creates an external reference in this branch.
    static member public CreateExternal(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.CreateExternal)}")

    /// Sets the flag to allow `grace assign` in this branch.
    static member public EnableAssign(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnableAssign)}")

    /// Sets the flag to allow promotion in this branch.
    static member public EnablePromotion(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnablePromotion)}")

    /// Sets the flag to allow commits in this branch.
    static member public EnableCommit(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnableCommit)}")

    /// Sets the flag to allow checkpoints in this branch.
    static member public EnableCheckpoint(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnableCheckpoint)}")

    /// Sets the flag to allow saves in this branch.
    static member public EnableSave(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnableSave)}")

    /// Sets the flag to allow tags in this branch.
    static member public EnableTag(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnableTag)}")

    /// Sets the flag to allow external references in this branch.
    static member public EnableExternal(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnableExternal)}")

    /// Sets the flag to allow auto-rebase in this branch.
    static member public EnableAutoRebase(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.EnableAutoRebase)}")

    /// Gets the diffs between a set of references.
    static member public GetDiffsForReferenceType(parameters: GetDiffsForReferenceTypeParameters) =
        postServer<GetDiffsForReferenceTypeParameters, (IReadOnlyList<ReferenceDto> * IReadOnlyList<DiffDto>)> (
            parameters |> ensureCorrelationIdIsSet,
            $"branch/{nameof (Branch.GetDiffsForReferenceType)}"
        )

    /// Gets the metadata for a specific reference from a branch.
    static member public GetReference(parameters: GetReferenceParameters) =
        postServer<GetReferenceParameters, ReferenceDto> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetReference)}")

    /// Gets the references from a branch.
    static member public GetReferences(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetReferences)}")

    /// Gets the promotions from a branch.
    static member public GetPromotions(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetPromotions)}")

    /// Gets the commits from a branch.
    static member public GetCommits(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetCommits)}")

    /// Gets the checkpoints from a branch.
    static member public GetCheckpoints(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetCheckpoints)}")

    /// Gets the saves from a branch.
    static member public GetSaves(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetSaves)}")

    /// Gets the tags from a branch.
    static member public GetTags(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetTags)}")

    /// Gets the external references from a branch.
    static member public GetExternals(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetExternals)}")

    /// Sets the name of a branch.
    static member public SetName(parameters: SetBranchNameParameters) =
        postServer<SetBranchNameParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.SetName)}")

    /// Gets the metadata for a branch.
    static member public Get(parameters: GetBranchParameters) =
        postServer<GetBranchParameters, BranchDto> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Get)}")

    /// Gets the events handled by a branch.
    static member public GetEvents(parameters: GetBranchVersionParameters) =
        postServer<GetBranchVersionParameters, IEnumerable<string>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetEvents)}")

    /// Gets the metadata for the parent branch.
    static member public GetParentBranch(parameters: BranchParameters) =
        postServer<BranchParameters, BranchDto> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetParentBranch)}")

    /// Gets a specific version of a branch from the server.
    static member public GetVersion(parameters: GetBranchVersionParameters) =
        postServer<GetBranchVersionParameters, IEnumerable<DirectoryVersionId>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetVersion)}")

    /// Gets the DirectoryVersions for a specific version of a branch from the server.
    static member public ListContents(parameters: ListContentsParameters) =
        postServer<ListContentsParameters, IEnumerable<DirectoryVersion>> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.ListContents)}")

    /// Gets the recursive size of a branch.
    static member public GetRecursiveSize(parameters: ListContentsParameters) =
        postServer<ListContentsParameters, int64> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.GetRecursiveSize)}")

    /// Delete the branch.
    static member public Delete(parameters: DeleteBranchParameters) =
        postServer<DeleteBranchParameters, string> (parameters |> ensureCorrelationIdIsSet, $"branch/{nameof (Branch.Delete)}")
