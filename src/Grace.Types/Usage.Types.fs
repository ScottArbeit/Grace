namespace Grace.Types

open Grace.Shared
open Grace.Types.Common
open NodaTime
open Orleans
open System

/// Contains immutable usage fact contracts for Grace operations telemetry.
module Usage =

    /// Identifies the only UsageFact schema version supported by this Grace.Types contract.
    [<Literal>]
    let UsageFactSchemaVersion = 1

    /// Identifies Grace Server as the source for repository storage usage facts emitted in v1.
    [<Literal>]
    let DefaultUsageFactSource = "Grace.Server"

    /// The durable identity of an immutable usage fact.
    type UsageFactId = Guid

    /// Identifies the known operational usage measurement carried by a usage fact.
    type UsageFactKind =
        | RepositoryStorageBytesMinute = 1

    /// Identifies the evidence confidence attached to a usage fact.
    type UsageFactConfidence =
        | Observed = 1

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
            SchemaVersion: int
            UsageFactId: UsageFactId
            CorrelationId: CorrelationId
            FactKind: UsageFactKind
            Scope: UsageFactScope
            Resource: UsageFactResource
            Source: string
            Confidence: UsageFactConfidence
            Quantity: int64
            ObservedAt: Instant
        }

        /// Represents the deterministic empty fact used before caller input contributes usage evidence.
        static member Empty =
            {
                Class = nameof UsageFact
                SchemaVersion = UsageFactSchemaVersion
                UsageFactId = UsageFactId.Empty
                CorrelationId = String.Empty
                FactKind = UsageFactKind.RepositoryStorageBytesMinute
                Scope = UsageFactScope.Empty
                Resource = UsageFactResource.Empty
                Source = String.Empty
                Confidence = UsageFactConfidence.Observed
                Quantity = 0L
                ObservedAt = Constants.DefaultTimestamp
            }

        /// Lists the fact kinds that v1 consumers must understand before accepting a usage fact.
        static member SupportedV1FactKinds =
            [|
                UsageFactKind.RepositoryStorageBytesMinute
            |]

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
                SchemaVersion = UsageFactSchemaVersion
                UsageFactId = usageFactId
                CorrelationId = correlationId
                FactKind = UsageFactKind.RepositoryStorageBytesMinute
                Scope = { OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId }
                Resource = { StoragePoolId = storagePoolId }
                Source = DefaultUsageFactSource
                Confidence = UsageFactConfidence.Observed
                Quantity = quantity
                ObservedAt = UsageFact.NormalizeObservedAtToMinute observedAt
            }

        /// Validates the contract invariants required before a raw fact can enter a durable usage stream.
        static member Validate(fact: UsageFact) =
            let errors = ResizeArray<string>()

            if isNull (box fact) then
                errors.Add("UsageFact is required.")
            else
                if fact.Class <> nameof UsageFact then errors.Add("Class must be UsageFact.")

                if fact.SchemaVersion <> UsageFactSchemaVersion then
                    errors.Add($"SchemaVersion '{fact.SchemaVersion}' is not supported. Expected '{UsageFactSchemaVersion}'.")

                if fact.UsageFactId = UsageFactId.Empty then
                    errors.Add("UsageFactId is required.")

                if String.IsNullOrWhiteSpace fact.CorrelationId then
                    errors.Add("CorrelationId is required.")

                if not (Array.contains fact.FactKind UsageFact.SupportedV1FactKinds) then
                    errors.Add($"FactKind '{int fact.FactKind}' is not supported.")

                if isNull (box fact.Scope) then
                    errors.Add("Scope is required.")
                else
                    if fact.Scope.OwnerId = OwnerId.Empty then
                        errors.Add("Scope.OwnerId is required.")

                    if fact.Scope.OrganizationId = OrganizationId.Empty then
                        errors.Add("Scope.OrganizationId is required.")

                    if fact.Scope.RepositoryId = RepositoryId.Empty then
                        errors.Add("Scope.RepositoryId is required.")

                if isNull (box fact.Resource) then
                    errors.Add("Resource is required.")
                else if String.IsNullOrWhiteSpace fact.Resource.StoragePoolId then
                    errors.Add("Resource.StoragePoolId is required.")

                if String.IsNullOrWhiteSpace fact.Source then errors.Add("Source is required.")

                if not (Enum.IsDefined(typeof<UsageFactConfidence>, fact.Confidence)) then
                    errors.Add($"Confidence '{int fact.Confidence}' is not supported.")

                if fact.Quantity <= 0L then errors.Add("Quantity must be greater than zero.")

                if fact.ObservedAt
                   <> UsageFact.NormalizeObservedAtToMinute fact.ObservedAt then
                    errors.Add("ObservedAt must be normalized to a UTC minute boundary.")

                if fact.ObservedAt = Constants.DefaultTimestamp then
                    errors.Add("ObservedAt is required.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)
