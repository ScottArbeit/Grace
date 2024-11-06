namespace Grace.Actors.Extensions

open Dapr.Actors
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Types
open Grace.Shared.Utilities
open System

module ActorProxy =

    type Dapr.Actors.Client.IActorProxyFactory with

        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IActor>(actorId: ActorId, actorType: string, correlationId: CorrelationId) =
            let actorProxy = actorProxyFactory.CreateActorProxy<'T>(actorId, actorType)
            memoryCache.CreateCorrelationIdEntry actorId correlationId
            //logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {actorType}; ActorId: {actorId}."
            actorProxy

    module Branch =
        let GetActorId (branchId: BranchId) = ActorId($"{branchId}")

        let CreateActorProxy (branchId: BranchId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IBranchActor>(GetActorId branchId, ActorName.Branch, correlationId)

    module BranchName =
        let GetActorId (repositoryId: RepositoryId) (branchName: BranchName) = ActorId($"{branchName}|{repositoryId}")

        let CreateActorProxy (repositoryId: RepositoryId) (branchName: BranchName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IBranchNameActor>(GetActorId repositoryId branchName, ActorName.BranchName, correlationId)

    module Diff =
        /// Gets an ActorId for a Diff actor.
        let GetActorId (directoryId1: DirectoryVersionId) (directoryId2: DirectoryVersionId) =
            if directoryId1 < directoryId2 then
                ActorId($"{directoryId1}*{directoryId2}")
            else
                ActorId($"{directoryId2}*{directoryId1}")

        let CreateActorProxy (directoryId1: DirectoryVersionId) (directoryId2: DirectoryVersionId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IDiffActor>(GetActorId directoryId1 directoryId2, ActorName.Diff, correlationId)

    module DirectoryVersion =
        let GetActorId (directoryVersionId: DirectoryVersionId) = ActorId($"{directoryVersionId}")

        let CreateActorProxy (directoryVersionId: DirectoryVersionId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(
                GetActorId directoryVersionId,
                ActorName.DirectoryVersion,
                correlationId
            )

    module Organization =
        let GetActorId (organizationId: OrganizationId) = ActorId($"{organizationId}")

        let CreateActorProxy (organizationId: OrganizationId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOrganizationActor>(GetActorId organizationId, ActorName.Organization, correlationId)

    module OrganizationName =
        let GetActorId (ownerId: OwnerId) (organizationName: OrganizationName) = ActorId($"{organizationName}|{ownerId}")

        let CreateActorProxy (ownerId: OwnerId) (organizationName: OrganizationName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOrganizationNameActor>(
                GetActorId ownerId organizationName,
                ActorName.OrganizationName,
                correlationId
            )

    module Owner =
        let GetActorId (ownerId: OwnerId) = ActorId($"{ownerId}")

        let CreateActorProxy (ownerId: OwnerId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOwnerActor>(GetActorId ownerId, ActorName.Owner, correlationId)

    module OwnerName =
        let GetActorId (ownerName: OwnerName) = ActorId(ownerName)

        let CreateActorProxy (ownerName: OwnerName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOwnerNameActor>(GetActorId ownerName, ActorName.OwnerName, correlationId)

    module Reference =
        let GetActorId (referenceId: ReferenceId) = ActorId($"{referenceId}")

        let CreateActorProxy (referenceId: ReferenceId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IReferenceActor>(GetActorId referenceId, ActorName.Reference, correlationId)

    module Repository =
        let GetActorId (repositoryId: RepositoryId) = ActorId($"{repositoryId}")

        let CreateActorProxy (repositoryId: RepositoryId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IRepositoryActor>(GetActorId repositoryId, ActorName.Repository, correlationId)

    module RepositoryName =
        let GetActorId (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) =
            ActorId($"{repositoryName}|{ownerId}|{organizationId}")

        let CreateActorProxy (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IRepositoryNameActor>(
                GetActorId ownerId organizationId repositoryName,
                ActorName.RepositoryName,
                correlationId
            )
