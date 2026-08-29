namespace Grace.Actors

open Grace.Actors.Interfaces
open Grace.Types.Common
open Grace.Types.Library
open Orleans
open System
open System.Threading
open System.Threading.Tasks

/// Owns one repository's bounded Library catalog and serialized Library change lane.
type RepositoryLibraryActor(coordinator: ILibraryCoordinator) =
    inherit Grain()

    interface IRepositoryLibraryActor with

        member this.InitializeCatalog libraryCatalog _correlationId =
            let repositoryId = this.GetPrimaryKey()

            if libraryCatalog.RepositoryId <> repositoryId then
                invalidArg (nameof libraryCatalog) "The Library catalog repository does not match the actor key."

            coordinator.InitializeAsync(repositoryId, libraryCatalog, CancellationToken.None)

        member this.GetCatalog _correlationId = coordinator.GetCatalogAsync(this.GetPrimaryKey(), CancellationToken.None)

        member this.SetCatalog libraryCatalog _correlationId =
            let repositoryId = this.GetPrimaryKey()

            if libraryCatalog.RepositoryId <> repositoryId then
                invalidArg (nameof libraryCatalog) "The Library catalog repository does not match the actor key."

            coordinator.SetCatalogAsync(repositoryId, libraryCatalog, CancellationToken.None)

        member this.IsInLibrary relativePath _correlationId =
            if String.IsNullOrWhiteSpace relativePath then
                Task.FromResult false
            else
                coordinator.IsInLibraryAsync(this.GetPrimaryKey(), relativePath, CancellationToken.None)

        member this.Submit command principalId correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()

                if command.RepositoryId <> repositoryId then
                    invalidArg (nameof command) "The Library command repository does not match the repository actor key."

                return! coordinator.SubmitAsync(command, principalId, correlationId, CancellationToken.None)
            }

        member this.Repair correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                do! coordinator.RepairAsync(repositoryId, CancellationToken.None)
            }
            :> Task

        member this.GetStatus correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                return! coordinator.GetStatusAsync(repositoryId, CancellationToken.None)
            }
