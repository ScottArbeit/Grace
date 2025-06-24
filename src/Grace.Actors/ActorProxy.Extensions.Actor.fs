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
open System.Collections.Generic

module ActorProxy =

    let getGrainIdentity (grainId: GrainId) = $"{grainId.Type}/{grainId.Key}"

    type Orleans.IGrainFactory with
        /// Creates an Orleans grain reference for the given interface and actor type, and adds the correlationId to the grain's context.
        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IGrainWithGuidKey>(primaryKey: Guid, correlationId) =
            //logToConsole $"Creating grain for {typeof<'T>.Name} with primary key: {primaryKey}."
            RequestContext.Set(Constants.CorrelationId, correlationId)
            let grain = orleansClient.GetGrain<'T>(primaryKey)
            //logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {typeof<'T>.Name}; GrainIdentity: {grain.GetGrainId()}."
            grain

        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IGrainWithStringKey>(primaryKey: String, correlationId) =
            //logToConsole $"Creating grain for {typeof<'T>.Name} with primary key: {primaryKey}."
            RequestContext.Set(Constants.CorrelationId, correlationId)
            let grain = orleansClient.GetGrain<'T>(primaryKey)
            //logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {typeof<'T>.Name}; GrainIdentity: {grain.GetGrainId()}."
            grain

    module Branch =
        /// Creates an ActorProxy for a Branch actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (branchId: BranchId) (repositoryId: RepositoryId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IBranchActor>(branchId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Branch)
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module BranchName =
        /// Creates an ActorProxy for a BranchName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (repositoryId: RepositoryId) (branchName: BranchName) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IBranchNameActor>($"{repositoryId}|{branchName}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.BranchName)
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Diff =
        /// Gets an ActorId for a Diff actor.
        let GetPrimaryKey (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) =
            if directoryVersionId1 < directoryVersionId2 then
                $"{directoryVersionId1}*{directoryVersionId2}"
            else
                $"{directoryVersionId2}*{directoryVersionId1}"

        /// Creates an ActorProxy for a Diff actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy
            (directoryVersionId1: DirectoryVersionId)
            (directoryVersionId2: DirectoryVersionId)
            (organizationId: OrganizationId)
            (repositoryId: RepositoryId)
            correlationId
            =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IDiffActor>((GetPrimaryKey directoryVersionId1 directoryVersionId2), correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Diff)
            orleansContext.Add(nameof (OrganizationId), organizationId)
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module DirectoryVersion =
        /// Creates an ActorProxy for a DirectoryVersion actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId: DirectoryVersionId) (repositoryId: RepositoryId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(directoryVersionId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.DirectoryVersion)
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module DirectoryAppearance =
        /// Creates an ActorProxy for a DirectoryAppearance actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId: DirectoryVersionId) (repositoryId: RepositoryId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IDirectoryAppearanceActor>(directoryVersionId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.DirectoryAppearance)
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module FileAppearance =
        /// Creates an ActorProxy for a FileAppearance actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (fileVersionWithRelativePath: string) (repositoryId: RepositoryId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IFileAppearanceActor>(fileVersionWithRelativePath, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.FileAppearance)
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module GlobalLock =
        /// Creates an ActorProxy for a GlobalLock actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (lockId: string) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IGlobalLockActor>(lockId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.GlobalLock)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Organization =
        /// Creates an ActorProxy for an Organization actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (organizationId: OrganizationId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOrganizationActor>(organizationId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Organization)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module OrganizationName =
        /// Creates an ActorProxy for an OrganizationName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationName: OrganizationName) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOrganizationNameActor>($"{ownerId}|{organizationName}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OrganizationName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Owner =
        /// Creates an ActorProxy for an Owner actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOwnerActor>(ownerId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OwnerName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module OwnerName =
        /// Creates an ActorProxy for an OwnerName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerName: OwnerName) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOwnerNameActor>(ownerName, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OwnerName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Reminder =
        /// Creates an ActorProxy for a Reminder actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (reminderId: ReminderId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IReminderActor>(reminderId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Reminder)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Reference =
        /// Creates an ActorProxy for a Reference actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (referenceId: ReferenceId) (repositoryId: RepositoryId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IReferenceActor>(referenceId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Reference)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Repository =
        /// Creates an ActorProxy for a Repository actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (organizationId: OrganizationId) (repositoryId: RepositoryId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IRepositoryActor>(repositoryId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof (OrganizationId), organizationId)
            orleansContext.Add(nameof (RepositoryId), repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Repository)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module RepositoryName =
        /// Gets an ActorId for a RepositoryName actor.
        let GetPrimaryKey (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) = $"{repositoryName}|{ownerId}|{organizationId}"

        /// Creates an ActorProxy for a RepositoryName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IRepositoryNameActor>($"{ownerId}|{organizationId}|{repositoryName}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof (OrganizationId), organizationId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.RepositoryName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain
