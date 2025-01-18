namespace Grace.Actors.Extensions

open Dapr.Actors
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Types
open Grace.Shared.Utilities
open System

module ActorProxy =

    type Dapr.Actors.Client.IActorProxyFactory with

        /// Creates a Dapr ActorProxy instance for the given interface and actor type, and adds the correlationId to the server's MemoryCache so
        ///   it's available in each actor's OnActivateAsync() method.
        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IActor>(actorId: ActorId, actorType: string, correlationId: CorrelationId) =
            let actorProxy = actorProxyFactory.CreateActorProxy<'T>(actorId, actorType)
            //addTiming BeforeSettingCorrelationIdInMemoryCache actorType correlationId
            memoryCache.CreateCorrelationIdEntry actorId correlationId
            //addTiming AfterSettingCorrelationIdInMemoryCache actorType correlationId
            //logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {actorType}; ActorId: {actorId}."
            actorProxy

    module Branch =
        /// Gets an ActorId for a Branch actor.
        let GetActorId (branchId: BranchId) = ActorId($"{branchId}")

        /// Creates an ActorProxy for a Branch actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (branchId: BranchId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IBranchActor>(GetActorId branchId, ActorName.Branch, correlationId)

    module BranchName =
        /// Gets an ActorId for a BranchName actor.
        let GetActorId (repositoryId: RepositoryId) (branchName: BranchName) = ActorId($"{branchName}|{repositoryId}")

        /// Creates an ActorProxy for a BranchName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (repositoryId: RepositoryId) (branchName: BranchName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IBranchNameActor>(GetActorId repositoryId branchName, ActorName.BranchName, correlationId)

    module Diff =
        /// Gets an ActorId for a Diff actor.
        let GetActorId (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) =
            if directoryVersionId1 < directoryVersionId2 then
                ActorId($"{directoryVersionId1}*{directoryVersionId2}")
            else
                ActorId($"{directoryVersionId2}*{directoryVersionId1}")

        /// Creates an ActorProxy for a Diff actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IDiffActor>(GetActorId directoryVersionId1 directoryVersionId2, ActorName.Diff, correlationId)

    module DirectoryVersion =
        /// Gets an ActorId for a DirectoryVersion actor.
        let GetActorId (directoryVersionId: DirectoryVersionId) = ActorId($"{directoryVersionId}")

        /// Creates an ActorProxy for a DirectoryVersion actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId: DirectoryVersionId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(
                GetActorId directoryVersionId,
                ActorName.DirectoryVersion,
                correlationId
            )

    module GlobalLock =
        /// Gets an ActorId for a GlobalLock actor.
        let GetActorId (lockId: string) = ActorId($"{lockId}")

        /// Creates an ActorProxy for a GlobalLock actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (lockId: string) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IGlobalLockActor>(GetActorId lockId, ActorName.GlobalLock, correlationId)

    module Organization =
        /// Gets an ActorId for an Organization actor.
        let GetActorId (organizationId: OrganizationId) = ActorId($"{organizationId}")

        /// Creates an ActorProxy for an Organization actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (organizationId: OrganizationId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOrganizationActor>(GetActorId organizationId, ActorName.Organization, correlationId)

    module OrganizationName =
        /// Gets an ActorId for an OrganizationName actor.
        let GetActorId (ownerId: OwnerId) (organizationName: OrganizationName) = ActorId($"{organizationName}|{ownerId}")

        /// Creates an ActorProxy for an OrganizationName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationName: OrganizationName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOrganizationNameActor>(
                GetActorId ownerId organizationName,
                ActorName.OrganizationName,
                correlationId
            )

    module Owner =
        /// Gets an ActorId for an Owner actor.
        let GetActorId (ownerId: OwnerId) = ActorId($"{ownerId}")

        /// Creates an ActorProxy for an Owner actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOwnerActor>(GetActorId ownerId, ActorName.Owner, correlationId)

    module OwnerName =
        /// Gets an ActorId for an OwnerName actor.
        let GetActorId (ownerName: OwnerName) = ActorId(ownerName)

        /// Creates an ActorProxy for an OwnerName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerName: OwnerName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IOwnerNameActor>(GetActorId ownerName, ActorName.OwnerName, correlationId)

    module Reminder =
        /// Gets an ActorId for a Reminder actor.
        let GetActorId (reminderId: ReminderId) = ActorId($"{reminderId}")

        /// Creates an ActorProxy for a Reminder actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (reminderId: ReminderId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IReminderActor>(GetActorId reminderId, ActorName.Reminder, correlationId)

    module Reference =
        /// Gets an ActorId for a Reference actor.
        let GetActorId (referenceId: ReferenceId) = ActorId($"{referenceId}")

        /// Creates an ActorProxy for a Reference actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (referenceId: ReferenceId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IReferenceActor>(GetActorId referenceId, ActorName.Reference, correlationId)

    module Repository =
        /// Gets an ActorId for a Repository actor.
        let GetActorId (repositoryId: RepositoryId) = ActorId($"{repositoryId}")

        /// Creates an ActorProxy for a Repository actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (repositoryId: RepositoryId) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IRepositoryActor>(GetActorId repositoryId, ActorName.Repository, correlationId)

    module RepositoryName =
        /// Gets an ActorId for a RepositoryName actor.
        let GetActorId (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) =
            ActorId($"{repositoryName}|{ownerId}|{organizationId}")

        /// Creates an ActorProxy for a RepositoryName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) correlationId =
            actorProxyFactory.CreateActorProxyWithCorrelationId<IRepositoryNameActor>(
                GetActorId ownerId organizationId repositoryName,
                ActorName.RepositoryName,
                correlationId
            )
