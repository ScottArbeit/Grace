namespace Grace.Server

open System
open System.IO
open System.Text.Json
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Usage

open Giraffe
open Grace.Actors.Services
open Grace.Actors.Extensions.ActorProxy
open Grace.Shared.Parameters.Repository
open Grace.Types.Common
open Microsoft.Azure.Cosmos
open NodaTime

/// Measures retained Artifact declarations without reading objects or recording observations.
module ArtifactSizeDiagnosis =
    /// Keeps declared bytes and distinct identities attached to verified scope and the non-atomic read window.
    type ArtifactSizeDiagnostic =
        {
            Scope: UsageFactScope
            DeclaredArtifactBytes: int64
            DistinctArtifactCount: int64
            EnumerationStartedAt: Instant
            EnumerationFinishedAt: Instant
        }

    /// Reuses the existing explicit three-ID diagnostic request contract.
    let internal validateParameters parameters = DirectoryVersionSizeDiagnosis.validateParameters parameters

    /// Requires current snapshot fields that determine declaration quantity and immutable source identity.
    let internal decodeDocument (document: JsonElement) =
        /// Checks field presence before typed deserialization can apply defaults.
        let require (name: string) (element: JsonElement) =
            let mutable value = Unchecked.defaultof<JsonElement>

            if
                element.ValueKind <> JsonValueKind.Object
                || not (element.TryGetProperty(name, &value))
            then
                raise (InvalidDataException "Artifact source is missing a required field.")

            value

        let state = require "State" document

        if state.ValueKind <> JsonValueKind.Array then
            raise (InvalidDataException "Artifact State must be an array.")

        for snapshot in state.EnumerateArray() do
            for name in
                [
                    "ArtifactId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                ] do
                let value = require name snapshot
                let mutable id = Guid.Empty

                if value.ValueKind <> JsonValueKind.String
                   || not (value.TryGetGuid(&id))
                   || id = Guid.Empty then
                    raise (InvalidDataException "Artifact source identity is invalid.")

            for name in [ "Size"; "CreatedAtUnixTimeTicks" ] do
                let value = require name snapshot
                let mutable number = 0L

                if value.ValueKind <> JsonValueKind.Number
                   || not (value.TryGetInt64(&number))
                   || (name = "Size" && number < 0L) then
                    raise (InvalidDataException "Artifact declaration is invalid.")

            for name in [ "Event"; "BlobPath" ] do
                let value = require name snapshot

                if
                    value.ValueKind <> JsonValueKind.String
                    || String.IsNullOrWhiteSpace(value.GetString())
                then
                    raise (InvalidDataException "Artifact source identity is invalid.")

        try
            JsonSerializer.Deserialize<ArtifactEvent array>(state.GetRawText(), Constants.JsonSerializerOptions)
        with
        | :? JsonException as error -> raise (InvalidDataException("Artifact source could not be decoded.", error))

    /// Projects one surviving stream and rejects a change of identity or scope inside it.
    let internal project (scope: UsageFactScope) (events: ArtifactEvent array) =
        if isNull events then raise (InvalidDataException "Artifact State is missing.")

        if events.Length = 0 then
            None
        else
            let first = events[0]

            if first.Event <> ArtifactEventNames.Created then
                raise (InvalidDataException "Artifact stream must start with Created.")

            let mutable projected = ArtifactMetadata.Default

            for index in 0 .. events.Length - 1 do
                let snapshot = events[index]

                if snapshot.ArtifactId = Guid.Empty
                   || snapshot.ArtifactId <> first.ArtifactId
                   || snapshot.OwnerId <> scope.OwnerId
                   || snapshot.OrganizationId <> scope.OrganizationId
                   || snapshot.RepositoryId <> scope.RepositoryId
                   || snapshot.BlobPath <> first.BlobPath
                   || snapshot.CreatedAtUnixTimeTicks
                      <> first.CreatedAtUnixTimeTicks
                   || snapshot.Size < 0L then
                    raise (InvalidDataException "Artifact stream identity or scope conflicts.")

                if
                    not
                        (
                            List.contains
                                snapshot.Event
                                [
                                    ArtifactEventNames.Created
                                    ArtifactEventNames.LogicalDeleted
                                    ArtifactEventNames.Undeleted
                                    ArtifactEventNames.BlobDeleted
                                    ArtifactEventNames.WorkItemLinkRemoved
                                ]
                        )
                    || (index > 0
                        && snapshot.Event = ArtifactEventNames.Created)
                then
                    raise (InvalidDataException "Artifact event order is invalid.")

                try
                    projected <- ArtifactMetadata.UpdateDto snapshot projected
                with
                | :? ArgumentException as error -> raise (InvalidDataException("Artifact snapshot contains an invalid metadata value.", error))

            Some projected

    /// Reads complete finite pages and returns no partial quantity when a provider, cap or cancellation fails.
    let internal enumerateWith (readPage: CancellationToken -> Task<ArtifactEvent array array * bool>) (scope: UsageFactScope) (token: CancellationToken) =
        task {
            let started = getCurrentInstant ()
            let distinct = Dictionary<Guid, ArtifactMetadata>()
            let mutable pages, documents, events = 0, 0, 0
            let mutable total = 0L
            let mutable more = true

            while more do
                token.ThrowIfCancellationRequested()

                if pages >= 32 then
                    raise (InvalidOperationException "Artifact page cap exceeded.")

                let! rows, hasMore = readPage token
                token.ThrowIfCancellationRequested()
                pages <- pages + 1
                documents <- Checked.op_Addition documents rows.Length

                if documents > 10000 then
                    raise (InvalidOperationException "Artifact document cap exceeded.")

                for row in rows do
                    token.ThrowIfCancellationRequested()
                    events <- Checked.op_Addition events row.Length

                    if events > 100000 then
                        raise (InvalidOperationException "Artifact event cap exceeded.")

                    match project scope row with
                    | None -> ()
                    | Some artifact ->
                        match distinct.TryGetValue artifact.ArtifactId with
                        | true, previous ->
                            if previous.Size <> artifact.Size
                               || previous.BlobPath <> artifact.BlobPath
                               || previous.CreatedAt <> artifact.CreatedAt then
                                raise (InvalidDataException "Repeated Artifact identity conflicts.")
                        | _ ->
                            total <- Checked.op_Addition total artifact.Size
                            distinct.Add(artifact.ArtifactId, artifact)

                more <- hasMore

            token.ThrowIfCancellationRequested()

            return
                {
                    Scope = scope
                    DeclaredArtifactBytes = total
                    DistinctArtifactCount = int64 distinct.Count
                    EnumerationStartedAt = started
                    EnumerationFinishedAt = getCurrentInstant ()
                }
        }

    /// Queries every Artifact document in the existing repository partition, including empty State shells.
    let internal readFromContainer (container: Container) (scope: UsageFactScope) cancellationToken =
        task {
            let query =
                QueryDefinition("SELECT c.State FROM c WHERE c.GrainType = @grainType AND c.PartitionKey = @partitionKey")
                    .WithParameter("@grainType", Grace.Actors.Constants.StateName.Artifact)
                    .WithParameter("@partitionKey", string scope.RepositoryId)

            let options = QueryRequestOptions(PartitionKey = PartitionKey(string scope.RepositoryId), MaxItemCount = 256)
            use iterator = container.GetItemQueryIterator<JsonElement>(query, requestOptions = options)

            return!
                enumerateWith
                    (fun token ->
                        task {
                            let! page = iterator.ReadNextAsync token
                            token.ThrowIfCancellationRequested()

                            return
                                page.Resource
                                |> Seq.map decodeDocument
                                |> Seq.toArray,
                                iterator.HasMoreResults
                        })
                    scope
                    cancellationToken
        }

    /// Rechecks the live repository around enumeration and discards a completed quantity if scope or cancellation changed.
    let internal diagnoseWith
        (checkRepository: CancellationToken -> Task<unit>)
        (collect: CancellationToken -> Task<ArtifactSizeDiagnostic>)
        (token: CancellationToken)
        =
        task {
            token.ThrowIfCancellationRequested()
            do! checkRepository token
            let! result = collect token
            do! checkRepository token
            token.ThrowIfCancellationRequested()
            return result
        }

    /// Requires a present, nondeleted repository bound to the three requested identifiers.
    let private checkRepository (scope: UsageFactScope) correlationId (token: CancellationToken) =
        task {
            let repositoryActor = Repository.CreateActorProxy scope.OrganizationId scope.RepositoryId correlationId

            let! repository =
                (repositoryActor.Get correlationId)
                    .WaitAsync(token)

            if repository.RepositoryId <> scope.RepositoryId
               || repository.OwnerId <> scope.OwnerId
               || repository.OrganizationId <> scope.OrganizationId
               || repository.UpdatedAt.IsNone
               || repository.DeletedAt.IsSome then
                raise (InvalidDataException "Repository is missing, deleted or outside the requested scope.")
        }

    /// Serves a fresh declaration scan after route-level SystemAdmin authorization, without publishing partial quantities.
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

                        let! result =
                            diagnoseWith (checkRepository scope correlationId) (readFromContainer ApplicationContext.cosmosContainer scope) deadline.Token

                        deadline.Token.ThrowIfCancellationRequested()
                        return! json (GraceReturnValue.Create result correlationId) next context
                with
                | :? JsonException ->
                    return! RequestErrors.BAD_REQUEST (GraceError.Create "The request body must be valid repository-scope JSON." correlationId) next context
                | :? InvalidDataException ->
                    return!
                        RequestErrors.BAD_REQUEST
                            (GraceError.Create "Repository scope or retained Artifact source is invalid; no quantity was produced." correlationId)
                            next
                            context
                | :? OperationCanceledException ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create "Artifact enumeration was cancelled or exceeded its deadline; no quantity was produced." correlationId)
                            next
                            context
                | _ ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create "Artifact enumeration failed or exceeded its bounds; no quantity was produced." correlationId)
                            next
                            context
            }
