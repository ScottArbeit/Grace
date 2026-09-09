namespace Grace.Actors

open Grace.Shared
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Runtime
open Orleans.Storage
open System
open System.Runtime.ExceptionServices
open System.Text.Json
open System.Threading.Tasks

/// Reads and writes remote Library records through the six configured Orleans persistence purposes.
module LibraryRecords =

    /// Compares persisted values through the same stable JSON representation used by Grace storage.
    let private valuesEqual<'T> (left: 'T) (right: 'T) =
        let leftJson = JsonSerializer.Serialize<'T>(left, Constants.JsonSerializerOptions)
        let rightJson = JsonSerializer.Serialize<'T>(right, Constants.JsonSerializerOptions)
        String.Equals(leftJson, rightJson, StringComparison.Ordinal)

    /// Rethrows the original storage exception after a recovery read cannot confirm its effect.
    let private rethrow<'T> (exceptionInfo: ExceptionDispatchInfo) : 'T =
        exceptionInfo.Throw()
        Unchecked.defaultof<'T>

    [<Literal>]
    let ControlStorageName = "GraceLibraryControlStorage"

    [<Literal>]
    let ChangesStorageName = "GraceLibraryChangesStorage"

    [<Literal>]
    let CurrentStorageName = "GraceLibraryCurrentStorage"

    [<Literal>]
    let ReceiptsStorageName = "GraceLibraryReceiptsStorage"

    [<Literal>]
    let HistoryStorageName = "GraceLibraryHistoryStorage"

    [<Literal>]
    let BaselinesStorageName = "GraceLibraryBaselinesStorage"

    [<Literal>]
    let ControlContainerName = "grace-library-control"

    [<Literal>]
    let ChangesContainerName = "grace-library-changes"

    [<Literal>]
    let CurrentContainerName = "grace-library-current"

    [<Literal>]
    let ReceiptsContainerName = "grace-library-receipts"

    [<Literal>]
    let HistoryContainerName = "grace-library-history"

    [<Literal>]
    let BaselinesContainerName = "grace-library-baselines"

    /// Builds the provider key whose leading components form the configured hierarchical partition key.
    let key (components: string list) =
        if List.isEmpty components
           || components
              |> List.exists String.IsNullOrWhiteSpace then
            invalidArg (nameof components) "Library record keys require non-empty components."

        String.Join('|', components)

    /// Reads one record into a fresh Orleans state wrapper so retries cannot reuse a stale ETag.
    let read<'T> (services: IServiceProvider) storageName grainType recordKey =
        task {
            let storage = services.GetRequiredKeyedService<IGrainStorage>(storageName)
            let state = GrainState<'T>()
            do! storage.ReadStateAsync(grainType, GrainId.Create(grainType, recordKey), state)
            return if state.RecordExists then Some(state.State, state.ETag) else None
        }

    /// Writes one record with a fresh state wrapper and confirms an ambiguous successful write by exact reread.
    let write<'T> (services: IServiceProvider) storageName grainType recordKey etag value =
        task {
            let storage = services.GetRequiredKeyedService<IGrainStorage>(storageName)
            let state = GrainState<'T>(value, etag)

            try
                do! storage.WriteStateAsync(grainType, GrainId.Create(grainType, recordKey), state)
                return state.ETag
            with
            | ex ->
                let original = ExceptionDispatchInfo.Capture(ex)

                try
                    match! read<'T> services storageName grainType recordKey with
                    | Some (existing, recoveredEtag) when valuesEqual existing value -> return recoveredEtag
                    | _ -> return rethrow original
                with
                | _ -> return rethrow original
        }

    /// Clears one record and accepts a missing recovery read as confirmation after an ambiguous response.
    let clear<'T> (services: IServiceProvider) storageName grainType recordKey etag value =
        task {
            let storage = services.GetRequiredKeyedService<IGrainStorage>(storageName)
            let state = GrainState<'T>(value, etag)

            try
                do! storage.ClearStateAsync(grainType, GrainId.Create(grainType, recordKey), state)
            with
            | ex ->
                let original = ExceptionDispatchInfo.Capture(ex)

                try
                    match! read<'T> services storageName grainType recordKey with
                    | None -> return ()
                    | Some _ -> return rethrow original
                with
                | _ -> return rethrow original
        }

    /// Creates an immutable record or returns its existing value for deterministic replay comparison.
    let create<'T> (services: IServiceProvider) storageName grainType recordKey (value: 'T) : Task<Choice<'T, 'T>> =
        task {
            match! read<'T> services storageName grainType recordKey with
            | Some (existing, _) -> return Choice2Of2 existing
            | None ->
                let! _ = write services storageName grainType recordKey null value
                return Choice1Of2 value
        }

    /// Creates an immutable record or accepts only its byte-equivalent deterministic replay.
    let createExact<'T> (services: IServiceProvider) storageName grainType recordKey (value: 'T) : Task<'T> =
        task {
            match! create<'T> services storageName grainType recordKey value with
            | Choice1Of2 created -> return created
            | Choice2Of2 existing ->
                if valuesEqual existing value then
                    return existing
                else
                    return invalidOp $"Library record '{recordKey}' already contains a different value."
        }
