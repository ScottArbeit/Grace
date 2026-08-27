namespace Grace.Actors

open Grace.Actors.Interfaces
open Grace.Types.Common
open Grace.Types.SynchronizedContent
open Orleans
open System.Threading
open System.Threading.Tasks

/// Provides one bounded Orleans command lane per repository without retaining synchronized item state in the grain.
type SynchronizedContentRepositoryActor(grainFactory: IGrainFactory, coordinator: ISynchronizedContentCoordinator) =
    inherit Grain()

    /// Reads the Repository actor's persisted synchronization root configuration for every operation.
    member private this.GetRootConfiguration(repositoryId: RepositoryId, correlationId: CorrelationId) =
        task {
            let repositoryActor = grainFactory.GetGrain<IRepositoryActor>(repositoryId)
            let! repository = repositoryActor.Get correlationId
            return repository.SynchronizedRootConfiguration
        }

    interface ISynchronizedContentRepositoryActor with

        member this.Submit command principalId correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()

                if command.RepositoryId <> repositoryId then
                    invalidArg (nameof command) "The synchronized command repository does not match the repository actor key."

                let! rootConfiguration = this.GetRootConfiguration(repositoryId, correlationId)

                return! coordinator.SubmitAsync(command, rootConfiguration, principalId, correlationId, CancellationToken.None)
            }

        member this.Repair correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! rootConfiguration = this.GetRootConfiguration(repositoryId, correlationId)
                do! coordinator.RepairAsync(repositoryId, rootConfiguration, CancellationToken.None)
            }
            :> Task

        member this.GetStatus correlationId =
            task {
                let repositoryId = this.GetPrimaryKey()
                let! rootConfiguration = this.GetRootConfiguration(repositoryId, correlationId)
                return! coordinator.GetStatusAsync(repositoryId, rootConfiguration, CancellationToken.None)
            }
