namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.CacheRegistration
open Grace.Types.Common
open System
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Threading

/// Sends the single selected-server request used to enroll a static Grace Cache identity.
type CacheRegistration() =
    /// Posts one enrollment request with the caller-selected server and already-resolved bearer, without redirects, retries, or configuration lookup.
    static member Enroll(request: CacheEnrollmentRequest, serverUri: Uri, bearer: string, correlationId: string, cancellationToken: CancellationToken) =
        task {
            if obj.ReferenceEquals(request, null) then
                return Error(GraceError.Create "Cache enrollment request is required." correlationId)
            elif isNull serverUri || not serverUri.IsAbsoluteUri then
                return Error(GraceError.Create "Cache enrollment requires an absolute Grace Server URI." correlationId)
            elif String.IsNullOrWhiteSpace bearer then
                return Error(GraceError.Create "Cache enrollment requires an authenticated bearer credential." correlationId)
            else
                try
                    use handler = new SocketsHttpHandler(AllowAutoRedirect = false)

                    use client =
                        new HttpClient(handler)
                        |> ClientIdentity.applyHeaders

                    client.DefaultRequestHeaders.TryAddWithoutValidation(Constants.CorrelationIdHeaderKey, correlationId)
                    |> ignore

                    client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", bearer)
                    use content = createJsonContent request
                    use! response = client.PostAsync(Uri($"{serverUri.AbsoluteUri.TrimEnd('/')}/cache/enroll"), content, cancellationToken)

                    if response.IsSuccessStatusCode then
                        let! result =
                            response.Content.ReadFromJsonAsync<GraceReturnValue<CacheRegistrationResult>>(Constants.JsonSerializerOptions, cancellationToken)

                        return
                            if obj.ReferenceEquals(result, null) then
                                Error(GraceError.Create "Cache enrollment returned an empty response." correlationId)
                            else
                                Ok result
                    else
                        return Error(GraceError.Create "Cache enrollment was rejected by the selected Grace Server." correlationId)
                with
                | :? OperationCanceledException as error -> return raise error
                | _ -> return Error(GraceError.Create "Cache enrollment could not reach the selected Grace Server." correlationId)
        }
