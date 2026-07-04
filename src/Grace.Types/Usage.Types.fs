namespace Grace.Types

open Grace.Shared
open Grace.Types.Common
open NodaTime
open Orleans
open System

/// Contains immutable usage fact contracts for Grace operations telemetry.
module Usage =

    /// The durable identity of an immutable usage fact.
    type UsageFactId = Guid

    /// Identifies the known operational usage measurement carried by a usage fact.
    type UsageFactKind =
        | RepositoryStorageBytesMinute = 1

    /// Identifies the Grace repository scope that owns a usage fact.
    [<GenerateSerializer>]
    type UsageFactScope =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
        }

        /// Represents the deterministic empty scope used before caller input contributes identifiers.
        static member Empty = { OwnerId = OwnerId.Empty; OrganizationId = OrganizationId.Empty; RepositoryId = RepositoryId.Empty }

    /// Identifies the Grace resource that produced a usage fact.
    [<GenerateSerializer>]
    type UsageFactResource =
        {
            StoragePoolId: StoragePoolId
        }

        /// Represents the deterministic empty resource used before caller input contributes identifiers.
        static member Empty = { StoragePoolId = StoragePoolId String.Empty }

    /// Captures one immutable raw operational usage measurement before downstream ledger projection.
    [<GenerateSerializer>]
    type UsageFact =
        {
            Class: string
            UsageFactId: UsageFactId
            CorrelationId: CorrelationId
            FactKind: UsageFactKind
            Scope: UsageFactScope
            Resource: UsageFactResource
            Quantity: int64
            ObservedAt: Instant
        }

        /// Represents the deterministic empty fact used before caller input contributes usage evidence.
        static member Empty =
            {
                Class = nameof UsageFact
                UsageFactId = UsageFactId.Empty
                CorrelationId = String.Empty
                FactKind = UsageFactKind.RepositoryStorageBytesMinute
                Scope = UsageFactScope.Empty
                Resource = UsageFactResource.Empty
                Quantity = 0L
                ObservedAt = Constants.DefaultTimestamp
            }

        /// Normalizes an observation timestamp to the minute bucket used by repository storage facts.
        static member NormalizeObservedAtToMinute(observedAt: Instant) =
            let minuteTicks = Duration.FromMinutes(1.0).BclCompatibleTicks

            Instant.FromUnixTimeTicks(
                (observedAt.ToUnixTimeTicks() / minuteTicks)
                * minuteTicks
            )

        /// Builds a repository storage fact with the timestamp normalized to the minute bucket Grace bills and replays.
        static member RepositoryStorageBytesMinute
            (
                usageFactId: UsageFactId,
                correlationId: CorrelationId,
                ownerId: OwnerId,
                organizationId: OrganizationId,
                repositoryId: RepositoryId,
                storagePoolId: StoragePoolId,
                quantity: int64,
                observedAt: Instant
            ) =
            {
                Class = nameof UsageFact
                UsageFactId = usageFactId
                CorrelationId = correlationId
                FactKind = UsageFactKind.RepositoryStorageBytesMinute
                Scope = { OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId }
                Resource = { StoragePoolId = storagePoolId }
                Quantity = quantity
                ObservedAt = UsageFact.NormalizeObservedAtToMinute observedAt
            }

        /// Validates the contract invariants required before a raw fact can enter a durable usage stream.
        static member Validate(fact: UsageFact) =
            let errors = ResizeArray<string>()

            if fact.Class <> nameof UsageFact then errors.Add("Class must be UsageFact.")

            if fact.UsageFactId = UsageFactId.Empty then
                errors.Add("UsageFactId is required.")

            if String.IsNullOrWhiteSpace fact.CorrelationId then
                errors.Add("CorrelationId is required.")

            if not (Enum.IsDefined(typeof<UsageFactKind>, fact.FactKind)) then
                errors.Add($"FactKind '{int fact.FactKind}' is not supported.")

            if fact.Scope.OwnerId = OwnerId.Empty then
                errors.Add("Scope.OwnerId is required.")

            if fact.Scope.OrganizationId = OrganizationId.Empty then
                errors.Add("Scope.OrganizationId is required.")

            if fact.Scope.RepositoryId = RepositoryId.Empty then
                errors.Add("Scope.RepositoryId is required.")

            if String.IsNullOrWhiteSpace fact.Resource.StoragePoolId then
                errors.Add("Resource.StoragePoolId is required.")

            if fact.Quantity <= 0L then errors.Add("Quantity must be greater than zero.")

            if fact.ObservedAt
               <> UsageFact.NormalizeObservedAtToMinute fact.ObservedAt then
                errors.Add("ObservedAt must be normalized to a UTC minute boundary.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)
