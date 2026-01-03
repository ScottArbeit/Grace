namespace Grace.SDK

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks

module Auth =

    type TokenProvider = unit -> Task<string option>

    let mutable private tokenProvider: TokenProvider option = None

    let setTokenProvider (provider: TokenProvider) = tokenProvider <- Some provider

    let clearTokenProvider () = tokenProvider <- None

    let tryGetAccessToken () =
        task {
            match tokenProvider with
            | None -> return None
            | Some provider ->
                try
                    return! provider ()
                with _ ->
                    return None
        }

    let addAuthorizationHeader (httpClient: HttpClient) =
        task {
            let! tokenOpt = tryGetAccessToken ()

            match tokenOpt with
            | Some token when not (String.IsNullOrWhiteSpace token) ->
                httpClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)
            | _ -> ()
        }
