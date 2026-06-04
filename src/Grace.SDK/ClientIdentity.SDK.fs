namespace Grace.SDK

open Grace.Shared
open Grace.Types.Common
open System
open System.Collections.Generic
open System.Diagnostics
open System.Net.Http
open System.Reflection

module ClientIdentity =

    [<Literal>]
    let LifecycleStatusPropertyKey = "SdkLifecycle.Status"

    [<Literal>]
    let LifecycleUnsupportedAfterPropertyKey = "SdkLifecycle.UnsupportedAfter"

    [<Literal>]
    let LifecycleUnsupportedAfterIsMalformedPropertyKey = "SdkLifecycle.UnsupportedAfterIsMalformed"

    [<Literal>]
    let LifecycleMinimumVersionPropertyKey = "SdkLifecycle.MinimumVersion"

    [<Literal>]
    let LifecycleRecommendedVersionPropertyKey = "SdkLifecycle.RecommendedVersion"

    [<Literal>]
    let LifecycleUpdateUrlPropertyKey = "SdkLifecycle.UpdateUrl"

    [<Literal>]
    let LifecycleUpdateUrlIsHttpsPropertyKey = "SdkLifecycle.UpdateUrlIsHttps"

    type LifecycleDiagnostics =
        {
            Status: string option
            UnsupportedAfter: string option
            UnsupportedAfterIsMalformed: bool
            MinimumVersion: string option
            RecommendedVersion: string option
            UpdateUrl: string option
            UpdateUrlIsHttps: bool option
        }

    let private getSdkAssemblyFileVersion () =
        let assembly = Assembly.GetExecutingAssembly()

        if String.IsNullOrWhiteSpace assembly.Location then
            $"{assembly.GetName().Version}"
        else
            let fileVersion =
                FileVersionInfo
                    .GetVersionInfo(
                        assembly.Location
                    )
                    .FileVersion

            if String.IsNullOrWhiteSpace fileVersion then
                $"{assembly.GetName().Version}"
            else
                fileVersion

    let mutable private configuredClientType: ClientType option = Some(ClientType.CLI(getSdkAssemblyFileVersion ()))
    let mutable private configuredApiContractVersion: string option = None

    let configure (clientType: ClientType) = configuredClientType <- Some clientType

    let configureApiContractVersion apiContractVersion =
        match ApiContractVersion.normalize apiContractVersion with
        | Ok normalized -> configuredApiContractVersion <- Some normalized
        | Error message -> invalidArg (nameof apiContractVersion) message

    let clearApiContractVersionOverride () = configuredApiContractVersion <- None

    let clear () =
        configuredClientType <- Some(ClientType.CLI(getSdkAssemblyFileVersion ()))
        configuredApiContractVersion <- None

    let tryGetConfiguredClientType () = configuredClientType

    let tryGetConfiguredApiContractVersion () = configuredApiContractVersion

    let private toHeaderValues clientType =
        match clientType with
        | ClientType.CLI version -> "CLI", version

    let applyHeaders (httpClient: HttpClient) =
        let apiContractVersion =
            configuredApiContractVersion
            |> Option.defaultValue ApiContractVersion.CurrentReleased

        httpClient.DefaultRequestHeaders.Remove(Constants.ServerApiVersionHeaderKey)
        |> ignore

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(Constants.ServerApiVersionHeaderKey, apiContractVersion)
        |> ignore

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

    let private tryGetHeader (response: HttpResponseMessage) headerName =
        match response.Headers.TryGetValues headerName with
        | true, values ->
            values
            |> Seq.tryFind (fun value -> not <| String.IsNullOrWhiteSpace value)
            |> Option.map (fun value -> value.Trim())
        | _ -> None

    let private hasMalformedUnsupportedAfter unsupportedAfter =
        match unsupportedAfter with
        | Some (value: string) ->
            let mutable parsed = Unchecked.defaultof<DateOnly>
            not <| DateOnly.TryParse(value, &parsed)
        | None -> false

    let private tryGetHttpsUpdateUrl updateUrl =
        match updateUrl with
        | Some value ->
            let mutable uri = Unchecked.defaultof<Uri>

            if Uri.TryCreate(value, UriKind.Absolute, &uri) then
                Some(uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            else
                Some false
        | None -> None

    let parseLifecycleDiagnostics (response: HttpResponseMessage) =
        let diagnostics =
            {
                Status = tryGetHeader response Constants.SdkLifecycleStatusHeaderKey
                UnsupportedAfter = tryGetHeader response Constants.SdkLifecycleUnsupportedAfterHeaderKey
                UnsupportedAfterIsMalformed =
                    tryGetHeader response Constants.SdkLifecycleUnsupportedAfterHeaderKey
                    |> hasMalformedUnsupportedAfter
                MinimumVersion = tryGetHeader response Constants.SdkLifecycleMinimumVersionHeaderKey
                RecommendedVersion = tryGetHeader response Constants.SdkLifecycleRecommendedVersionHeaderKey
                UpdateUrl = tryGetHeader response Constants.SdkLifecycleUpdateUrlHeaderKey
                UpdateUrlIsHttps =
                    tryGetHeader response Constants.SdkLifecycleUpdateUrlHeaderKey
                    |> tryGetHttpsUpdateUrl
            }

        if diagnostics.Status.IsSome
           || diagnostics.UnsupportedAfter.IsSome
           || diagnostics.MinimumVersion.IsSome
           || diagnostics.RecommendedVersion.IsSome
           || diagnostics.UpdateUrl.IsSome then
            Some diagnostics
        else
            None

    let private addIfSome (properties: Dictionary<string, obj>) key value =
        match value with
        | Some value -> properties[key] <- value
        | None -> ()

    let lifecycleDiagnosticsToProperties diagnostics =
        let properties = Dictionary<string, obj>()

        addIfSome properties LifecycleStatusPropertyKey diagnostics.Status
        addIfSome properties LifecycleUnsupportedAfterPropertyKey diagnostics.UnsupportedAfter
        properties[LifecycleUnsupportedAfterIsMalformedPropertyKey] <- diagnostics.UnsupportedAfterIsMalformed
        addIfSome properties LifecycleMinimumVersionPropertyKey diagnostics.MinimumVersion
        addIfSome properties LifecycleRecommendedVersionPropertyKey diagnostics.RecommendedVersion
        addIfSome properties LifecycleUpdateUrlPropertyKey diagnostics.UpdateUrl
        addIfSome properties LifecycleUpdateUrlIsHttpsPropertyKey diagnostics.UpdateUrlIsHttps
        properties

    let enhanceWithLifecycleDiagnostics (response: HttpResponseMessage) (result: GraceResult<'T>) =
        match parseLifecycleDiagnostics response with
        | Some diagnostics ->
            let properties = lifecycleDiagnosticsToProperties diagnostics

            match result with
            | Ok graceReturnValue -> graceReturnValue.enhance properties |> Ok
            | Error graceError -> graceError.enhance properties |> Error
        | None -> result

    let okWithLifecycleDiagnostics (response: HttpResponseMessage) (graceReturnValue: GraceReturnValue<'T>) =
        enhanceWithLifecycleDiagnostics response (Ok graceReturnValue)

    let errorWithLifecycleDiagnostics (response: HttpResponseMessage) (graceError: GraceError) = enhanceWithLifecycleDiagnostics response (Error graceError)
