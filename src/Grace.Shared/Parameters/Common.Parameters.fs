namespace Grace.Shared.Parameters

open Orleans
open System

module Common =

    [<GenerateSerializer>]
    type CommonParameters() =
        member val public CorrelationId: string = String.Empty with get, set
        member val public Principal: string = String.Empty with get, set
