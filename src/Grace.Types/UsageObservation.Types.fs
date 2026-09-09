namespace Grace.Types

open System
open Grace.Shared
open Grace.Types.Usage
open NodaTime
open Orleans

/// Defines completed source readings that do not add to minute usage.
module UsageObservation =
    /// Preserves one immutable DirectoryVersion declaration reading and its complete non-atomic enumeration window.
    [<GenerateSerializer>]
    type DirectoryVersionSizeObservation =
        {
            ObservationId: Guid
            Scope: UsageFactScope
            DeclaredLogicalBytes: int64
            DistinctContentCount: int64
            EnumerationStartedAt: Instant
            EnumerationFinishedAt: Instant
        }

        /// Rejects incomplete identity, scope, quantities and read windows before SQL acceptance.
        static member Validate(value: DirectoryVersionSizeObservation) =
            let errors = ResizeArray<string>()

            if isNull (box value) then
                errors.Add "Observation is required."
            else
                if value.ObservationId = Guid.Empty then errors.Add "ObservationId is required."

                if isNull (box value.Scope) then
                    errors.Add "Scope is required."
                elif value.Scope.OwnerId = Guid.Empty
                     || value.Scope.OrganizationId = Guid.Empty
                     || value.Scope.RepositoryId = Guid.Empty then
                    errors.Add "Scope requires non-empty owner, organization and repository IDs."

                if value.DeclaredLogicalBytes < 0L then
                    errors.Add "DeclaredLogicalBytes must be nonnegative."

                if value.DistinctContentCount < 0L then
                    errors.Add "DistinctContentCount must be nonnegative."

                if value.EnumerationStartedAt = Constants.DefaultTimestamp
                   || value.EnumerationFinishedAt = Constants.DefaultTimestamp then
                    errors.Add "A complete enumeration window is required."

                if value.EnumerationFinishedAt < value.EnumerationStartedAt then
                    errors.Add "The enumeration window is reversed."

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)
