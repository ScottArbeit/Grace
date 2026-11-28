namespace Grace.Types

open Grace.Types.Common
open Orleans

/// Contains the usage contracts shared by Grace's repository size scans.
module Usage =

    /// Identifies the owner, organization, and repository that a repository size scan reads.
    [<GenerateSerializer>]
    type UsageScope =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
        }

        /// Represents the deterministic empty scope used before caller input contributes identifiers.
        static member Empty = { OwnerId = OwnerId.Empty; OrganizationId = OrganizationId.Empty; RepositoryId = RepositoryId.Empty }
