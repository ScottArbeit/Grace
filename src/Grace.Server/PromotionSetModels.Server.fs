namespace Grace.Server

open Grace.Actors.PromotionSetConflictModel
open Microsoft.Extensions.Configuration
open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks

module PromotionSetModels =

    type OpenRouterSettings = { ApiBase: string; ApiKeyEnvVar: string; Model: string; RequestHeaders: Dictionary<string, string> option }

    type PromotionSetModelsSettings = { Provider: string; OpenRouter: OpenRouterSettings }

    let private toOption (value: string) = if String.IsNullOrWhiteSpace value then None else Some value

    let private truncateForPrompt (content: string option) =
        let maxLength = 12000

        content
        |> Option.map (fun text ->
            if String.IsNullOrEmpty text then text
            elif text.Length <= maxLength then text
            else text[.. (maxLength - 1)])

    let private buildPrompt (request: ConflictResolutionModelRequest) =
        let baseContent =
            truncateForPrompt request.BaseContent
            |> Option.defaultValue "<none>"

        let oursContent =
            truncateForPrompt request.OursContent
            |> Option.defaultValue "<none>"

        let theirsContent =
            truncateForPrompt request.TheirsContent
            |> Option.defaultValue "<none>"

        String.Join(
            Environment.NewLine,
            [|
                $"File path: {request.FilePath}"
                String.Empty
                "Resolve this merge conflict and return ONLY JSON with this exact schema:"
                "{"
                "  \"proposedContent\": string|null,"
                "  \"shouldDelete\": boolean,"
                "  \"confidence\": number,"
                "  \"explanation\": string"
                "}"
                String.Empty
                "Rules:"
                "- confidence must be between 0.0 and 1.0."
                "- use shouldDelete=true only when file should be deleted."
                "- when shouldDelete=false, proposedContent must contain full merged file content."
                "- do not include markdown code fences."
                String.Empty
                "BASE:"
                baseContent
                String.Empty
                "OURS:"
                oursContent
                String.Empty
                "THEIRS:"
                theirsContent
            |]
        )

    let private tryExtractJsonPayload (content: string) =
        if String.IsNullOrWhiteSpace content then
            None
        else
            let trimmed = content.Trim()

            if trimmed.StartsWith("{", StringComparison.Ordinal) then
                Some trimmed
            else
                let firstBrace = trimmed.IndexOf('{')
                let lastBrace = trimmed.LastIndexOf('}')

                if firstBrace >= 0 && lastBrace > firstBrace then
                    Some(trimmed.Substring(firstBrace, lastBrace - firstBrace + 1))
                else
                    None

    let private tryGetProperty (name: string) (jsonElement: JsonElement) =
        let mutable propertyElement = Unchecked.defaultof<JsonElement>

        if jsonElement.TryGetProperty(name, &propertyElement) then
            Some propertyElement
        else
            None

    let private parseModelResponse (content: string) =
        match tryExtractJsonPayload content with
        | None -> Error "Model response did not contain a JSON payload."
        | Some jsonPayload ->
            try
                use payloadDocument = JsonDocument.Parse(jsonPayload)
                let rootElement = payloadDocument.RootElement

                let confidence =
                    match tryGetProperty "confidence" rootElement with
                    | Some confidenceElement when confidenceElement.ValueKind = JsonValueKind.Number ->
                        let mutable parsedConfidence = 0.0

                        if confidenceElement.TryGetDouble(&parsedConfidence) then
                            Ok parsedConfidence
                        else
                            Error "Model response confidence could not be parsed."
                    | _ -> Error "Model response is missing numeric confidence."

                let proposedContentResult =
                    match tryGetProperty "proposedContent" rootElement with
                    | Some proposedContentElement when proposedContentElement.ValueKind = JsonValueKind.Null -> Ok None
                    | Some proposedContentElement when proposedContentElement.ValueKind = JsonValueKind.String -> Ok(Some(proposedContentElement.GetString()))
                    | Some _ -> Error "Model response proposedContent must be string or null."
                    | None -> Ok None

                let shouldDeleteResult =
                    match tryGetProperty "shouldDelete" rootElement with
                    | Some shouldDeleteElement when shouldDeleteElement.ValueKind = JsonValueKind.True -> Ok true
                    | Some shouldDeleteElement when shouldDeleteElement.ValueKind = JsonValueKind.False -> Ok false
                    | _ -> Ok false

                let explanation =
                    match tryGetProperty "explanation" rootElement with
                    | Some explanationElement when explanationElement.ValueKind = JsonValueKind.String -> toOption (explanationElement.GetString())
                    | _ -> None

                match confidence, proposedContentResult, shouldDeleteResult with
                | Ok parsedConfidence, Ok proposedContent, Ok shouldDelete ->
                    if Double.IsNaN parsedConfidence
                       || Double.IsInfinity parsedConfidence then
                        Error "Model response confidence must be finite."
                    elif parsedConfidence < 0.0 || parsedConfidence > 1.0 then
                        Error "Model response confidence must be in range [0.0, 1.0]."
                    elif not shouldDelete && proposedContent.IsNone then
                        Error "Model response proposedContent is required when shouldDelete is false."
                    else
                        Ok { ProposedContent = proposedContent; ShouldDelete = shouldDelete; Confidence = parsedConfidence; Explanation = explanation }
                | Error errorText, _, _
                | _, Error errorText, _
                | _, _, Error errorText -> Error errorText
            with
            | ex -> Error $"Failed to parse model response JSON: {ex.Message}"

    type NullConflictResolutionModelProvider() =
        interface IConflictResolutionModelProvider with
            member _.ProviderName = "none"
            member _.SuggestResolution _ = Task.FromResult(Error "Conflict resolution model provider is not configured.")

    type OpenRouterConflictResolutionModelProvider(settings: OpenRouterSettings) =
        let httpClient = new HttpClient()

        let apiBase =
            if String.IsNullOrWhiteSpace settings.ApiBase then
                "https://openrouter.ai/api/v1"
            else
                settings.ApiBase.TrimEnd('/')

        let requestUri = Uri($"{apiBase}/chat/completions")

        interface IConflictResolutionModelProvider with
            member _.ProviderName = "OpenRouter"

            member _.SuggestResolution(request: ConflictResolutionModelRequest) =
                task {
                    let apiKey = Environment.GetEnvironmentVariable(settings.ApiKeyEnvVar)

                    if String.IsNullOrWhiteSpace apiKey then
                        return Error $"Conflict resolution model API key is not configured in environment variable '{settings.ApiKeyEnvVar}'."
                    else
                        try
                            let payload =
                                {|
                                    model = settings.Model
                                    temperature = 0.0
                                    messages =
                                        [|
                                            {|
                                                role = "system"
                                                content = "You are a merge-conflict resolver. Return strict JSON only and no additional text."
                                            |}
                                            {| role = "user"; content = buildPrompt request |}
                                        |]
                                |}

                            let payloadJson = JsonSerializer.Serialize(payload)
                            use requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
                            requestMessage.Headers.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)

                            settings.RequestHeaders
                            |> Option.defaultValue (Dictionary<string, string>())
                            |> Seq.iter (fun header ->
                                if not <| String.IsNullOrWhiteSpace header.Key then
                                    requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value)
                                    |> ignore)

                            requestMessage.Content <- new StringContent(payloadJson, Encoding.UTF8, "application/json")

                            use! responseMessage = httpClient.SendAsync(requestMessage)
                            let! responseBody = responseMessage.Content.ReadAsStringAsync()

                            if not responseMessage.IsSuccessStatusCode then
                                return
                                    Error(
                                        $"Conflict resolution model request failed with status {(int responseMessage.StatusCode)} ({responseMessage.ReasonPhrase})."
                                    )
                            else
                                try
                                    use responseDocument = JsonDocument.Parse(responseBody)
                                    let rootElement = responseDocument.RootElement
                                    let choicesElement = rootElement.GetProperty("choices")

                                    if choicesElement.GetArrayLength() = 0 then
                                        return Error "Conflict resolution model response did not include choices."
                                    else
                                        let firstChoiceElement = choicesElement[0]
                                        let messageElement = firstChoiceElement.GetProperty("message")
                                        let content = messageElement.GetProperty("content").GetString()

                                        match parseModelResponse content with
                                        | Ok parsedResponse -> return Ok parsedResponse
                                        | Error errorText -> return Error errorText
                                with
                                | ex -> return Error($"Conflict resolution model response was malformed and could not be parsed: {ex.Message}")
                        with
                        | ex -> return Error $"Conflict resolution model request failed: {ex.Message}"
                }

    let private tryGetSettings (configuration: IConfiguration) =
        if isNull configuration then
            None
        else
            let promotionSetModelsSection = configuration.GetSection("Grace:PromotionSetModels")

            if isNull promotionSetModelsSection then
                None
            else
                let openRouterSection = promotionSetModelsSection.GetSection("OpenRouter")

                let requestHeadersSection = openRouterSection.GetSection("RequestHeaders")

                let requestHeaders =
                    if isNull requestHeadersSection then
                        None
                    else
                        let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

                        requestHeadersSection.GetChildren()
                        |> Seq.iter (fun section ->
                            if not <| String.IsNullOrWhiteSpace section.Key then
                                headers[section.Key] <- section.Value)

                        if headers.Count = 0 then None else Some headers

                Some
                    {
                        Provider = promotionSetModelsSection["Provider"]
                        OpenRouter =
                            {
                                ApiBase = openRouterSection["ApiBase"]
                                ApiKeyEnvVar = openRouterSection["ApiKeyEnvVar"]
                                Model = openRouterSection["Model"]
                                RequestHeaders = requestHeaders
                            }
                    }

    let createProvider (configuration: IConfiguration) =
        match tryGetSettings configuration with
        | Some settings when not <| String.IsNullOrWhiteSpace settings.Provider ->
            match settings.Provider.Trim() with
            | "OpenRouter" when
                not
                <| String.IsNullOrWhiteSpace settings.OpenRouter.Model
                ->
                OpenRouterConflictResolutionModelProvider(settings.OpenRouter) :> IConflictResolutionModelProvider
            | _ -> NullConflictResolutionModelProvider() :> IConflictResolutionModelProvider
        | _ -> NullConflictResolutionModelProvider() :> IConflictResolutionModelProvider
