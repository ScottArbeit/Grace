namespace Grace.Server

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Giraffe
open Grace.Actors
open Grace.Actors.Services
open Grace.Actors.Extensions.ActorProxy
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.Usage
open Microsoft.Azure.Cosmos
open NodaTime

/// Reads retained DirectoryVersion declarations without materializing content or recording usage facts.
module DirectoryVersionSizeDiagnosis =

    /// Binds a completed declaration count to the requested repository and its non-atomic read window.
    type DirectoryVersionSizeDiagnostic =
        {
            Scope: UsageFactScope
            DeclaredLogicalBytes: int64
            DistinctContentCount: int64
            EnumerationStartedAt: Instant
            EnumerationFinishedAt: Instant
        }

    /// Rejects name resolution and incomplete identifiers before reading repository state.
    let internal validateParameters (parameters: GetRepositoryParameters) =
        /// Accepts only explicit, non-empty repository scope identifiers.
        let parseId (value: string) =
            match Guid.TryParse value with
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

    /// Checks each retained document and returns direct content identities using the current storage layout.
    let private declarations (scope: UsageFactScope) correlationId (row: DirectoryVersionEventValue) =
        if isNull (box row)
           || isNull row.State
           || row.State.Length = 0 then
            raise (InvalidDataException "DirectoryVersion source is missing Created data.")

        let created =
            row.State
            |> Array.choose (fun event ->
                match event.Event with
                | Created directory -> Some directory
                | _ -> None)

        if created.Length <> 1
           || (match row.State[0].Event with
               | Created _ -> false
               | _ -> true) then
            raise (InvalidDataException "DirectoryVersion source must start with exactly one Created event.")

        let directory =
            (row.State
             |> Array.fold (fun dto event -> DirectoryVersionDto.UpdateDto event dto) DirectoryVersionDto.Default)
                .DirectoryVersion

        if isNull (box directory)
           || directory.DirectoryVersionId = Guid.Empty
           || directory.CreatedAt = Constants.DefaultTimestamp
           || directory.OwnerId <> scope.OwnerId
           || directory.OrganizationId <> scope.OrganizationId
           || directory.RepositoryId <> scope.RepositoryId
           || isNull directory.Files then
            raise (InvalidDataException "DirectoryVersion source is incomplete or does not match the requested scope.")

        directory.Files
        |> Seq.map (fun file ->
            if
                isNull (box file)
                || file.Size < 0L
                || String.IsNullOrWhiteSpace file.RelativePath
                || not (ContentAddress.isValidAddress file.Sha256Hash)
                || not (ContentAddress.isValidAddress file.Blake3Hash)
                || isNull (box file.ContentReference)
            then
                raise (InvalidDataException "DirectoryVersion contains an invalid file declaration.")

            match file.ContentReference.ReferenceType, file.ContentReference.Manifest with
            | FileContentReferenceType.WholeFileContent, None ->
                struct ("whole", string scope.RepositoryId, StorageKeys.wholeFileContentObjectKey file), file.Size
            | FileContentReferenceType.FileManifest, Some manifest ->
                match DirectoryVersion.validateManifestBackedFileForSaveBoundary correlationId file manifest with
                | Error error -> raise (InvalidDataException error.Error)
                | Ok () -> struct ("manifest", manifest.StoragePoolId, manifest.ManifestAddress), manifest.Size
            | _ -> raise (InvalidDataException "DirectoryVersion contains an inconsistent content reference."))

    /// Discards the request-local count unless every page and declaration completes within fixed work limits.
    let internal enumerateWith
        (readPage: CancellationToken -> Task<DirectoryVersionEventValue array * bool>)
        (scope: UsageFactScope)
        correlationId
        (cancellationToken: CancellationToken)
        =
        task {
            let started = getCurrentInstant ()
            let values = Dictionary<struct (string * string * string), int64>()
            let mutable pages = 0
            let mutable documents = 0
            let mutable references = 0
            let mutable total = 0L
            let mutable more = true

            while more do
                cancellationToken.ThrowIfCancellationRequested()

                if pages >= 32 then
                    raise (InvalidDataException "DirectoryVersion enumeration exceeded 32 pages.")

                let! rows, hasMore = readPage cancellationToken
                pages <- pages + 1
                documents <- Checked.op_Addition documents rows.Length

                if documents > 10000 then
                    raise (InvalidDataException "DirectoryVersion enumeration exceeded 10000 documents.")

                let mutable index = 0

                while index < rows.Length do
                    cancellationToken.ThrowIfCancellationRequested()

                    use entries =
                        (declarations scope correlationId rows[index])
                            .GetEnumerator()

                    while entries.MoveNext() do
                        cancellationToken.ThrowIfCancellationRequested()
                        references <- references + 1

                        if references > 100000 then
                            raise (InvalidDataException "DirectoryVersion enumeration exceeded 100000 direct references.")

                        let key, size = entries.Current

                        match values.TryGetValue key with
                        | true, previous when previous <> size -> raise (InvalidDataException "Content declarations disagree about logical length.")
                        | true, _ -> ()
                        | false, _ ->
                            total <- Checked.op_Addition total size
                            values.Add(key, size)

                    index <- index + 1

                more <- hasMore

            cancellationToken.ThrowIfCancellationRequested()

            return
                {
                    Scope = scope
                    DeclaredLogicalBytes = total
                    DistinctContentCount = int64 values.Count
                    EnumerationStartedAt = started
                    EnumerationFinishedAt = getCurrentInstant ()
                }
        }

    /// Requires persisted declaration fields while honoring the serializer's omitted Size encoding for zero-length files.
    let internal decodeDocument (document: JsonElement) =
        /// Requires fields that carry scope and content declarations on the persisted wire shape.
        let requireFields (names: string list) (element: JsonElement) =
            names
            |> List.iter (fun name ->
                let mutable value = Unchecked.defaultof<JsonElement>

                if
                    element.ValueKind <> JsonValueKind.Object
                    || not (element.TryGetProperty(name, &value))
                then
                    raise (InvalidDataException $"DirectoryVersion source is missing required field '{name}'."))

        requireFields [ "State" ] document
        let state = document.GetProperty "State"

        if state.ValueKind <> JsonValueKind.Array
           || state.GetArrayLength() = 0 then
            raise (InvalidDataException "DirectoryVersion source is missing Created data.")

        let first = state[0]
        requireFields [ "Event" ] first
        requireFields [ "created" ] (first.GetProperty "Event")
        let created = first.GetProperty("Event").GetProperty("created")

        requireFields
            [
                "DirectoryVersionId"
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
                "CreatedAt"
                "Files"
            ]
            created

        let files = created.GetProperty "Files"

        if files.ValueKind <> JsonValueKind.Array then
            raise (InvalidDataException "DirectoryVersion source must contain direct file declarations.")

        files.EnumerateArray()
        |> Seq.iter (fun file ->
            requireFields
                [
                    "RelativePath"
                    "Sha256Hash"
                    "Blake3Hash"
                    "ContentReference"
                ]
                file

            requireFields [ "ReferenceType"; "Manifest" ] (file.GetProperty "ContentReference")
            let mutable size = Unchecked.defaultof<JsonElement>
            let mutable bytes = 0L

            if
                file.TryGetProperty("Size", &size)
                && (size.ValueKind <> JsonValueKind.Number
                    || not (size.TryGetInt64(&bytes)))
            then
                raise (InvalidDataException "DirectoryVersion file Size must be an integer or omitted for zero."))

        try
            JsonSerializer.Deserialize<DirectoryVersionEventValue>(document.GetRawText(), Constants.JsonSerializerOptions)
        with
        | :? JsonException as error -> raise (InvalidDataException("DirectoryVersion source could not be decoded.", error))

    /// Enumerates only the configured repository partition and DirectoryVersion grain without provisioning storage.
    let internal readFromContainer (container: Container) scope correlationId cancellationToken =
        task {
            let query =
                QueryDefinition("SELECT c.State FROM c WHERE c.GrainType = @grainType AND c.PartitionKey = @partitionKey")
                    .WithParameter("@grainType", Grace.Actors.Constants.StateName.DirectoryVersion)
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
                    correlationId
                    cancellationToken
        }

    /// Requires an existing repository in the supplied scope before returning a completed read-only diagnostic.
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
                            let! result = readFromContainer ApplicationContext.cosmosContainer scope correlationId deadline.Token
                            return! json (GraceReturnValue.Create result correlationId) next context
                with
                | :? System.Text.Json.JsonException ->
                    return! RequestErrors.BAD_REQUEST (GraceError.Create "The request body must be valid repository-scope JSON." correlationId) next context
                | :? InvalidDataException as error -> return! RequestErrors.BAD_REQUEST (GraceError.Create error.Message correlationId) next context
                | :? OperationCanceledException ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create "DirectoryVersion enumeration was cancelled or exceeded its deadline; no quantity was produced." correlationId)
                            next
                            context
                | _ ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create "DirectoryVersion enumeration failed; no quantity was produced." correlationId)
                            next
                            context
            }
