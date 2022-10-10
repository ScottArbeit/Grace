namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Dto.Diff
open Grace.Shared.Parameters.Diff
open Grace.Shared.Types
open System
open System.Threading.Tasks

type Diff() =
    /// Gets a diff between two directory versions by DirectoryId's.
    static member public GetDiff(parameters: GetDiffParameters) =
        postServer<GetDiffParameters, DiffDto>(parameters |> ensureCorrelationIdIsSet, $"diff/{nameof(Diff.GetDiff)}")

    /// Gets a diff between two directory versions by Sha256Hash.
    static member public GetDiffBySha256Hash(parameters: GetDiffBySha256HashParameters) =
        postServer<GetDiffBySha256HashParameters, DiffDto>(parameters |> ensureCorrelationIdIsSet, $"diff/{nameof(Diff.GetDiffBySha256Hash)}")

    /// Populates the diff between two directory versions by DirectoryId's.
    static member public Populate(parameters: PopulateParameters) =
        postServer<PopulateParameters, string>(parameters |> ensureCorrelationIdIsSet, $"diff/{nameof(Diff.Populate)}")
