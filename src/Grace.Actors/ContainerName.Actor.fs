namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Actors.Runtime
open Dapr.Client
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Diagnostics
open System.Threading.Tasks
open Azure.Storage.Blobs

module ContainerName =

    let GetActorId (repositoryId: RepositoryId) = ActorId($"{repositoryId}")

    /// In-memory cache actor for mapping RepositoryId's to object storage container names.
    type ContainerNameActor(host: ActorHost) =
        inherit Actor(host)

        let mutable actorStartTime: Instant = Instant.MinValue
        let mutable containerName: ContainerName = String.Empty
        let mutable azureContainerClient: BlobContainerClient = null

        let actorName = ActorName.ContainerName
        let log = host.LoggerFactory.CreateLogger(actorName)

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            log.LogInformation("{CurrentInstant} Activated {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
            Task.CompletedTask

        override this.OnPreActorMethodAsync context =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant()
            //logger.LogInformation $"Entering ContainerNameActor.{context.MethodName}."
            Task.CompletedTask

        override this.OnPostActorMethodAsync context =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            log.LogInformation("{CurrentInstant}: CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration_ms}ms.", getCurrentInstantExtended(), this.correlationId, actorName, context.MethodName, this.Id, duration_ms)
            //logger.LogInformation $"ContainerNameActor.{context.MethodName} took {methodDuration.TotalMilliseconds}ms."
            Task.CompletedTask

        interface IContainerNameActor with
            member this.GetContainerName correlationId =
                task {
                    try
                        this.correlationId <- correlationId
                        if not <| String.IsNullOrEmpty(containerName) then
                            return Ok containerName
                        else
                            let repositoryId = Guid.Parse(host.Id.GetId())
                            let repositoryActorId = ActorId($"{repositoryId}")
                            let repositoryActorProxy = ActorProxyFactory().CreateActorProxy<IRepositoryActor>(repositoryActorId, ActorName.Repository)
                            let! repositoryDto = repositoryActorProxy.Get correlationId

                            let organizationActorId = ActorId($"{repositoryDto.OrganizationId}")
                            let organizationActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(organizationActorId, ActorName.Organization)
                            let! organizationDto = organizationActorProxy.Get correlationId
    
                            let ownerActorId = ActorId($"{repositoryDto.OwnerId}")
                            let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(ownerActorId, ActorName.Owner)
                            let! ownerDto = ownerActorProxy.Get correlationId
    
                            containerName <- $"{ownerDto.OwnerName}-{organizationDto.OrganizationName}-{repositoryDto.RepositoryName}".ToLowerInvariant()
                            return Ok containerName
                    with ex ->
                        Activity.Current.SetStatus(ActivityStatusCode.Error, "Exception while creating a container name.")
                            .AddTag("repositoryId", $"{host.Id.GetId()}")
                            .AddTag("ex.Message", $"{ex.Message}")
                            .AddTag("ex.StackTrace", $"{ex.StackTrace}") |> ignore
                        let exc = createExceptionResponse ex
                        logToConsole $"{exc}"
                        return Error "Exception while creating a container name."
                }