namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System.Runtime.Serialization

/// Contains the usage contracts: repository scopes, the storage meter's durable state, and the storage usage answer.
module Usage =

    /// Names one counter zero crossing inside a repository's storage meter, so applying it twice counts it once.
    [<GenerateSerializer>]
    type RepositoryStorageMeterChangeId =
        {
            [<Id(0u)>]
            StoragePoolId: StoragePoolId
            [<Id(1u)>]
            ManifestAddress: ManifestAddress
            [<Id(2u)>]
            Revision: int64
        }

    /// The storage meter's durable state: a running total and the changes it applied that counters have not released.
    /// It never grows with the number of retained manifests, only with changes still in flight.
    [<GenerateSerializer>]
    type RepositoryStorageMeterDto =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            RepositoryId: RepositoryId
            [<Id(2u)>]
            RetainedManifestBytes: int64
            [<Id(3u)>]
            AppliedChanges: RepositoryStorageMeterChangeId list
        }

        /// Represents a repository whose meter has applied nothing, which reads as zero retained bytes.
        static member Default = { Class = nameof RepositoryStorageMeterDto; RepositoryId = RepositoryId.Empty; RetainedManifestBytes = 0L; AppliedChanges = [] }

    /// Identifies the owner, organization, and repository that a usage read or size scan covers.
    [<GenerateSerializer>]
    type UsageScope =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
        }

        /// Represents the deterministic empty scope used before caller input contributes identifiers.
        static member Empty = { OwnerId = OwnerId.Empty; OrganizationId = OrganizationId.Empty; RepositoryId = RepositoryId.Empty }

    /// Names a class of content that Grace keeps for a repository and that counts toward its storage usage.
    [<KnownType("GetKnownTypes")>]
    type StorageContentClass =
        /// Files stored as manifests of content blocks, Library content included.
        | ManifestBackedFiles
        /// Files stored whole, without a manifest.
        | WholeFiles
        /// Retained work item text at its original UTF-8 length.
        | WorkItemText
        /// Artifact payloads.
        | ArtifactPayloads

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<StorageContentClass>()

    /// Says whether a storage usage answer counts every content class or only some of them.
    [<KnownType("GetKnownTypes")>]
    type StorageUsageCoverage =
        | Partial
        | Complete

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<StorageUsageCoverage>()

    /// The retained logical bytes for one content class; a class Grace does not count yet has no number, not zero.
    [<GenerateSerializer>]
    type StorageContentClassUsage =
        {
            [<Id(0u)>]
            ContentClass: StorageContentClass
            [<Id(1u)>]
            Counted: bool
            [<Id(2u)>]
            RetainedLogicalBytes: int64 option
        }

    /// The answer to `GET /usage/storage`: retained logical bytes by content class, with the coverage stated.
    /// There is no total while coverage is partial, so a partial answer never passes for one.
    [<GenerateSerializer>]
    type RepositoryStorageUsage =
        {
            [<Id(0u)>]
            Scope: UsageScope
            [<Id(1u)>]
            MeasuredAt: Instant
            [<Id(2u)>]
            Coverage: StorageUsageCoverage
            [<Id(3u)>]
            ContentClasses: StorageContentClassUsage list
        }

        /// The content classes Grace keeps for a repository, in the order an answer lists them.
        static member AllContentClasses =
            [
                StorageContentClass.ManifestBackedFiles
                StorageContentClass.WholeFiles
                StorageContentClass.WorkItemText
                StorageContentClass.ArtifactPayloads
            ]

        /// Builds the answer from the classes the meter counts; every other class reads as not counted yet.
        static member Create scope measuredAt (counted: (StorageContentClass * int64) list) =
            let classes =
                RepositoryStorageUsage.AllContentClasses
                |> List.map (fun contentClass ->
                    match counted
                          |> List.tryFind (fun (counter, _) -> counter = contentClass)
                        with
                    | Some (_, bytes) -> { ContentClass = contentClass; Counted = true; RetainedLogicalBytes = Some bytes }
                    | None -> { ContentClass = contentClass; Counted = false; RetainedLogicalBytes = None })

            {
                Scope = scope
                MeasuredAt = measuredAt
                Coverage =
                    if classes
                       |> List.forall (fun usage -> usage.Counted) then
                        StorageUsageCoverage.Complete
                    else
                        StorageUsageCoverage.Partial
                ContentClasses = classes
            }
