namespace Grace.Actors.Extensions

open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Webhooks
open Grace.Shared.Utilities
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text

/// Groups Orleans actor helpers for actor proxy keys, proxies, state, or workflow transitions.
module ActorProxy =

    /// Formats the Orleans grain type and key for diagnostics and proxy tracing.
    let getGrainIdentity (grainId: GrainId) = $"{grainId.Type}/{grainId.Key}"

    /// Coordinates scoped upload session actor id logic for the ActorProxy Extensions actor.
    let private scopedUploadSessionActorId (repositoryId: RepositoryId) (uploadSessionId: UploadSessionId) =
        let preimage = $"grace.upload-session.v1\n{repositoryId}\n{uploadSessionId:N}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage))
        Guid(hash |> Array.take 16)

    /// Wraps orleans records exchanged by actor queries or projections.
    type Orleans.IGrainFactory with
        /// Creates an Orleans grain reference for the given interface and actor type, and adds the correlationId to the grain's context.
        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IGrainWithGuidKey>(primaryKey: Guid, correlationId) =
            //logToConsole $"Creating grain for {typeof<'T>.Name} with primary key: {primaryKey}."
            RequestContext.Set(Constants.CorrelationId, correlationId)
            let grain = orleansClient.GetGrain<'T>(primaryKey)
            //logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {typeof<'T>.Name}; GrainIdentity: {grain.GetGrainId()}."
            grain

        /// Creates an Orleans grain proxy and attaches the caller correlation id to the actor context.
        member this.CreateActorProxyWithCorrelationId<'T when 'T :> IGrainWithStringKey>(primaryKey: String, correlationId) =
            //logToConsole $"Creating grain for {typeof<'T>.Name} with primary key: {primaryKey}."
            RequestContext.Set(Constants.CorrelationId, correlationId)
            let grain = orleansClient.GetGrain<'T>(primaryKey)
            //logToConsole $"Created actor proxy: CorrelationId: {correlationId}; ActorType: {typeof<'T>.Name}; GrainIdentity: {grain.GetGrainId()}."
            grain

    /// Groups Orleans actor helpers for branch keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for branch name keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for diff keys, proxies, state, or workflow transitions.
    module Diff =
        /// Attempts to parse guid exact and returns no value when the required invariant is not met.
        let private tryParseGuidExact (format: string) (value: string) =
            try
                Some(Guid.ParseExact(value, format))
            with
            | :? FormatException -> None
            | :? ArgumentException -> None

        /// Parses a compound grain key into the owner, organization, and repository identifiers it carries.
        let TryParsePrimaryKey (primaryKey: string) =
            if String.IsNullOrWhiteSpace primaryKey then
                None
            elif primaryKey.Length = 64 then
                match tryParseGuidExact "N" primaryKey[0..31], tryParseGuidExact "N" primaryKey[32..63] with
                | Some directoryVersionId1, Some directoryVersionId2 -> Some(directoryVersionId1, directoryVersionId2)
                | _ -> None
            elif primaryKey.Contains("*", StringComparison.Ordinal) then
                match primaryKey.Split("*", StringSplitOptions.None) with
                | [| directoryVersionId1; directoryVersionId2 |] ->
                    match tryParseGuidExact "D" directoryVersionId1, tryParseGuidExact "D" directoryVersionId2 with
                    | Some directoryVersionId1, Some directoryVersionId2 -> Some(directoryVersionId1, directoryVersionId2)
                    | _ -> None
                | _ -> None
            else
                None

        /// Parses a compound grain key and raises when any required identifier segment is invalid.
        let ParsePrimaryKey (primaryKey: string) =
            match TryParsePrimaryKey primaryKey with
            | Some directoryVersionIds -> directoryVersionIds
            | None -> invalidArg (nameof primaryKey) $"Diff actor primary key is not a supported format: {primaryKey}"

        /// Gets an ActorId for a Diff actor.
        let GetPrimaryKey (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) =
            let directoryVersionId1Text = directoryVersionId1.ToString("N")
            let directoryVersionId2Text = directoryVersionId2.ToString("N")

            if directoryVersionId1 < directoryVersionId2 then
                $"{directoryVersionId1Text}{directoryVersionId2Text}"
            else
                $"{directoryVersionId2Text}{directoryVersionId1Text}"

        /// Builds a typed actor proxy from the identifiers encoded in a primary key.
        let CreateActorProxyForPrimaryKey (primaryKey: string) (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryId: RepositoryId) correlationId =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IDiffActor>(primaryKey, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Diff)
            orleansContext.Add(nameof OwnerId, ownerId)
            orleansContext.Add(nameof OrganizationId, organizationId)
            orleansContext.Add(nameof RepositoryId, repositoryId)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

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
            CreateActorProxyForPrimaryKey (GetPrimaryKey directoryVersionId1 directoryVersionId2) ownerId organizationId repositoryId correlationId

    /// Groups Orleans actor helpers for directory version keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for directory appearance keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for file appearance keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for global lock keys, proxies, state, or workflow transitions.
    module GlobalLock =
        /// Creates an ActorProxy for a GlobalLock actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (lockId: string) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IGlobalLockActor>(lockId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.GlobalLock)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for organization keys, proxies, state, or workflow transitions.
    module Organization =
        /// Creates an ActorProxy for an Organization actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (organizationId: OrganizationId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOrganizationActor>(organizationId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Organization)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for organization name keys, proxies, state, or workflow transitions.
    module OrganizationName =
        /// Creates an ActorProxy for an OrganizationName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (organizationName: OrganizationName) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOrganizationNameActor>($"{ownerId}|{organizationName}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OrganizationName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for owner keys, proxies, state, or workflow transitions.
    module Owner =
        /// Creates an ActorProxy for an Owner actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerId: OwnerId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOwnerActor>(ownerId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OwnerName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for owner name keys, proxies, state, or workflow transitions.
    module OwnerName =
        /// Creates an ActorProxy for an OwnerName actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (ownerName: OwnerName) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IOwnerNameActor>(ownerName, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.OwnerName)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for personal access token keys, proxies, state, or workflow transitions.
    module PersonalAccessToken =
        /// Creates an ActorProxy for a PersonalAccessToken actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (userId: string) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IPersonalAccessTokenActor>(userId, correlationId)

            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.PersonalAccessToken)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for reminder keys, proxies, state, or workflow transitions.
    module Reminder =
        /// Creates an ActorProxy for a Reminder actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (reminderId: ReminderId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IReminderActor>(reminderId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Reminder)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for reference keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for repository keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for repository name keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for promotion queue keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for promotion set keys, proxies, state, or workflow transitions.
    module PromotionSet =
        open Grace.Types.PromotionSet

        /// Creates an ActorProxy for a PromotionSet actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (promotionSetId: PromotionSetId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IPromotionSetActor>(promotionSetId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.PromotionSet)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for validation set keys, proxies, state, or workflow transitions.
    module ValidationSet =
        open Grace.Types.Validation

        /// Creates an ActorProxy for a ValidationSet actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (validationSetId: ValidationSetId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IValidationSetActor>(validationSetId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.ValidationSet)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for validation result keys, proxies, state, or workflow transitions.
    module ValidationResult =
        open Grace.Types.Validation

        /// Creates an ActorProxy for a ValidationResult actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (validationResultId: ValidationResultId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IValidationResultActor>(validationResultId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.ValidationResult)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for artifact keys, proxies, state, or workflow transitions.
    module Artifact =
        open Grace.Types.Artifact

        /// Creates an ActorProxy for an Artifact actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (artifactId: ArtifactId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IArtifactActor>(artifactId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Artifact)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for approval request keys, proxies, state, or workflow transitions.
    module ApprovalRequest =
        /// Builds the approval-request index key from repository scope and target branch identifiers.
        let scopeKey (scope: ApprovalScope) = $"{scope.OwnerId:N}|{scope.OrganizationId:N}|{scope.RepositoryId:N}|{scope.TargetBranchId:N}"

        /// Creates the typed Orleans proxy for the ActorProxy Extensions actor key.
        let CreateActorProxy (approvalRequestId: ApprovalRequestId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IApprovalRequestActor>(approvalRequestId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.ApprovalRequest)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

        /// Creates the approval-request index actor for a repository and target branch scope.
        let CreateIndexActorProxy (scope: ApprovalScope) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IApprovalRequestIndexActor>(scopeKey scope, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, scope.RepositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.ApprovalRequestIndex)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for upload session keys, proxies, state, or workflow transitions.
    module UploadSession =
        open Grace.Types.UploadSession

        /// Builds actor proxy for primary key data needed by the ActorProxy Extensions actor.
        let private createActorProxyForPrimaryKey (primaryKey: Guid) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IUploadSessionActor>(primaryKey, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.UploadSession)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

        /// Creates an ActorProxy for an UploadSession actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (uploadSessionId: UploadSessionId) (repositoryId: RepositoryId) (correlationId: string) =
            createActorProxyForPrimaryKey (scopedUploadSessionActorId repositoryId uploadSessionId) repositoryId correlationId

        /// Builds a typed actor proxy from the identifiers encoded in a primary key.
        let CreateActorProxyForPrimaryKey (primaryKey: Guid) (repositoryId: RepositoryId) (correlationId: string) =
            createActorProxyForPrimaryKey primaryKey repositoryId correlationId

    /// Groups Orleans actor helpers for policy keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for review keys, proxies, state, or workflow transitions.
    module Review =
        open Grace.Types.Review

        /// Creates an ActorProxy for a Review actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (promotionSetId: PromotionSetId) (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IReviewActor>(promotionSetId, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.Review)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for work item keys, proxies, state, or workflow transitions.
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

    /// Groups Orleans actor helpers for work item number keys, proxies, state, or workflow transitions.
    module WorkItemNumber =
        /// Creates an ActorProxy for a WorkItemNumber actor. The primary key is repository-scoped.
        let CreateActorProxy (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IWorkItemNumberActor>($"{repositoryId}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.WorkItemNumber)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for work item number counter keys, proxies, state, or workflow transitions.
    module WorkItemNumberCounter =
        /// Creates an ActorProxy for a WorkItemNumberCounter actor. The primary key is repository-scoped.
        let CreateActorProxy (repositoryId: RepositoryId) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IWorkItemNumberCounterActor>($"{repositoryId}", correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(nameof RepositoryId, repositoryId)
            orleansContext.Add(Constants.ActorNameProperty, ActorName.WorkItemNumberCounter)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for access control keys, proxies, state, or workflow transitions.
    module AccessControl =
        /// Creates an ActorProxy for an AccessControl actor, and adds the correlationId to the server's MemoryCache so
        ///   it's available in the OnActivateAsync() method.
        let CreateActorProxy (scopeKey: string) (correlationId: string) =
            let grain = orleansClient.CreateActorProxyWithCorrelationId<IAccessControlActor>(scopeKey, correlationId)
            let orleansContext = Dictionary<string, obj>()
            orleansContext.Add(Constants.ActorNameProperty, ActorName.AccessControl)
            memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
            grain

    /// Groups Orleans actor helpers for repository permission keys, proxies, state, or workflow transitions.
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
