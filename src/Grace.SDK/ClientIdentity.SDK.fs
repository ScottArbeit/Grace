namespace Grace.SDK

open Grace.Shared
open Grace.Types.Common
open System.Net.Http

module ClientIdentity =

    let mutable private configuredClientType: ClientType option = None
    let mutable private configuredApiContractVersion: string option = None

    let configure (clientType: ClientType) = configuredClientType <- Some clientType

    let configureApiContractVersion apiContractVersion =
        match ApiContractVersion.normalize apiContractVersion with
        | Ok normalized -> configuredApiContractVersion <- Some normalized
        | Error message -> invalidArg (nameof apiContractVersion) message

    let clearApiContractVersionOverride () = configuredApiContractVersion <- None

    let clear () =
        configuredClientType <- None
        configuredApiContractVersion <- None

    let tryGetConfiguredClientType () = configuredClientType

    let tryGetConfiguredApiContractVersion () = configuredApiContractVersion

    let private toHeaderValues clientType =
        match clientType with
        | ClientType.CLI version -> "CLI", version

    let applyHeaders (httpClient: HttpClient) =
        match configuredApiContractVersion with
        | Some apiContractVersion ->
            httpClient.DefaultRequestHeaders.Remove(Constants.ServerApiVersionHeaderKey)
            |> ignore

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(Constants.ServerApiVersionHeaderKey, apiContractVersion)
            |> ignore
        | None -> ()

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
