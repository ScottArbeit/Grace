namespace Grace.Server

open Microsoft.Extensions.Options
open Orleans.Configuration
open Orleans.Persistence.Cosmos
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Defines the six purpose-specific Orleans Cosmos providers used by remote Libraries.
module LibraryPersistence =

    [<Literal>]
    let ControlStorageName = Grace.Actors.LibraryRecords.ControlStorageName

    [<Literal>]
    let ChangesStorageName = Grace.Actors.LibraryRecords.ChangesStorageName

    [<Literal>]
    let CurrentStorageName = Grace.Actors.LibraryRecords.CurrentStorageName

    [<Literal>]
    let ReceiptsStorageName = Grace.Actors.LibraryRecords.ReceiptsStorageName

    [<Literal>]
    let HistoryStorageName = Grace.Actors.LibraryRecords.HistoryStorageName

    [<Literal>]
    let BaselinesStorageName = Grace.Actors.LibraryRecords.BaselinesStorageName

    [<Literal>]
    let ControlContainerName = Grace.Actors.LibraryRecords.ControlContainerName

    [<Literal>]
    let ChangesContainerName = Grace.Actors.LibraryRecords.ChangesContainerName

    [<Literal>]
    let CurrentContainerName = Grace.Actors.LibraryRecords.CurrentContainerName

    [<Literal>]
    let ReceiptsContainerName = Grace.Actors.LibraryRecords.ReceiptsContainerName

    [<Literal>]
    let HistoryContainerName = Grace.Actors.LibraryRecords.HistoryContainerName

    [<Literal>]
    let BaselinesContainerName = Grace.Actors.LibraryRecords.BaselinesContainerName

    /// Maps one validated provider key to its document id and configured hierarchical partition prefix.
    type GraceDocumentIdProvider(options: IOptions<ClusterOptions>, partitionKeyLevelCount: int) =
        let defaultProvider = DefaultDocumentIdProvider(options)

        do
            if partitionKeyLevelCount < 1
               || partitionKeyLevelCount > 3 then
                invalidArg (nameof partitionKeyLevelCount) "Library Cosmos keys support one to three partition components."

        /// Reads the ordered partition components encoded at the start of the provider key.
        member private _.PartitionValues(grainType: string, grainId: GrainId) =
            let values =
                grainId
                    .Key
                    .ToString()
                    .Split('|', StringSplitOptions.None)

            if values.Length < partitionKeyLevelCount
               || values
                  |> Array.take partitionKeyLevelCount
                  |> Array.exists String.IsNullOrWhiteSpace then
                invalidArg (nameof grainId) $"Library record '{grainType}' requires {partitionKeyLevelCount} non-empty partition components."

            values |> Array.take partitionKeyLevelCount

        interface IDocumentIdProvider with
            member this.GetDocumentIdentifiers(grainType, grainId) =
                let values = this.PartitionValues(grainType, grainId)
                ValueTask<struct (string * string)>(struct (defaultProvider.GetId(grainType, grainId), values[0]))

            member this.GetDocumentKey(grainType, grainId) =
                let values = this.PartitionValues(grainType, grainId)

                ValueTask<CosmosDocumentKey>(CosmosDocumentKey(defaultProvider.GetId(grainType, grainId), values :> IReadOnlyList<string>))
