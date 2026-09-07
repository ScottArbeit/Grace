namespace Grace.Actors

open Grace.Shared
open Grace.Types.Common
open Grace.Types.Library
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.Threading

/// Runs the bounded full-partition SQL reads that Orleans point storage cannot express.
module LibraryQueries =

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

    /// Reads every current item in stable provider-document order from the exact repository item partition.
    let readCurrentItems (services: IServiceProvider) (repositoryId: RepositoryId) cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.CurrentContainerName

            let options = QueryRequestOptions(PartitionKey = Nullable(partition repositoryId "item"), MaxItemCount = Nullable 2000)

            let query =
                QueryDefinition("SELECT VALUE c.State FROM c WHERE c.PartitionKey = @repository AND c.PartitionKey2 = 'item' ORDER BY c.id")
                    .WithParameter("@repository", repositoryId.ToString("D"))

            use iterator = container.GetItemQueryIterator<LibraryCurrentItemDocument>(query, requestOptions = options)
            let values = ResizeArray<LibraryCurrentItemDocument>()

            while iterator.HasMoreResults do
                let! page = iterator.ReadNextAsync(cancellationToken)
                values.AddRange page.Resource

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

    /// Reads committed journal records after one position while touching only required cursor segments.
    let readChanges (services: IServiceProvider) (repositoryId: RepositoryId) afterCursor committedCursor maximumCount cancellationToken =
        task {
            let container =
                (database services)
                    .GetContainer LibraryRecords.ChangesContainerName

            let results = ResizeArray<LibraryAcceptedChangeRecord>()
            let mutable cursor = afterCursor + 1L

            while cursor <= committedCursor
                  && results.Count < maximumCount do
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
                      && results.Count < maximumCount do
                    let! page = iterator.ReadNextAsync(cancellationToken)

                    for value in page.Resource do
                        if results.Count < maximumCount then results.Add value

                cursor <- segmentEnd + 1L

            return results.ToArray()
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
