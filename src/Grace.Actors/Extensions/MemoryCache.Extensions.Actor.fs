namespace Grace.Actors.Extensions

open Grace.Shared.Constants
open Grace.Types.Types
open Orleans.Runtime
open Microsoft.Extensions.Caching.Memory
open System
open System.Collections.Generic
open Grace.Shared
open Grace.Shared.Validation

module MemoryCache =
    [<Literal>]
    let ownerIdPrefix = "OwI"

    [<Literal>]
    let organizationIdPrefix = "OrI"

    [<Literal>]
    let repositoryIdPrefix = "ReI"

    [<Literal>]
    let branchIdPrefix = "BrI"

    [<Literal>]
    let ownerNamePrefix = "OwN"

    [<Literal>]
    let organizationNamePrefix = "OrN"

    [<Literal>]
    let repositoryNamePrefix = "ReN"

    [<Literal>]
    let branchNamePrefix = "BrN"

    [<Literal>]
    let correlationIdPrefix = "CoI"

    [<Literal>]
    let orleansContextPrefix = "Orl"

    type Microsoft.Extensions.Caching.Memory.IMemoryCache with

        /// Get a value from MemoryCache, if it exists.
        member this.GetFromCache<'T>(key: string) =
            let mutable value = Unchecked.defaultof<'T>
            if this.TryGetValue(key, &value) then Some value else None

        /// Create a new entry in MemoryCache with a default expiration time.
        member this.CreateWithDefaultExpirationTime (key: string) value =
            use newCacheEntry = this.CreateEntry(key, Value = value, AbsoluteExpiration = DateTimeOffset.UtcNow.Add MemoryCache.DefaultExpirationTime)
            //Utilities.logToConsole $"In CreateWithDefaultExpirationTime: {key}: {value}"
            ()

        /// Create a new entry in MemoryCache to link an ActorId with a CorrelationId.
        member this.CreateCorrelationIdEntry (identityString: string) (correlationId: CorrelationId) =
            use newCacheEntry =
                this.CreateEntry($"{correlationIdPrefix}:{identityString}", Value = correlationId, AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds 5)

            ()

        /// Check if we have an entry in MemoryCache for an ActorId, and return the CorrelationId if we have it.
        member this.GetCorrelationIdEntry(identityString: string) = this.GetFromCache<string> $"{correlationIdPrefix}:{identityString}"

        /// Create a new entry in MemoryCache to confirm that an OwnerId exists.
        member this.CreateOwnerIdEntry (ownerId: OwnerId) (value: string) = this.CreateWithDefaultExpirationTime $"{ownerIdPrefix}:{ownerId}" value

        /// Check if we have an entry in MemoryCache for an OwnerId.
        member this.GetOwnerIdEntry(ownerId: OwnerId) = this.GetFromCache<string> $"{ownerIdPrefix}:{ownerId}"

        /// Remove an entry in MemoryCache for an OwnerId.
        member this.RemoveOwnerIdEntry(ownerId: OwnerId) = this.Remove($"{ownerIdPrefix}:{ownerId}")


        /// Create a new entry in MemoryCache to confirm that an OwnerId has been deleted.
        member this.CreateDeletedOwnerIdEntry (ownerId: OwnerId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{ownerIdPrefix}:{ownerId}:Deleted" value

        /// Check if we have an entry in MemoryCache for a deleted OwnerId.
        member this.GetDeletedOwnerIdEntry(ownerId: OwnerId) = this.GetFromCache<string> $"{ownerIdPrefix}:{ownerId}:Deleted"

        /// Remove an entry in MemoryCache for a deleted OwnerId.
        member this.RemoveDeletedOwnerIdEntry(ownerId: OwnerId) = this.Remove($"{ownerIdPrefix}:{ownerId}:Deleted")


        /// Create a new entry in MemoryCache to confirm that an OrganizationId exists.
        member this.CreateOrganizationIdEntry (organizationId: OrganizationId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{organizationIdPrefix}:{organizationId}" value

        /// Check if we have an entry in MemoryCache for an OrganizationId.
        member this.GetOrganizationIdEntry(organizationId: OrganizationId) = this.GetFromCache<string> $"{organizationIdPrefix}:{organizationId}"

        /// Remove an entry in MemoryCache for an OrganizationId.
        member this.RemoveOrganizationIdEntry(organizationId: OrganizationId) = this.Remove($"{organizationIdPrefix}:{organizationId}")


        /// Create a new entry in MemoryCache to confirm that an OrganizationId has been deleted.
        member this.CreateDeletedOrganizationIdEntry (organizationId: OrganizationId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{organizationIdPrefix}:{organizationId}:Deleted" value

        /// Check if we have an entry in MemoryCache for a deleted OrganizationId.
        member this.GetDeletedOrganizationIdEntry(organizationId: OrganizationId) = this.GetFromCache<string> $"{organizationIdPrefix}:{organizationId}:Deleted"

        /// Remove an entry in MemoryCache for a deleted OrganizationId.
        member this.RemoveDeletedOrganizationIdEntry(organizationId: OrganizationId) = this.Remove($"{organizationIdPrefix}:{organizationId}:Deleted")


        /// Create a new entry in MemoryCache to confirm that a RepositoryId exists.
        member this.CreateRepositoryIdEntry (repositoryId: RepositoryId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{repositoryIdPrefix}:{repositoryId}" value

        /// Check if we have an entry in MemoryCache for a RepositoryId.
        member this.GetRepositoryIdEntry(repositoryId: RepositoryId) = this.GetFromCache<string> $"{repositoryIdPrefix}:{repositoryId}"

        /// Remove an entry in MemoryCache for a RepositoryId.
        member this.RemoveRepositoryIdEntry(repositoryId: RepositoryId) = this.Remove($"{repositoryIdPrefix}:{repositoryId}")


        /// Create a new entry in MemoryCache to confirm that a RepositoryId has been deleted.
        member this.CreateDeletedRepositoryIdEntry (repositoryId: RepositoryId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{repositoryIdPrefix}:{repositoryId}:Deleted" value

        /// Check if we have an entry in MemoryCache for a deleted RepositoryId.
        member this.GetDeletedRepositoryIdEntry(repositoryId: RepositoryId) = this.GetFromCache<string> $"{repositoryIdPrefix}:{repositoryId}:Deleted"

        /// Remove an entry in MemoryCache for a deleted RepositoryId.
        member this.RemoveDeletedRepositoryIdEntry(repositoryId: RepositoryId) = this.Remove($"{repositoryIdPrefix}:{repositoryId}:Deleted")


        /// Create a new entry in MemoryCache to confirm that a BranchId exists.
        member this.CreateBranchIdEntry (branchId: BranchId) (value: string) = this.CreateWithDefaultExpirationTime $"{branchIdPrefix}:{branchId}" value

        /// Check if we have an entry in MemoryCache for a BranchId.
        member this.GetBranchIdEntry(branchId: BranchId) = this.GetFromCache<string> $"{branchIdPrefix}:{branchId}"

        /// Remove an entry in MemoryCache for a BranchId.
        member this.RemoveBranchIdEntry(branchId: BranchId) = this.Remove($"{branchIdPrefix}:{branchId}")


        /// Create a new entry in MemoryCache to confirm that a BranchId has been deleted.
        member this.CreateDeletedBranchIdEntry (branchId: BranchId) (value: string) =
            this.CreateWithDefaultExpirationTime $"{branchIdPrefix}:{branchId}:Deleted" value

        /// Check if we have an entry in MemoryCache for a deleted BranchId.
        member this.GetDeletedBranchIdEntry(branchId: BranchId) = this.GetFromCache<string> $"{branchIdPrefix}:{branchId}:Deleted"

        /// Remove an entry in MemoryCache for a deleted BranchId.
        member this.RemoveDeletedBranchIdEntry(branchId: BranchId) = this.Remove($"{branchIdPrefix}:{branchId}:Deleted")


        /// Create a new entry in MemoryCache to link an OwnerName with an OwnerId.
        member this.CreateOwnerNameEntry (ownerName: OwnerName) (ownerId: OwnerId) =
            this.CreateWithDefaultExpirationTime $"{ownerNamePrefix}:{ownerName}" ownerId

        /// Check if we have an entry in MemoryCache for an OwnerName, and return the OwnerId if we have it.
        member this.GetOwnerNameEntry(ownerName: string) = this.GetFromCache<OwnerId> $"{ownerNamePrefix}:{ownerName}"

        /// Remove an entry in MemoryCache for an OwnerName.
        member this.RemoveOwnerNameEntry(ownerName: string) = this.Remove($"{ownerNamePrefix}:{ownerName}")


        /// Create a new entry in MemoryCache to link an OrganizationName with an OrganizationId.
        member this.CreateOrganizationNameEntry (organizationName: OrganizationName) (organizationId: OrganizationId) =
            this.CreateWithDefaultExpirationTime $"{organizationNamePrefix}:{organizationName}" organizationId

        /// Check if we have an entry in MemoryCache for an OrganizationName, and return the OrganizationId if we have it.
        member this.GetOrganizationNameEntry(organizationName: string) = this.GetFromCache<OrganizationId> $"{organizationNamePrefix}:{organizationName}"

        /// Remove an entry in MemoryCache for an OrganizationName.
        member this.RemoveOrganizationNameEntry(organizationName: string) = this.Remove($"{organizationNamePrefix}:{organizationName}")


        /// Create a new entry in MemoryCache to link a RepositoryName with a RepositoryId.
        member this.CreateRepositoryNameEntry (repositoryName: RepositoryName) (repositoryId: RepositoryId) =
            this.CreateWithDefaultExpirationTime $"{repositoryNamePrefix}:{repositoryName}" repositoryId

        /// Check if we have an entry in MemoryCache for a RepositoryName, and return the RepositoryId if we have it.
        member this.GetRepositoryNameEntry(repositoryName: string) = this.GetFromCache<RepositoryId> $"{repositoryNamePrefix}:{repositoryName}"

        /// Remove an entry in MemoryCache for a RepositoryName.
        member this.RemoveRepositoryNameEntry(repositoryName: string) = this.Remove($"{repositoryNamePrefix}:{repositoryName}")


        /// Create a new entry in MemoryCache to link a BranchName with a BranchId.
        member this.CreateBranchNameEntry(repositoryId: string, branchName: string, branchId: BranchId) =
            this.CreateWithDefaultExpirationTime $"{branchNamePrefix}:{repositoryId}-{branchName}" branchId

        /// Create a new entry in MemoryCache to link a BranchName with a BranchId.
        member this.CreateBranchNameEntry(repositoryId: RepositoryId, branchName: string, branchId: BranchId) =
            this.CreateWithDefaultExpirationTime $"{branchNamePrefix}:{repositoryId}-{branchName}" branchId

        /// Check if we have an entry in MemoryCache for a BranchName, and return the BranchId if we have it.
        member this.GetBranchNameEntry(repositoryId: string, branchName: string) = this.GetFromCache<BranchId> $"{branchNamePrefix}:{repositoryId}-{branchName}"

        /// Check if we have an entry in MemoryCache for a BranchName, and return the BranchId if we have it.
        member this.GetBranchNameEntry(repositoryId: RepositoryId, branchName: string) =
            this.GetFromCache<BranchId> $"{branchNamePrefix}:{repositoryId}-{branchName}"

        /// Remove an entry in MemoryCache for a BranchName.
        member this.RemoveBranchNameEntry(repositoryId: string, branchName: string) = this.Remove($"{branchNamePrefix}:{repositoryId}-{branchName}")

        /// Remove an entry in MemoryCache for a BranchName.
        member this.RemoveBranchNameEntry(repositoryId: RepositoryId, branchName: string) = this.Remove($"{branchNamePrefix}:{repositoryId}-{branchName}")


        /// Create a new entry in MemoryCache to store the current thread count information.
        member this.CreateThreadCountEntry(threadInfo: string) =
            use newCacheEntry = this.CreateEntry("ThreadCounts", Value = threadInfo, AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds 6)
            ()

        /// Check if we have an entry in MemoryCache for the current ThreadCount.
        member this.GetThreadCountEntry() = this.GetFromCache<string> "ThreadCounts"

        /// Create a new entry in MemoryCache to store context information for an Orleans grain.
        member this.CreateOrleansContextEntry(grainId: GrainId, orleansContext: Dictionary<string, obj>) =
            use newCacheEntry =
                this.CreateEntry($"{orleansContextPrefix}:{grainId}", Value = orleansContext, AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds 60)

            ()

        /// Check if we have an entry in MemoryCache for the Orleans context, and return the value if we have it.
        member this.GetOrleansContextEntry(grainId: GrainId) =
            this.GetFromCache<Dictionary<string, obj>> $"{orleansContextPrefix}:{grainId}"
            |> Option.bind (fun orleansContext -> Some(orleansContext :> IReadOnlyDictionary<string, obj>))
