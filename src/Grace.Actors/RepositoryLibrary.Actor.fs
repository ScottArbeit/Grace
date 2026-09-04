namespace Grace.Actors

open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.Library
open Orleans
open System
open System.Globalization
open System.Threading
open System.Threading.Tasks

/// Contains the final authorization gate shared by the Orleans actor and focused tests.
module RepositoryLibrary =

    /// The bounded Receipts-purpose record kind used only for failed Library GraceEvent envelopes.
    [<Literal>]
    let FailedGraceEventRecordKind = "grace-event-fallback"

    /// Formats the accepted cursor as the bounded fallback record identity.
    let fallbackRecordKey (cursor: int64) = cursor.ToString("D20", CultureInfo.InvariantCulture)

    /// Derives the stable broker identity shared by first send and every ambiguous retry.
    let stableMessageId (repositoryId: RepositoryId) cursor = $"LibraryContentAvailable/{repositoryId:D}/{fallbackRecordKey cursor}"

    /// Builds the three-component actor key required by the Receipts storage purpose.
    let fallbackActorKey (repositoryId: RepositoryId) cursor = $"{repositoryId:D}|{FailedGraceEventRecordKind}|{fallbackRecordKey cursor}"

    /// Retries deterministic fallback identities in repository order and stops at the first still-retained envelope.
    let recoverFailedGraceEvents startCursor appliedThrough retry =
        task {
            let mutable cursor = max 1L startCursor
            let mutable resolved = true

            while resolved && cursor <= appliedThrough do
                let! retryResolved = retry cursor
                resolved <- retryResolved

                if retryResolved then cursor <- cursor + 1L

            return cursor
        }

    /// Rechecks current authority before invoking any durable submission effect.
    let submitWhenAuthorized authorize submit =
        task {
            match! authorize () with
            | Allowed _ ->
                let! receipt = submit ()
                return LibrarySubmitResult.Submitted receipt
            | Denied reason -> return LibrarySubmitResult.Forbidden reason
        }

/// Owns one repository's bounded Library catalog and serialized Library change lane.
type RepositoryLibraryActor(coordinator: ILibraryCoordinator, authorizer: ILibraryWriteAuthorizer, store: ILibraryStore, codec: ILibraryCursorCodec) =
    inherit Grain()

    let mutable graceEventRecoveryCursor = 1L

    /// Resolves one bounded failed-notification actor without widening the repository actor constructor contract.
    member private this.GetGraceEventFallbackActor(repositoryId, cursor) =
        this.GrainFactory.GetGrain<ILibraryGraceEventFallbackActor>(RepositoryLibrary.fallbackActorKey repositoryId cursor)

    interface IRepositoryLibraryActor with

        member this.InitializeCatalog libraryCatalog _correlationId =
            let repositoryId = this.GetPrimaryKey()

            if libraryCatalog.RepositoryId <> repositoryId then
                invalidArg (nameof libraryCatalog) "The Library catalog repository does not match the actor key."

            coordinator.InitializeAsync(repositoryId, libraryCatalog, CancellationToken.None)

        member this.GetCatalog _correlationId = coordinator.GetCatalogAsync(this.GetPrimaryKey(), CancellationToken.None)

        member this.SetCatalog requestHash result _correlationId =
            let repositoryId = this.GetPrimaryKey()

            if result.LibraryCatalog.RepositoryId <> repositoryId then
                invalidArg (nameof result) "The Library catalog repository does not match the actor key."

            coordinator.SetCatalogAsync(repositoryId, requestHash, result, CancellationToken.None)

        member this.IsInLibrary relativePath _correlationId =
            if String.IsNullOrWhiteSpace relativePath then
                Task.FromResult false
            else
                coordinator.IsInLibraryAsync(this.GetPrimaryKey(), relativePath, CancellationToken.None)

        member this.Submit command principalId authorization correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()

                if command.RepositoryId <> repositoryId then
                    invalidArg (nameof command) "The Library command repository does not match the repository actor key."

                let! result =
                    RepositoryLibrary.submitWhenAuthorized
                        (fun () -> authorizer.CheckAsync(repositoryId, authorization, CancellationToken.None))
                        (fun () -> coordinator.SubmitAsync(command, principalId, correlationId, CancellationToken.None))

                match result.Receipt with
                | Some receipt when receipt.Change.IsSome && receipt.Cursor.IsSome ->
                    let cursorEpoch, cursor =
                        match codec.TryDecode(repositoryId, receipt.Cursor.Value) with
                        | Some acceptedPosition -> acceptedPosition
                        | None -> invalidOp "The accepted Library receipt cursor could not be decoded for stable notification identity."

                    let! canonical = store.ReadCanonicalAsync(repositoryId, cursor, CancellationToken.None)

                    let acceptedCorrelationId =
                        match canonical with
                        | Some accepted when accepted.OperationId = receipt.OperationId -> accepted.CorrelationId
                        | Some _ -> invalidOp "The accepted Library change does not match its stable receipt operation identity."
                        | None -> invalidOp "The accepted Library change is unavailable for stable notification reconstruction."

                    let payload =
                        LibraryContentAvailable.Create(
                            repositoryId,
                            codec.Encode(repositoryId, cursorEpoch, 0L),
                            receipt.Cursor.Value,
                            receipt.LibraryCatalogVersion,
                            receipt.RecordedAt,
                            acceptedCorrelationId
                        )

                    let graceEvent = GraceEvent.LibraryContentAvailableEvent payload
                    let metadata = EventMetadata.New acceptedCorrelationId "RepositoryLibraryActor"

                    let messageId = RepositoryLibrary.stableMessageId repositoryId cursor

                    match
                        tryCreateGraceEventEnvelope
                            repositoryId
                            RepositoryLibrary.FailedGraceEventRecordKind
                            (RepositoryLibrary.fallbackRecordKey cursor)
                            messageId
                            graceEvent
                            metadata
                        with
                    | Some envelope ->
                        let fallbackActor = this.GetGraceEventFallbackActor(repositoryId, cursor)
                        do! fallbackActor.Publish envelope
                    | None -> ()
                | _ -> ()

                return result
            }

        member this.Repair correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! coordinator.RepairAsync(repositoryId, CancellationToken.None)

                let! control = store.ReadControlAsync(repositoryId, CancellationToken.None)

                let! nextCursor =
                    RepositoryLibrary.recoverFailedGraceEvents graceEventRecoveryCursor control.Document.AppliedThrough (fun cursor ->
                        let fallbackActor = this.GetGraceEventFallbackActor(repositoryId, cursor)
                        fallbackActor.Retry())

                graceEventRecoveryCursor <- nextCursor
            }
            :> Task

        member this.ProjectHistory correlationId = coordinator.ProjectHistoryAsync(this.GetPrimaryKey(), CancellationToken.None)

        member this.GetStatus correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                return! coordinator.GetStatusAsync(repositoryId, CancellationToken.None)
            }
