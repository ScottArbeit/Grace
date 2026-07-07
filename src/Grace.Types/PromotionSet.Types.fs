namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Microsoft.Extensions.Configuration
open NodaTime
open Orleans
open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Runtime.Serialization
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Contains promotion set helpers.
module PromotionSet =

    /// Represents promotion set status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PromotionSetStatus =
        | Ready
        | Running
        | Succeeded
        | Failed
        | Blocked

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<PromotionSetStatus>()

    /// Represents steps computation status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type StepsComputationStatus =
        | NotComputed
        | Computing
        | Computed
        | ComputeFailed

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<StepsComputationStatus>()

    /// Represents step conflict status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type StepConflictStatus =
        | NoConflicts
        | AutoResolved
        | BlockedPendingReview
        | Failed

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<StepConflictStatus>()

    /// Represents conflict resolution method.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ConflictResolutionMethod =
        | None
        | ModelSuggested
        | ManualOverride

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ConflictResolutionMethod>()

    /// Represents the conflict resolution outcome contract.
    [<GenerateSerializer>]
    type ConflictResolutionOutcome = { ModelResolution: string; Confidence: float; Accepted: bool option }

    /// Represents the conflict hunk contract.
    [<GenerateSerializer>]
    type ConflictHunk = { StartLine: int; EndLine: int; OursContent: string; TheirsContent: string }

    /// Represents conflict analysis.
    [<GenerateSerializer>]
    type ConflictAnalysis =
        {
            FilePath: string
            OriginalHunks: ConflictHunk list
            ProposedResolution: ConflictResolutionOutcome option
            ResolutionMethod: ConflictResolutionMethod
        }

    /// Represents the conflict resolution decision contract.
    [<GenerateSerializer>]
    type ConflictResolutionDecision = { FilePath: string; Accepted: bool; OverrideContentArtifactId: ArtifactId option }

    /// Represents the promotion pointer contract.
    [<GenerateSerializer>]
    type PromotionPointer = { BranchId: BranchId; ReferenceId: ReferenceId; DirectoryVersionId: DirectoryVersionId }

    /// Represents promotion set approval policy snapshot.
    [<CLIMutable; GenerateSerializer>]
    type PromotionSetApprovalPolicySnapshot =
        {
            ApprovalPolicyId: Guid
            Version: int
            Subject: string
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            RequiredResponder: string
            TimeoutSeconds: int option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                ApprovalPolicyId = Guid.Empty
                Version = 1
                Subject = String.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                TargetBranchId = BranchId.Empty
                RequiredResponder = String.Empty
                TimeoutSeconds = Option.None
            }

    /// Represents promotion set step.
    [<GenerateSerializer>]
    type PromotionSetStep =
        {
            StepId: PromotionSetStepId
            Order: int
            OriginalPromotion: PromotionPointer
            OriginalBasePromotionReferenceId: ReferenceId
            OriginalBaseDirectoryVersionId: DirectoryVersionId
            ComputedAgainstBaseDirectoryVersionId: DirectoryVersionId
            AppliedDirectoryVersionId: DirectoryVersionId
            ConflictSummaryArtifactId: ArtifactId option
            ConflictStatus: StepConflictStatus
        }

    /// Represents promotion set dto.
    [<GenerateSerializer>]
    type PromotionSetDto =
        {
            Class: string
            PromotionSetId: PromotionSetId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            OnBehalfOf: UserId list
            Steps: PromotionSetStep list
            ComputedAgainstParentTerminalPromotionReferenceId: ReferenceId option
            StepsComputationStatus: StepsComputationStatus
            StepsComputationAttempt: int
            StepsComputationError: string option
            StepsComputationUpdatedAt: Instant option
            Status: PromotionSetStatus
            CreatedBy: UserId
            CreatedAt: Instant
            UpdatedAt: Instant option
            DeletedAt: Instant option
            DeleteReason: DeleteReason
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof PromotionSetDto
                PromotionSetId = PromotionSetId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                TargetBranchId = BranchId.Empty
                OnBehalfOf = []
                Steps = []
                ComputedAgainstParentTerminalPromotionReferenceId = Option.None
                StepsComputationStatus = StepsComputationStatus.NotComputed
                StepsComputationAttempt = 0
                StepsComputationError = Option.None
                StepsComputationUpdatedAt = Option.None
                Status = PromotionSetStatus.Ready
                CreatedBy = UserId String.Empty
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = Option.None
                DeletedAt = Option.None
                DeleteReason = String.Empty
            }

    /// Represents promotion set command.
    [<KnownType("GetKnownTypes")>]
    type PromotionSetCommand =
        | CreatePromotionSet of
            promotionSetId: PromotionSetId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            targetBranchId: BranchId
        | UpdateInputPromotions of promotionPointers: PromotionPointer list
        | RecomputeStepsIfStale of reason: string option
        | ResolveConflicts of stepId: PromotionSetStepId * resolutions: ConflictResolutionDecision list
        | Apply of approvalPolicies: PromotionSetApprovalPolicySnapshot list
        | DeleteLogical of force: bool * deleteReason: DeleteReason

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<PromotionSetCommand>()

    /// Represents promotion set event type.
    [<KnownType("GetKnownTypes")>]
    type PromotionSetEventType =
        | Created of promotionSetId: PromotionSetId * ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId * targetBranchId: BranchId
        | InputPromotionsUpdated of promotionPointers: PromotionPointer list
        | RecomputeStarted of computedAgainstTerminal: ReferenceId
        | StepsUpdated of steps: PromotionSetStep list * computedAgainstTerminal: ReferenceId
        | RecomputeFailed of reason: string * computedAgainstTerminal: ReferenceId
        | Blocked of reason: string * artifactId: ArtifactId option
        | ApplyStarted
        | Applied of terminalPromotionReferenceId: ReferenceId
        | ApplyFailed of reason: string
        | LogicalDeleted of force: bool * deleteReason: DeleteReason

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<PromotionSetEventType>()

    /// Represents the promotion set event contract.
    type PromotionSetEvent = { Event: PromotionSetEventType; Metadata: EventMetadata }

    /// Contains promotion set dto helpers.
    module PromotionSetDto =
        /// Carries optional promotion-set fields that can be patched without replacing the full promotion set.
        let UpdateDto (promotionSetEvent: PromotionSetEvent) (currentDto: PromotionSetDto) =
            let updatedDto, shouldUpdateComputationTimestamp =
                match promotionSetEvent.Event with
                | Created (promotionSetId, ownerId, organizationId, repositoryId, targetBranchId) ->
                    { PromotionSetDto.Default with
                        PromotionSetId = promotionSetId
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        RepositoryId = repositoryId
                        TargetBranchId = targetBranchId
                        CreatedBy = UserId promotionSetEvent.Metadata.Principal
                        CreatedAt = promotionSetEvent.Metadata.Timestamp
                    },
                    false
                | InputPromotionsUpdated promotionPointers ->
                    let steps =
                        promotionPointers
                        |> List.mapi (fun index pointer ->
                            {
                                StepId = Guid.NewGuid()
                                Order = index
                                OriginalPromotion = pointer
                                OriginalBasePromotionReferenceId = ReferenceId.Empty
                                OriginalBaseDirectoryVersionId = DirectoryVersionId.Empty
                                ComputedAgainstBaseDirectoryVersionId = DirectoryVersionId.Empty
                                AppliedDirectoryVersionId = DirectoryVersionId.Empty
                                ConflictSummaryArtifactId = Option.None
                                ConflictStatus = StepConflictStatus.NoConflicts
                            })

                    { currentDto with
                        Steps = steps
                        Status = PromotionSetStatus.Ready
                        StepsComputationStatus = StepsComputationStatus.NotComputed
                        ComputedAgainstParentTerminalPromotionReferenceId = Option.None
                        StepsComputationError = Option.None
                    },
                    true
                | RecomputeStarted computedAgainstTerminal ->
                    { currentDto with
                        Status = PromotionSetStatus.Running
                        StepsComputationStatus = StepsComputationStatus.Computing
                        ComputedAgainstParentTerminalPromotionReferenceId = Some computedAgainstTerminal
                        StepsComputationError = Option.None
                    },
                    true
                | StepsUpdated (steps, computedAgainstTerminal) ->
                    { currentDto with
                        Steps = steps
                        Status = PromotionSetStatus.Ready
                        StepsComputationStatus = StepsComputationStatus.Computed
                        ComputedAgainstParentTerminalPromotionReferenceId = Some computedAgainstTerminal
                        StepsComputationAttempt = currentDto.StepsComputationAttempt + 1
                        StepsComputationError = Option.None
                    },
                    true
                | RecomputeFailed (reason, computedAgainstTerminal) ->
                    { currentDto with
                        Status = PromotionSetStatus.Failed
                        StepsComputationStatus = StepsComputationStatus.ComputeFailed
                        ComputedAgainstParentTerminalPromotionReferenceId = Some computedAgainstTerminal
                        StepsComputationError = Some reason
                    },
                    true
                | Blocked (reason, _) ->
                    { currentDto with
                        Status = PromotionSetStatus.Blocked
                        StepsComputationStatus = StepsComputationStatus.ComputeFailed
                        StepsComputationError = Some reason
                    },
                    true
                | ApplyStarted -> { currentDto with Status = PromotionSetStatus.Running }, false
                | Applied _ -> { currentDto with Status = PromotionSetStatus.Succeeded }, false
                | ApplyFailed reason -> { currentDto with Status = PromotionSetStatus.Failed; StepsComputationError = Some reason }, false
                | LogicalDeleted (_, deleteReason) -> { currentDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }, false

            let onBehalfOf =
                updatedDto.OnBehalfOf
                |> List.append [ UserId promotionSetEvent.Metadata.Principal ]
                |> List.distinct

            let computationUpdatedAt =
                if shouldUpdateComputationTimestamp then
                    Some promotionSetEvent.Metadata.Timestamp
                else
                    updatedDto.StepsComputationUpdatedAt

            { updatedDto with OnBehalfOf = onBehalfOf; UpdatedAt = Some promotionSetEvent.Metadata.Timestamp; StepsComputationUpdatedAt = computationUpdatedAt }

