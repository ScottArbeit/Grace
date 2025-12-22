namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Stack =

    [<GenerateSerializer>]
    type StackLayer =
        { ChangeId: ChangeId
          BranchId: BranchId
          Order: int }

        static member Default =
            { ChangeId = ChangeId.Empty
              BranchId = BranchId.Empty
              Order = 0 }

    [<GenerateSerializer>]
    type StackDto =
        { StackId: StackId
          RepositoryId: RepositoryId
          BaseBranchId: BranchId
          Layers: StackLayer array
          CreatedAt: Instant }

        static member Default =
            { StackId = StackId.Empty
              RepositoryId = RepositoryId.Empty
              BaseBranchId = BranchId.Empty
              Layers = Array.empty
              CreatedAt = Constants.DefaultTimestamp }

    [<KnownType("GetKnownTypes")>]
    type StackCommand =
        | Create of stackId: StackId * repositoryId: RepositoryId * baseBranchId: BranchId * layers: StackLayer array
        | AddLayer of layer: StackLayer
        | RemoveLayer of changeId: ChangeId
        | ReorderLayers of layers: StackLayer array
        | UpdateBaseBranch of baseBranchId: BranchId

        static member GetKnownTypes() = GetKnownTypes<StackCommand>()

    [<KnownType("GetKnownTypes")>]
    type StackEventType =
        | Created of stackId: StackId * repositoryId: RepositoryId * baseBranchId: BranchId * layers: StackLayer array
        | LayerAdded of StackLayer
        | LayerRemoved of ChangeId
        | LayersReordered of StackLayer array
        | BaseBranchUpdated of BranchId

        static member GetKnownTypes() = GetKnownTypes<StackEventType>()

    type StackEvent =
        { Event: StackEventType
          Metadata: EventMetadata }

    type StackDto with
        static member UpdateDto stackEvent currentDto =
            match stackEvent.Event with
            | Created(stackId, repositoryId, baseBranchId, layers) ->
                { currentDto with
                    StackId = stackId
                    RepositoryId = repositoryId
                    BaseBranchId = baseBranchId
                    Layers = layers
                    CreatedAt = stackEvent.Metadata.Timestamp }
            | LayerAdded layer ->
                { currentDto with Layers = Array.append currentDto.Layers [| layer |] }
            | LayerRemoved changeId ->
                { currentDto with Layers = currentDto.Layers |> Array.filter (fun layer -> layer.ChangeId <> changeId) }
            | LayersReordered layers -> { currentDto with Layers = layers }
            | BaseBranchUpdated baseBranchId -> { currentDto with BaseBranchId = baseBranchId }
