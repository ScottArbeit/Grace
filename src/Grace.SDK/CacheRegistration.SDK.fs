namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.CacheRegistration
open Grace.Types.Common
open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Threading

/// Classifies the terminal result of the single cache enrollment transport attempt.
type CacheEnrollmentTransportOutcome =
    /// The server returned a success response whose body is available for strict CLI validation.
    | Accepted of GraceReturnValue<CacheRegistrationResult>
    /// The selected server definitely rejected the request or returned an unusable protocol response.
    | Rejected of GraceError
    /// The connection, response, timeout, or caller cancellation occurred after transport began and server acceptance is unknown.
    | Indeterminate of GraceError

/// Sends the single selected-server request used to enroll a static Grace Cache identity.
type CacheRegistration() =
    /// Posts one enrollment request with an already-resolved bearer, disabled redirects, and no retry or configuration lookup.
    static member Enroll(request: CacheEnrollmentRequest, serverUri: Uri, bearer: string, correlationId: string, cancellationToken: CancellationToken) =
        task {
            if obj.ReferenceEquals(request, null) then
                return Rejected(GraceError.Create "Cache enrollment request is required." correlationId)
            elif isNull serverUri || not serverUri.IsAbsoluteUri then
                return Rejected(GraceError.Create "Cache enrollment requires an absolute Grace Server URI." correlationId)
            elif String.IsNullOrWhiteSpace bearer then
                return Rejected(GraceError.Create "Cache enrollment requires an authenticated bearer credential." correlationId)
            else
                try
                    use handler = new SocketsHttpHandler(AllowAutoRedirect = false)

                    use client =
                        new HttpClient(handler)
                        |> ClientIdentity.applyHeaders

                    // A bounded one-shot call makes transport loss terminal and testable without adding retry behavior.
                    client.Timeout <- TimeSpan.FromSeconds(2.0)

                    client.DefaultRequestHeaders.TryAddWithoutValidation(Constants.CorrelationIdHeaderKey, correlationId)
                    |> ignore

                    client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", bearer)
                    use content = createJsonContent request

                    try
                        use! response = client.PostAsync(Uri($"{serverUri.AbsoluteUri.TrimEnd('/')}/cache/enroll"), content, cancellationToken)

                        if not response.IsSuccessStatusCode then
                            return Rejected(GraceError.Create "Cache enrollment was rejected by the selected Grace Server." correlationId)
                        else
                            try
                                let! result =
                                    response.Content.ReadFromJsonAsync<GraceReturnValue<CacheRegistrationResult>>(
                                        Constants.JsonSerializerOptions,
                                        cancellationToken
                                    )

                                return
                                    if obj.ReferenceEquals(result, null) then
                                        Rejected(GraceError.Create "Cache enrollment returned an empty response." correlationId)
                                    else
                                        Accepted result
                            with
                            | :? OperationCanceledException ->
                                return Indeterminate(GraceError.Create "Cache enrollment outcome is unknown after transport started." correlationId)
                            | :? HttpRequestException
                            | :? IOException ->
                                return Indeterminate(GraceError.Create "Cache enrollment outcome is unknown after transport started." correlationId)
                            | _ -> return Rejected(GraceError.Create "Cache enrollment returned an invalid response." correlationId)
                    with
                    | :? OperationCanceledException
                    | :? HttpRequestException
                    | :? IOException -> return Indeterminate(GraceError.Create "Cache enrollment outcome is unknown after transport started." correlationId)
                    | _ -> return Indeterminate(GraceError.Create "Cache enrollment outcome is unknown after transport started." correlationId)
                with
                | _ -> return Rejected(GraceError.Create "Cache enrollment could not prepare the selected-server request." correlationId)
        }
