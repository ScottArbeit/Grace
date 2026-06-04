namespace Grace.Server.Middleware

open Grace.Shared
open Grace.Types.Common
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open System
open System.Text.Json
open System.Threading.Tasks

module SdkLifecyclePolicy =

    [<Literal>]
    let KnownClientTypeCli = "CLI"

    [<Literal>]
    let MinimumSupportedCliVersion = "0.1.0"

    [<Literal>]
    let MinimumNonDeprecatedCliVersion = "0.2.0"

    [<Literal>]
    let RecommendedCliVersion = "0.2.0"

    [<Literal>]
    let CliUnsupportedAfter = "2026-12-01"

    [<Literal>]
    let CliUpdateUrl = "https://github.com/ScottArbeit/Grace/releases"

    [<Literal>]
    let DeprecatedStatus = "deprecated"

    [<Literal>]
    let UnsupportedStatus = "unsupported"

    type ClientIdentity = { ClientType: string option; ClientVersion: string option }

    type LifecycleHeaders = { Status: string; UnsupportedAfter: string; MinimumSupportedVersion: string; RecommendedVersion: string; UpdateUrl: string }

    type UnsupportedClient =
        {
            ClientType: string
            ClientVersion: string
            UnsupportedAfter: string
            MinimumSupportedVersion: string
            RecommendedVersion: string
            UpdateUrl: string
            Message: string
        }

    type LifecycleDecision =
        | Continue
        | ContinueWithHeaders of LifecycleHeaders
        | RejectUnsupported of UnsupportedClient

    let private tryParseVersion (value: string) =
        let mutable parsed = Unchecked.defaultof<Version>

        if Version.TryParse(value, &parsed) then Some parsed else None

    let private compareVersionTo (candidate: Version) (threshold: string) =
        match tryParseVersion threshold with
        | Some thresholdVersion -> candidate.CompareTo(thresholdVersion)
        | None -> 0

    let private isCli (clientType: string) = clientType.Equals(KnownClientTypeCli, StringComparison.OrdinalIgnoreCase)

    let decide (identity: ClientIdentity) =
        match identity.ClientType, identity.ClientVersion with
        | Some clientType, Some clientVersion when isCli clientType ->
            match tryParseVersion clientVersion with
            | Some parsedVersion when compareVersionTo parsedVersion MinimumSupportedCliVersion < 0 ->
                RejectUnsupported(
                    {
                        ClientType = KnownClientTypeCli
                        ClientVersion = clientVersion
                        UnsupportedAfter = CliUnsupportedAfter
                        MinimumSupportedVersion = MinimumSupportedCliVersion
                        RecommendedVersion = RecommendedCliVersion
                        UpdateUrl = CliUpdateUrl
                        Message = $"Grace CLI/SDK version {clientVersion} is no longer supported. Update to version {RecommendedCliVersion} or newer."
                    }: UnsupportedClient
                )
            | Some parsedVersion when compareVersionTo parsedVersion MinimumNonDeprecatedCliVersion < 0 ->
                ContinueWithHeaders
                    {
                        Status = DeprecatedStatus
                        UnsupportedAfter = CliUnsupportedAfter
                        MinimumSupportedVersion = MinimumSupportedCliVersion
                        RecommendedVersion = RecommendedCliVersion
                        UpdateUrl = CliUpdateUrl
                    }
            | Some _ -> Continue
            | None -> Continue
        | _ -> Continue

type SdkLifecycleMiddleware(next: RequestDelegate) =

    let tryGetHeader (context: HttpContext) headerName =
        match context.Request.Headers.TryGetValue headerName with
        | true, values when
            values.Count > 0
            && not (String.IsNullOrWhiteSpace values[0])
            ->
            Some(values[ 0 ].ToString())
        | _ -> None

    let getCorrelationId (context: HttpContext) =
        match context.Items.TryGetValue(Constants.CorrelationId) with
        | true, value when not (isNull value) -> value.ToString()
        | _ -> String.Empty

    let setLifecycleHeaders (response: HttpResponse) (headers: SdkLifecyclePolicy.LifecycleHeaders) =
        response.Headers[
            Constants.SdkLifecycleStatusHeaderKey
        ] <- StringValues(headers.Status)

        response.Headers[
            Constants.SdkLifecycleUnsupportedAfterHeaderKey
        ] <- StringValues(headers.UnsupportedAfter)

        response.Headers[
            Constants.SdkLifecycleMinimumVersionHeaderKey
        ] <- StringValues(headers.MinimumSupportedVersion)

        response.Headers[
            Constants.SdkLifecycleRecommendedVersionHeaderKey
        ] <- StringValues(headers.RecommendedVersion)

        response.Headers[
            Constants.SdkLifecycleUpdateUrlHeaderKey
        ] <- StringValues(headers.UpdateUrl)

    let setUnsupportedLifecycleHeaders (response: HttpResponse) (unsupportedClient: SdkLifecyclePolicy.UnsupportedClient) =
        response.Headers[
            Constants.SdkLifecycleStatusHeaderKey
        ] <- StringValues(SdkLifecyclePolicy.UnsupportedStatus)

        response.Headers[
            Constants.SdkLifecycleUnsupportedAfterHeaderKey
        ] <- StringValues(unsupportedClient.UnsupportedAfter)

        response.Headers[
            Constants.SdkLifecycleMinimumVersionHeaderKey
        ] <- StringValues(unsupportedClient.MinimumSupportedVersion)

        response.Headers[
            Constants.SdkLifecycleRecommendedVersionHeaderKey
        ] <- StringValues(unsupportedClient.RecommendedVersion)

        response.Headers[
            Constants.SdkLifecycleUpdateUrlHeaderKey
        ] <- StringValues(unsupportedClient.UpdateUrl)

    let createUnsupportedClientError (context: HttpContext) (unsupportedClient: SdkLifecyclePolicy.UnsupportedClient) =
        let error = GraceError.Create "UnsupportedClientVersion" (getCorrelationId context)

        error
            .enhance("clientType", unsupportedClient.ClientType)
            .enhance("clientVersion", unsupportedClient.ClientVersion)
            .enhance("unsupportedAfter", unsupportedClient.UnsupportedAfter)
            .enhance("minimumSupportedVersion", unsupportedClient.MinimumSupportedVersion)
            .enhance("recommendedVersion", unsupportedClient.RecommendedVersion)
            .enhance("updateUrl", unsupportedClient.UpdateUrl)
            .enhance ("message", unsupportedClient.Message)

    member _.Invoke(context: HttpContext) =
        let identity: SdkLifecyclePolicy.ClientIdentity =
            { ClientType = tryGetHeader context Constants.ClientTypeHeaderKey; ClientVersion = tryGetHeader context Constants.ClientVersionHeaderKey }

        match SdkLifecyclePolicy.decide identity with
        | SdkLifecyclePolicy.Continue -> next.Invoke(context)
        | SdkLifecyclePolicy.ContinueWithHeaders headers ->
            setLifecycleHeaders context.Response headers
            next.Invoke(context)
        | SdkLifecyclePolicy.RejectUnsupported unsupportedClient ->
            task {
                setUnsupportedLifecycleHeaders context.Response unsupportedClient
                context.Response.StatusCode <- StatusCodes.Status426UpgradeRequired
                context.Response.ContentType <- "application/json; charset=utf-8"

                let error = createUnsupportedClientError context unsupportedClient
                let body = JsonSerializer.Serialize(error, Constants.JsonSerializerOptions)
                do! context.Response.WriteAsync(body)
            }
            :> Task
