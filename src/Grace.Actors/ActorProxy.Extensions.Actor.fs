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
        let CreateActorProxy (branchId: BranchId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IBranchActor>(branchId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Branch)
            orleansContext.Add(nameof RepositoryId, repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module BranchName =
        /// Creates an ActorProxy for a BranchName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (repositoryId: RepositoryId) (branchName: BranchName) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IBranchNameActor>($"{repositoryId}|{branchName}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.BranchName)
            orleansContext.Add(nameof RepositoryId, repositoryId)
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
            (ownerId: OwnerId)
            (organizationId: OrganizationId)
            (repositoryId: RepositoryId)
            correlationId
            =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IDiffActor>((GetPrimaryKey directoryVersionId1 directoryVersionId2), correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Diff)
            orleansContext.Add(nameof OwnerId, ownerId)
            orleansContext.Add(nameof OrganizationId, organizationId)
            orleansContext.Add(nameof RepositoryId, repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module DirectoryVersion =
        /// Creates an ActorProxy for a DirectoryVersion actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId: DirectoryVersionId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(directoryVersionId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.DirectoryVersion)
            orleansContext.Add(nameof RepositoryId, repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module DirectoryAppearance =
        /// Creates an ActorProxy for a DirectoryAppearance actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (directoryVersionId: DirectoryVersionId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IDirectoryAppearanceActor>(directoryVersionId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.DirectoryAppearance)
            orleansContext.Add(nameof RepositoryId, repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module FileAppearance =
        /// Creates an ActorProxy for a FileAppearance actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (fileVersionWithRelativePath: string) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IFileAppearanceActor>(fileVersionWithRelativePath, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.FileAppearance)
            orleansContext.Add(nameof RepositoryId, repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module GlobalLock =
        /// Creates an ActorProxy for a GlobalLock actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (lockId: string) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IGlobalLockActor>(lockId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.GlobalLock)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Organization =
        /// Creates an ActorProxy for an Organization actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (organizationId: OrganizationId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOrganizationActor>(organizationId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Organization)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module OrganizationName =
        /// Creates an ActorProxy for an OrganizationName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationName: OrganizationName) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOrganizationNameActor>($"{ownerId}|{organizationName}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OrganizationName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Owner =
        /// Creates an ActorProxy for an Owner actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOwnerActor>(ownerId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OwnerName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module OwnerName =
        /// Creates an ActorProxy for an OwnerName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerName: OwnerName) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOwnerNameActor>(ownerName, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OwnerName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module PersonalAccessToken =
        /// Creates an ActorProxy for a PersonalAccessToken actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (userId: string) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IPersonalAccessTokenActor>(userId, correlationId)

            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.PersonalAccessToken)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Reminder =
        /// Creates an ActorProxy for a Reminder actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (reminderId: ReminderId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IReminderActor>(reminderId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Reminder)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Reference =
        /// Creates an ActorProxy for a Reference actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (referenceId: ReferenceId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IReferenceActor>(referenceId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Reference)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Repository =
        /// Creates an ActorProxy for a Repository actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (organizationId: OrganizationId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IRepositoryActor>(repositoryId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof OrganizationId, organizationId)
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Repository)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module RepositoryName =
        /// Gets an ActorId for a RepositoryName actor.
        let GetPrimaryKey (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) = $"{repositoryName}|{ownerId}|{organizationId}"

        /// Creates an ActorProxy for a RepositoryName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryName: RepositoryName) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IRepositoryNameActor>($"{ownerId}|{organizationId}|{repositoryName}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof OrganizationId, organizationId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.RepositoryName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module PromotionGroup =
        open Grace.Types.PromotionGroup

        /// Creates an ActorProxy for a PromotionGroup actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (promotionGroupId: PromotionGroupId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IPromotionGroupActor>(promotionGroupId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.PromotionGroup)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module PromotionQueue =
        open Grace.Types.Queue

        /// Creates an ActorProxy for a PromotionQueue actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (targetBranchId: BranchId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IPromotionQueueActor>(targetBranchId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.PromotionQueue)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module IntegrationCandidate =
        open Grace.Types.Queue

        /// Creates an ActorProxy for an IntegrationCandidate actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (candidateId: CandidateId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IIntegrationCandidateActor>(candidateId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.IntegrationCandidate)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module GateAttestation =
        open Grace.Types.Queue

        /// Creates an ActorProxy for a GateAttestation actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (gateAttestationId: GateAttestationId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IGateAttestationActor>(gateAttestationId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.GateAttestation)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module ConflictReceipt =
        open Grace.Types.Queue

        /// Creates an ActorProxy for a ConflictReceipt actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (conflictReceiptId: ConflictReceiptId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IConflictReceiptActor>(conflictReceiptId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.ConflictReceipt)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Policy =
        open Grace.Types.Policy

        /// Creates an ActorProxy for a Policy actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (targetBranchId: BranchId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IPolicyActor>(targetBranchId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Policy)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Review =
        open Grace.Types.Review

        /// Creates an ActorProxy for a Review actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (candidateId: CandidateId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IReviewActor>(candidateId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Review)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module Stage0 =
        open Grace.Types.Review

        /// Creates an ActorProxy for a Stage 0 analysis actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (referenceId: ReferenceId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IStage0Actor>(referenceId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Stage0)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module WorkItem =
        open Grace.Types.WorkItem

        /// Creates an ActorProxy for a WorkItem actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (workItemId: WorkItemId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IWorkItemActor>(workItemId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.WorkItem)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module AccessControl =
        /// Creates an ActorProxy for an AccessControl actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (scopeKey: string) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IAccessControlActor>(scopeKey, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.AccessControl)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    module RepositoryPermission =
        /// Creates an ActorProxy for a RepositoryPermission actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IRepositoryPermissionActor>($"{repositoryId}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.RepositoryPermission)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain
