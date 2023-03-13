namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Dapr.Actors.Runtime
open Grace.Shared.Constants
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Storage =

    /// Retrieves the actor's state from storage.
    let RetrieveState<'T> (stateManager: IActorStateManager) (dtoStateName: string) =
        task {
            return! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> 
                task {
                    try
                        let! conditionalValue = stateManager.TryGetStateAsync<'T>(dtoStateName)
                        return if conditionalValue.HasValue then
                                  Some conditionalValue.Value
                               else
                                  None
                    with ex ->
                        logToConsole $"{createExceptionResponse ex}"
                        raise ex
                        return None
                })
        }

    /// Saves the actor's state to storage.
    let SaveState<'T> (stateManager: IActorStateManager) (dtoStateName: string) dto = 
        task {
            do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync<'T>(dtoStateName, dto))
        } :> Task

    /// Deletes the actor's state from storage.
    let DeleteState (stateManager: IActorStateManager) (dtoStateName: string) = 
        task {
            //return! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> 
                //task {
                    return! stateManager.TryRemoveStateAsync(dtoStateName)
                //})
        }

    //module AzureBlobStorage =

    //    let private getBlobClient (repositoryId: RepositoryId) (relativePath: RelativePath) =
            
    //        let blobClient = BlobClient(Uri())

    //    let GetReadSharedAccessSignature (repositoryId: RepositoryId) (containerName: StorageContainerName) (relativePath: RelativePath) =
    //        let blobContainerClient = getContainerClient containerName
    //        let blobSasBuilder = BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddMinutes(Constants.SharedAccessSignatureExpiration))
    //        blobSasBuilder.BlobName <- relativePath.ToString()
    //        Task.FromResult(blobContainerClient.GenerateSasUri(blobSasBuilder).ToString())
