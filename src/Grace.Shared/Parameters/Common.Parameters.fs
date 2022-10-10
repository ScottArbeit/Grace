namespace Grace.Shared.Parameters

open System

module Common =

    type CommonParameters() =
        member val public CorrelationId: string = String.Empty with get, set
        member val public Principal: string = String.Empty with get, set
