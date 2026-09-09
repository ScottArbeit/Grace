open System
open System.IO
open System.Text.Json
open System.Net.Http
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Reflection
open Grace.Types.Common
open Grace.Types.Library
open Grace.Shared
open Grace.Actors
open Grace.Server
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open Orleans.Configuration
open Orleans.Persistence.Cosmos
open Orleans.Serialization.Serializers
open Orleans.Storage
open Orleans.Serialization
open Orleans
open global.NodaTime

let checks = ResizeArray<string>()

/// Records one finite assertion and stops the experiment on the first failure.
let check name ok =
    if not ok then
        failwith name
    else
        checks.Add name
        printfn "PASS %s" name

let options =
    CosmosClientOptions(
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,
        UseSystemTextJsonSerializerWithOptions = Constants.JsonSerializerOptions
    )

options.HttpClientFactory <-
    fun () ->
        let handler = new HttpClientHandler()

        handler.ServerCertificateCustomValidationCallback <-
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator

        new HttpClient(handler)

/// Connects only to the independently owned local emulator using its public development key.
let newClient () =
    new CosmosClient(
        "https://127.0.0.1:18090",
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
        options
    )

let databaseName = "issue1077-" + Guid.NewGuid().ToString("N")

let purposes =
    [ LibraryRecords.ControlStorageName, LibraryRecords.ControlContainerName, 1
      LibraryRecords.ChangesStorageName, LibraryRecords.ChangesContainerName, 2
      LibraryRecords.CurrentStorageName, LibraryRecords.CurrentContainerName, 2 ]

/// Instantiates the actual registered provider types with production keys and resource creation disabled.
let services (client: CosmosClient) =
    task {
        let sc = ServiceCollection()
        sc.AddSerializer(fun _ -> ()) |> ignore
        sc.AddLogging() |> ignore
        sc.AddSingleton<CosmosClient>(client) |> ignore

        let config =
            ConfigurationBuilder()
                .AddInMemoryCollection(
                    [ KeyValuePair(
                          Utilities.getConfigKey Constants.EnvironmentVariables.AzureCosmosDBDatabaseName,
                          databaseName
                      ) ]
                )
                .Build()

        sc.AddSingleton<IConfiguration>(config) |> ignore
        let bootstrap = sc.BuildServiceProvider()

        let cluster =
            Options.Create(ClusterOptions(ClusterId = "preflight1077", ServiceId = "Grace"))

        for name, container, depth in purposes do
            let opts =
                CosmosGrainStorageOptions(
                    ContainerName = container,
                    DatabaseName = databaseName,
                    PartitionKeyLevelCount = depth,
                    IsResourceCreationEnabled = false
                )

            opts.ConfigureCosmosClient(fun _ -> ValueTask.FromResult client)

            let provider =
                CosmosGrainStorage(
                    name,
                    opts,
                    bootstrap.GetRequiredService<ILoggerFactory>(),
                    bootstrap,
                    cluster,
                    LibraryPersistence.GraceDocumentIdProvider(cluster, depth),
                    bootstrap.GetRequiredService<IActivatorProvider>()
                )

            let init =
                typeof<CosmosGrainStorage>.GetMethod ("Init", BindingFlags.Instance ||| BindingFlags.NonPublic)

            do! init.Invoke(provider, [| box CancellationToken.None |]) :?> Task

            sc.AddKeyedSingleton<IGrainStorage>(name, provider)
            |> ignore

        return sc.BuildServiceProvider()
    }

let instant = Instant.FromUtc(2026, 9, 9, 8, 0)
let repo = Guid.Parse "10770000-0000-0000-0000-000000000001"
let catalogVersion = Guid.NewGuid()
let epoch = Guid.NewGuid()
let tokenKey = Array.create 32 107uy

let signedCursor value =
    LibraryTokens.cursor tokenKey repo epoch value

let itemX, itemY = Guid.NewGuid(), Guid.NewGuid()

