namespace Grace.SDK

open Grace.Shared
open Grace.Types.Types
open System.Net.Http

module ClientIdentity =

    let mutable private configuredClientType: ClientType option = None

    let configure (clientType: ClientType) = configuredClientType <- Some clientType

    let clear () = configuredClientType <- None

    let tryGetConfiguredClientType () = configuredClientType

    let private toHeaderValues clientType =
        match clientType with
        | ClientType.CLI version -> "CLI", version

    let applyHeaders (httpClient: HttpClient) =
        match configuredClientType with
        | Some clientType ->
            let clientTypeName, clientVersion = toHeaderValues clientType

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(Constants.ClientTypeHeaderKey, clientTypeName)
            |> ignore

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(Constants.ClientVersionHeaderKey, clientVersion)
            |> ignore
        | None -> ()

        httpClient

    let getHttpClient correlationId =
        Grace.Shared.Utilities.getHttpClient correlationId
        |> applyHeaders