/// Contains promotion set conflict model helpers.
module PromotionSetConflictModel =

    /// Represents the conflict resolution model request contract.
    [<CLIMutable>]
    type ConflictResolutionModelRequest = { FilePath: string; BaseContent: string option; OursContent: string option; TheirsContent: string option }

    /// Represents the conflict resolution model response contract.
    [<CLIMutable>]
    type ConflictResolutionModelResponse = { ProposedContent: string option; ShouldDelete: bool; Confidence: float; Explanation: string option }

    /// Represents i conflict resolution model provider.
    type IConflictResolutionModelProvider =
        /// Identifies the conflict-resolution provider in diagnostics and result metadata.
        abstract member ProviderName: string
        /// Requests a model-backed conflict-resolution proposal for the supplied file contents.
        abstract member SuggestResolution: ConflictResolutionModelRequest -> Task<Result<ConflictResolutionModelResponse, string>>

    /// Represents the open router settings contract.
    type OpenRouterSettings = { ApiBase: string; ApiKeyEnvVar: string; Model: string; RequestHeaders: Dictionary<string, string> option }

    /// Represents the promotion set models settings contract.
    type PromotionSetModelsSettings = { Provider: string; OpenRouter: OpenRouterSettings }

    /// Converts option.
    let private toOption (value: string) = if String.IsNullOrWhiteSpace value then Option.None else Option.Some value

    /// Caps file content included in model prompts so conflict-resolution requests stay within a bounded size.
    let private truncateForPrompt (content: string option) =
        let maxLength = 12000

        content
        |> Option.map (fun text ->
            if String.IsNullOrEmpty text then text
            elif text.Length <= maxLength then text
            else text[.. (maxLength - 1)])

    /// Builds prompt.
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

    /// Attempts to extract json payload.
    let private tryExtractJsonPayload (content: string) =
        if String.IsNullOrWhiteSpace content then
            Option.None
        else
            let trimmed = content.Trim()

            if trimmed.StartsWith("{", StringComparison.Ordinal) then
                Option.Some trimmed
            else
                let firstBrace = trimmed.IndexOf('{')
                let lastBrace = trimmed.LastIndexOf('}')

                if firstBrace >= 0 && lastBrace > firstBrace then
                    Option.Some(trimmed.Substring(firstBrace, lastBrace - firstBrace + 1))
                else
                    Option.None

    /// Attempts to get property.
    let private tryGetProperty (name: string) (jsonElement: JsonElement) =
        let mutable propertyElement = Unchecked.defaultof<JsonElement>

        if jsonElement.TryGetProperty(name, &propertyElement) then
            Option.Some propertyElement
        else
            Option.None

    /// Attempts to parse model response.
    let tryParseModelResponse (content: string) =
        match tryExtractJsonPayload content with
        | Option.None -> Error "Model response did not contain a JSON payload."
        | Option.Some jsonPayload ->
            try
                use payloadDocument = JsonDocument.Parse(jsonPayload)
                let rootElement = payloadDocument.RootElement

                let confidence =
                    match tryGetProperty "confidence" rootElement with
                    | Option.Some confidenceElement when confidenceElement.ValueKind = JsonValueKind.Number ->
                        let mutable parsedConfidence = 0.0

                        if confidenceElement.TryGetDouble(&parsedConfidence) then
                            Ok parsedConfidence
                        else
                            Error "Model response confidence could not be parsed."
                    | _ -> Error "Model response is missing numeric confidence."

                let proposedContentResult =
                    match tryGetProperty "proposedContent" rootElement with
                    | Option.Some proposedContentElement when proposedContentElement.ValueKind = JsonValueKind.Null -> Ok Option.None
                    | Option.Some proposedContentElement when proposedContentElement.ValueKind = JsonValueKind.String ->
                        Ok(Option.Some(proposedContentElement.GetString()))
                    | Option.Some _ -> Error "Model response proposedContent must be string or null."
                    | Option.None -> Ok Option.None

                let shouldDeleteResult =
                    match tryGetProperty "shouldDelete" rootElement with
                    | Option.Some shouldDeleteElement when shouldDeleteElement.ValueKind = JsonValueKind.True -> Ok true
                    | Option.Some shouldDeleteElement when shouldDeleteElement.ValueKind = JsonValueKind.False -> Ok false
                    | _ -> Ok false

                let explanation =
                    match tryGetProperty "explanation" rootElement with
                    | Option.Some explanationElement when explanationElement.ValueKind = JsonValueKind.String -> toOption (explanationElement.GetString())
                    | _ -> Option.None

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

    /// Represents null conflict resolution model provider.
    type NullConflictResolutionModelProvider() =
        interface IConflictResolutionModelProvider with
            /// Identifies the conflict-resolution provider in diagnostics and result metadata.
            member _.ProviderName = "none"
            /// Requests a model-backed conflict-resolution proposal for the supplied file contents.
            member _.SuggestResolution _ = Task.FromResult(Error "Conflict resolution model provider is not configured.")

    /// Represents open router conflict resolution model provider.
    type OpenRouterConflictResolutionModelProvider(settings: OpenRouterSettings) =
        let httpClient = new HttpClient()

        let apiBase =
            if String.IsNullOrWhiteSpace settings.ApiBase then
                "https://openrouter.ai/api/v1"
            else
                settings.ApiBase.TrimEnd('/')

        let requestUri = Uri($"{apiBase}/chat/completions")

        interface IConflictResolutionModelProvider with
            /// Identifies the conflict-resolution provider in diagnostics and result metadata.
            member _.ProviderName = "OpenRouter"

            /// Requests a model-backed conflict-resolution proposal for the supplied file contents.
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

                                        match tryParseModelResponse content with
                                        | Ok parsedResponse -> return Ok parsedResponse
                                        | Error errorText -> return Error errorText
                                with
                                | ex -> return Error($"Conflict resolution model response was malformed and could not be parsed: {ex.Message}")
                        with
                        | ex -> return Error $"Conflict resolution model request failed: {ex.Message}"
                }

    /// Attempts to get settings.
    let private tryGetSettings (configuration: IConfiguration) =
        if isNull configuration then
            Option.None
        else
            let promotionSetModelsSection = configuration.GetSection("Grace:PromotionSetModels")

            if isNull promotionSetModelsSection then
                Option.None
            else
                let openRouterSection = promotionSetModelsSection.GetSection("OpenRouter")
                let requestHeadersSection = openRouterSection.GetSection("RequestHeaders")

                let requestHeaders =
                    if isNull requestHeadersSection then
                        Option.None
                    else
                        let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

                        requestHeadersSection.GetChildren()
                        |> Seq.iter (fun section ->
                            if not <| String.IsNullOrWhiteSpace section.Key then
                                headers[section.Key] <- section.Value)

                        if headers.Count = 0 then Option.None else Option.Some headers

                Option.Some
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

    /// Builds provider from the validated inputs used by this contract.
    let createProvider (configuration: IConfiguration) =
        match tryGetSettings configuration with
        | Option.Some settings when not <| String.IsNullOrWhiteSpace settings.Provider ->
            match settings.Provider.Trim() with
            | "OpenRouter" when
                not
                <| String.IsNullOrWhiteSpace settings.OpenRouter.Model
                ->
                OpenRouterConflictResolutionModelProvider(settings.OpenRouter) :> IConflictResolutionModelProvider
            | _ -> NullConflictResolutionModelProvider() :> IConflictResolutionModelProvider
        | _ -> NullConflictResolutionModelProvider() :> IConflictResolutionModelProvider
