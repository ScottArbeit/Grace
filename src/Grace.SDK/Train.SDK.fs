namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Train
open Grace.Shared.Utilities
open Grace.Types.Train
open Grace.Types.Types

type Train() =

    static member public Create(parameters: CreateTrainParameters) =
        postServer<CreateTrainParameters, TrainDto> (parameters |> ensureCorrelationIdIsSet, $"train/{nameof (Train.Create)}")

    static member public Enqueue(parameters: EnqueueTrainParameters) =
        postServer<EnqueueTrainParameters, TrainDto> (parameters |> ensureCorrelationIdIsSet, $"train/enqueue")

    static member public Dequeue(parameters: DequeueTrainParameters) =
        postServer<DequeueTrainParameters, TrainDto> (parameters |> ensureCorrelationIdIsSet, $"train/dequeue")

    static member public Build(parameters: BuildTrainParameters) =
        postServer<BuildTrainParameters, OperationId> (parameters |> ensureCorrelationIdIsSet, $"train/build")

    static member public Apply(parameters: ApplyTrainParameters) =
        postServer<ApplyTrainParameters, OperationId> (parameters |> ensureCorrelationIdIsSet, $"train/apply")

    static member public Get(parameters: GetTrainParameters) =
        postServer<GetTrainParameters, TrainDto> (parameters |> ensureCorrelationIdIsSet, $"train/{nameof (Train.Get)}")
