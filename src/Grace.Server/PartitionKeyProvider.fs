namespace Grace.Server

open Orleans.Providers.CosmosDB
open System.Threading.Tasks

/// Provides a custom partition key for Orleans grains stored in CosmosDB.
type PartitionKeyProvider() =
    interface IPartitionKeyProvider with
        /// Supplies the Orleans partition key used when no request-specific partition was set.
        member this.GetPartitionKey(grainType: string, grainId: GrainId) : ValueTask<string> =
            // Use the sanitized string version of the RepositoryId as the partition key.
            ValueTask<string>(CosmosIdSanitizer.Sanitize(grainId.ToString()))
