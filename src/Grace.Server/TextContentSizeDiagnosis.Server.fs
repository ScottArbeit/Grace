namespace Grace.Server

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Giraffe
open Grace.Actors
open Grace.Actors.Services
open Grace.Actors.Extensions.ActorProxy
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.TextContent
open Grace.Types.Usage
open Grace.Types.WorkItem
open Microsoft.Azure.Cosmos
open NodaTime

/// Observes retained WorkItem text declarations without reading blobs or recording usage facts.
module TextContentSizeDiagnosis =

    /// Keeps TextContent-specific quantities attached to their verified repository scope and non-atomic read window.
    type TextContentSizeDiagnostic =
        {
            Scope: UsageFactScope
            DeclaredTextContentUtf8Bytes: int64
            DistinctTextContentCount: int64
            EnumerationStartedAt: Instant
            EnumerationFinishedAt: Instant
        }

    /// Rejects name selectors and incomplete identifiers before reading repository state.
    let internal validateParameters (parameters: GetRepositoryParameters) =
        /// Accepts only explicit, non-empty scope identifiers.
        let parseId value =
            match Guid.TryParse(value: string) with
            | true, id when id <> Guid.Empty -> Some id
            | _ -> None

        if isNull (box parameters) then
            Error "A repository scope is required."
        elif
            [
                parameters.OwnerName
                parameters.OrganizationName
                parameters.RepositoryName
            ]
            |> List.exists (String.IsNullOrEmpty >> not)
        then
            Error "Use explicit owner, organization and repository IDs; name selectors are not supported."
        else
            match parseId parameters.OwnerId, parseId parameters.OrganizationId, parseId parameters.RepositoryId with
            | Some owner, Some organization, Some repository -> Ok { OwnerId = owner; OrganizationId = organization; RepositoryId = repository }
            | _ -> Error "OwnerId, OrganizationId and RepositoryId must be non-empty GUIDs."

    /// Preserves explicit null options while rejecting missing declaration fields on the current persisted wire shape.
    let internal decodeDocument (document: JsonElement) =
        /// Requires fields whose absence could hide a scope or retained reference.
        let requireFields (names: string list) (element: JsonElement) =
            names
            |> List.iter (fun name ->
                let mutable value = Unchecked.defaultof<JsonElement>

                if
                    element.ValueKind <> JsonValueKind.Object
                    || not (element.TryGetProperty(name, &value))
                then
                    raise (InvalidDataException $"WorkItem source is missing required field '{name}'."))

        /// Checks the actual description encoding, where a clear retains its identity and writes TextContent as null.
        let requireDescription allowNull (description: JsonElement) =
            if description.ValueKind = JsonValueKind.Null then
                if not allowNull then
                    raise (InvalidDataException "WorkItem description event is missing its description.")
            else
                requireFields [ "DescriptionId"; "TextContent" ] description
                let content = description.GetProperty "TextContent"

                if content.ValueKind <> JsonValueKind.Null then
                    requireFields
                        [
                            "TextContentId"
                            "Blake3Hash"
                            "Utf8ByteLength"
                        ]
                        content

        requireFields [ "State" ] document
        let state = document.GetProperty "State"

        if state.ValueKind <> JsonValueKind.Array
           || state.GetArrayLength() = 0 then
            raise (InvalidDataException "WorkItem source is missing Created data.")

        state.EnumerateArray()
        |> Seq.iter (fun item ->
            requireFields [ "Event" ] item
            let event = item.GetProperty "Event"

            if event.ValueKind <> JsonValueKind.Object then
                raise (InvalidDataException "WorkItem event must identify its case.")

            let mutable payload = Unchecked.defaultof<JsonElement>

            if event.TryGetProperty("created", &payload) then
                requireFields
                    [
                        "workItemId"
                        "ownerId"
                        "organizationId"
                        "repositoryId"
                        "description"
                    ]
                    payload

                requireDescription true (payload.GetProperty "description")
            elif
                event.TryGetProperty("descriptionSet", &payload)
                || event.TryGetProperty("descriptionCleared", &payload)
            then
                requireDescription false payload)

        try
            JsonSerializer.Deserialize<WorkItemEvent array>(state.GetRawText(), Constants.JsonSerializerOptions)
        with
        | :? JsonException as error -> raise (InvalidDataException("WorkItem source could not be decoded.", error))

    /// Validates the document's Created authority and returns all retained description references, including superseded content.
    let private declarations (scope: UsageFactScope) (events: WorkItemEvent array) =
        if isNull events || events.Length = 0 then
            raise (InvalidDataException "WorkItem source is missing Created data.")

        let mutable createdCount = 0

        events
        |> Array.iter (fun event ->
            match event.Event with
            | Created _ -> createdCount <- createdCount + 1
            | _ -> ())

        if createdCount <> 1 then
            raise (InvalidDataException "WorkItem source must contain exactly one Created event.")

        match events[0].Event with
        | Created (workItemId, _, ownerId, organizationId, repositoryId, _, _) when
            workItemId <> Guid.Empty
            && ownerId = scope.OwnerId
            && organizationId = scope.OrganizationId
            && repositoryId = scope.RepositoryId
            ->
            ()
        | _ -> raise (InvalidDataException "WorkItem source must start with Created in the requested scope.")

        events
        |> Seq.choose (fun event ->
            match event.Event with
            | Created (_, _, _, _, _, _, description) -> description
            | DescriptionSet description ->
                if
                    isNull (box description)
                    || description.TextContent.IsNone
                then
                    raise (InvalidDataException "A retained description set is missing its TextContent reference.")

                Some description
            | DescriptionCleared description -> Some description
            | _ -> None)
        |> Seq.map (fun description ->
            if
                isNull (box description)
                || description.DescriptionId = Guid.Empty
            then
                raise (InvalidDataException "WorkItem source contains an invalid description identity.")

            description.TextContent)

    /// Produces a quantity only after every scoped event page is exhausted within the fixed request limits.
    let internal enumerateWith
        (readPage: CancellationToken -> Task<WorkItemEvent array array * bool>)
        (scope: UsageFactScope)
        (cancellationToken: CancellationToken)
        =
        task {
            let started = getCurrentInstant ()
            let distinct = Dictionary<Guid, TextContent>()
            let mutable pages, documents, references = 0, 0, 0
            let mutable total = 0L
            let mutable more = true

            while more do
                cancellationToken.ThrowIfCancellationRequested()

                if pages >= 32 then
                    raise (InvalidDataException "TextContent enumeration exceeded 32 pages.")

                let! rows, hasMore = readPage cancellationToken
                pages <- pages + 1
                documents <- Checked.op_Addition documents rows.Length

                if documents > 10000 then
                    raise (InvalidDataException "TextContent enumeration exceeded 10000 WorkItem documents.")

                let mutable index = 0

                while index < rows.Length do
                    cancellationToken.ThrowIfCancellationRequested()
                    use entries = (declarations scope rows[index]).GetEnumerator()

                    while entries.MoveNext() do
                        cancellationToken.ThrowIfCancellationRequested()
                        references <- references + 1

                        if references > 100000 then
                            raise (InvalidDataException "TextContent enumeration exceeded 100000 description references.")

                        match entries.Current with
                        | None -> ()
                        | Some content ->
                            if
                                isNull (box content)
                                || content.TextContentId = Guid.Empty
                                || content.Utf8ByteLength <= 0L
                                || not (ContentAddress.isValidAddress content.Blake3Hash)
                            then
                                raise (InvalidDataException "WorkItem source contains an invalid TextContent declaration.")

                            match distinct.TryGetValue content.TextContentId with
                            | true, previous when previous <> content ->
                                raise (InvalidDataException "TextContent declarations disagree about one immutable identity.")
                            | true, _ -> ()
                            | false, _ ->
                                total <- Checked.op_Addition total content.Utf8ByteLength
                                distinct.Add(content.TextContentId, content)

                    index <- index + 1

                more <- hasMore

            cancellationToken.ThrowIfCancellationRequested()

            return
                {
                    Scope = scope
                    DeclaredTextContentUtf8Bytes = total
                    DistinctTextContentCount = int64 distinct.Count
                    EnumerationStartedAt = started
                    EnumerationFinishedAt = getCurrentInstant ()
                }
        }

    /// Reads the configured WorkItem partition without provisioning storage, reading blobs or materializing content.
    let internal readFromContainer (container: Container) (scope: UsageFactScope) cancellationToken =
        task {
            let query =
                QueryDefinition("SELECT c.State FROM c WHERE c.GrainType = @grainType AND c.PartitionKey = @partitionKey")
                    .WithParameter("@grainType", Grace.Actors.Constants.StateName.WorkItem)
                    .WithParameter("@partitionKey", string scope.RepositoryId)

            let options = QueryRequestOptions(PartitionKey = PartitionKey(string scope.RepositoryId), MaxItemCount = 256)
            use iterator = container.GetItemQueryIterator<JsonElement>(query, requestOptions = options)

            return!
                enumerateWith
                    (fun token ->
                        task {
                            let! page = iterator.ReadNextAsync token

                            return
                                page.Resource
                                |> Seq.map decodeDocument
                                |> Seq.toArray,
                                iterator.HasMoreResults
                        })
                    scope
                    cancellationToken
        }

    /// Requires an existing repository in the requested scope before returning a completed TextContent declaration diagnostic.
    let Diagnose: HttpHandler =
        fun next context ->
            task {
                let correlationId = Grace.Server.Services.getCorrelationId context

                try
                    let! parameters = context.BindJsonAsync<GetRepositoryParameters>()

                    match validateParameters parameters with
                    | Error message -> return! RequestErrors.BAD_REQUEST (GraceError.Create message correlationId) next context
                    | Ok scope ->
                        use deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
                        deadline.CancelAfter(TimeSpan.FromSeconds 30.)
                        let repositoryActor = Repository.CreateActorProxy scope.OrganizationId scope.RepositoryId correlationId

                        let! repository =
                            (repositoryActor.Get correlationId)
                                .WaitAsync(deadline.Token)

                        if repository.RepositoryId <> scope.RepositoryId
                           || repository.UpdatedAt.IsNone
                           || repository.OwnerId <> scope.OwnerId
                           || repository.OrganizationId <> scope.OrganizationId then
                            return!
                                RequestErrors.BAD_REQUEST
                                    (GraceError.Create "Repository does not exist in the requested owner and organization scope." correlationId)
                                    next
                                    context
                        else
                            let! result = readFromContainer ApplicationContext.cosmosContainer scope deadline.Token
                            return! json (GraceReturnValue.Create result correlationId) next context
                with
                | :? JsonException ->
                    return! RequestErrors.BAD_REQUEST (GraceError.Create "The request body must be valid repository-scope JSON." correlationId) next context
                | :? InvalidDataException as error -> return! RequestErrors.BAD_REQUEST (GraceError.Create error.Message correlationId) next context
                | :? OperationCanceledException ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create "TextContent enumeration was cancelled or exceeded its deadline; no quantity was produced." correlationId)
                            next
                            context
                | _ ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create "TextContent enumeration failed; no quantity was produced." correlationId)
                            next
                            context
            }
