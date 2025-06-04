namespace Grace.Actors.Extensions

open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Shared
open Grace.Types.Types
open Grace.Shared.Utilities
open Orleans
open Orleans.Runtime
open System

module ActorProxy =

    let getGrainIdentity (grainId: GrainId) = $"{grainId.Type}/{grainId.Key}"

    type Orleans.IGrainFactory with
        /// Creates an Orleans grain reference for the given interface and actor type, and adds the correlationId to the grain's context.
        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IGrainWithGuidKey>(primaryKey: Guid, correlationId) =
            logToConsole $"Creating grain for {typeof<'T>.Name} with primary key: {primaryKey}."
            let grain = orleansClient.GetGrain<'T>(primaryKey)
            RequestContext.Set(Constants.CorrelationId, correlationId)
            logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {typeof<'T>.Name}; GrainIdentity: {grain.GetGrainId()}."
            grain

        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IGrainWithStringKey>(primaryKey: String, correlationId) =
            logToConsole $"Creating grain for {typeof<'T>.Name} with primary key: {primaryKey}."
            let grain = orleansClient.GetGrain<'T>(primaryKey)
            RequestContext.Set(Constants.CorrelationId, correlationId)
            logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {typeof<'T>.Name}; GrainIdentity: {grain.GetGrainId()}."
            grain

    module Branch =
        /// Creates an ActorProxy for a Branch actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (branchId: BranchId) (repositoryId: RepositoryId) correlationId =
            task {
                RequestContext.Set(Constants.ActorNameProperty, ActorName.Branch)
                RequestContext.Set(nameof (RepositoryId), repositoryId)
                let grain = orleansClient.CreateActorProxyWithCorrelationId<IBranchActor>(branchId, correlationId)
                return grain
            }

    module BranchName =
        /// Creates an ActorProxy for a BranchName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (repositoryId: RepositoryId) (branchName: BranchName) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.BranchName)
            orleansClient.CreateActorProxyWithCorrelationId<IBranchNameActor>($"{repositoryId}|{branchName}", correlationId)

    module Diff =
        /// Gets an ActorId for a Diff actor.
        let GetPrimaryKey (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) =
            if directoryVersionId1 < directoryVersionId2 then
                $"{directoryVersionId1}*{directoryVersionId2}"
            else
                $"{directoryVersionId2}*{directoryVersionId1}"

        /// Creates an ActorProxy for a Diff actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) (repositoryId: RepositoryId) correlationId =
            task {
                RequestContext.Set(Constants.ActorNameProperty, ActorName.Diff)
                RequestContext.Set(nameof (RepositoryId), repositoryId)
                let grain = orleansClient.CreateActorProxyWithCorrelationId<IDiffActor>((GetPrimaryKey directoryVersionId1 directoryVersionId2), correlationId)
                return grain
            }

    module DirectoryVersion =
        /// Creates an ActorProxy for a DirectoryVersion actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId: DirectoryVersionId) (repositoryId: RepositoryId) correlationId =
            task {
                RequestContext.Set(Constants.ActorNameProperty, ActorName.DirectoryVersion)
                RequestContext.Set(nameof (RepositoryId), repositoryId)
                let grain = orleansClient.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(directoryVersionId, correlationId)
                return grain
            }

    module DirectoryAppearance =
        /// Creates an ActorProxy for a DirectoryAppearance actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId: DirectoryVersionId) (repositoryId: RepositoryId) correlationId =
            task {
                RequestContext.Set(Constants.ActorNameProperty, ActorName.DirectoryAppearance)
                RequestContext.Set(nameof (RepositoryId), repositoryId)
                let grain = orleansClient.CreateActorProxyWithCorrelationId<IDirectoryAppearanceActor>(directoryVersionId, correlationId)
                return grain
            }

    module FileAppearance =
        /// Creates an ActorProxy for a FileAppearance actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (fileVersionWithRelativePath: string) (repositoryId: RepositoryId) correlationId =
            task {
                RequestContext.Set(Constants.ActorNameProperty, ActorName.FileAppearance)
                RequestContext.Set(nameof (RepositoryId), repositoryId)
                let grain = orleansClient.CreateActorProxyWithCorrelationId<IFileAppearanceActor>(fileVersionWithRelativePath, correlationId)
                return grain
            }

    module GlobalLock =
        /// Creates an ActorProxy for a GlobalLock actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (lockId: string) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.GlobalLock)
            orleansClient.CreateActorProxyWithCorrelationId<IGlobalLockActor>(lockId, correlationId)

    module Organization =
        /// Creates an ActorProxy for an Organization actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (organizationId: OrganizationId) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.Organization)
            orleansClient.CreateActorProxyWithCorrelationId<IOrganizationActor>(organizationId, correlationId)

    module OrganizationName =
        /// Creates an ActorProxy for an OrganizationName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationName: OrganizationName) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.OrganizationName)
            orleansClient.CreateActorProxyWithCorrelationId<IOrganizationNameActor>($"{ownerId}|{organizationName}", correlationId)

    module Owner =
        /// Creates an ActorProxy for an Owner actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.Owner)
            orleansClient.CreateActorProxyWithCorrelationId<IOwnerActor>(ownerId, correlationId)

    module OwnerName =
        /// Creates an ActorProxy for an OwnerName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerName: OwnerName) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.OwnerName)
            orleansClient.CreateActorProxyWithCorrelationId<IOwnerNameActor>(ownerName, correlationId)

    module Reminder =
        /// Creates an ActorProxy for a Reminder actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (reminderId: ReminderId) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.Reminder)
            orleansClient.CreateActorProxyWithCorrelationId<IReminderActor>(reminderId, correlationId)

    module Reference =
        /// Creates an ActorProxy for a Reference actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (referenceId: ReferenceId) (repositoryId: RepositoryId) correlationId =
            task {
                RequestContext.Set(Constants.ActorNameProperty, ActorName.Reference)
                RequestContext.Set(nameof (RepositoryId), repositoryId)
                let grain = orleansClient.CreateActorProxyWithCorrelationId<IReferenceActor>(referenceId, correlationId)
                return grain
            }

    module Repository =
        /// Creates an ActorProxy for a Repository actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (repositoryId: RepositoryId) correlationId =
            task {
                RequestContext.Set(Constants.ActorNameProperty, ActorName.Repository)
                RequestContext.Set(nameof (RepositoryId), repositoryId)
                let grain = orleansClient.CreateActorProxyWithCorrelationId<IRepositoryActor>(repositoryId, correlationId)
                return grain
            }

    module RepositoryName =
        /// Gets an ActorId for a RepositoryName actor.
        let GetPrimaryKey (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) = $"{repositoryName}|{ownerId}|{organizationId}"

        /// Creates an ActorProxy for a RepositoryName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) correlationId =
            RequestContext.Set(Constants.ActorNameProperty, ActorName.RepositoryName)
            orleansClient.CreateActorProxyWithCorrelationId<IRepositoryNameActor>($"{ownerId}|{organizationId}|{repositoryName}", correlationId)
