namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.PatchSet
open Grace.Shared.Utilities
open Grace.Types.PatchSet
open Grace.Types.Types

type PatchSet() =

    static member public Create(parameters: CreatePatchSetParameters) =
        postServer<CreatePatchSetParameters, PatchSetDto> (parameters |> ensureCorrelationIdIsSet, $"patchset/{nameof (PatchSet.Create)}")

    static member public Get(parameters: GetPatchSetParameters) =
        postServer<GetPatchSetParameters, PatchSetDto> (parameters |> ensureCorrelationIdIsSet, $"patchset/{nameof (PatchSet.Get)}")
