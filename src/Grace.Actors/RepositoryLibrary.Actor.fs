namespace Grace.Actors

open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.Library
open Orleans
open System
open System.Threading
open System.Threading.Tasks

/// Contains the final authorization gate shared by the Orleans actor and focused tests.
module RepositoryLibrary =

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
                    let! controlRead = store.ReadControlAsync(repositoryId, CancellationToken.None)

                    let payload =
                        LibraryContentAvailable.Create(
                            repositoryId,
                            codec.Encode(repositoryId, controlRead.Document.CursorEpoch, 0L),
                            receipt.Cursor.Value,
                            receipt.LibraryCatalogVersion,
                            receipt.RecordedAt,
                            correlationId
                        )

                    do! publishGraceEvent (GraceEvent.LibraryContentAvailableEvent payload) (EventMetadata.New correlationId "RepositoryLibraryActor")
                | _ -> ()

                return result
            }

        member this.Repair correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! coordinator.RepairAsync(repositoryId, CancellationToken.None)
            }
            :> Task

        member this.ProjectHistory correlationId = coordinator.ProjectHistoryAsync(this.GetPrimaryKey(), CancellationToken.None)

        member this.GetStatus correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                return! coordinator.GetStatusAsync(repositoryId, CancellationToken.None)
            }
