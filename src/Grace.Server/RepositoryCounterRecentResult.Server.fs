namespace Grace.Server

open Grace.Shared
open Grace.Actors
open Grace.Types.Common
open Grace.Types.RepositoryContentCounter
open StackExchange.Redis
open System
open System.Globalization
open System.Text
open System.Threading
open System.Threading.Tasks

/// Provides a nonauthoritative Redis accelerator for recently completed repository counter operations.
module RepositoryCounterRecentResult =

    /// Defines the exact ten-minute lifetime required for recent counter results.
    let expiry = TimeSpan.FromMinutes 10.0

    /// Bounds individual Redis connection and command waits without adding an outer retry policy.
    let private commandTimeout = TimeSpan.FromSeconds 2.0

    /// Creates the opaque direct-lookup key for one repository-manifest operation.
    let key repositoryId storagePoolId manifestAddress operationId =
        let identity = $"{repositoryId:N}|{storagePoolId}|{manifestAddress}|{operationId}"
        let digest = Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes identity)
        $"grace:repository-counter:recent:v1:{Convert.ToHexStringLower digest}"

    /// Serializes the bounded change without treating Redis as a membership ledger.
    let serialize (change: RepositoryContentCounterCompletedChange) =
        let operation =
            match change.Operation with
            | RepositoryContentCounterChangeOperation.Added -> "add"
            | RepositoryContentCounterChangeOperation.Removed -> "remove"

        let encodedOperationId = Convert.ToBase64String(Encoding.UTF8.GetBytes(string change.OperationId))
        String.Join("|", encodedOperationId, operation, change.PreviousCount, change.CurrentCount)

    /// Parses a bounded recent result and rejects malformed or inconsistent cache data as a miss.
    let tryDeserialize value =
        if String.IsNullOrWhiteSpace value then
            None
        else
            match value.Split('|') with
            | [| operationId; operation; previousCount; currentCount |] ->
                try
                    let operationId =
                        Convert.FromBase64String operationId
                        |> Encoding.UTF8.GetString
                        |> RepositoryContentCounterOperationId

                    let operation =
                        match operation with
                        | "add" -> Some RepositoryContentCounterChangeOperation.Added
                        | "remove" -> Some RepositoryContentCounterChangeOperation.Removed
                        | _ -> None

                    match operation,
                          Int64.TryParse(previousCount, NumberStyles.None, CultureInfo.InvariantCulture),
                          Int64.TryParse(currentCount, NumberStyles.None, CultureInfo.InvariantCulture)
                        with
                    | Some operation, (true, previousCount), (true, currentCount) when previousCount >= 0L && currentCount >= 0L ->
                        Some { OperationId = operationId; Operation = operation; PreviousCount = previousCount; CurrentCount = currentCount }
                    | _ -> None
                with
                | :? FormatException
                | :? DecoderFallbackException -> None
            | _ -> None

    /// Represents an intentionally absent Redis configuration as cache misses and ignored best-effort writes.
    type UnavailableRepositoryCounterRecentResult() =
        interface IRepositoryCounterRecentResult with
            member _.TryGetAsync(_, _, _, _, _) = Task.FromResult<RepositoryContentCounterCompletedChange option>(None)

            member _.TrySetAsync(_, _, _, _, _) = Task.FromResult false

    /// Uses one lazy StackExchange.Redis connection with native reconnect and bounded direct GET/SET calls.
    type RedisRepositoryCounterRecentResult(host: string, port: int) =

        let configuration =
            ConfigurationOptions(
                AbortOnConnectFail = false,
                ConnectTimeout = int commandTimeout.TotalMilliseconds,
                SyncTimeout = int commandTimeout.TotalMilliseconds,
                AsyncTimeout = int commandTimeout.TotalMilliseconds
            )

        do configuration.EndPoints.Add(host, port)

        let connection = lazy (task { return! ConnectionMultiplexer.ConnectAsync configuration })

        let database (cancellationToken: CancellationToken) : Task<IDatabase> =
            task {
                let! multiplexer = connection.Value.WaitAsync(commandTimeout, cancellationToken)
                return multiplexer.GetDatabase()
            }

        interface IRepositoryCounterRecentResult with
            member _.TryGetAsync(repositoryId, storagePoolId, manifestAddress, operationId, (cancellationToken: CancellationToken)) =
                task {
                    try
                        let! database = database cancellationToken

                        let! value =
                            database
                                .StringGetAsync(key repositoryId storagePoolId manifestAddress operationId)
                                .WaitAsync(commandTimeout, cancellationToken)

                        return if value.IsNullOrEmpty then None else tryDeserialize (string value)
                    with
                    | :? RedisException
                    | :? TimeoutException -> return None
                }

            member _.TrySetAsync(repositoryId, storagePoolId, manifestAddress, change, (cancellationToken: CancellationToken)) =
                task {
                    try
                        let! database = database cancellationToken

                        return!
                            database
                                .StringSetAsync(key repositoryId storagePoolId manifestAddress change.OperationId, serialize change, expiry)
                                .WaitAsync(commandTimeout, cancellationToken)
                    with
                    | :? RedisException
                    | :? TimeoutException -> return false
                }
