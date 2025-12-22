namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Change
open Grace.Shared.Utilities
open Grace.Types.Change
open Grace.Types.Types

type Change() =

    static member public Create(parameters: CreateChangeParameters) =
        postServer<CreateChangeParameters, ChangeDto> (parameters |> ensureCorrelationIdIsSet, $"change/{nameof (Change.Create)}")

    static member public UpdateMetadata(parameters: UpdateChangeMetadataParameters) =
        postServer<UpdateChangeMetadataParameters, ChangeDto> (parameters |> ensureCorrelationIdIsSet, $"change/update-metadata")

    static member public Get(parameters: GetChangeParameters) =
        postServer<GetChangeParameters, ChangeDto> (parameters |> ensureCorrelationIdIsSet, $"change/{nameof (Change.Get)}")

    static member public List(parameters: ListChangesParameters) =
        postServer<ListChangesParameters, ChangeDto array> (parameters |> ensureCorrelationIdIsSet, $"change/{nameof (Change.List)}")
