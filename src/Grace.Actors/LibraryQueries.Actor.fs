namespace Grace.Actors

open Grace.Shared
open Grace.Types.Common
open Grace.Types.Library
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NodaTime

/// Runs the bounded full-partition SQL reads that Orleans point storage cannot express.
module LibraryQueries =

    /// Caps one immutable baseline shard below the provider document limit.
    [<Literal>]
    let BaselineShardMaximumBytes = 1_000_000

    /// Uses the production JSON contract without diagnostic indentation so item bytes compose exactly inside a shard.
    let private baselineSerializerOptions =
        let options = JsonSerializerOptions(Constants.JsonSerializerOptions)
        options.WriteIndented <- false
        options

    /// Serializes one baseline shard with the exact options used for its durable hash and byte limit.
    let serializeBaselineShard (shard: LibraryBaselineShardDocument) = JsonSerializer.SerializeToUtf8Bytes(shard, baselineSerializerOptions)

    /// Returns the exact serialized size of an empty baseline shard before item bytes and separators are added.
    let emptyBaselineShardBytes =
        serializeBaselineShard { SchemaVersion = 1; Items = Array.empty }
        |> Array.length

    /// Adds one independently serialized item to the exact shard byte count without repeatedly serializing the growing buffer.
    let appendBaselineItem (current: ResizeArray<LibraryItemDto>) currentBytes item =
        let itemBytes =
            JsonSerializer
                .SerializeToUtf8Bytes(
                    item,
                    baselineSerializerOptions
                )
                .Length

        let separatorBytes = if current.Count = 0 then 0 else 1
        let candidateBytes = currentBytes + separatorBytes + itemBytes

        if current.Count = 0 then
            let singleton = { SchemaVersion = 1; Items = [| item |] }
            let exactSingletonBytes = (serializeBaselineShard singleton).Length

            if exactSingletonBytes <> candidateBytes then
                invalidOp $"The incremental Library baseline singleton byte count {candidateBytes} did not match exact serialization {exactSingletonBytes}."

        if candidateBytes <= BaselineShardMaximumBytes then
            current.Add item
            candidateBytes, None
        else
            if current.Count = 0 then
                invalidOp "One Library baseline item exceeds the one-megabyte shard bound."

            let completed = { SchemaVersion = 1; Items = current.ToArray() }
            let exactBytes = (serializeBaselineShard completed).Length

            if exactBytes <> currentBytes then
                invalidOp $"The incremental Library baseline byte count {currentBytes} did not match exact serialization {exactBytes}."

            current.Clear()
            current.Add item
            let singletonBytes = emptyBaselineShardBytes + itemBytes

            if singletonBytes > BaselineShardMaximumBytes then
                invalidOp "One Library baseline item exceeds the one-megabyte shard bound."

            singletonBytes, Some completed

    /// Completes the remaining baseline buffer after the last current-item query page.
    let finishBaselineShard (current: ResizeArray<LibraryItemDto>) currentBytes =
        if current.Count = 0 then
            None
        else
            let shard = { SchemaVersion = 1; Items = current.ToArray() }
            let serializedBytes = (serializeBaselineShard shard).Length

            if serializedBytes <> currentBytes then
                invalidOp $"The incremental Library baseline byte count {currentBytes} did not match exact serialization {serializedBytes}."

            if serializedBytes > BaselineShardMaximumBytes then
                invalidOp "A Library baseline shard exceeded the one-megabyte byte bound."

            current.Clear()
            Some shard

    /// Returns the configured Library database through the silo's shared Cosmos client.
    let private database (services: IServiceProvider) =
        let client = services.GetRequiredService<CosmosClient>()
        let configuration = services.GetRequiredService<IConfiguration>()
        let name = configuration[Utilities.getConfigKey Constants.EnvironmentVariables.AzureCosmosDBDatabaseName]

        if String.IsNullOrWhiteSpace name then
            invalidOp "The Library Cosmos database name is not configured."

        client.GetDatabase name

    /// Builds one complete two-level hierarchical partition key.
    let private partition (repositoryId: RepositoryId) (purpose: string) =
        PartitionKeyBuilder()
            .Add(repositoryId.ToString("D"))
            .Add(purpose)
            .Build()

    /// Reads one bounded current-item page in stable provider-document order from the exact repository item partition.
    let readCurrentItemPage (services: IServiceProvider) (repositoryId: RepositoryId) continuationToken cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.CurrentContainerName

            let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId "item"), MaxItemCount = Nullable 256)

            let query =
                QueryDefinition("SELECT VALUE c.State FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = 'item' ORDER BY c.id")
                    .WithParameter("@repository", repositoryId.ToString("D"))

            use iterator = container.GetItemQueryIterator<LibraryCurrentItemDocument>(query, defaultArg continuationToken null, options)

            if iterator.HasMoreResults then
                let! page = iterator.ReadNextAsync(cancellationToken)

                return
                    page.Resource |> Seq.toArray,
                    if String.IsNullOrWhiteSpace page.ContinuationToken then
                        None
                    else
                        Some page.ContinuationToken
            else
                return Array.empty, None
        }

    /// Reads every current item for the bounded catalog and descendant checks that require the complete namespace graph.
    let readCurrentItems (services: IServiceProvider) (repositoryId: RepositoryId) cancellationToken =
        task {
            let values = ResizeArray<LibraryCurrentItemDocument>()
            let mutable continuationToken = None
            let mutable more = true

            while more do
                let! page, next = readCurrentItemPage services repositoryId continuationToken cancellationToken
                values.AddRange page
                continuationToken <- next
                more <- next.IsSome

            return values.ToArray()
        }

    /// Reports whether a live item directly occupies one parent identity.
    let hasLiveChildren (services: IServiceProvider) (repositoryId: RepositoryId) (parent: LibraryParentDto) cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.CurrentContainerName

            let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId "item"), MaxItemCount = Nullable 1)

            let baseSql = "SELECT TOP 1 VALUE true FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = 'item' AND IS_NULL(c.State.Item.Tombstone)"

            let query =
                match parent.Kind, parent.LibraryPath, parent.ItemId with
                | "item", None, Some itemId ->
                    QueryDefinition(
                        baseSql
                        + " AND c.State.Item.Namespace.Parent.Kind = 'item' AND c.State.Item.Namespace.Parent.ItemId = @itemId"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@itemId", itemId)
                | "root", Some root, None ->
                    QueryDefinition(
                        baseSql
                        + " AND c.State.Item.Namespace.Parent.Kind = 'root' AND STRINGEQUALS(c.State.Item.Namespace.Parent.LibraryPath, @root, true)"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@root", root)
                | _ -> invalidArg (nameof parent) "A Library parent must identify one root or directory item."

            use iterator = container.GetItemQueryIterator<bool>(query, requestOptions = options)

            if iterator.HasMoreResults then
                let! page = iterator.ReadNextAsync(cancellationToken)
                return page.Resource |> Seq.isEmpty |> not
            else
                return false
        }

    /// Appends only the visible contiguous journal prefix and reports the first missing cursor.
    let appendContiguousChanges expectedCursor maximumCount (results: ResizeArray<LibraryAcceptedChangeRecord>) changes =
        let mutable cursor = expectedCursor
        let mutable gap = false

        for change in changes do
            if not gap && results.Count < maximumCount then
                if change.Cursor = cursor then
                    results.Add change
                    cursor <- cursor + 1L
                elif change.Cursor > cursor then
                    gap <- true

        cursor, gap

    /// Selects one public change page while retaining continuation until its pinned boundary is reached.
    let changePageWindow position boundary pageSize (records: LibraryAcceptedChangeRecord array) =
        let selected = records |> Array.truncate pageSize

        let lastPosition =
            if Array.isEmpty selected then
                position
            else
                selected[selected.Length - 1].Cursor

        selected, lastPosition, lastPosition < boundary

    /// Reads committed journal records after one position while touching only required cursor segments.
    let readChanges (services: IServiceProvider) (repositoryId: RepositoryId) afterCursor committedCursor maximumCount cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.ChangesContainerName

            let results = ResizeArray<LibraryAcceptedChangeRecord>()
            let mutable cursor = afterCursor + 1L
            let mutable gap = false

            while cursor <= committedCursor
                  && results.Count < maximumCount
                  && not gap do
                let segment =
                    (cursor - 1L) / 200L
                    |> fun value -> value.ToString("D20")

                let segmentEnd = Math.Min(committedCursor, (Int64.Parse segment + 1L) * 200L)
                let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId segment), MaxItemCount = Nullable maximumCount)

                let query =
                    QueryDefinition(
                        "SELECT VALUE c.State FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = @segment AND c.State.Cursor >= @cursor AND c.State.Cursor <= @committed ORDER BY c.State.Cursor"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@segment", segment)
                        .WithParameter("@cursor", cursor)
                        .WithParameter("@committed", committedCursor)

                use iterator = container.GetItemQueryIterator<LibraryAcceptedChangeRecord>(query, requestOptions = options)

                while iterator.HasMoreResults
                      && results.Count < maximumCount
                      && not gap do
                    let! page = iterator.ReadNextAsync(cancellationToken)
                    let nextCursor, pageGap = appendContiguousChanges cursor maximumCount results page.Resource
                    cursor <- nextCursor
                    gap <- pageGap

                if not gap
                   && results.Count < maximumCount
                   && cursor <= segmentEnd then
                    gap <- true

            return results.ToArray()
        }

    /// Reads the existing committed boundary without activating the Library actor or repairing its pending turn.
    let readDiagnosticControl (services: IServiceProvider) (repositoryId: RepositoryId) (token: CancellationToken) =
        task {
            token.ThrowIfCancellationRequested()

            let! stored =
                (LibraryRecords.read<LibraryControlDocument>
                    services
                    LibraryRecords.ControlStorageName
                    "Grace.Library.Control.v2"
                    (LibraryRecords.key [ repositoryId.ToString("D") ]))
                    .WaitAsync(token)

            match stored with
            | Some (control, _) when
                control.SchemaVersion = 1
                && control.Epoch <> Guid.Empty
                && control.CommittedCursor >= 0L
                && control.ReplayFloor > 0L
                && control.ReplayFloor - 1L
                   <= control.CommittedCursor
                && control.ItemRecordCount >= 0
                && control.SlotRecordCount >= 0
                && control.HistoryThrough >= 0L
                && control.HistoryThrough <= control.CommittedCursor
                && control.NotifyThrough >= 0L
                && control.NotifyThrough <= control.CommittedCursor
                && not (isNull (box control.Catalog))
                && control.Catalog.RepositoryId = repositoryId
                && control.Catalog.Version <> Guid.Empty
                && not (isNull control.Catalog.Libraries)
                ->
                return control
            | _ -> return raise (InvalidDataException "An existing valid Library control record is required.")
        }

    /// Validates an immutable declared manifest without reading payloads or counting reference multiplicity.
    let validateDiagnosticLocation (descriptor: LibraryContentVersionDto) (location: LibraryContentLocationDocument) =
        if isNull (box location)
           || location.SchemaVersion <> 1
           || location.Content <> descriptor
           || isNull (box location.Manifest)
           || String.IsNullOrWhiteSpace location.AuthorizedScope then
            raise (InvalidDataException "The committed Library content mapping is incomplete or conflicting.")

        let manifest = location.Manifest

        if descriptor.Size <= 0L
           || not (ContentAddress.isValidAddress descriptor.Blake3Hash)
           || not (ContentAddress.isValidAddress descriptor.Sha256Hash)
           || descriptor.ContentVersionId
              <> LibraryDecision.contentVersionId descriptor.Blake3Hash
           || manifest.Class <> "FileManifest"
           || manifest.Size <> descriptor.Size
           || manifest.FileContentHash <> descriptor.Blake3Hash
           || String.IsNullOrWhiteSpace manifest.StoragePoolId
           || String.IsNullOrWhiteSpace manifest.ChunkingSuiteId
           || isNull manifest.Blocks
           || manifest.Blocks.Count = 0 then
            raise (InvalidDataException "The committed Library content declaration is invalid.")

        let mutable offset = 0L

        for block in manifest.Blocks do
            if
                isNull (box block)
                || block.Offset <> offset
                || block.Size <= 0L
                || not (ContentAddress.isValidAddress block.Address)
            then
                raise (InvalidDataException "The Library manifest does not describe contiguous valid content ranges.")

            offset <- Checked.op_Addition offset block.Size

        if offset <> manifest.Size
           || ContentAddress.computeManifestAddressForManifest manifest
              <> manifest.ManifestAddress then
            raise (InvalidDataException "The Library manifest identity does not match its complete declaration.")

        manifest

    /// Exhausts the fixed accepted prefix, rejecting gaps and conflicts before returning any quantity.
    let enumerateDiagnosticContentWith
        (boundary: int64)
        (readPage: int64 -> CancellationToken -> Task<LibraryAcceptedChangeRecord array>)
        (readLocation: LibraryContentVersionId -> CancellationToken -> Task<LibraryContentLocationDocument option>)
        (token: CancellationToken)
        =
        task {
            let manifests = Dictionary<StoragePoolId * ManifestAddress, FileManifest>()
            let mutable position = 0L
            let mutable total = 0L

            while position < boundary do
                token.ThrowIfCancellationRequested()
                let! rows = readPage position token

                if rows.Length = 0 then
                    raise (InvalidDataException "Committed Library history has a missing cursor.")

                let mutable index = 0

                while index < rows.Length do
                    token.ThrowIfCancellationRequested()
                    let row = rows[index]

                    if row.SchemaVersion <> 1
                       || row.Cursor <> position + 1L
                       || row.Cursor > boundary then
                        raise (InvalidDataException "Committed Library history is not the exact selected prefix.")

                    if row.Change.Item.ItemKind <> ItemKind.File
                       && row.Change.Item.ItemKind <> ItemKind.Directory then
                        raise (InvalidDataException "Committed Library history has an unknown item kind.")

                    match row.Change.Item.Content with
                    | None when
                        row.Change.Item.ItemKind = ItemKind.File
                        && row.Change.Item.Tombstone.IsNone
                        ->
                        raise (InvalidDataException "A live Library file is missing its committed content declaration.")
                    | None -> ()
                    | Some _ when row.Change.Item.ItemKind <> ItemKind.File ->
                        raise (InvalidDataException "A Library directory has an invalid content declaration.")
                    | Some descriptor ->
                        let! stored = readLocation descriptor.ContentVersionId token

                        let location =
                            stored
                            |> Option.defaultWith (fun () -> raise (InvalidDataException "A committed Library content mapping is missing."))

                        let manifest = validateDiagnosticLocation descriptor location
                        let identity = manifest.StoragePoolId, manifest.ManifestAddress

                        match manifests.TryGetValue identity with
                        | true, previous when previous <> manifest -> raise (InvalidDataException "A Library manifest identity has conflicting declarations.")
                        | true, _ -> ()
                        | false, _ ->
                            total <- Checked.op_Addition total manifest.Size
                            manifests.Add(identity, manifest)

                    position <- row.Cursor
                    index <- index + 1

            token.ThrowIfCancellationRequested()
            return total, int64 manifests.Count
        }

    /// Requires all selected source containers, even at zero, then reads permanent history through the captured control.
    let readDiagnosticContent (services: IServiceProvider) (repositoryId: RepositoryId) (token: CancellationToken) =
        task {
            let started = SystemClock.Instance.GetCurrentInstant()
            let db = database services

            let names =
                [|
                    LibraryRecords.ControlContainerName
                    LibraryRecords.ChangesContainerName
                    LibraryRecords.CurrentContainerName
                |]

            let mutable index = 0

            while index < names.Length do
                let! _ =
                    db
                        .GetContainer(names[index])
                        .ReadContainerAsync(cancellationToken = token)

                index <- index + 1

            let! control = readDiagnosticControl services repositoryId token

            let! total, count =
                enumerateDiagnosticContentWith
                    control.CommittedCursor
                    (fun after token -> readChanges services repositoryId after control.CommittedCursor 200 token)
                    (fun contentId token ->
                        task {
                            let! stored =
                                (LibraryRecords.read<LibraryContentLocationDocument>
                                    services
                                    LibraryRecords.CurrentStorageName
                                    "Grace.Library.Content.v2"
                                    (LibraryRecords.key [ repositoryId.ToString("D")
                                                          "content"
                                                          contentId.ToString("D") ]))
                                    .WaitAsync(token)

                            return stored |> Option.map fst
                        })
                    token

            return total, count, control.Epoch, control.CommittedCursor, started
        }

    /// Establishes query-client visibility of the last committed item before baseline enumeration.
    let waitForItemCursor (services: IServiceProvider) (repositoryId: RepositoryId) itemId expectedCursor cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.CurrentContainerName

            let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId "item"), MaxItemCount = Nullable 1)
            let mutable visible = false
            let mutable attempts = 0

            while not visible && attempts < 8 do
                attempts <- attempts + 1

                let query =
                    QueryDefinition(
                        "SELECT TOP 1 VALUE c.State.LastCursor FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = 'item' AND c.State.Item.ItemId = @itemId"
                    )
                        .WithParameter("@repository", repositoryId.ToString("D"))
                        .WithParameter("@itemId", itemId)

                use iterator = container.GetItemQueryIterator<int64>(query, requestOptions = options)

                if iterator.HasMoreResults then
                    let! page = iterator.ReadNextAsync(cancellationToken)
                    visible <- page.Resource |> Seq.exists ((=) expectedCursor)

            if not visible then
                invalidOp $"The current Library partition did not expose committed cursor {expectedCursor}."
        }
