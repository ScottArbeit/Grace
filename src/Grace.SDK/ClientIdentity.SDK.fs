namespace Grace.SDK

open Grace.Shared
open Grace.Types.Common
open System
open System.Collections.Generic
open System.Diagnostics
open System.Net.Http
open System.Reflection

/// Applies SDK client identity, API contract version, and lifecycle diagnostics to HTTP calls.
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

    /// Lifecycle advisory headers returned by the server for SDK version support and update guidance.
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

    /// Reads the SDK assembly file version used as the default CLI client version.
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

    /// Sets the client type and version stamped into future SDK request headers.
    let configure (clientType: ClientType) = configuredClientType <- Some clientType

    /// Normalizes and overrides the API contract version header sent with SDK requests.
    let configureApiContractVersion apiContractVersion =
        match ApiContractVersion.normalize apiContractVersion with
        | Ok normalized -> configuredApiContractVersion <- Some normalized
        | Error message -> invalidArg (nameof apiContractVersion) message

    /// Clears the API contract version override so requests use the released default.
    let clearApiContractVersionOverride () = configuredApiContractVersion <- None

    /// Restores default SDK identity headers and removes any API contract version override.
    let clear () =
        configuredClientType <- Some(ClientType.CLI(getSdkAssemblyFileVersion ()))
        configuredApiContractVersion <- None

    /// Exposes the currently configured client identity for tests and host diagnostics.
    let tryGetConfiguredClientType () = configuredClientType

    /// Exposes the optional API contract version override for tests and host diagnostics.
    let tryGetConfiguredApiContractVersion () = configuredApiContractVersion

    /// Converts a client identity into the type and version header values expected by Grace Server.
    let private toHeaderValues clientType =
        match clientType with
        | ClientType.CLI version -> "CLI", version

    /// Rewrites SDK identity and API contract headers on the supplied HTTP client.
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
            /// Header-ready client identity values derived from the configured client type.
            let clientTypeName, clientVersion = toHeaderValues clientType

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(Constants.ClientTypeHeaderKey, clientTypeName)
            |> ignore

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(Constants.ClientVersionHeaderKey, clientVersion)
            |> ignore
        | None -> ()

        httpClient

    /// Creates a Grace HTTP client with the current SDK identity headers already applied.
    let getHttpClient correlationId =
        Grace.Shared.Utilities.getHttpClient correlationId
        |> applyHeaders

    /// Reads the first non-empty value for a response header and trims surrounding whitespace.
    let private tryGetHeader (response: HttpResponseMessage) headerName =
        match response.Headers.TryGetValues headerName with
        | true, values ->
            values
            |> Seq.tryFind (fun value -> not <| String.IsNullOrWhiteSpace value)
            |> Option.map (fun value -> value.Trim())
        | _ -> None

    /// Detects lifecycle unsupported-after values that are present but not valid ISO dates.
    let private hasMalformedUnsupportedAfter unsupportedAfter =
        match unsupportedAfter with
        | Some (value: string) ->
            let mutable parsed = Unchecked.defaultof<DateOnly>
            not <| DateOnly.TryParse(value, &parsed)
        | None -> false

    /// Reports whether the lifecycle update URL is an absolute HTTPS URL when one is supplied.
    let private tryGetHttpsUpdateUrl updateUrl =
        match updateUrl with
        | Some value ->
            let mutable uri = Unchecked.defaultof<Uri>

            if Uri.TryCreate(value, UriKind.Absolute, &uri) then
                Some(uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            else
                Some false
        | None -> None

    /// Parses SDK lifecycle response headers into diagnostics when the server sent advisory data.
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

    /// Adds an optional lifecycle value to a Grace result property bag.
    let private addIfSome (properties: Dictionary<string, obj>) key value =
        match value with
        | Some value -> properties[key] <- value
        | None -> ()

    /// Converts lifecycle diagnostics into Grace result properties with stable SDK property keys.
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

    /// Enriches a success or error result with SDK lifecycle properties from the HTTP response.
    let enhanceWithLifecycleDiagnostics (response: HttpResponseMessage) (result: GraceResult<'T>) =
        match parseLifecycleDiagnostics response with
        | Some diagnostics ->
            let properties = lifecycleDiagnosticsToProperties diagnostics

            match result with
            | Ok graceReturnValue -> graceReturnValue.enhance properties |> Ok
            | Error graceError -> graceError.enhance properties |> Error
        | None -> result

    /// Wraps a successful Grace return value with lifecycle diagnostics from the HTTP response.
    let okWithLifecycleDiagnostics (response: HttpResponseMessage) (graceReturnValue: GraceReturnValue<'T>) =
        enhanceWithLifecycleDiagnostics response (Ok graceReturnValue)

    /// Wraps a Grace error with lifecycle diagnostics from the HTTP response.
    let errorWithLifecycleDiagnostics (response: HttpResponseMessage) (graceError: GraceError) = enhanceWithLifecycleDiagnostics response (Error graceError)