/// Builds valid deterministic manifest identities from fixture payloads using current Grace helpers.
let content fill size =
    let bytes = Array.create size (byte fill)
    let hash = ContentAddress.computeBlake3Hex bytes
    let sha = Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData bytes)
    let blocks = [ ContentBlock.Create(hash, 0L, int64 size) ]
    let suite = ChunkingSuiteId "coverage-fixture"
    let address = ContentAddress.computeManifestAddress suite hash (int64 size) blocks

    let manifest =
        FileManifest.Create(address, suite, hash, int64 size, StoragePoolId "default", blocks)

    let descriptor =
        { ContentVersionId = LibraryDecision.contentVersionId hash
          Blake3Hash = hash
          Sha256Hash = sha
          Size = int64 size
          CreatedAt = instant }

    { SchemaVersion = 1
      Content = descriptor
      AuthorizedScope = "coverage-fixture"
      Manifest = manifest }

let a, b, orphan, pending, c =
    content 65 10, content 66 20, content 79 7, content 80 9, content 67 30

/// Builds typed fixture records; provider envelopes are real while actor admission is not exercised.
let item itemId cursor location deleted =
    let ns =
        { Parent =
            { Kind = "root"
              LibraryPath = Some "shared"
              ItemId = None }
          Name = "sample.bin"
          NamespaceVersion = Guid.NewGuid() }

    { ItemId = itemId
      ItemKind = "file"
      LastChangeCursor = signedCursor cursor
      Namespace = (if deleted then None else Some ns)
      Content =
        (if deleted then
             None
         else
             Some location.Content)
      ContentRevision =
        (if deleted then
             None
         else
             Some(signedCursor cursor))
      Tombstone =
        (if deleted then
             Some
                 { DeletedAt = instant
                   DeletedBy = "fixture"
                   DeleteCursor = signedCursor cursor
                   LastNamespace = ns
                   LastContentVersionId = Some location.Content.ContentVersionId }
         else
             None) }

let change cursor kind itemId location deleted : LibraryAcceptedChangeRecord =
    { SchemaVersion = 1
      Cursor = cursor
      RequestHash = "fixture"
      CorrelationId = "coverage-fixture"
      Change =
        { OperationId = Guid.NewGuid()
          ChangeKind = kind
          AcceptedAt = instant
          AcceptedBy = "fixture"
          LibraryCatalogVersion = catalogVersion
          Item = item itemId cursor location deleted
          Conflict = None }
      PriorNamespace = None
      PriorContentVersionId = None
      ConsumedNamespaceVersion = None
      ConsumedContentVersionId = None
      ConsumedContentRevision = None
      ConsumedSlotVersion = None
      AddedItemRecord = false
      AddedSlotRecord = false }

let records =
    [| change 1L "createFile" itemX a false
       change 2L "updateContent" itemX b false
       change 3L "rename" itemX b false
       change 4L "createFile" itemY a false
       change 5L "delete" itemX b true
       change 6L "updateContent" itemY pending false |]



