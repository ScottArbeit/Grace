namespace Grace.Server

open Grace.Shared
open Grace.Actors
open Grace.Types.Common
open Grace.Types.RepositoryContentCounter
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
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

    /// Gives the lazy first connection enough bounded time for a healthy CI container to finish its handshake.
    let connectionTimeout = TimeSpan.FromSeconds 10.0

    /// Builds the StackExchange.Redis configuration whose native reconnect lifecycle is bounded by Grace at each call boundary.
    let configurationForEndpoint (host: string) (port: int) =
        let configuration =
            ConfigurationOptions(
                AbortOnConnectFail = false,
                ConnectTimeout = int connectionTimeout.TotalMilliseconds,
                SyncTimeout = int commandTimeout.TotalMilliseconds,
                AsyncTimeout = int connectionTimeout.TotalMilliseconds
            )

        configuration.EndPoints.Add(host, port)
        configuration

    /// Requires an explicit readiness probe when StackExchange.Redis returns its reconnecting multiplexer before a socket is connected.
    let requiresReadinessProbe isConnected = not isConnected

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
        String.Join("|", encodedOperationId, operation, change.PreviousCount, change.CurrentCount, change.Revision)

    /// Parses a bounded recent result and rejects malformed or inconsistent cache data as a miss.
    let tryDeserialize value =
        if String.IsNullOrWhiteSpace value then
            None
        else
            match value.Split('|') with
            | [| operationId; operation; previousCount; currentCount; revision |] ->
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
                          Int64.TryParse(currentCount, NumberStyles.None, CultureInfo.InvariantCulture),
                          Int64.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture)
                        with
                    | Some operation, (true, previousCount), (true, currentCount), (true, revision) when
                        previousCount >= 0L
                        && currentCount >= 0L
                        && revision > 0L
                        ->
                        Some
                            {
                                OperationId = operationId
                                Operation = operation
                                PreviousCount = previousCount
                                CurrentCount = currentCount
                                Revision = revision
                            }
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

    /// Uses one lazy StackExchange.Redis connection with native reconnect, readiness proof, bounded commands, and structured failure evidence.
    type RedisRepositoryCounterRecentResult(host: string, port: int, log: ILogger) =

        let configuration = configurationForEndpoint host port

        let connection = lazy (task { return! ConnectionMultiplexer.ConnectAsync configuration })

        let database (cancellationToken: CancellationToken) : Task<IDatabase> =
            task {
                let! multiplexer = connection.Value.WaitAsync(connectionTimeout, cancellationToken)
                let database = multiplexer.GetDatabase()

                if requiresReadinessProbe multiplexer.IsConnected then
                    let! _ =
                        database
                            .PingAsync()
                            .WaitAsync(connectionTimeout, cancellationToken)

                    ()

                return database
            }

        let logBoundaryFailure operation cacheKey fallback (error: Exception) =
            log.LogWarning(
                error,
                "Redis repository counter recent-result {Operation} failed for cache key {CacheKey}; using nonauthoritative fallback {Fallback}.",
                operation,
                cacheKey,
                fallback
            )

        /// Creates the Redis accelerator with a no-op logger for focused callers that intentionally omit structured diagnostics.
        new(host: string, port: int) = new RedisRepositoryCounterRecentResult(host, port, NullLogger.Instance)

        interface IRepositoryCounterRecentResult with
            member _.TryGetAsync(repositoryId, storagePoolId, manifestAddress, operationId, (cancellationToken: CancellationToken)) =
                task {
                    let cacheKey = key repositoryId storagePoolId manifestAddress operationId

                    try
                        let! database = database cancellationToken

                        let! value =
                            database
                                .StringGetAsync(cacheKey)
                                .WaitAsync(commandTimeout, cancellationToken)

                        return if value.IsNullOrEmpty then None else tryDeserialize (string value)
                    with
                    | :? RedisException as error ->
                        logBoundaryFailure "GET" cacheKey "cache-miss" error
                        return None
                    | :? TimeoutException as error ->
                        logBoundaryFailure "GET" cacheKey "cache-miss" error
                        return None
                }

            member _.TrySetAsync(repositoryId, storagePoolId, manifestAddress, change, (cancellationToken: CancellationToken)) =
                task {
                    let cacheKey = key repositoryId storagePoolId manifestAddress change.OperationId

                    try
                        let! database = database cancellationToken

                        return!
                            database
                                .StringSetAsync(cacheKey, serialize change, expiry)
                                .WaitAsync(commandTimeout, cancellationToken)
                    with
                    | :? RedisException as error ->
                        logBoundaryFailure "SET" cacheKey "unconfirmed-write" error
                        return false
                    | :? TimeoutException as error ->
                        logBoundaryFailure "SET" cacheKey "unconfirmed-write" error
                        return false
                }

        interface IDisposable with
            /// Releases the singleton multiplexer when application shutdown follows a completed connection attempt.
            member _.Dispose() =
                if connection.IsValueCreated
                   && connection.Value.IsCompletedSuccessfully then
                    connection.Value.Result.Dispose()