/// Waits for fixture IO so each observed effect has a deterministic boundary.
let awaitTask (value: Task<'T>) = value.GetAwaiter().GetResult()
let client = newClient ()

let db =
    (client.CreateDatabaseAsync(databaseName)
     |> awaitTask)
        .Database

for _, name, depth in purposes do
    let paths =
        [| "/PartitionKey"
           "/PartitionKey2"
           "/PartitionKey3" |]
        |> Array.take depth

    let props =
        if depth = 1 then
            ContainerProperties(name, paths[0])
        else
            ContainerProperties(name, paths :> IReadOnlyList<string>)

    db.CreateContainerAsync(props)
    |> awaitTask
    |> ignore

let sp = services client |> awaitTask

/// Encodes the same repository-led provider key as RepositoryLibraryActor.
let key tail =
    LibraryRecords.key (repo.ToString("D") :: tail)

/// Selects the permanent cursor segment and immutable record identity.
let changeKey cursor =
    key [ ((cursor - 1L) / 200L).ToString("D20")
          cursor.ToString("D20") ]

/// Seeds fixture state through the real Orleans provider; diagnostic reads never call this.
let write storage typ key value =
    LibraryRecords.write sp storage typ key null value
    |> awaitTask
    |> ignore

/// Reads the provider control directly without running the actor repair path.
let readControl (services: IServiceProvider) =
    LibraryRecords.read<LibraryControlDocument>
        services
        LibraryRecords.ControlStorageName
        "Grace.Library.Control.v2"
        (key [])
    |> awaitTask

let control =
    { SchemaVersion = 1
      Catalog = { LibraryCatalogDto.CreateInitial(repo, instant, "fixture") with Libraries = [| "shared" |] }
      Epoch = epoch
      CommittedCursor = 205L
      ReplayFloor = 203L
      Pending = Some(LibraryPendingDecision.ItemChange(change 206L "updateContent" itemY pending false))
      ItemRecordCount = 2
      SlotRecordCount = 2
      HistoryThrough = 2L
      NotifyThrough = 1L }

write LibraryRecords.ControlStorageName "Grace.Library.Control.v2" (key []) control

for location in [ a; b; pending; orphan ] do
    write
        LibraryRecords.CurrentStorageName
        "Grace.Library.Content.v2"
        (key [ "content"
               location.Content.ContentVersionId.ToString("D") ])
        location

for cursor in 1L .. 206L do
    let location =
        if cursor = 206L then pending
        elif cursor = 2L then b
        else a

    let record =
        change
            cursor
            (if cursor = 1L then
                 "createFile"
             else
                 "updateContent")
            itemX
            location
            false

    write LibraryRecords.ChangesStorageName "Grace.Library.Change.v2" (changeKey cursor) record

/// Captures all fixture envelopes including ETags for the no-write comparison.
let snapshot () =
    purposes
    |> List.collect (fun (_, name, _) ->
        use it =
            db
                .GetContainer(name)
                .GetItemQueryIterator<JsonElement>("SELECT * FROM c")

        let rows = ResizeArray<string>()

        while it.HasMoreResults do
            let page = it.ReadNextAsync() |> awaitTask

            for row in page do
                rows.Add(row.GetRawText())

        rows |> Seq.sort |> Seq.toList)

let before = snapshot ()
File.WriteAllLines(Path.Combine(__SOURCE_DIRECTORY__, "production-envelopes.jsonl"), before)
let mutable actualPages = 0
let mutable actualRows = 0

for segment in
    [ "00000000000000000000"
      "00000000000000000001" ] do
    let partition =
        PartitionKeyBuilder()
            .Add(repo.ToString("D"))
            .Add(segment)
            .Build()

    let query =
        QueryDefinition(
            "SELECT VALUE c.State FROM c WHERE c.PartitionKey = @repo AND c.PartitionKey2 = @segment AND c.State.Cursor <= 205 ORDER BY c.State.Cursor"
        )
            .WithParameter("@repo", repo.ToString("D"))
            .WithParameter("@segment", segment)

    use iterator =
        db
            .GetContainer(LibraryRecords.ChangesContainerName)
            .GetItemQueryIterator<LibraryAcceptedChangeRecord>(
                query,
                requestOptions = QueryRequestOptions(PartitionKey = partition, MaxItemCount = 17)
            )

    while iterator.HasMoreResults do
        let page = iterator.ReadNextAsync() |> awaitTask
        actualPages <- actualPages + 1
        actualRows <- actualRows + page.Count

printfn "ACTUAL COSMOS pages=%d rows=%d segments=2" actualPages actualRows

/// Retains the original preflight accumulator separately from the compiled production adapter checks.
let observe (services: IServiceProvider) boundary (token: CancellationToken) afterBatch =
    let found = Dictionary<struct (StoragePoolId * ManifestAddress), int64>()
    let mutable cursor = 0L
    let mutable batches = 0

    while cursor < boundary do
        token.ThrowIfCancellationRequested()

        let rows =
            LibraryQueries.readChanges services repo cursor boundary 17 token
            |> awaitTask

        if rows.Length = 0 then failwith "gap"

        for row in rows do
            if row.Cursor <> cursor + 1L then
                failwith "gap"

            cursor <- row.Cursor

            match row.Change.Item.Content with
            | None -> ()
            | Some descriptor ->
                token.ThrowIfCancellationRequested()

                let location =
                    LibraryRecords.read<LibraryContentLocationDocument>
                        services
                        LibraryRecords.CurrentStorageName
                        "Grace.Library.Content.v2"
                        (key [ "content"
                               descriptor.ContentVersionId.ToString("D") ])
                    |> awaitTask

                match location with
                | None -> failwith "mapping"
                | Some (location, _) ->
                    if location.Content <> descriptor
                       || location.Manifest.Size <> descriptor.Size
                       || location.Manifest.FileContentHash
                          <> descriptor.Blake3Hash then
                        failwith "mapping conflict"

                    found[struct (location.Manifest.StoragePoolId, location.Manifest.ManifestAddress)] <- descriptor.Size

        batches <- batches + 1
        afterBatch batches

    token.ThrowIfCancellationRequested()
    found.Values |> Seq.sum, batches

let productionTotal, productionCount, productionEpoch, productionBoundary, _ =
    LibraryQueries.readDiagnosticContent sp repo CancellationToken.None
    |> awaitTask

check
    "production adapter captures and exhausts actual prefix"
    (productionTotal = 30L
     && productionCount = 2L
     && productionEpoch = epoch
     && productionBoundary = 205L)

let capture = readControl sp |> Option.get |> fst

let bytes, batches =
    observe sp capture.CommittedCursor CancellationToken.None ignore

check
    "actual provider control and cross-segment history below ReplayFloor: 30 bytes"
    (bytes = 30L
     && batches > 1
     && capture.ReplayFloor = 203L
     && capture.Pending.IsSome)

let after = snapshot ()
check "diagnostic did not change any provider document or ETag" (before = after)
let fresh = newClient ()
let freshSp = services fresh |> awaitTask

let retryBytes, _ =
    observe freshSp capture.CommittedCursor CancellationToken.None ignore

check "fresh client and provider retry rereads same committed prefix" (retryBytes = bytes)
let _, etag = readControl sp |> Option.get

LibraryRecords.write
    sp
    LibraryRecords.ControlStorageName
    "Grace.Library.Control.v2"
    (key [])
    etag
    { control with
        CommittedCursor = 206L
        Pending = None }
|> awaitTask
|> ignore

let pinnedBytes, _ =
    observe freshSp capture.CommittedCursor CancellationToken.None ignore

let advanced = readControl freshSp |> Option.get |> fst

check
    "tail advancement retains frozen prefix"
    (pinnedBytes = 30L
     && advanced.CommittedCursor = 206L
     && advanced.Epoch = capture.Epoch)

/// Requires the attempted read to fail before it can publish a quantity.
let fails name action =
    let mutable failed = false

    try
        action ()
    with
    | _ -> failed <- true

    check name failed

let cancellation = new CancellationTokenSource()

fails "cancellation after partial accumulation yields no quantity" (fun () ->
    observe sp 205L cancellation.Token (fun batch -> if batch = 1 then cancellation.Cancel())
    |> ignore)

fails "pre-cancelled fresh attempt yields no quantity" (fun () ->
    observe sp 205L cancellation.Token ignore
    |> ignore)

let providerId =
    LibraryPersistence.GraceDocumentIdProvider(Options.Create(ClusterOptions(ServiceId = "Grace")), 2)
    :> IDocumentIdProvider

/// Injects one precisely addressed missing-source failure in the disposable database.
let delete typ recordKey container partition =
    let doc =
        providerId
            .GetDocumentKey(typ, Orleans.Runtime.GrainId.Create(typ, recordKey))
            .AsTask()
        |> awaitTask

    db
        .GetContainer(container)
        .DeleteItemAsync<JsonElement>(doc.DocumentId, partition)
    |> awaitTask
    |> ignore

/// Builds the exact two-level provider partition used for an injected failure.
let pk (purpose: string) =
    PartitionKeyBuilder()
        .Add(repo.ToString("D"))
        .Add(purpose)
        .Build()

delete "Grace.Library.Change.v2" (changeKey 200L) LibraryRecords.ChangesContainerName (pk "00000000000000000000")

fails "exact gap at cursor 200 yields no quantity" (fun () ->
    observe sp 205L CancellationToken.None ignore
    |> ignore)

write
    LibraryRecords.ChangesStorageName
    "Grace.Library.Change.v2"
    (changeKey 200L)
    (change 200L "updateContent" itemX a false)

delete
    "Grace.Library.Content.v2"
    (key [ "content"
           b.Content.ContentVersionId.ToString("D") ])
    LibraryRecords.CurrentContainerName
    (pk "content")

fails "missing required immutable mapping yields no quantity" (fun () ->
    observe sp 205L CancellationToken.None ignore
    |> ignore)

let absent =
    LibraryRecords.read<LibraryControlDocument>
        sp
        LibraryRecords.ControlStorageName
        "Grace.Library.Control.v2"
        (LibraryRecords.key [ Guid.NewGuid().ToString("D") ])
    |> awaitTask

check "missing control remains absent without provisioning" absent.IsNone
let capturedControl, capturedEtag = readControl freshSp |> Option.get

LibraryRecords.write
    sp
    LibraryRecords.ControlStorageName
    "Grace.Library.Control.v2"
    (key [])
    capturedEtag
    { capturedControl with Epoch = Guid.NewGuid() }
|> awaitTask
|> ignore

let changedEpoch = readControl freshSp |> Option.get |> fst
check "final control reread detects epoch change" (changedEpoch.Epoch <> capture.Epoch)
let zeroRepo = Guid.NewGuid()
let zeroKey = LibraryRecords.key [ zeroRepo.ToString("D") ]

write
    LibraryRecords.ControlStorageName
    "Grace.Library.Control.v2"
    zeroKey
    { control with
        Catalog = LibraryCatalogDto.CreateInitial(zeroRepo, instant, "fixture")
        CommittedCursor = 0L
        Pending = None
        ReplayFloor = 1L
        HistoryThrough = 0L
        NotifyThrough = 0L
        ItemRecordCount = 0
        SlotRecordCount = 0 }

let zero =
    LibraryRecords.read<LibraryControlDocument> sp LibraryRecords.ControlStorageName "Grace.Library.Control.v2" zeroKey
    |> awaitTask
    |> Option.get
    |> fst

check "existing zero control persists and rereads at cursor zero" (zero.CommittedCursor = 0L)

let productionZero, productionZeroCount, _, productionZeroBoundary, _ =
    LibraryQueries.readDiagnosticContent sp zeroRepo CancellationToken.None
    |> awaitTask

check
    "production adapter returns explicit zero only with all source containers"
    (productionZero = 0L
     && productionZeroCount = 0L
     && productionZeroBoundary = 0L)

db
    .GetContainer(LibraryRecords.ChangesContainerName)
    .DeleteContainerAsync()
|> awaitTask
|> ignore

fails "production zero refuses a missing required changes container" (fun () ->
    LibraryQueries.readDiagnosticContent sp zeroRepo CancellationToken.None
    |> awaitTask
    |> ignore)

fails "missing changes container yields no quantity and is not provisioned" (fun () ->
    observe sp 205L CancellationToken.None ignore
    |> ignore)

let result =
    {| database = databaseName
       actualCosmosPages = actualPages
       actualCosmosRows = actualRows
       checks = checks.ToArray()
       bytes = bytes
       batches = batches
       segments = 2
       recordsSeeded = 206
       boundary = 205
       replayFloor = 203
       provider = "10.2.2-hpk.1"
       actorAcceptance = "not exercised; typed fixtures seeded through actual provider" |}

File.WriteAllText(
    Path.Combine(__SOURCE_DIRECTORY__, "production-result.json"),
    JsonSerializer.Serialize(result, Constants.JsonSerializerOptions)
)

printfn "RESULT %d checks" checks.Count
